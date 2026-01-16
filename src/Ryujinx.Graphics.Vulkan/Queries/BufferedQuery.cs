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
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"Created batch result buffer for {type} ({(is64Bit ? "64-bit" : "32-bit")}), capacity: {capacity}");
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

        private class QueryPoolManager
        {
            public QueryPool QueryPool { get; set; }
            public uint NextIndex { get; set; }
            public int ReferenceCount { get; set; }
            public const uint PoolSize = 24576; // 从1024改为30000
        }

        public unsafe BufferedQuery(VulkanRenderer gd, Device device, PipelineFull pipeline, CounterType type, bool result32Bit, bool isTbdrPlatform)
        {
            _api = gd.Api;
            _device = device;
            _pipeline = pipeline;
            _type = type;
            _result32Bit = result32Bit;
            _isTbdrPlatform = isTbdrPlatform;
            
            // 初始化批量结果缓冲区
            if (isTbdrPlatform)
            {
                BatchQueryManager.CreateResultBuffer(gd, device, type, !result32Bit, 8192); // 从1024改为30000
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
            return result != _defaultValue;
        }

        public long AwaitResult(AutoResetEvent wakeSignal = null)
        {
            long data = _defaultValue;

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
                int spinCount = 0;
                
                // TBDR平台优化：更积极的轮询策略
                while (WaitingForValue(data) && iterations++ < MaxQueryRetries)
                {
                    data = Marshal.ReadInt64(_bufferMap);
                    if (WaitingForValue(data))
                    {
                        if (_isTbdrPlatform)
                        {
                            // TBDR平台：使用SpinWait减少上下文切换
                            if (spinCount < 50)
                            {
                                Thread.SpinWait(100);
                                spinCount++;
                            }
                            else if (spinCount < 100)
                            {
                                Thread.Sleep(0);
                                spinCount++;
                            }
                            else
                            {
                                wakeSignal.WaitOne(0);
                            }
                        }
                        else
                        {
                            wakeSignal.WaitOne(1);
                        }
                    }
                }

                if (iterations >= MaxQueryRetries)
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"Error: Query result {_type} timed out. Attempts: {iterations}");
                    
                    // 强制返回默认值，避免阻塞
                    return 0;
                }
            }

            return data;
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
                    
                    Marshal.WriteInt64(_bufferMap, result);
                    _usingBatchBuffer = false;
                    _batchResultReady = false;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"Error copying batch result for {_type}: {ex.Message}");
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
