using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    class BufferedQuery : IDisposable
    {
        private const int MaxQueryRetries = 5000;
        private const long DefaultValue = unchecked((long)0xFFFFFFFEFFFFFFFE);
        private const long DefaultValueInt = 0xFFFFFFFE;
        private const ulong HighMask = 0xFFFFFFFF00000000;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly PipelineFull _pipeline;

        private QueryPool _queryPool;

        private readonly BufferHolder _buffer;
        private readonly nint _bufferMap;
        private readonly CounterType _type;
        private readonly bool _result32Bit;
        private readonly bool _isSupported;

        private readonly long _defaultValue;
        private int? _resetSequence;

        // 新增：TBDR平台标志和自适应延迟
        private readonly bool _isTbdrPlatform;
        private int _adaptiveDelayMs = 1;
        private int _consecutiveTimeouts = 0;

        public unsafe BufferedQuery(VulkanRenderer gd, Device device, PipelineFull pipeline, CounterType type, bool result32Bit)
        {
            _api = gd.Api;
            _device = device;
            _pipeline = pipeline;
            _type = type;
            _result32Bit = result32Bit;
            
            // 使用IsTBDR判断是否为移动平台
            _isTbdrPlatform = gd.IsTBDR;

            _isSupported = QueryTypeSupported(gd, type);

            if (_isSupported)
            {
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
            }

            BufferHolder buffer = gd.BufferManager.Create(gd, sizeof(long), forConditionalRendering: true);

            _bufferMap = buffer.Map(0, sizeof(long));
            _defaultValue = result32Bit ? DefaultValueInt : DefaultValue;
            Marshal.WriteInt64(_bufferMap, _defaultValue);
            _buffer = buffer;
            
            // TBDR平台：调整默认延迟
            if (_isTbdrPlatform)
            {
                _adaptiveDelayMs = 2;
                Logger.Debug?.Print(LogClass.Gpu, "TBDR platform detected, enabling query optimizations");
            }
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
                _pipeline.BeginQuery(this, _queryPool, needsReset, isOcclusion, isOcclusion && resetSequence != null);
            }
            _resetSequence = null;
        }

        public void End(bool withResult)
        {
            if (_isSupported)
            {
                _pipeline.EndQuery(_queryPool);
            }

            if (withResult && _isSupported)
            {
                Marshal.WriteInt64(_bufferMap, _defaultValue);
                _pipeline.CopyQueryResults(this);
            }
            else
            {
                // Dummy result, just return 0.
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
                int maxRetries = _isTbdrPlatform ? MaxQueryRetries * 3 / 2 : MaxQueryRetries;
                int delayMs = _adaptiveDelayMs;
                
                while (WaitingForValue(data) && iterations++ < maxRetries)
                {
                    data = Marshal.ReadInt64(_bufferMap);
                    if (WaitingForValue(data))
                    {
                        // TBDR平台：动态调整延迟策略
                        if (_isTbdrPlatform)
                        {
                            // 渐进式延迟增加
                            if (iterations < 500) delayMs = 1;
                            else if (iterations < 1500) delayMs = 2;
                            else if (iterations < 3000) delayMs = 4;
                            else if (iterations < 6000) delayMs = 8;
                            else delayMs = 16;
                            
                            // 每500次迭代增加一点额外延迟
                            if (iterations % 500 == 0 && delayMs < 10)
                            {
                                delayMs++;
                            }
                        }
                        
                        wakeSignal.WaitOne(delayMs);
                    }
                }

                if (iterations >= maxRetries)
                {
                    if (_isTbdrPlatform)
                    {
                        _consecutiveTimeouts++;
                        // 连续超时增加延迟，但有一个上限
                        if (_consecutiveTimeouts > 2)
                        {
                            _adaptiveDelayMs = Math.Min(_adaptiveDelayMs * 2, 32);
                            _consecutiveTimeouts = 0;
                        }
                        
                        Logger.Error?.Print(LogClass.Gpu, 
                            $"Error: Query result {_type} timed out on TBDR platform after {maxRetries} tries. Adaptive delay: {_adaptiveDelayMs}ms");
                        
                        // 通知PipelineFull有超时发生
                        (_pipeline as PipelineFull)?.NotifyQueryTimeout();
                    }
                    else
                    {
                        Logger.Error?.Print(LogClass.Gpu, 
                            $"Error: Query result {_type} timed out. Took more than {maxRetries} tries.");
                    }
                }
                else
                {
                    // 成功时逐渐降低延迟
                    if (_isTbdrPlatform && _adaptiveDelayMs > 1 && iterations < 500)
                    {
                        _adaptiveDelayMs = Math.Max(1, _adaptiveDelayMs / 2);
                        _consecutiveTimeouts = 0;
                    }
                }
            }

            return data;
        }

        public void PoolReset(CommandBuffer cmd, int resetSequence)
        {
            if (_isSupported)
            {
                _api.CmdResetQueryPool(cmd, _queryPool, 0, 1);
            }

            _resetSequence = resetSequence;
        }

        public void PoolCopy(CommandBufferScoped cbs)
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
                0,
                1,
                buffer,
                0,
                (ulong)(_result32Bit ? sizeof(int) : sizeof(long)),
                flags);
        }

        public unsafe void Dispose()
        {
            _buffer.Dispose();
            if (_isSupported)
            {
                _api.DestroyQueryPool(_device, _queryPool, null);
            }
            _queryPool = default;
        }
    }
}
