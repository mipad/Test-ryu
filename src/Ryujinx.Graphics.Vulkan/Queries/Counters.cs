using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Threading.Tasks;
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
                Logger.Info?.Print(LogClass.Gpu, $"Initialized {count} counter queues for TBDR platform");
            }
        }

        public void ResetCounterPool()
        {
            foreach (var queue in _counterQueues)
            {
                queue.ResetCounterPool();
            }
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            _counterQueues[(int)CounterType.SamplesPassed].ResetFutureCounters(cmd, count);
        }

        public CounterQueueEvent QueueReport(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            return _counterQueues[(int)type].QueueReport(resultHandler, divisor, _pipeline.DrawCount, hostReserved);
        }
        
        // 异步队列报告
        public async Task<CounterQueueEvent> QueueReportAsync(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            var evt = _counterQueues[(int)type].QueueReport(resultHandler, divisor, _pipeline.DrawCount, hostReserved);
            
            // 异步等待结果
            await evt.GetResultAsync();
            
            return evt;
        }

        public void QueueReset(CounterType type)
        {
            _counterQueues[(int)type].QueueReset(_pipeline.DrawCount);
        }

        public void Update()
        {
            foreach (var queue in _counterQueues)
            {
                queue.Flush(false);
            }
        }
        
        // 异步更新
        public async Task UpdateAsync()
        {
            var tasks = new Task[_counterQueues.Length];
            
            for (int i = 0; i < _counterQueues.Length; i++)
            {
                tasks[i] = _counterQueues[i].FlushAsync();
            }
            
            await Task.WhenAll(tasks);
        }

        public void Flush(CounterType type)
        {
            _counterQueues[(int)type].Flush(true);
        }
        
        // 异步刷新
        public async Task FlushAsync(CounterType type)
        {
            await _counterQueues[(int)type].FlushAsync();
        }

        public void Dispose()
        {
            foreach (var queue in _counterQueues)
            {
                queue.Dispose();
            }
        }
    }
}