using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    /// <summary>
    /// 优化的批量查询管理器
    /// </summary>
    class BufferedQueryBatchManager
    {
        private readonly Vk _api;
        private readonly Device _device;
        private readonly bool _isTbdrPlatform;
        
        private readonly ConcurrentDictionary<CounterType, QueryPoolGroup> _queryPools;
        private readonly ConcurrentBag<QueryResultBuffer> _resultBuffers;
        
        private const uint BatchBufferSize = 256 * 1024; // 256KB缓冲区
        private const uint MaxQueriesPerCopy = 64; // 每次复制最大查询数
        
        public BufferedQueryBatchManager(VulkanRenderer gd, Device device, bool isTbdrPlatform)
        {
            _api = gd.Api;
            _device = device;
            _isTbdrPlatform = isTbdrPlatform;
            
            _queryPools = new ConcurrentDictionary<CounterType, QueryPoolGroup>();
            _resultBuffers = new ConcurrentBag<QueryResultBuffer>();
            
            // 预分配一些结果缓冲区
            for (int i = 0; i < 4; i++)
            {
                _resultBuffers.Add(new QueryResultBuffer(gd, device, BatchBufferSize));
            }
            
            Logger.Info?.Print(LogClass.Gpu, $"Query batch manager initialized for TBDR: {isTbdrPlatform}");
        }
        
        public QueryAllocation AllocateQuery(CounterType type, VulkanRenderer gd, Device device)
        {
            var group = _queryPools.GetOrAdd(type, _ => new QueryPoolGroup(gd, device, type, _isTbdrPlatform));
            return group.Allocate();
        }
        
        public QueryResultBuffer RentResultBuffer()
        {
            if (_resultBuffers.TryTake(out var buffer))
            {
                return buffer;
            }
            
            return null;
        }
        
        public void ReturnResultBuffer(QueryResultBuffer buffer)
        {
            if (buffer != null)
            {
                _resultBuffers.Add(buffer);
            }
        }
        
        public unsafe void CopyBatchResults(
            CommandBuffer cmd,
            List<QueryCopyOperation> operations)
        {
            if (operations.Count == 0) return;
            
            // 按查询池和结果缓冲区分组
            var groups = operations
                .GroupBy(op => new { op.QueryPool, op.ResultBuffer, op.Is64Bit })
                .ToList();
            
            foreach (var group in groups)
            {
                var operationsInGroup = group.ToList();
                var queryPool = group.Key.QueryPool;
                var resultBuffer = group.Key.ResultBuffer;
                var is64Bit = group.Key.Is64Bit;
                
                // 处理分组内的操作，合并连续查询
                var ranges = MergeContinuousQueries(operationsInGroup);
                
                foreach (var range in ranges)
                {
                    uint copyCount = range.QueryCount;
                    if (copyCount == 0) continue;
                    
                    QueryResultFlags flags = QueryResultFlags.ResultWaitBit;
                    if (is64Bit)
                    {
                        flags |= QueryResultFlags.Result64Bit;
                    }
                    
                    // 批量复制查询结果
                    _api.CmdCopyQueryPoolResults(
                        cmd,
                        queryPool,
                        range.FirstIndex,
                        copyCount,
                        resultBuffer.Buffer,
                        resultBuffer.Offset + range.ResultOffset,
                        (ulong)(is64Bit ? sizeof(long) : sizeof(int)) * copyCount,
                        flags);
                    
                    if (_isTbdrPlatform)
                    {
                        // TBDR平台：添加内存屏障确保结果可用
                        MemoryBarrier memoryBarrier = new()
                        {
                            SType = StructureType.MemoryBarrier,
                            SrcAccessMask = AccessFlags.TransferWriteBit,
                            DstAccessMask = AccessFlags.HostReadBit,
                        };
                        
                        _api.CmdPipelineBarrier(
                            cmd,
                            PipelineStageFlags.TransferBit,
                            PipelineStageFlags.HostBit,
                            0,
                            1,
                            &memoryBarrier,
                            0,
                            null,
                            0,
                            null);
                    }
                }
            }
        }
        
        private List<QueryCopyRange> MergeContinuousQueries(List<QueryCopyOperation> operations)
        {
            if (operations.Count == 0) return new List<QueryCopyRange>();
            
            // 按查询索引排序
            var sortedOps = operations.OrderBy(op => op.QueryIndex).ToList();
            var ranges = new List<QueryCopyRange>();
            
            QueryCopyRange currentRange = null;
            uint expectedIndex = 0;
            
            foreach (var op in sortedOps)
            {
                if (currentRange == null)
                {
                    // 开始新范围
                    currentRange = new QueryCopyRange
                    {
                        FirstIndex = op.QueryIndex,
                        ResultOffset = op.ResultOffset,
                        QueryCount = 1
                    };
                    expectedIndex = op.QueryIndex + 1;
                }
                else if (op.QueryIndex == expectedIndex && 
                        op.ResultOffset == currentRange.ResultOffset + 
                        (currentRange.QueryCount * (op.Is64Bit ? sizeof(long) : sizeof(int))))
                {
                    // 连续查询，扩展当前范围
                    currentRange.QueryCount++;
                    expectedIndex++;
                }
                else
                {
                    // 不连续，保存当前范围并开始新范围
                    ranges.Add(currentRange);
                    currentRange = new QueryCopyRange
                    {
                        FirstIndex = op.QueryIndex,
                        ResultOffset = op.ResultOffset,
                        QueryCount = 1
                    };
                    expectedIndex = op.QueryIndex + 1;
                }
            }
            
            if (currentRange != null)
            {
                ranges.Add(currentRange);
            }
            
            return ranges;
        }
        
        public void Dispose()
        {
            foreach (var kvp in _queryPools)
            {
                kvp.Value.Dispose();
            }
            
            foreach (var buffer in _resultBuffers)
            {
                buffer.Dispose();
            }
            
            _queryPools.Clear();
            _resultBuffers.Clear();
        }
        
        private class QueryCopyRange
        {
            public uint FirstIndex;
            public ulong ResultOffset;
            public uint QueryCount;
        }
    }
    
    /// <summary>
    /// 查询池组，管理同一类型的多个查询池
    /// </summary>
    class QueryPoolGroup : IDisposable
    {
        private const uint PoolSize = 2048; // 更大的查询池
        private const uint MaxPools = 8; // 最多8个池
        
        private readonly Vk _api;
        private readonly Device _device;
        private readonly CounterType _type;
        private readonly bool _isTbdrPlatform;
        
        private readonly List<QueryPool> _pools;
        private readonly uint[] _nextIndices;
        private readonly int[] _refCounts;
        private int _activePoolIndex;
        
        public QueryPoolGroup(VulkanRenderer gd, Device device, CounterType type, bool isTbdrPlatform)
        {
            _api = gd.Api;
            _device = device;
            _type = type;
            _isTbdrPlatform = isTbdrPlatform;
            
            _pools = new List<QueryPool>();
            _nextIndices = new uint[MaxPools];
            _refCounts = new int[MaxPools];
            
            // 预分配第一个查询池
            AllocateNewPool();
            
            Logger.Debug?.Print(LogClass.Gpu, $"Created query pool group for {type}, TBDR: {isTbdrPlatform}");
        }
        
        private unsafe void AllocateNewPool()
        {
            if (_pools.Count >= MaxPools)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Maximum query pools reached for {_type}. Recycling pool 0.");
                
                // 回收第一个池
                var oldPool = _pools[0];
                _api.DestroyQueryPool(_device, oldPool, null);
                _pools.RemoveAt(0);
                
                // 调整数组
                for (int i = 0; i < _pools.Count; i++)
                {
                    _nextIndices[i] = _nextIndices[i + 1];
                    _refCounts[i] = _refCounts[i + 1];
                }
            }
            
            QueryPipelineStatisticFlags flags = _type == CounterType.PrimitivesGenerated ?
                QueryPipelineStatisticFlags.GeometryShaderPrimitivesBit : 0;
            
            QueryPoolCreateInfo queryPoolCreateInfo = new()
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryCount = PoolSize,
                QueryType = GetQueryType(_type),
                PipelineStatistics = flags,
            };
            
            QueryPool pool = default;
            _api.CreateQueryPool(_device, in queryPoolCreateInfo, null, out pool).ThrowOnError();
            
            _pools.Add(pool);
            _nextIndices[_pools.Count - 1] = 0;
            _refCounts[_pools.Count - 1] = 0;
            
            if (_isTbdrPlatform)
            {
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Allocated new query pool for {_type}, size: {PoolSize}");
            }
        }
        
        public QueryAllocation Allocate()
        {
            lock (_pools)
            {
                // 查找有可用空间的池
                for (int i = 0; i < _pools.Count; i++)
                {
                    int poolIndex = (_activePoolIndex + i) % _pools.Count;
                    
                    if (_nextIndices[poolIndex] < PoolSize)
                    {
                        uint queryIndex = _nextIndices[poolIndex]++;
                        _refCounts[poolIndex]++;
                        _activePoolIndex = poolIndex;
                        
                        return new QueryAllocation
                        {
                            QueryPool = _pools[poolIndex],
                            QueryIndex = queryIndex,
                            IsPooled = true
                        };
                    }
                }
                
                // 所有池都满了，分配新池
                AllocateNewPool();
                _activePoolIndex = _pools.Count - 1;
                uint newIndex = _nextIndices[_activePoolIndex]++;
                _refCounts[_activePoolIndex] = 1;
                
                return new QueryAllocation
                {
                    QueryPool = _pools[_activePoolIndex],
                    QueryIndex = newIndex,
                    IsPooled = true
                };
            }
        }
        
        public void Release(uint queryIndex, QueryPool pool)
        {
            lock (_pools)
            {
                int poolIndex = _pools.IndexOf(pool);
                if (poolIndex >= 0)
                {
                    _refCounts[poolIndex]--;
                    
                    // 如果引用计数为0且不是当前活跃池，可以回收
                    if (_refCounts[poolIndex] == 0 && poolIndex != _activePoolIndex)
                    {
                        // 重置这个池的索引，以便重用
                        _nextIndices[poolIndex] = 0;
                    }
                }
            }
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
        
        public unsafe void Dispose()
        {
            lock (_pools)
            {
                foreach (var pool in _pools)
                {
                    _api.DestroyQueryPool(_device, pool, null);
                }
                _pools.Clear();
            }
        }
    }
    
    /// <summary>
    /// 查询结果缓冲区
    /// </summary>
    class QueryResultBuffer : IDisposable
    {
        private readonly BufferHolder _buffer;
        private readonly nint _mappedMemory;
        private readonly uint _size;
        private uint _nextOffset;
        private readonly object _lock = new object();
        
        public Buffer Buffer => _buffer.GetBuffer().GetUnsafe().Value;
        public ulong Offset => 0;
        
        public QueryResultBuffer(VulkanRenderer gd, Device device, uint size)
        {
            _size = size;
            _buffer = gd.BufferManager.Create(gd, (int)size, forConditionalRendering: true);
            _mappedMemory = _buffer.Map(0, (int)size);
            _nextOffset = 0;
        }
        
        public bool TryAllocate(uint requiredSize, out ulong offset, out nint mappedPtr)
        {
            lock (_lock)
            {
                if (_nextOffset + requiredSize <= _size)
                {
                    offset = _nextOffset;
                    mappedPtr = _mappedMemory + (int)offset;
                    _nextOffset += requiredSize;
                    return true;
                }
            }
            
            offset = 0;
            mappedPtr = nint.Zero;
            return false;
        }
        
        public void Reset()
        {
            lock (_lock)
            {
                _nextOffset = 0;
            }
        }
        
        public void Dispose()
        {
            _buffer.Dispose();
        }
    }
    
    /// <summary>
    /// 查询分配信息
    /// </summary>
    struct QueryAllocation
    {
        public QueryPool QueryPool;
        public uint QueryIndex;
        public bool IsPooled;
    }
    
    /// <summary>
    /// 查询复制操作
    /// </summary>
    struct QueryCopyOperation
    {
        public QueryPool QueryPool;
        public uint QueryIndex;
        public Buffer ResultBuffer;
        public ulong ResultOffset;
        public bool Is64Bit;
    }
    
    /// <summary>
    /// 优化的批量查询类
    /// </summary>
    class BufferedQuery : IDisposable
    {
        private const int MaxQueryRetries = 5000; // 减少重试次数，依赖更积极的轮询
        private const int SpinWaitIterations = 100; // 自旋等待迭代次数
        private const long DefaultValue = unchecked((long)0xFFFFFFFEFFFFFFFE);
        private const long DefaultValueInt = 0xFFFFFFFE;
        private const ulong HighMask = 0xFFFFFFFF00000000;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly PipelineFull _pipeline;
        
        private readonly QueryPool _queryPool;
        private readonly uint _queryIndex;
        private readonly bool _isPooled;
        
        private readonly CounterType _type;
        private readonly bool _result32Bit;
        private readonly bool _isSupported;
        private readonly bool _isTbdrPlatform;
        
        private readonly long _defaultValue;
        private int? _resetSequence;
        
        // 批量处理支持
        private QueryResultBuffer _resultBuffer;
        private ulong _resultOffset;
        private nint _resultMappedPtr;
        private readonly bool _useBatchProcessing;
        
        // 静态批量管理器
        private static ConcurrentDictionary<(VulkanRenderer, Device), BufferedQueryBatchManager> _batchManagers = 
            new ConcurrentDictionary<(VulkanRenderer, Device), BufferedQueryBatchManager>();
        
        public BufferedQuery(
            VulkanRenderer gd,
            Device device,
            PipelineFull pipeline,
            CounterType type,
            bool result32Bit,
            bool isTbdrPlatform)
        {
            _api = gd.Api;
            _device = device;
            _pipeline = pipeline;
            _type = type;
            _result32Bit = result32Bit;
            _isTbdrPlatform = isTbdrPlatform;
            
            _isSupported = QueryTypeSupported(gd, type);
            
            // 获取或创建批量管理器
            var batchManager = _batchManagers.GetOrAdd(
                (gd, device),
                key => new BufferedQueryBatchManager(key.Item1, key.Item2, isTbdrPlatform));
            
            if (_isSupported)
            {
                // 从批量管理器分配查询
                var allocation = batchManager.AllocateQuery(type, gd, device);
                _queryPool = allocation.QueryPool;
                _queryIndex = allocation.QueryIndex;
                _isPooled = allocation.IsPooled;
                
                // 尝试获取结果缓冲区
                _useBatchProcessing = true;
                _resultBuffer = batchManager.RentResultBuffer();
                
                if (_resultBuffer != null && 
                    _resultBuffer.TryAllocate((uint)(_result32Bit ? sizeof(int) : sizeof(long)), 
                                             out _resultOffset, out _resultMappedPtr))
                {
                    // 初始化结果为默认值
                    if (_result32Bit)
                    {
                        Marshal.WriteInt32(_resultMappedPtr, (int)DefaultValueInt);
                    }
                    else
                    {
                        Marshal.WriteInt64(_resultMappedPtr, DefaultValue);
                    }
                }
                else
                {
                    // 回退到单独缓冲区
                    _useBatchProcessing = false;
                    CreateFallbackBuffer(gd);
                }
            }
            else
            {
                // 不支持批量处理
                _useBatchProcessing = false;
                CreateFallbackBuffer(gd);
                
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
                _isPooled = false;
            }
            
            _defaultValue = result32Bit ? DefaultValueInt : DefaultValue;
            
            if (_isTbdrPlatform && _useBatchProcessing)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Using batch processing for {type} query on TBDR platform");
            }
        }
        
        private void CreateFallbackBuffer(VulkanRenderer gd)
        {
            BufferHolder buffer = gd.BufferManager.Create(gd, sizeof(long), forConditionalRendering: true);
            _resultMappedPtr = buffer.Map(0, sizeof(long));
            _defaultValue = _result32Bit ? DefaultValueInt : DefaultValue;
            Marshal.WriteInt64(_resultMappedPtr, _defaultValue);
            
            // 存储缓冲区引用
            _resultBuffer = new QueryResultBuffer(gd, _device, (uint)sizeof(long))
            {
                // 这里简化处理，实际应该使用BufferHolder包装
            };
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
            // 对于批量处理，返回完整的缓冲区
            if (_useBatchProcessing && _resultBuffer != null)
            {
                return new Auto<DisposableBuffer>(new DisposableBuffer(_api, _device, _resultBuffer.Buffer, false));
            }
            
            // 回退到原始实现
            return null; // 注意：这里可能需要调整
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
                bool needsReset = resetSequence == null || _resetSequence == null || 
                                 resetSequence.Value != _resetSequence.Value;
                bool isOcclusion = _type == CounterType.SamplesPassed;
                
                _pipeline.BeginQuery(this, _queryPool, _queryIndex, needsReset, isOcclusion, 
                                   isOcclusion && resetSequence != null);
            }
            _resetSequence = null;
        }
        
        public void End(bool withResult)
        {
            if (_isSupported)
            {
                _pipeline.EndQuery(_queryPool, _queryIndex);
            }
            
            if (withResult && _isSupported)
            {
                // 将结果写入缓冲区
                if (_result32Bit)
                {
                    Marshal.WriteInt32(_resultMappedPtr, (int)_defaultValue);
                }
                else
                {
                    Marshal.WriteInt64(_resultMappedPtr, _defaultValue);
                }
                
                _pipeline.CopyQueryResults(this, _queryIndex);
            }
            else if (!_useBatchProcessing)
            {
                // 仅对非批量处理写入0
                Marshal.WriteInt64(_resultMappedPtr, 0);
            }
        }
        
        private bool WaitingForValue(long data)
        {
            return data == _defaultValue ||
                (!_result32Bit && ((ulong)data & HighMask) == ((ulong)_defaultValue & HighMask));
        }
        
        public bool TryGetResult(out long result)
        {
            result = _result32Bit ? 
                Marshal.ReadInt32(_resultMappedPtr) : 
                Marshal.ReadInt64(_resultMappedPtr);
            
            return result != _defaultValue;
        }
        
        public long AwaitResult(AutoResetEvent wakeSignal = null)
        {
            long data = _defaultValue;
            
            if (wakeSignal == null)
            {
                // 无信号：直接读取
                data = _result32Bit ? 
                    Marshal.ReadInt32(_resultMappedPtr) : 
                    Marshal.ReadInt64(_resultMappedPtr);
            }
            else
            {
                int iterations = 0;
                int spinCount = 0;
                
                // 优化的轮询策略
                while (WaitingForValue(data) && iterations++ < MaxQueryRetries)
                {
                    data = _result32Bit ? 
                        Marshal.ReadInt32(_resultMappedPtr) : 
                        Marshal.ReadInt64(_resultMappedPtr);
                    
                    if (WaitingForValue(data))
                    {
                        if (_isTbdrPlatform)
                        {
                            // TBDR平台：使用自旋等待和短间隔混合策略
                            if (spinCount++ < SpinWaitIterations)
                            {
                                Thread.SpinWait(50); // 短自旋等待
                            }
                            else
                            {
                                // 使用非常短的等待时间
                                wakeSignal.WaitOne(0, false);
                                spinCount = 0;
                            }
                        }
                        else
                        {
                            // 非TBDR平台：使用标准等待
                            wakeSignal.WaitOne(1);
                        }
                    }
                }
                
                if (iterations >= MaxQueryRetries)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Query result {_type} timed out. Attempts: {iterations}");
                    
                    // 返回安全值
                    return 0;
                }
            }
            
            return data;
        }
        
        public void PoolReset(CommandBuffer cmd, int resetSequence)
        {
            if (_isSupported && !_isPooled)
            {
                _api.CmdResetQueryPool(cmd, _queryPool, 0, 1);
            }
            _resetSequence = resetSequence;
        }
        
        public void PoolCopy(CommandBufferScoped cbs, uint queryIndex)
        {
            // 这个方法现在被批量复制替代
            // 保持兼容性，但实际不执行操作
        }
        
        // 批量复制支持方法
        public QueryCopyOperation GetCopyOperation()
        {
            return new QueryCopyOperation
            {
                QueryPool = _queryPool,
                QueryIndex = _queryIndex,
                ResultBuffer = _resultBuffer?.Buffer ?? default,
                ResultOffset = _resultOffset,
                Is64Bit = !_result32Bit
            };
        }
        
        public unsafe void Dispose()
        {
            // 释放批量缓冲区
            if (_useBatchProcessing && _resultBuffer != null)
            {
                // 返回缓冲区到管理器
                if (_batchManagers.TryGetValue((_pipeline.Gd, _device), out var manager))
                {
                    manager.ReturnResultBuffer(_resultBuffer);
                }
                _resultBuffer = null;
            }
            
            // 释放查询资源
            if (_isSupported && !_isPooled)
            {
                _api.DestroyQueryPool(_device, _queryPool, null);
            }
        }
        
        // 静态清理方法
        public static void CleanupBatchManagers()
        {
            foreach (var manager in _batchManagers.Values)
            {
                manager.Dispose();
            }
            _batchManagers.Clear();
        }
    }
}