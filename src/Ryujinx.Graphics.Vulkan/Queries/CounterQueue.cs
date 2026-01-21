using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

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
        
        // 批量查询支持
        private readonly List<BufferedQuery> _activeBatchQueries = new();
        private readonly object _batchLock = new();

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
                    // 收集需要重置的查询索引
                    var indicesToReset = new List<uint>();
                    var queriesToReset = new List<BufferedQuery>();
                    
                    foreach (BufferedQuery query in _queryPool)
                    {
                        if (count == 0) break;
                        
                        indicesToReset.Add(query.GetQueryIndex());
                        queriesToReset.Add(query);
                        count--;
                    }
                    
                    // 以16个为一组进行批量重置
                    if (indicesToReset.Count > 0)
                    {
                        // 排序索引
                        indicesToReset.Sort();
                        
                        // 分组处理
                        for (int i = 0; i < indicesToReset.Count; i += 16)
                        {
                            uint startIndex = indicesToReset[i];
                            uint groupCount = (uint)Math.Min(16, indicesToReset.Count - i);
                            
                            // 执行批量重置
                            foreach (var query in queriesToReset)
                            {
                                query.PoolReset(cmd, ResetSequence);
                            }
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
                    ulong localCounter = _accumulatedCounter;
                    if (evt.TryConsume(ref localCounter, true, _waiterCount == 0 ? _wakeSignal : null))
                    {
                        _accumulatedCounter = localCounter;
                    }
                    else
                    {
                        // 如果查询失败，重新放回队列
                        lock (_lock)
                        {
                            _events.Enqueue(evt);
                        }
                    }
                }

                if (_waiterCount > 0)
                {
                    _eventConsumed.Set();
                }
            }
        }

        internal BufferedQuery GetQueryObject()
        {
            lock (_lock)
            {
                if (_queryPool.Count > 0)
                {
                    BufferedQuery result = _queryPool.Dequeue();
                    
                    // 如果是TBDR平台，尝试分配批量缓冲区槽位
                    if (_isTbdrPlatform)
                    {
                        result.TryAllocateBatchSlot(out _, out _);
                    }
                    
                    lock (_batchLock)
                    {
                        _activeBatchQueries.Add(result);
                    }
                    
                    return result;
                }

                var newQuery = new BufferedQuery(_gd, _device, _pipeline, Type, _gd.IsAmdWindows, _isTbdrPlatform);
                
                if (_isTbdrPlatform)
                {
                    newQuery.TryAllocateBatchSlot(out _, out _);
                }
                
                lock (_batchLock)
                {
                    _activeBatchQueries.Add(newQuery);
                }
                
                return newQuery;
            }
        }

        internal void ReturnQueryObject(BufferedQuery query)
        {
            lock (_lock)
            {
                lock (_batchLock)
                {
                    _activeBatchQueries.Remove(query);
                }
                
                _queryPool.Enqueue(query);
            }
        }
        
        // 获取批量查询信息
        internal List<QueryBatch> CollectBatchQueries()
        {
            var batches = new List<QueryBatch>();
            
            lock (_batchLock)
            {
                // 直接收集每个查询的批次信息，不进行分组合并
                foreach (var query in _activeBatchQueries)
                {
                    var batchInfo = query.GetBatchInfo();
                    if (batchInfo.QueryPool.Handle != 0)
                    {
                        batches.Add(batchInfo);
                    }
                }
            }
            
            return batches;
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
                while (_events.Count > 0)
                {
                    CounterQueueEvent flush = _events.Peek();
                    
                    ulong localCounter = _accumulatedCounter;
                    if (!flush.TryConsume(ref localCounter, true))
                    {
                        return;
                    }
                    
                    _accumulatedCounter = localCounter;
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

        public void Dispose()
        {
            lock (_lock)
            {
                while (_events.Count > 0)
                {
                    CounterQueueEvent evt = _events.Dequeue();
                    evt.Dispose();
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
            
            lock (_batchLock)
            {
                _activeBatchQueries.Clear();
            }

            _queuedEvent.Dispose();
            _wakeSignal.Dispose();
            _eventConsumed.Dispose();
        }
    }
}