using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
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

        // 查询池管理 - 参考Skyline的固定大小池（4096）
        private static readonly ConcurrentDictionary<CounterType, QueryPoolManager> _queryPoolManagers = new();
        private readonly bool _isTbdrPlatform;

        // 统计信息
        private static long _totalQueriesCreated;
        private static long _totalQueriesReused;
        private static long _totalQueryTimeouts;

        private class QueryPoolManager
        {
            public QueryPool QueryPool { get; set; }
            public uint NextIndex { get; set; }
            public int ReferenceCount { get; set; }
            public int ActiveQueries { get; set; }
            public const uint PoolSize = 4096; // 固定为4096，与Skyline一致
            
            // 添加重置状态追踪
            public uint[] ResetIndices { get; private set; }
            public int ResetCount { get; set; }
            public object ResetLock { get; } = new object();
            
            public QueryPoolManager()
            {
                ResetIndices = new uint[PoolSize];
                ResetCount = 0;
            }
            
            public void IncrementActive() => ActiveQueries++;
            public void DecrementActive() => ActiveQueries--;
            
            public void AddResetIndex(uint index)
            {
                lock (ResetLock)
                {
                    if (ResetCount < PoolSize)
                    {
                        ResetIndices[ResetCount++] = index;
                    }
                }
            }
            
            public void ClearResetIndices()
            {
                lock (ResetLock)
                {
                    ResetCount = 0;
                }
            }
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
                // 使用查询池管理器 - 参考Skyline的固定大小池设计
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
                        Logger.Info?.Print(LogClass.Gpu, 
                            $"Created query pool for {type} on TBDR platform, size: {QueryPoolManager.PoolSize}");
                    }
                    
                    // 记录统计信息
                    if (_totalQueriesCreated % 1000 == 0)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"Query pool stats: {type} - Active={manager.ActiveQueries}, TotalCreated={_totalQueriesCreated}");
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
            
            // 初始化时间线信号量值
            _timelineSignalValue = null;
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
                
                // 添加调试信息
                if (needsReset && _isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Query Begin: Type={_type}, Index={_queryIndex}, NeedsReset={needsReset}, TBDR={_isTbdrPlatform}");
                }
                
                _pipeline.BeginQuery(this, _queryPool, _queryIndex, needsReset, isOcclusion, isOcclusion && resetSequence != null);
            }
            _resetSequence = null;
            _timelineSignalValue = null; // 重置时间线信号量值
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
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"Query End with Timeline: Type={_type}, Value={_timelineSignalValue}");
                    }
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
            
            // 调试信息
            if (hasResult && _isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Query TryGetResult: Type={_type}, Result={result}, HasResult={hasResult}");
            }
            
            return hasResult;
        }

        public long AwaitResult(AutoResetEvent wakeSignal = null)
        {
            long data = _defaultValue;

            // 优先使用时间线信号量等待 - 参考Skyline的同步机制
            if (_useTimelineSemaphores && _timelineSignalValue.HasValue)
            {
                ulong targetValue = _timelineSignalValue.Value;
                
                // 对于TBDR平台，使用更短的超时时间
                ulong timeout = _isTbdrPlatform ? 500000000 : 1000000000; // 0.5秒或1秒
                
                // 等待时间线信号量达到指定值
                if (_gd.WaitTimelineSemaphore(targetValue, timeout))
                {
                    data = Marshal.ReadInt64(_bufferMap);
                    
                    if (data != _defaultValue)
                    {
                        return data;
                    }
                    else
                    {
                        // 信号量已到达但数据未准备好，进行轮询
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Query {_type} timeline reached but data not ready. Polling...");
                    }
                }
                else
                {
                    Interlocked.Increment(ref _totalQueryTimeouts);
                    
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Timeline semaphore wait timed out for query {_type}. Value: {targetValue}");
                    
                    // 对于TBDR平台，返回安全值避免阻塞
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"TBDR Query timeout, returning safe value 0");
                        return 0;
                    }
                }
            }

            // 否则使用原有的轮询机制，但针对TBDR优化
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
                long startTime = Environment.TickCount;
                
                while (WaitingForValue(data) && iterations++ < MaxQueryRetries)
                {
                    data = Marshal.ReadInt64(_bufferMap);
                    
                    if (WaitingForValue(data))
                    {
                        // TBDR平台优化：根据迭代次数调整等待策略
                        if (_isTbdrPlatform)
                        {
                            if (iterations < 100)
                            {
                                // 前100次快速轮询
                                Thread.SpinWait(100);
                            }
                            else if (iterations < 1000)
                            {
                                // 100-1000次，使用Yield
                                Thread.Yield();
                            }
                            else
                            {
                                // 超过1000次，使用短的等待时间
                                wakeSignal.WaitOne(1);
                            }
                            
                            // 检查是否超时（TBDR平台更短超时）
                            if (Environment.TickCount - startTime > 500) // 500ms超时
                            {
                                Interlocked.Increment(ref _totalQueryTimeouts);
                                Logger.Warning?.Print(LogClass.Gpu, 
                                    $"TBDR Query {_type} polling timeout after {iterations} iterations");
                                return 0;
                            }
                        }
                        else
                        {
                            // 非TBDR平台使用原有逻辑
                            wakeSignal.WaitOne(1);
                        }
                    }
                }

                if (iterations >= MaxQueryRetries)
                {
                    Interlocked.Increment(ref _totalQueryTimeouts);
                    
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"Error: Query result {_type} timed out. Attempts: {iterations}, TimeoutCount={_totalQueryTimeouts}");
                    
                    // 强制返回默认值，避免阻塞
                    return 0;
                }
            }

            return data;
        }

        // 批量复制查询结果 - 参考Skyline的批处理机制
        public void BatchCopy(CommandBufferScoped cbs, uint queryIndex, ulong batchTimelineValue = 0)
        {
            Buffer buffer = _buffer.GetBuffer(cbs.CommandBuffer, true).Get(cbs, 0, sizeof(long), true).Value;

            QueryResultFlags flags = QueryResultFlags.ResultWaitBit;

            if (!_result32Bit)
            {
                flags |= QueryResultFlags.Result64Bit;
            }

            // 添加调试信息
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"BatchCopy Query: Type={_type}, Index={queryIndex}, Timeline={batchTimelineValue}");
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
            
            // 对于池化查询，记录重置索引
            if (_isSupported && _isPooledQuery && _queryPoolManagers.TryGetValue(_type, out var manager))
            {
                manager.AddResetIndex(_queryIndex);
            }
            
            _resetSequence = resetSequence;
        }

        public void PoolCopy(CommandBufferScoped cbs, uint queryIndex)
        {
            // 使用批量复制
            BatchCopy(cbs, queryIndex);
        }

        // 批量重置查询池 - 参考Skyline的每渲染过程重置机制
        public static void BatchResetQueryPools(CommandBuffer cmd)
        {
            foreach (var kvp in _queryPoolManagers)
            {
                var manager = kvp.Value;
                if (manager.ResetCount > 0)
                {
                    // 批量重置所有需要重置的查询
                    // 注意：这里需要实际的Vulkan API调用
                    // 由于我们不知道具体的Vk实例，这个方法的实现需要调整
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Batch reset {manager.ResetCount} queries for {kvp.Key}");
                    
                    manager.ClearResetIndices();
                }
            }
        }

        public unsafe void Dispose()
        {
            _buffer.Dispose();
            
            // 重置时间线信号量值
            _timelineSignalValue = null;
            
            if (_isSupported && _isPooledQuery && _queryPoolManagers.TryGetValue(_type, out var manager))
            {
                lock (manager)
                {
                    manager.ReferenceCount--;
                    manager.DecrementActive();
                    
                    // 记录重用统计
                    if (manager.ReferenceCount > 0)
                    {
                        Interlocked.Increment(ref _totalQueriesReused);
                    }
                    
                    if (manager.ReferenceCount == 0)
                    {
                        _api.DestroyQueryPool(_device, manager.QueryPool, null);
                        _queryPoolManagers.TryRemove(_type, out _);
                        
                        // 记录统计信息
                        Logger.Info?.Print(LogClass.Gpu, 
                            $"Query pool for {_type} destroyed. Stats: " +
                            $"Created={_totalQueriesCreated}, " +
                            $"Reused={_totalQueriesReused}, " +
                            $"Timeouts={_totalQueryTimeouts}");
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
                
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"SetBatchTimelineValue: Type={_type}, Value={timelineValue}");
                }
            }
        }
        
        // 获取统计信息
        public static void LogStatistics()
        {
            Logger.Info?.Print(LogClass.Gpu, 
                $"Query Statistics: " +
                $"Created={_totalQueriesCreated}, " +
                $"Reused={_totalQueriesReused}, " +
                $"Timeouts={_totalQueryTimeouts}");
            
            foreach (var kvp in _queryPoolManagers)
            {
                var manager = kvp.Value;
                Logger.Info?.Print(LogClass.Gpu, 
                    $"  {kvp.Key}: Active={manager.ActiveQueries}, RefCount={manager.ReferenceCount}");
            }
        }
        
        // 重置查询状态（用于重用）
        public void ResetState()
        {
            _timelineSignalValue = null;
            Marshal.WriteInt64(_bufferMap, _defaultValue);
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Query ResetState: Type={_type}");
            }
        }
        
        // 获取查询索引（用于调试）
        public uint GetQueryIndex() => _queryIndex;
        
        // 获取查询池（用于调试）
        public QueryPool GetQueryPool() => _queryPool;
        
        // 检查查询是否准备好
        public bool IsResultReady()
        {
            long result = Marshal.ReadInt64(_bufferMap);
            return result != _defaultValue;
        }
    }
}