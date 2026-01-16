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
        
        // 统计信息
        private DateTime _lastStatisticsLog = DateTime.UtcNow;
        private int _totalReports;
        private int _totalResets;

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
            
            Logger.Info?.Print(LogClass.Gpu, $"Counters system initialized with {count} queue types");
        }

        public void ResetCounterPool()
        {
            Interlocked.Increment(ref _totalResets);
            
            foreach (var queue in _counterQueues)
            {
                queue.ResetCounterPool();
            }
            
            // 定期记录统计信息
            var now = DateTime.UtcNow;
            if ((now - _lastStatisticsLog).TotalSeconds > 30)
            {
                LogStatistics();
                _lastStatisticsLog = now;
            }
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            _counterQueues[(int)CounterType.SamplesPassed].ResetFutureCounters(cmd, count);
            
            if (_isTbdrPlatform && count > 0)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Counters ResetFutureCounters: Count={count}");
            }
        }

        public CounterQueueEvent QueueReport(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            Interlocked.Increment(ref _totalReports);
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Counters QueueReport: Type={type}, Divisor={divisor}, HostReserved={hostReserved}");
            }
            
            return _counterQueues[(int)type].QueueReport(resultHandler, divisor, _pipeline.DrawCount, hostReserved);
        }

        public void QueueReset(CounterType type)
        {
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Counters QueueReset: Type={type}");
            }
            
            _counterQueues[(int)type].QueueReset(_pipeline.DrawCount);
        }

        public void Update()
        {
            foreach (var queue in _counterQueues)
            {
                queue.Flush(false);
            }
            
            // 定期记录查询统计
            var now = DateTime.UtcNow;
            if ((now - _lastStatisticsLog).TotalSeconds > 10)
            {
                BufferedQuery.LogStatistics();
                _lastStatisticsLog = now;
            }
        }

        public void Flush(CounterType type)
        {
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Counters Flush: Type={type}");
            }
            
            _counterQueues[(int)type].Flush(true);
        }
        
        private void LogStatistics()
        {
            Logger.Info?.Print(LogClass.Gpu, 
                $"Counters System Stats: " +
                $"TotalReports={_totalReports}, " +
                $"TotalResets={_totalResets}");
        }

        public void Dispose()
        {
            Logger.Info?.Print(LogClass.Gpu, "Counters system disposing...");
            
            LogStatistics();
            BufferedQuery.LogStatistics();
            
            foreach (var queue in _counterQueues)
            {
                queue.Dispose();
            }
            
            Logger.Info?.Print(LogClass.Gpu, "Counters system disposed");
        }
    }
}