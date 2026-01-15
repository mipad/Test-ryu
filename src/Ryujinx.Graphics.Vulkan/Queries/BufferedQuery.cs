using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    class BufferedQuery : IDisposable
    {
        private const int MaxQueryRetries = 10000;
        private const long DefaultValue = unchecked((long)0xFFFFFFFEFFFFFFFE);
        private const long DefaultValueInt = 0xFFFFFFFE;
        private const ulong HighMask = 0xFFFFFFFF00000000;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly PipelineFull _pipeline;
        private readonly VulkanRenderer _gd;

        private QueryPool _queryPool;
        private readonly uint _queryIndex;
        private readonly bool _isPooledQuery;

        private readonly BufferHolder _buffer;
        private readonly nint _bufferMap;
        private readonly CounterType _type;
        private readonly bool _result32Bit;
        private readonly bool _isSupported;

        private readonly long _defaultValue;
        private int? _resetSequence;
        
        // 时间线信号量支持
        private ulong? _timelineSignalValue;
        private readonly bool _useTimelineSemaphores;
        private TaskCompletionSource<long> _asyncResultTask;
        private bool _asyncResultPending;

        // 查询池管理
        private static readonly ConcurrentDictionary<CounterType, QueryPoolManager> _queryPoolManagers = new();
        private readonly bool _isTbdrPlatform;

        // 统计信息
        private static long _totalQueriesCreated;
        private static long _totalQueriesReused;
        private static long _totalAsyncWaits;
        private static long _totalTimelineWaits;

        private class QueryPoolManager
        {
            public QueryPool QueryPool { get; set; }
            public uint NextIndex { get; set; }
            public int ReferenceCount { get; set; }
            public int ActiveQueries { get; set; }
            public const uint PoolSize = 4096; // 固定为4096
            
            public void IncrementActive() => ActiveQueries++;
            public void DecrementActive() => ActiveQueries--;
        }

        public unsafe BufferedQuery(VulkanRenderer gd, Device device, PipelineFull pipeline, CounterType type, bool result32Bit, bool isTbdrPlatform)
        {
            _gd = gd;
            _api = gd.Api;
            _device = device;
            _pipeline = pipeline;
            _type = type;
            _result32Bit = result32Bit;
            _isTbdrPlatform = isTbdrPlatform;
            
            // 检查是否支持时间线信号量
            _useTimelineSemaphores = gd.SupportsTimelineSemaphores && gd.TimelineSemaphore.Handle != 0;

            _isSupported = QueryTypeSupported(gd, type);

            if (_isSupported)
            {
                // 使用查询池管理器
                var manager = _queryPoolManagers.GetOrAdd(type, _ =>
                {
                    QueryPipelineStatisticFlags flags = type == CounterType.PrimitivesGenerated ?
                        QueryPipelineStatisticFlags.GeometryShaderPrimitivesBit : 0;

                    QueryPoolCreateInfo queryPoolCreateInfo = new()
                    {
                        SType = StructureType.QueryPoolCreateInfo,
                        QueryCount = QueryPoolManager.PoolSize,
                        QueryType = GetQueryType(type),
                        PipelineStatistics = flags,
                    };

                    QueryPool pool = default;
                    gd.Api.CreateQueryPool(device, in queryPoolCreateInfo, null, out pool).ThrowOnError();
                    
                    return new QueryPoolManager
                    {
                        QueryPool = pool,
                        NextIndex = 0,
                        ReferenceCount = 0,
                        ActiveQueries = 0
                    };
                });

                lock (manager)
                {
                    manager.ReferenceCount++;
                    manager.IncrementActive();
                    Interlocked.Increment(ref _totalQueriesCreated);
                    
                    _queryPool = manager.QueryPool;
                    _queryIndex = manager.NextIndex;
                    manager.NextIndex = (manager.NextIndex + 1) % QueryPoolManager.PoolSize;
                    _isPooledQuery = true;
                    
                    if (_isTbdrPlatform && manager.ReferenceCount == 1)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"Created query pool for {type} on TBDR platform, size: {QueryPoolManager.PoolSize}");
                    }
                }
            }
            else
            {
                // 回退：创建单个查询
                QueryPipelineStatisticFlags flags = type == CounterType.PrimitivesGenerated ?
                    QueryPipelineStatisticFlags.GeometryShaderPrimitivesBit : 0;

                QueryPoolCreateInfo queryPoolCreateInfo = new()
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryCount = 1,
                    QueryType = GetQueryType(type),
                    PipelineStatistics = flags,
                };

                gd.Api.CreateQueryPool(device, in queryPoolCreateInfo, null, out _queryPool).ThrowOnError();
                _queryIndex = 0;
                _isPooledQuery = false;
                Interlocked.Increment(ref _totalQueriesCreated);
            }

            BufferHolder buffer = gd.BufferManager.Create(gd, sizeof(long), forConditionalRendering: true);

            _bufferMap = buffer.Map(0, sizeof(long));
            _defaultValue = result32Bit ? DefaultValueInt : DefaultValue;
            Marshal.WriteInt64(_bufferMap, _defaultValue);
            _buffer = buffer;
        }

        private static bool QueryTypeSupported(VulkanRenderer gd, CounterType type)
        {
            return type switch
            {
                CounterType.SamplesPassed => true,
                CounterType.PrimitivesGenerated => gd.Capabilities.SupportsPipelineStatisticsQuery,
                CounterType.TransformFeedbackPrimitivesWritten => gd.Capabilities.SupportsTransformFeedbackQueries,
                _ => false,
            };
        }

        private static QueryType GetQueryType(CounterType type)
        {
            return type switch
            {
                CounterType.SamplesPassed => QueryType.Occlusion,
                CounterType.PrimitivesGenerated => QueryType.PipelineStatistics,
                CounterType.TransformFeedbackPrimitivesWritten => QueryType.TransformFeedbackStreamExt,
                _ => QueryType.Occlusion,
            };
        }

        public Auto<DisposableBuffer> GetBuffer()
        {
            return _buffer.GetBuffer();
        }

        public void Reset()
        {
            End(false);
            Begin(null);
        }

        public void Begin(int? resetSequence)
        {
            if (_isSupported)
            {
                bool needsReset = resetSequence == null || _resetSequence == null || resetSequence.Value != _resetSequence.Value;
                bool isOcclusion = _type == CounterType.SamplesPassed;
                _pipeline.BeginQuery(this, _queryPool, _queryIndex, needsReset, isOcclusion, isOcclusion && resetSequence != null);
            }
            _resetSequence = null;
            _timelineSignalValue = null; // 重置时间线信号量值
            _asyncResultPending = false;
            _asyncResultTask = null;
        }

        public void End(bool withResult)
        {
            if (_isSupported)
            {
                _pipeline.EndQuery(_queryPool, _queryIndex);
            }

            if (withResult && _isSupported)
            {
                Marshal.WriteInt64(_bufferMap, _defaultValue);
                _pipeline.CopyQueryResults(this, _queryIndex);
                
                // 如果支持时间线信号量，分配一个信号量值
                if (_useTimelineSemaphores)
                {
                    _timelineSignalValue = _gd.GetNextTimelineValue();
                    _asyncResultPending = true;
                }
            }
            else
            {
                Marshal.WriteInt64(_bufferMap, 0);
            }
        }

        private bool WaitingForValue(long data)
        {
            return data == _defaultValue ||
                (!_result32Bit && ((ulong)data & HighMask) == ((ulong)_defaultValue & HighMask));
        }

        public bool TryGetResult(out long result)
        {
            result = Marshal.ReadInt64(_bufferMap);
            bool hasResult = result != _defaultValue;
            
            if (hasResult && _asyncResultPending)
            {
                _asyncResultPending = false;
                _asyncResultTask?.TrySetResult(result);
            }
            
            return hasResult;
        }

        public long AwaitResult(AutoResetEvent wakeSignal = null)
        {
            long data = _defaultValue;

            // 优先使用时间线信号量异步等待
            if (_useTimelineSemaphores && _timelineSignalValue.HasValue && _asyncResultPending)
            {
                Interlocked.Increment(ref _totalTimelineWaits);
                ulong targetValue = _timelineSignalValue.Value;
                
                // 异步等待时间线信号量
                if (_gd.WaitTimelineSemaphore(targetValue, 1000000000)) // 1秒超时
                {
                    data = Marshal.ReadInt64(_bufferMap);
                    _asyncResultPending = false;
                    _asyncResultTask?.TrySetResult(data);
                    return data != _defaultValue ? data : 0;
                }
                else
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"Timeline semaphore wait timed out for query {_type}. Value: {targetValue}");
                    
                    _asyncResultPending = false;
                    _asyncResultTask?.TrySetCanceled();
                    return 0;
                }
            }

            // 否则使用原有的轮询机制
            if (wakeSignal == null)
            {
                if (WaitingForValue(data))
                {
                    data = Marshal.ReadInt64(_bufferMap);
                }
            }
            else
            {
                int iterations = 0;
                
                while (WaitingForValue(data) && iterations++ < MaxQueryRetries)
                {
                    data = Marshal.ReadInt64(_bufferMap);
                    if (WaitingForValue(data))
                    {
                        if (_isTbdrPlatform && iterations < 1000)
                        {
                            Thread.Yield();
                        }
                        else
                        {
                            wakeSignal.WaitOne(_isTbdrPlatform ? 0 : 1);
                        }
                    }
                }

                if (iterations >= MaxQueryRetries)
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"Error: Query result {_type} timed out. Attempts: {iterations}");
                    
                    return 0;
                }
            }

            if (!WaitingForValue(data))
            {
                _asyncResultPending = false;
                _asyncResultTask?.TrySetResult(data);
            }
            
            return data;
        }

        // 异步获取结果，不阻塞调用线程
        public Task<long> GetResultAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _totalAsyncWaits);
            
            // 首先尝试直接获取结果
            if (TryGetResult(out long immediateResult))
            {
                return Task.FromResult(immediateResult);
            }
            
            // 如果支持时间线信号量且有待处理的结果
            if (_useTimelineSemaphores && _timelineSignalValue.HasValue && _asyncResultPending)
            {
                _asyncResultTask = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                
                // 启动后台任务等待时间线信号量
                Task.Run(async () =>
                {
                    try
                    {
                        ulong targetValue = _timelineSignalValue.Value;
                        
                        if (await Task.Run(() => _gd.WaitTimelineSemaphore(targetValue, 1000000000)))
                        {
                            long result = Marshal.ReadInt64(_bufferMap);
                            _asyncResultPending = false;
                            _asyncResultTask.TrySetResult(result != _defaultValue ? result : 0);
                        }
                        else
                        {
                            Logger.Warning?.Print(LogClass.Gpu, 
                                $"Async timeline semaphore wait timed out for query {_type}");
                            _asyncResultPending = false;
                            _asyncResultTask.TrySetResult(0);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Gpu, 
                            $"Async query wait failed: {ex.Message}");
                        _asyncResultPending = false;
                        _asyncResultTask.TrySetResult(0);
                    }
                }, cancellationToken);
                
                return _asyncResultTask.Task;
            }
            
            // 回退到轮询方式
            return Task.Run(() =>
            {
                int iterations = 0;
                while (iterations++ < MaxQueryRetries)
                {
                    if (TryGetResult(out long result))
                    {
                        return result;
                    }
                    
                    Thread.Sleep(_isTbdrPlatform ? 0 : 1);
                    
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return 0L;
                    }
                }
                
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Async query poll timed out for {_type}");
                return 0L;
            }, cancellationToken);
        }

        // 批量复制查询结果
        public void BatchCopy(CommandBufferScoped cbs, uint queryIndex, ulong batchTimelineValue = 0)
        {
            Buffer buffer = _buffer.GetBuffer(cbs.CommandBuffer, true).Get(cbs, 0, sizeof(long), true).Value;

            QueryResultFlags flags = QueryResultFlags.ResultWaitBit;

            if (!_result32Bit)
            {
                flags |= QueryResultFlags.Result64Bit;
            }

            _api.CmdCopyQueryPoolResults(
                cbs.CommandBuffer,
                _queryPool,
                queryIndex,
                1,
                buffer,
                0,
                (ulong)(_result32Bit ? sizeof(int) : sizeof(long)),
                flags);
            
            // 设置批次时间线信号量值
            if (batchTimelineValue > 0 && _useTimelineSemaphores)
            {
                SetBatchTimelineValue(batchTimelineValue);
            }
        }

        public void PoolReset(CommandBuffer cmd, int resetSequence)
        {
            if (_isSupported && !_isPooledQuery)
            {
                _api.CmdResetQueryPool(cmd, _queryPool, 0, 1);
            }
            _resetSequence = resetSequence;
        }

        public void PoolCopy(CommandBufferScoped cbs, uint queryIndex)
        {
            // 使用批量复制
            BatchCopy(cbs, queryIndex);
        }

        public unsafe void Dispose()
        {
            _buffer.Dispose();
            
            // 重置时间线信号量值
            _timelineSignalValue = null;
            _asyncResultPending = false;
            _asyncResultTask?.TrySetCanceled();
            _asyncResultTask = null;
            
            if (_isSupported && _isPooledQuery && _queryPoolManagers.TryGetValue(_type, out var manager))
            {
                lock (manager)
                {
                    manager.ReferenceCount--;
                    manager.DecrementActive();
                    
                    if (manager.ReferenceCount == 0)
                    {
                        _api.DestroyQueryPool(_device, manager.QueryPool, null);
                        _queryPoolManagers.TryRemove(_type, out _);
                        
                        // 记录统计信息
                        Logger.Info?.Print(LogClass.Gpu, 
                            $"Query pool for {_type} destroyed. Stats: Created={_totalQueriesCreated}, " +
                            $"Reused={_totalQueriesReused}, AsyncWaits={_totalAsyncWaits}, " +
                            $"TimelineWaits={_totalTimelineWaits}");
                    }
                }
            }
            else if (_isSupported && !_isPooledQuery)
            {
                _api.DestroyQueryPool(_device, _queryPool, null);
            }
        }
        
        // 设置批次时间线信号量值
        internal void SetBatchTimelineValue(ulong timelineValue)
        {
            if (_useTimelineSemaphores)
            {
                _timelineSignalValue = timelineValue;
                _asyncResultPending = true;
            }
        }
        
        // 获取统计信息
        public static void LogStatistics()
        {
            Logger.Info?.Print(LogClass.Gpu, 
                $"Query Statistics: Created={_totalQueriesCreated}, Reused={_totalQueriesReused}, " +
                $"AsyncWaits={_totalAsyncWaits}, TimelineWaits={_totalTimelineWaits}");
        }
        
        // 重置查询状态（用于重用）
        public void ResetState()
        {
            _timelineSignalValue = null;
            _asyncResultPending = false;
            _asyncResultTask = null;
            Marshal.WriteInt64(_bufferMap, _defaultValue);
        }
    }
}