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
                    evt.TryConsume(ref _accumulatedCounter, true, _waiterCount == 0 ? _wakeSignal : null);
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
        
        internal List<QueryBatch> CollectBatchQueries()
        {
            var batches = new List<QueryBatch>();
            
            lock (_batchLock)
            {
                var groups = _activeBatchQueries
                    .Where(q => q.GetBatchInfo().QueryPool.Handle != 0)
                    .GroupBy(q => (q.GetQueryPool().Handle, q.GetBatchInfo().ResultBuffer.Handle, q.Is64Bit()))
                    .ToList();
                
                foreach (var group in groups)
                {
                    var queries = group.OrderBy(q => q.GetQueryIndex()).ToList();
                    
                    int start = 0;
                    while (start < queries.Count)
                    {
                        int end = start;
                        while (end + 1 < queries.Count && 
                               queries[end + 1].GetQueryIndex() == queries[end].GetQueryIndex() + 1 &&
                               queries[end + 1].GetBatchInfo().ResultOffset == 
                               queries[end].GetBatchInfo().ResultOffset + (ulong)(queries[end].Is64Bit() ? sizeof(long) : sizeof(int)))
                        {
                            end++;
                        }
                        
                        var firstQuery = queries[start];
                        var batchInfo = firstQuery.GetBatchInfo();
                        var batch = new QueryBatch(
                            batchInfo.QueryPool,
                            firstQuery.GetQueryIndex(),
                            (uint)(end - start + 1),
                            batchInfo.ResultBuffer,
                            batchInfo.ResultOffset,
                            batchInfo.Is64Bit);
                        
                        batches.Add(batch);
                        start = end + 1;
                    }
                }
            }
            
            if (_isTbdrPlatform && batches.Count > 0)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"TBDR: Collected {batches.Count} query batches for {Type}, total queries: {batches.Sum(b => b.Count)}");
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
                    if (!flush.TryConsume(ref _accumulatedCounter, true))
                    {
                        return;
                    }
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
        
        public VulkanRenderer Gd => _gd;
    }
}