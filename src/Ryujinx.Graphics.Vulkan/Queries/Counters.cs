using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using Ryujinx.Common.Logging;

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
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Initialized {count} counter queues for TBDR platform with batch processing");
            }
        }

        public void ResetCounterPool()
        {
            foreach (var queue in _counterQueues)
            {
                if (queue != null)
                {
                    queue.ResetCounterPool();
                }
            }
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            var occlusionQueue = _counterQueues[(int)CounterType.SamplesPassed];
            if (occlusionQueue != null)
            {
                occlusionQueue.ResetFutureCounters(cmd, count);
            }
        }

        public CounterQueueEvent QueueReport(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            var queue = _counterQueues[(int)type];
            if (queue != null)
            {
                return queue.QueueReport(resultHandler, divisor, _pipeline.DrawCount, hostReserved);
            }
            return null;
        }

        public void QueueReset(CounterType type)
        {
            var queue = _counterQueues[(int)type];
            if (queue != null)
            {
                queue.QueueReset(_pipeline.DrawCount);
            }
        }

        public void Update()
        {
            foreach (var queue in _counterQueues)
            {
                if (queue != null)
                {
                    queue.Flush(false);
                }
            }
        }

        public void Flush(CounterType type)
        {
            var queue = _counterQueues[(int)type];
            if (queue != null)
            {
                queue.Flush(true);
            }
        }

        public void Dispose()
        {
            foreach (var queue in _counterQueues)
            {
                queue?.Dispose();
            }
            
            // 清理批量管理器
            BufferedQuery.CleanupBatchManagers();
        }
    }
}