using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using Ryujinx.Common.Logging;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    class Counters : IDisposable
    {
        private readonly CounterQueue[] _counterQueues;
        private readonly PipelineFull _pipeline;
        private readonly bool _isTbdrPlatform;

        public Counters(VulkanRenderer gd, Device device, PipelineFull pipeline)
        {
            _pipeline = pipeline;
            _isTbdrPlatform = gd.IsTBDR;

            int count = Enum.GetNames(typeof(CounterType)).Length;

            _counterQueues = new CounterQueue[count];

            for (int index = 0; index < _counterQueues.Length; index++)
            {
                CounterType type = (CounterType)index;
                _counterQueues[index] = new CounterQueue(gd, device, pipeline, type, _isTbdrPlatform);
            }
            
            if (_isTbdrPlatform)
            {
                Logger.Info?.Print(LogClass.Gpu, $"Initialized {count} counter queues for TBDR platform");
            }
        }

        public void ResetCounterPool()
        {
            Logger.Debug?.Print(LogClass.Gpu, "Resetting all counter pools");
            
            foreach (var queue in _counterQueues)
            {
                queue.ResetCounterPool();
            }
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            Logger.Debug?.Print(LogClass.Gpu, 
                $"Resetting {count} future samples passed counters");
            
            _counterQueues[(int)CounterType.SamplesPassed].ResetFutureCounters(cmd, count);
        }

        public CounterQueueEvent QueueReport(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            Logger.Debug?.Print(LogClass.Gpu, 
                $"Queueing report for {type}, divisor: {divisor}, hostReserved: {hostReserved}");
            
            return _counterQueues[(int)type].QueueReport(resultHandler, divisor, _pipeline.DrawCount, hostReserved);
        }

        public void QueueReset(CounterType type)
        {
            Logger.Debug?.Print(LogClass.Gpu, 
                $"Queueing reset for {type}");
            
            _counterQueues[(int)type].QueueReset(_pipeline.DrawCount);
        }

        public void Update()
        {
            Logger.Debug?.Print(LogClass.Gpu, 
                "Updating all counters");
            
            foreach (var queue in _counterQueues)
            {
                queue.Flush(false);
            }
        }

        public void Flush(CounterType type)
        {
            Logger.Debug?.Print(LogClass.Gpu, 
                $"Flushing counter queue for {type}");
            
            _counterQueues[(int)type].Flush(true);
        }
        
        // 收集所有批量查询
        public List<QueryBatch> CollectAllBatchQueries()
        {
            var allBatches = new List<QueryBatch>();
            
            foreach (var queue in _counterQueues)
            {
                var batches = queue.CollectBatchQueries();
                allBatches.AddRange(batches);
            }
            
            if (_isTbdrPlatform && allBatches.Count > 0)
            {
                int totalQueries = 0;
                foreach (var batch in allBatches)
                {
                    totalQueries += (int)batch.Count;
                }
                
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"TBDR: Collected {allBatches.Count} batches, {totalQueries} total queries");
            }
            
            return allBatches;
        }

        public void Dispose()
        {
            Logger.Debug?.Print(LogClass.Gpu, 
                "Disposing counters");
            
            foreach (var queue in _counterQueues)
            {
                queue.Dispose();
            }
            
            // 清理批量缓冲区
            BatchQueryManager.DisposeAll();
            
            Logger.Debug?.Print(LogClass.Gpu, 
                "Counters disposed");
        }
    }
}