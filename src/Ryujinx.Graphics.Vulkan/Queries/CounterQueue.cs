using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    class CounterQueue : IDisposable
    {
        private const int QueryPoolInitialSize = 100;

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly PipelineFull _pipeline;
        private readonly bool _isTbdrPlatform;

        public CounterType Type { get; }
        public bool Disposed { get; private set; }

        private readonly Queue<CounterQueueEvent> _events = new();
        private CounterQueueEvent _current;

        private ulong _accumulatedCounter;
        private int _waiterCount;

        private readonly object _lock = new();

        private readonly Queue<BufferedQuery> _queryPool;
        private readonly AutoResetEvent _queuedEvent = new(false);
        private readonly AutoResetEvent _wakeSignal = new(false);
        private readonly AutoResetEvent _eventConsumed = new(false);

        private readonly Thread _consumerThread;

        public int ResetSequence { get; private set; }
        
        // 统计信息
        private long _eventsProcessed;
        private long _eventsTimedOut;
        private DateTime _lastStatisticsLog = DateTime.UtcNow;

        internal CounterQueue(VulkanRenderer gd, Device device, PipelineFull pipeline, CounterType type, bool isTbdrPlatform)
        {
            _gd = gd;
            _device = device;
            _pipeline = pipeline;
            Type = type;
            _isTbdrPlatform = isTbdrPlatform;

            _queryPool = new Queue<BufferedQuery>(QueryPoolInitialSize);
            for (int i = 0; i < QueryPoolInitialSize; i++)
            {
                _queryPool.Enqueue(new BufferedQuery(_gd, _device, _pipeline, type, _gd.IsAmdWindows, _isTbdrPlatform));
            }

            _current = new CounterQueueEvent(this, type, 0);

            _consumerThread = new Thread(EventConsumer)
            {
                Name = $"CounterQueue_{type}",
                IsBackground = true
            };
            _consumerThread.Start();
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, $"Created counter queue for {type} on TBDR platform");
            }
            
            Logger.Info?.Print(LogClass.Gpu, $"CounterQueue initialized for {type}");
        }

        public void ResetCounterPool()
        {
            ResetSequence++;
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueue ResetCounterPool: Type={Type}, Sequence={ResetSequence}");
            }
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            lock (_queryPool)
            {
                count = Math.Min(count, _queryPool.Count);

                if (count > 0)
                {
                    foreach (BufferedQuery query in _queryPool)
                    {
                        query.PoolReset(cmd, ResetSequence);

                        if (--count == 0)
                        {
                            break;
                        }
                    }
                }
                
                if (_isTbdrPlatform && count > 0)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueue ResetFutureCounters: Type={Type}, Count={count}");
                }
            }
        }

        private void EventConsumer()
        {
            while (!Disposed)
            {
                CounterQueueEvent evt = null;
                lock (_lock)
                {
                    if (_events.Count > 0)
                    {
                        evt = _events.Dequeue();
                    }
                }

                if (evt == null)
                {
                    _queuedEvent.WaitOne();
                }
                else
                {
                    Interlocked.Increment(ref _eventsProcessed);
                    
                    bool consumed = evt.TryConsume(ref _accumulatedCounter, true, _waiterCount == 0 ? _wakeSignal : null);
                    
                    if (!consumed)
                    {
                        Interlocked.Increment(ref _eventsTimedOut);
                        
                        // 对于TBDR平台，记录超时但继续处理
                        if (_isTbdrPlatform)
                        {
                            Logger.Warning?.Print(LogClass.Gpu, 
                                $"CounterQueue event timed out: Type={Type}, DrawIndex={evt.DrawIndex}");
                        }
                    }
                }

                if (_waiterCount > 0)
                {
                    _eventConsumed.Set();
                }
                
                // 定期记录统计信息
                var now = DateTime.UtcNow;
                if ((now - _lastStatisticsLog).TotalSeconds > 60)
                {
                    LogStatistics();
                    _lastStatisticsLog = now;
                }
            }
            
            Logger.Debug?.Print(LogClass.Gpu, $"CounterQueue consumer thread exiting: Type={Type}");
        }

        internal BufferedQuery GetQueryObject()
        {
            lock (_lock)
            {
                if (_queryPool.Count > 0)
                {
                    BufferedQuery result = _queryPool.Dequeue();
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"CounterQueue GetQueryObject from pool: Type={Type}, PoolSize={_queryPool.Count}");
                    }
                    
                    return result;
                }

                // 池为空，创建新的查询对象
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueue creating new query object: Type={Type}");
                    
                return new BufferedQuery(_gd, _device, _pipeline, Type, _gd.IsAmdWindows, _isTbdrPlatform);
            }
        }

        internal void ReturnQueryObject(BufferedQuery query)
        {
            lock (_lock)
            {
                query.ResetState(); // 重置查询状态以便重用
                _queryPool.Enqueue(query);
                
                if (_isTbdrPlatform && _queryPool.Count % 10 == 0)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueue ReturnQueryObject: Type={Type}, PoolSize={_queryPool.Count}");
                }
            }
        }

        public CounterQueueEvent QueueReport(EventHandler<ulong> resultHandler, float divisor, ulong lastDrawIndex, bool hostReserved)
        {
            CounterQueueEvent result;
            ulong draws = lastDrawIndex - _current.DrawIndex;

            lock (_lock)
            {
                if (hostReserved)
                {
                    _current.ReserveForHostAccess();
                }

                _current.Complete(draws > 0 && Type != CounterType.TransformFeedbackPrimitivesWritten, divisor);
                _events.Enqueue(_current);

                _current.OnResult += resultHandler;

                result = _current;

                _current = new CounterQueueEvent(this, Type, lastDrawIndex);
                
                // 调试信息
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueue QueueReport: Type={Type}, Draws={draws}, EventsCount={_events.Count}");
                }
            }

            _queuedEvent.Set();

            return result;
        }

        public void QueueReset(ulong lastDrawIndex)
        {
            ulong draws = lastDrawIndex - _current.DrawIndex;

            lock (_lock)
            {
                _current.Clear(draws != 0);
                
                if (_isTbdrPlatform && draws != 0)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueue QueueReset: Type={Type}, Draws={draws}");
                }
            }
        }

        public void Flush(bool blocking)
        {
            if (!blocking)
            {
                _wakeSignal.Set();
                return;
            }

            lock (_lock)
            {
                int flushedCount = 0;
                
                while (_events.Count > 0)
                {
                    CounterQueueEvent flush = _events.Peek();
                    if (!flush.TryConsume(ref _accumulatedCounter, true))
                    {
                        return;
                    }
                    _events.Dequeue();
                    flushedCount++;
                }
                
                if (_isTbdrPlatform && flushedCount > 0)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueue Flush: Type={Type}, Flushed={flushedCount}");
                }
            }
        }

        public void FlushTo(CounterQueueEvent evt)
        {
            Interlocked.Increment(ref _waiterCount);

            _wakeSignal.Set();

            int waitIterations = 0;
            while (!evt.Disposed && waitIterations++ < 1000) // 防止无限等待
            {
                _eventConsumed.WaitOne(_isTbdrPlatform ? 1 : 10);
                
                // 检查事件是否已处理
                if (evt.Disposed)
                {
                    break;
                }
                
                // 对于TBDR平台，如果等待时间过长，记录警告
                if (_isTbdrPlatform && waitIterations > 100)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"CounterQueue FlushTo taking too long: Type={Type}, Iterations={waitIterations}");
                }
            }

            Interlocked.Decrement(ref _waiterCount);
            
            if (waitIterations >= 1000)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"CounterQueue FlushTo timeout: Type={Type}");
            }
        }

        private void LogStatistics()
        {
            Logger.Info?.Print(LogClass.Gpu, 
                $"CounterQueue {Type} Stats: " +
                $"EventsProcessed={_eventsProcessed}, " +
                $"EventsTimedOut={_eventsTimedOut}, " +
                $"CurrentEvents={_events.Count}, " +
                $"QueryPoolSize={_queryPool.Count}");
        }

        public void Dispose()
        {
            Logger.Info?.Print(LogClass.Gpu, $"CounterQueue disposing: Type={Type}");
            
            lock (_lock)
            {
                LogStatistics();
                
                while (_events.Count > 0)
                {
                    CounterQueueEvent evt = _events.Dequeue();
                    evt.Dispose();
                }

                Disposed = true;
            }

            _queuedEvent.Set();

            if (_consumerThread.IsAlive)
            {
                if (!_consumerThread.Join(5000)) // 5秒超时
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"CounterQueue consumer thread did not exit gracefully: Type={Type}");
                }
            }

            _current?.Dispose();

            foreach (BufferedQuery query in _queryPool)
            {
                query.Dispose();
            }

            _queuedEvent.Dispose();
            _wakeSignal.Dispose();
            _eventConsumed.Dispose();
            
            Logger.Info?.Print(LogClass.Gpu, $"CounterQueue disposed: Type={Type}");
        }
    }
}