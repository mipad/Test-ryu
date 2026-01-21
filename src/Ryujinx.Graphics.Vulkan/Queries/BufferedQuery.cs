using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    // 查询批次结构体 - 用于批量处理
    public struct QueryBatch
    {
        public QueryPool QueryPool;
        public uint StartIndex;
        public uint Count;
        public Buffer ResultBuffer;
        public ulong ResultOffset;
        public bool Is64Bit;
        
        public QueryBatch(QueryPool pool, uint start, uint count, Buffer buffer, ulong offset, bool is64Bit)
        {
            QueryPool = pool;
            StartIndex = start;
            Count = count;
            ResultBuffer = buffer;
            ResultOffset = offset;
            Is64Bit = is64Bit;
        }
    }
    
    // 批量查询结果缓冲区
    class BatchResultBuffer : IDisposable
    {
        private readonly Vk _api;
        private readonly Device _device;
        private readonly BufferHolder _buffer;
        private readonly nint _mappedPtr;
        private readonly int _elementSize;
        private readonly int _capacity;
        private int _usedCount;
        private readonly object _lock = new();
        
        public BatchResultBuffer(VulkanRenderer gd, Device device, int elementSize, int capacity)
        {
            _api = gd.Api;
            _device = device;
            _elementSize = elementSize;
            _capacity = capacity;
            
            _buffer = gd.BufferManager.Create(gd, elementSize * capacity);
            _mappedPtr = _buffer.Map(0, elementSize * capacity);
            _usedCount = 0;
        }
        
        public bool TryAllocate(int count, out ulong offset, out nint mappedPtr)
        {
            lock (_lock)
            {
                if (_usedCount + count > _capacity)
                {
                    offset = 0;
                    mappedPtr = nint.Zero;
                    return false;
                }
                
                offset = (ulong)(_usedCount * _elementSize);
                mappedPtr = _mappedPtr + (_usedCount * _elementSize);
                _usedCount += count;
                
                return true;
            }
        }
        
        public void Reset()
        {
            lock (_lock)
            {
                _usedCount = 0;
            }
        }
        
        public Buffer GetBuffer()
        {
            return _buffer.GetBuffer().GetUnsafe().Value;
        }
        
        public nint GetMappedPtr()
        {
            return _mappedPtr;
        }
        
        public void Dispose()
        {
            _buffer.Dispose();
        }
    }
    
    // 批量查询管理器
    static class BatchQueryManager
    {
        private static readonly ConcurrentDictionary<CounterType, BatchResultBuffer> _resultBuffers64 = new();
        private static readonly ConcurrentDictionary<CounterType, BatchResultBuffer> _resultBuffers32 = new();
        private static readonly object _lock = new();
        
        public static bool TryGetResultBuffer(CounterType type, bool is64Bit, out BatchResultBuffer buffer)
        {
            var dict = is64Bit ? _resultBuffers64 : _resultBuffers32;
            
            if (!dict.TryGetValue(type, out buffer))
            {
                lock (_lock)
                {
                    if (!dict.TryGetValue(type, out buffer))
                    {
                        // 延迟创建，需要VulkanRenderer实例
                        return false;
                    }
                }
            }
            
            return true;
        }
        
        public static void CreateResultBuffer(VulkanRenderer gd, Device device, CounterType type, bool is64Bit, int capacity)
        {
            var dict = is64Bit ? _resultBuffers64 : _resultBuffers32;
            int elementSize = is64Bit ? sizeof(long) : sizeof(int);
            
            lock (_lock)
            {
                if (!dict.ContainsKey(type))
                {
                    var buffer = new BatchResultBuffer(gd, device, elementSize, capacity);
                    dict[type] = buffer;
                    
                    if (gd.IsTBDR)
                    {
                        if (Logger.Debug.HasValue)
                        {
                            Logger.Debug.Value.Print(LogClass.Gpu, 
                                $"Created batch result buffer for {type} ({(is64Bit ? "64-bit" : "32-bit")}), capacity: {capacity}");
                        }
                    }
                }
            }
        }
        
        public static void DisposeAll()
        {
            lock (_lock)
            {
                foreach (var buffer in _resultBuffers64.Values)
                    buffer.Dispose();
                foreach (var buffer in _resultBuffers32.Values)
                    buffer.Dispose();
                
                _resultBuffers64.Clear();
                _resultBuffers32.Clear();
            }
        }
    }

    class BufferedQuery : IDisposable
    {
        private const int MaxQueryRetries = 10000;
        private const long DefaultValue = unchecked((long)0xFFFFFFFEFFFFFFFE);
        private const long DefaultValueInt = 0xFFFFFFFE;
        private const ulong HighMask = 0xFFFFFFFF00000000;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly PipelineFull _pipeline;

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

        // 添加查询池管理
        private static readonly ConcurrentDictionary<CounterType, QueryPoolManager> _queryPoolManagers = new();
        private readonly bool _isTbdrPlatform;
        
        // 批量查询支持
        private BatchResultBuffer _batchBuffer;
        private ulong _batchBufferOffset;
        private bool _usingBatchBuffer;
        private bool _batchResultReady;

        // 简单的结果稳定化
        private long _lastStableResult = 0;
        private const long StabilityThreshold = 32; // 用于稳定结果的阈值
        
        // 智能回退策略字段
        private long _lastSuccessfulResult = 0; // 上次成功获取的结果
        private int _consecutiveTimeouts = 0; // 连续超时次数
        private int _historicalResultAge = 0; // 历史结果年龄（帧数）
        private const int MaxHistoricalAge = 3; // 历史结果最大有效年龄
        private const int MaxConsecutiveTimeouts = 5; // 最大连续超时次数，超过则强制重置
        private readonly bool _enableHistoricalFallback = true; // 是否启用历史回退
        
        // 查询策略枚举
        private enum QueryTimeoutStrategy
        {
            Strict,      // 严格模式：超时返回0
            Historical,  // 历史模式：使用上次成功结果
            Smoothed,    // 平滑模式：应用平滑滤波
            Adaptive     // 自适应：根据查询类型动态选择
        }
        
        // 当前查询类型的推荐策略
        private readonly QueryTimeoutStrategy _recommendedStrategy;

        private class QueryPoolManager
        {
            public QueryPool QueryPool { get; set; }
            public uint NextIndex { get; set; }
            public int ReferenceCount { get; set; }
            public const uint PoolSize = 8192;
        }

        public unsafe BufferedQuery(VulkanRenderer gd, Device device, PipelineFull pipeline, CounterType type, bool result32Bit, bool isTbdrPlatform)
        {
            _api = gd.Api;
            _device = device;
            _pipeline = pipeline;
            _type = type;
            _result32Bit = result32Bit;
            _isTbdrPlatform = isTbdrPlatform;
            
            // 根据查询类型设置推荐策略
            _recommendedStrategy = GetRecommendedStrategy(type);
            
            // 初始化批量结果缓冲区
            if (isTbdrPlatform)
            {
                BatchQueryManager.CreateResultBuffer(gd, device, type, !result32Bit, 2048);
            }

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
                        ReferenceCount = 0
                    };
                });

                lock (manager)
                {
                    manager.ReferenceCount++;
                    _queryPool = manager.QueryPool;
                    _queryIndex = manager.NextIndex;
                    manager.NextIndex = (manager.NextIndex + 1) % QueryPoolManager.PoolSize;
                    _isPooledQuery = true;
                    
                    if (_isTbdrPlatform && manager.ReferenceCount == 1)
                    {
                        if (Logger.Info.HasValue)
                        {
                            Logger.Info.Value.Print(LogClass.Gpu, 
                                $"Created query pool for {type} on TBDR platform, size: {QueryPoolManager.PoolSize}");
                        }
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
            }

            BufferHolder buffer = gd.BufferManager.Create(gd, sizeof(long), forConditionalRendering: true);

            _bufferMap = buffer.Map(0, sizeof(long));
            _defaultValue = result32Bit ? DefaultValueInt : DefaultValue;
            Marshal.WriteInt64(_bufferMap, _defaultValue);
            _buffer = buffer;
            _usingBatchBuffer = false;
            _batchResultReady = false;
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
        
        // 根据查询类型获取推荐策略
        private static QueryTimeoutStrategy GetRecommendedStrategy(CounterType type)
        {
            return type switch
            {
                CounterType.SamplesPassed => QueryTimeoutStrategy.Historical,  // 遮挡查询：使用历史值避免闪烁
                CounterType.PrimitivesGenerated => QueryTimeoutStrategy.Strict,  // 统计信息：需要精确值
                CounterType.TransformFeedbackPrimitivesWritten => QueryTimeoutStrategy.Historical,  // 变换反馈：保持连续性
                _ => QueryTimeoutStrategy.Adaptive  // 其他：自适应
            };
        }

        public Auto<DisposableBuffer> GetBuffer()
        {
            return _buffer.GetBuffer();
        }
        
        public QueryPool GetQueryPool() => _queryPool;
        public uint GetQueryIndex() => _queryIndex;
        public bool Is64Bit() => !_result32Bit;
        public CounterType GetCounterType() => _type;

        public void Reset()
        {
            End(false);
            Begin(null);
            
            // 重置历史记录
            _consecutiveTimeouts = 0;
            _historicalResultAge = 0;
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
            
            // 查询开始前，如果历史结果太旧则重置
            if (_historicalResultAge > MaxHistoricalAge)
            {
                _lastSuccessfulResult = 0;
                _historicalResultAge = 0;
            }
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
            if (result == _defaultValue)
                return false;
            
            // 成功获取结果，更新历史记录
            UpdateHistoricalRecord(result);
            
            // 简单的结果稳定化处理
            result = StabilizeResult(result);
            return true;
        }
        
        // 性能统计专用方法（尝试获取，不等待，不使用历史回退）
        public bool TryGetResultForPerformanceStats(out long result)
        {
            result = Marshal.ReadInt64(_bufferMap);
            
            if (result == _defaultValue)
            {
                // 对于性能统计，我们也不尝试使用批量缓冲区
                return false;
            }
            
            // 成功获取结果，但不更新历史记录
            result = StabilizeResult(result);
            return true;
        }
        
        // 带历史回退的结果获取方法
        public bool TryGetResultWithFallback(out long result, bool allowHistoricalFallback = true)
        {
            // 1. 优先尝试获取实时结果
            if (TryGetResult(out result))
            {
                return true;
            }
            
            // 2. 检查是否有历史结果可用（根据应用场景决定）
            if (allowHistoricalFallback && 
                _lastSuccessfulResult != 0 && 
                _historicalResultAge < MaxHistoricalAge &&
                _consecutiveTimeouts < MaxConsecutiveTimeouts)
            {
                result = _lastSuccessfulResult;
                _historicalResultAge++;
                return true;
            }
            
            // 3. 回退失败
            result = 0;
            return false;
        }
        
        // 简单的结果稳定化方法
        private long StabilizeResult(long rawResult)
        {
            // 对于32位结果，需要特殊处理
            if (_result32Bit && rawResult > int.MaxValue)
            {
                rawResult = (int)rawResult;
            }
            
            // 使用滞后阈值来稳定结果，避免在阈值附近来回跳动
            long newStableResult;
            
            if (rawResult == 0)
            {
                // 如果结果为0，直接使用0
                newStableResult = 0;
            }
            else if (Math.Abs(rawResult - _lastStableResult) < StabilityThreshold)
            {
                // 如果变化小于阈值，保持上次的稳定结果（滞后作用）
                newStableResult = _lastStableResult;
            }
            else
            {
                // 如果变化足够大，更新稳定结果
                newStableResult = rawResult;
            }
            
            _lastStableResult = newStableResult;
            return newStableResult;
        }
        
        // 更新历史记录
        private void UpdateHistoricalRecord(long newResult)
        {
            _lastSuccessfulResult = newResult;
            _historicalResultAge = 0;
            _consecutiveTimeouts = 0;
        }
        
        // 获取原始结果（不应用稳定化）
        public bool TryGetRawResult(out long result)
        {
            result = Marshal.ReadInt64(_bufferMap);
            return result != _defaultValue;
        }

        // 增强版的AwaitResult方法，带智能回退策略
        public long AwaitResult(AutoResetEvent wakeSignal = null)
        {
            long data = _defaultValue;
            int attempts = 0;
            const int MaxAttempts = 100;
            const int BaseSpinCount = 50;
            
            while (WaitingForValue(data) && attempts++ < MaxAttempts)
            {
                data = Marshal.ReadInt64(_bufferMap);
                
                if (WaitingForValue(data))
                {
                    // 分层等待策略
                    if (attempts < BaseSpinCount)
                    {
                        Thread.SpinWait(100);  // 快速自旋
                    }
                    else if (attempts < BaseSpinCount * 2)
                    {
                        Thread.Sleep(0);  // 出让时间片
                    }
                    else
                    {
                        // 考虑使用历史结果
                        if (_enableHistoricalFallback && 
                            _lastSuccessfulResult != 0 && 
                            attempts > MaxAttempts * 3 / 4)
                        {
                            bool shouldUseHistorical = ShouldUseHistoricalValue();
                            
                            if (shouldUseHistorical)
                            {
                                _consecutiveTimeouts++;
                                _historicalResultAge++;
                                
                                // 如果连续超时次数过多，强制重置历史记录
                                if (_consecutiveTimeouts >= MaxConsecutiveTimeouts)
                                {
                                    _lastSuccessfulResult = 0;
                                    _consecutiveTimeouts = 0;
                                    return 0;
                                }
                                
                                return _lastSuccessfulResult;
                            }
                        }
                        
                        // 正常等待
                        wakeSignal?.WaitOne(1);
                    }
                }
            }
            
            if (data != _defaultValue)
            {
                // 成功获取结果，更新历史
                UpdateHistoricalRecord(data);
                data = StabilizeResult(data);
            }
            else if (attempts >= MaxAttempts)
            {
                // 超时处理
                _consecutiveTimeouts++;
                
                if (_enableHistoricalFallback && 
                    _lastSuccessfulResult != 0 && 
                    _consecutiveTimeouts < MaxConsecutiveTimeouts)
                {
                    bool shouldUseHistorical = ShouldUseHistoricalValue();
                    
                    if (shouldUseHistorical)
                    {
                        _historicalResultAge++;
                        data = _lastSuccessfulResult;
                    }
                    else
                    {
                        data = 0;
                    }
                }
                else
                {
                    // 无可用历史值或历史值已过期
                    data = 0;
                    
                    // 如果连续超时次数过多，强制重置历史记录
                    if (_consecutiveTimeouts >= MaxConsecutiveTimeouts)
                    {
                        _lastSuccessfulResult = 0;
                        _consecutiveTimeouts = 0;
                    }
                }
            }
            
            return data;
        }
        
        // 判断是否应该使用历史值
        private bool ShouldUseHistoricalValue()
        {
            // 根据查询类型决定策略
            switch (_recommendedStrategy)
            {
                case QueryTimeoutStrategy.Strict:
                    return false; // 严格模式：不使用历史值
                    
                case QueryTimeoutStrategy.Historical:
                    return true; // 历史模式：总是使用历史值
                    
                case QueryTimeoutStrategy.Smoothed:
                    // 平滑模式：根据历史值年龄决定
                    return _historicalResultAge < MaxHistoricalAge / 2;
                    
                case QueryTimeoutStrategy.Adaptive:
                    // 自适应模式：根据查询类型和超时次数决定
                    return _type switch
                    {
                        CounterType.SamplesPassed => _consecutiveTimeouts < 3, // 遮挡查询：容忍3次超时
                        CounterType.TransformFeedbackPrimitivesWritten => _consecutiveTimeouts < 2, // 变换反馈：容忍2次超时
                        _ => false // 其他类型：不使用历史值
                    };
                    
                default:
                    return false;
            }
        }
        
        // 条件渲染专用方法（优先使用历史值避免闪烁）
        public long GetResultForConditionalRendering()
        {
            long result;
            
            // 先尝试快速获取结果
            if (TryGetResult(out result))
            {
                return result;
            }
            
            // 快速获取失败，使用历史值避免闪烁
            if (_lastSuccessfulResult != 0 && _historicalResultAge < MaxHistoricalAge)
            {
                _historicalResultAge++;
                return _lastSuccessfulResult;
            }
            
            // 没有历史值，使用完整等待
            return AwaitResult();
        }
        
        // 性能统计专用方法（需要精确值）
        public long GetResultForPerformanceStats()
        {
            long result;
            
            // 尝试获取结果
            if (TryGetResult(out result))
            {
                return result;
            }
            
            // 对于性能统计，我们不使用历史值
            return AwaitResult();
        }
        
        // 用于批量处理的方法
        public bool TryAllocateBatchSlot(out ulong offset, out nint mappedPtr)
        {
            if (_isTbdrPlatform && BatchQueryManager.TryGetResultBuffer(_type, !_result32Bit, out _batchBuffer))
            {
                if (_batchBuffer.TryAllocate(1, out offset, out mappedPtr))
                {
                    _batchBufferOffset = offset;
                    _usingBatchBuffer = true;
                    _batchResultReady = false;
                    return true;
                }
            }
            
            offset = 0;
            mappedPtr = nint.Zero;
            _usingBatchBuffer = false;
            _batchResultReady = false;
            return false;
        }
        
        public QueryBatch GetBatchInfo()
        {
            if (_usingBatchBuffer && _batchBuffer != null)
            {
                return new QueryBatch(
                    _queryPool,
                    _queryIndex,
                    1,
                    _batchBuffer.GetBuffer(),
                    _batchBufferOffset,
                    !_result32Bit);
            }
            
            return default;
        }
        
        public bool TryCopyFromBatchResult()
        {
            if (_usingBatchBuffer && _batchBuffer != null && _batchResultReady)
            {
                try
                {
                    // 从批量缓冲区复制结果到本地缓冲区
                    nint srcPtr = _batchBuffer.GetMappedPtr() + (int)_batchBufferOffset;
                    
                    long result;
                    if (_result32Bit)
                    {
                        result = Marshal.ReadInt32(srcPtr);
                    }
                    else
                    {
                        result = Marshal.ReadInt64(srcPtr);
                    }
                    
                    // 应用稳定化处理
                    result = StabilizeResult(result);
                    
                    Marshal.WriteInt64(_bufferMap, result);
                    
                    // 更新历史记录
                    UpdateHistoricalRecord(result);
                    
                    _usingBatchBuffer = false;
                    _batchResultReady = false;
                    return true;
                }
                catch (Exception ex)
                {
                    return false;
                }
            }
            
            return false;
        }
        
        public void MarkBatchResultReady()
        {
            _batchResultReady = true;
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
        }
        
        // 批量复制方法
        public static void CopyBatch(Vk api, CommandBufferScoped cbs, QueryBatch batch)
        {
            QueryResultFlags flags = QueryResultFlags.ResultWaitBit;
            if (batch.Is64Bit)
            {
                flags |= QueryResultFlags.Result64Bit;
            }

            api.CmdCopyQueryPoolResults(
                cbs.CommandBuffer,
                batch.QueryPool,
                batch.StartIndex,
                batch.Count,
                batch.ResultBuffer,
                batch.ResultOffset,
                (ulong)(batch.Is64Bit ? sizeof(long) : sizeof(int)) * batch.Count,
                flags);
        }

        public unsafe void Dispose()
        {
            _buffer.Dispose();
            
            if (_isSupported && _isPooledQuery && _queryPoolManagers.TryGetValue(_type, out var manager))
            {
                lock (manager)
                {
                    manager.ReferenceCount--;
                    if (manager.ReferenceCount == 0)
                    {
                        _api.DestroyQueryPool(_device, manager.QueryPool, null);
                        _queryPoolManagers.TryRemove(_type, out _);
                    }
                }
            }
            else if (_isSupported && !_isPooledQuery)
            {
                _api.DestroyQueryPool(_device, _queryPool, null);
            }
        }
    }
}