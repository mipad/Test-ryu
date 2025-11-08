using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    class Counters : IDisposable
    {
        private readonly CounterQueue[] _counterQueues;
        private readonly PipelineFull _pipeline;

        public Counters(VulkanRenderer gd, Device device, PipelineFull pipeline)
        {
            _pipeline = pipeline;

            int count = Enum.GetNames(typeof(CounterType)).Length;

            _counterQueues = new CounterQueue[count];

            for (int index = 0; index < _counterQueues.Length; index++)
            {
                CounterType type = (CounterType)index;
                _counterQueues[index] = new CounterQueue(gd, device, pipeline, type);
            }
        }

        // 新增：条件渲染加速方法
        public bool AccelerateHostConditionalRendering(BufferHandle buffer, int offset, bool isEqual)
        {
            // 这里可以实现主机条件渲染加速逻辑
            // 暂时返回 false，需要实际实现
            return false;
        }

        // 新增：启用变换反馈方法
        public void EnableTransformFeedback(bool enabled)
        {
            // 这里可以实现变换反馈启用/禁用逻辑
            // 暂时留空，需要实际实现
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

        public void Flush(CounterType type)
        {
            _counterQueues[(int)type].Flush(true);
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