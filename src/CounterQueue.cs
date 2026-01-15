using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        
        // 批量处理相关
        private readonly List<CounterQueueEvent> _pendingBatchEvents = new();
        private readonly object _batchLock = new();
        private int _batchSize = 0;
        private const int TargetBatchSize = 64;

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

            _consumerThread = new Thread(EventConsumer);
            _consumerThread.Start();
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, $"Created counter queue for {type} on TBDR platform");
            }
        }

        public void ResetCounterPool()
        {
            ResetSequence++;
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
                    // 使用新的方法签名，移除ref参数
                    var result = evt.TryConsumeWithResult(true, _waiterCount == 0 ? _wakeSignal : null);
                    if (result.Success)
                    {
                        _accumulatedCounter = result.Result;
                    }
                }

                if (_waiterCount > 0)
                {
                    _eventConsumed.Set();
                }
            }
        }
        
        // 异步处理事件
        private async void ProcessEventAsync(CounterQueueEvent evt)
        {
            try
            {
                var result = await evt.TryConsumeAsync(true, _waiterCount == 0 ? _wakeSignal : null);
                if (result.Success)
                {
                    Interlocked.Exchange(ref _accumulatedCounter, result.Result);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Error processing counter event: {ex.Message}");
            }
        }

        internal BufferedQuery GetQueryObject()
        {
            lock (_lock)
            {
                if (_queryPool.Count > 0)
                {
                    BufferedQuery result = _queryPool.Dequeue();
                    return result;
                }

                return new BufferedQuery(_gd, _device, _pipeline, Type, _gd.IsAmdWindows, _isTbdrPlatform);
            }
        }

        internal void ReturnQueryObject(BufferedQuery query)
        {
            lock (_lock)
            {
                query.ResetState();
                _queryPool.Enqueue(query);
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
                
                lock (_batchLock)
                {
                    _pendingBatchEvents.Add(_current);
                    _batchSize++;
                    
                    if (_batchSize >= TargetBatchSize)
                    {
                        ProcessBatch();
                    }
                }

                _current.OnResult += resultHandler;

                result = _current;

                _current = new CounterQueueEvent(this, Type, lastDrawIndex);
            }

            _queuedEvent.Set();

            return result;
        }
        
        private void ProcessBatch()
        {
            if (_pendingBatchEvents.Count == 0) return;
            
            lock (_batchLock)
            {
                foreach (var evt in _pendingBatchEvents)
                {
                    _events.Enqueue(evt);
                }
                
                _pendingBatchEvents.Clear();
                _batchSize = 0;
            }
        }

        public void QueueReset(ulong lastDrawIndex)
        {
            ulong draws = lastDrawIndex - _current.DrawIndex;

            lock (_lock)
            {
                _current.Clear(draws != 0);
            }
        }

        public void Flush(bool blocking)
        {
            lock (_batchLock)
            {
                ProcessBatch();
            }
            
            if (!blocking)
            {
                _wakeSignal.Set();
                return;
            }

            lock (_lock)
            {
                while (_events.Count > 0)
                {
                    CounterQueueEvent flush = _events.Peek();
                    var result = flush.TryConsumeWithResult(true);
                    if (!result.Success)
                    {
                        return;
                    }
                    _accumulatedCounter = result.Result;
                    _events.Dequeue();
                }
            }
        }

        public void FlushTo(CounterQueueEvent evt)
        {
            Interlocked.Increment(ref _waiterCount);

            _wakeSignal.Set();

            while (!evt.Disposed)
            {
                _eventConsumed.WaitOne(1);
            }

            Interlocked.Decrement(ref _waiterCount);
        }
        
        public async Task FlushAsync()
        {
            lock (_batchLock)
            {
                ProcessBatch();
            }
            
            _wakeSignal.Set();
            
            await Task.Run(() =>
            {
                while (true)
                {
                    lock (_lock)
                    {
                        if (_events.Count == 0)
                            break;
                    }
                    Thread.Sleep(1);
                }
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                while (_events.Count > 0)
                {
                    CounterQueueEvent evt = _events.Dequeue();
                    evt.Dispose();
                }
                
                lock (_batchLock)
                {
                    foreach (var evt in _pendingBatchEvents)
                    {
                        evt.Dispose();
                    }
                    _pendingBatchEvents.Clear();
                }

                Disposed = true;
            }

            _queuedEvent.Set();

            _consumerThread.Join();

            _current?.Dispose();

            foreach (BufferedQuery query in _queryPool)
            {
                query.Dispose();
            }

            _queuedEvent.Dispose();
            _wakeSignal.Dispose();
            _eventConsumed.Dispose();
        }
    }
}