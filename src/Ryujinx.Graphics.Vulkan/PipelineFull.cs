using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    partial class PipelineFull : PipelineBase, IPipeline
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024;

        private readonly List<(QueryPool, uint, bool)> _activeQueries;
        private CounterQueueEvent _activeConditionalRender;

        private readonly List<(BufferedQuery, uint, bool)> _pendingQueryCopies; // 添加时间戳标志
        private readonly List<BufferHolder> _activeBufferMirrors;

        // 批量查询处理（与Skyline类似）
        private readonly Queue<(BufferedQuery, uint, bool)> _queryBatchQueue = new();
        private int _batchQueryCount = 0;
        private readonly bool _isTbdrPlatform;
        private readonly int _targetBatchSize;
        private readonly object _batchLock = new();
        
        // 批次时间线信号量映射
        private readonly Dictionary<ulong, List<BufferedQuery>> _batchTimelineMap = new();
        private ulong _currentBatchTimelineValue = 0;

        private ulong _byteWeight;

        private readonly List<BufferHolder> _backingSwaps;

        // 时间戳查询支持
        private bool _enableTimestampQueries = false;
        private ulong _lastTimestamp = 0;

        public PipelineFull(VulkanRenderer gd, Device device) : base(gd, device)
        {
            _activeQueries = [];
            _pendingQueryCopies = [];
            _backingSwaps = [];
            _activeBufferMirrors = [];

            CommandBuffer = (Cbs = gd.CommandBufferPool.Rent()).CommandBuffer;

            IsMainPipeline = true;
            
            _isTbdrPlatform = gd.IsTBDR;
            _targetBatchSize = _isTbdrPlatform ? 8 : 64;
            _enableTimestampQueries = gd.Capabilities.SupportsTimestampQueries;
            
            if (_isTbdrPlatform)
            {
                Logger.Info?.Print(LogClass.Gpu, "TBDR platform: Using batch query processing");
            }
        }

        private void CopyPendingQuery()
        {
            // 批量复制所有查询结果（支持时间戳）
            foreach (var (query, index, hasTimestamp) in _pendingQueryCopies)
            {
                query.BatchCopy(Cbs, index, _currentBatchTimelineValue);
                
                // 如果查询包含时间戳，记录它
                if (hasTimestamp && _enableTimestampQueries)
                {
                    _lastTimestamp = Gd.GetNextTimelineValue();
                    Gd.RegisterTimestamp(DrawCount, _lastTimestamp);
                }
            }

            _pendingQueryCopies.Clear();
            
            // 如果有批次时间线信号量值，提交它
            if (_currentBatchTimelineValue > 0 && Gd.SupportsTimelineSemaphores && Gd.TimelineSemaphore.Handle != 0)
            {
                Gd.CommandBufferPool.AddTimelineSignalToBuffer(
                    Cbs.CommandBufferIndex, 
                    Gd.TimelineSemaphore, 
                    _currentBatchTimelineValue);
                
                // 通知批次中的所有查询
                if (_batchTimelineMap.TryGetValue(_currentBatchTimelineValue, out var batchQueries))
                {
                    foreach (var query in batchQueries)
                    {
                        query.SetBatchTimelineValue(_currentBatchTimelineValue);
                    }
                    _batchTimelineMap.Remove(_currentBatchTimelineValue);
                }
                
                _currentBatchTimelineValue = 0;
            }
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            if (FramebufferParams == null)
            {
                return;
            }

            if (componentMask != 0xf || Gd.IsQualcommProprietary)
            {
                var dstTexture = FramebufferParams.GetColorView(index);
                if (dstTexture == null)
                {
                    return;
                }

                Span<float> clearColor = stackalloc float[4];
                clearColor[0] = color.Red;
                clearColor[1] = color.Green;
                clearColor[2] = color.Blue;
                clearColor[3] = color.Alpha;

                Gd.HelperShader.Clear(
                    Gd,
                    dstTexture,
                    clearColor,
                    componentMask,
                    (int)FramebufferParams.Width,
                    (int)FramebufferParams.Height,
                    FramebufferParams.GetAttachmentComponentType(index),
                    ClearScissor);
            }
            else
            {
                ClearRenderTargetColor(index, layer, layerCount, color);
            }
        }

        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            if (FramebufferParams == null)
            {
                return;
            }

            if ((stencilMask != 0 && stencilMask != 0xff) || Gd.IsQualcommProprietary)
            {
                var dstTexture = FramebufferParams.GetDepthStencilView();
                if (dstTexture == null)
                {
                    return;
                }

                Gd.HelperShader.Clear(
                    Gd,
                    dstTexture,
                    depthValue,
                    depthMask,
                    stencilValue,
                    stencilMask,
                    (int)FramebufferParams.Width,
                    (int)FramebufferParams.Height,
                    FramebufferParams.AttachmentFormats[FramebufferParams.AttachmentsCount - 1],
                    ClearScissor);
            }
            else
            {
                ClearRenderTargetDepthStencil(layer, layerCount, depthValue, depthMask, stencilValue, stencilMask != 0);
            }
        }

        public void EndHostConditionalRendering()
        {
            if (Gd.Capabilities.SupportsConditionalRendering)
            {
                // 实现条件渲染结束逻辑
            }

            _activeConditionalRender?.ReleaseHostAccess();
            _activeConditionalRender = null;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            if (value is CounterQueueEvent evt)
            {
                if (compare == 0 && evt.Type == CounterType.SamplesPassed && evt.ClearCounter)
                {
                    if (!value.ReserveForHostAccess())
                    {
                        // 无法保留主机访问，刷新查询并返回false
                        FlushPendingQuery();
                        return false;
                    }

                    // 对于TBDR平台，需要确保查询结果可用
                    if (_isTbdrPlatform)
                    {
                        // 确保命令缓冲区已提交
                        if (AutoFlush.ShouldFlushQuery())
                        {
                            FlushCommandsImpl();
                        }
                        
                        // 等待查询结果可用
                        // 注意：这里我们只刷新，不阻塞等待，因为阻塞可能导致死锁
                        // 条件渲染将依赖查询的AwaitResult方法
                        
                        // 记录日志以便调试
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"TBDR: Conditional rendering using query type={evt.Type}, drawIndex={evt.DrawIndex}");
                    }

                    if (Gd.Capabilities.SupportsConditionalRendering)
                    {
                        // 条件渲染设置
                        // 注意：这里需要确保查询结果已经可用
                        // 实现条件渲染开始逻辑
                    }

                    _activeConditionalRender = evt;
                    return true;
                }
            }

            // 非遮挡查询或比较值不为0，刷新查询并返回false
            FlushPendingQuery();
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            // 总是刷新查询并返回false，因为我们不支持两个事件的比较
            FlushPendingQuery();
            return false;
        }

        private void FlushPendingQuery()
        {
            if (AutoFlush.ShouldFlushQuery())
            {
                FlushCommandsImpl();
            }
        }

        public CommandBufferScoped GetPreloadCommandBuffer()
        {
            PreloadCbs ??= Gd.CommandBufferPool.Rent();

            return PreloadCbs.Value;
        }

        public void FlushCommandsIfWeightExceeding(IAuto disposedResource, ulong byteWeight)
        {
            bool usedByCurrentCb = disposedResource.HasCommandBufferDependency(Cbs);

            if (PreloadCbs != null && !usedByCurrentCb)
            {
                usedByCurrentCb = disposedResource.HasCommandBufferDependency(PreloadCbs.Value);
            }

            if (usedByCurrentCb)
            {
                _byteWeight += byteWeight;

                if (_byteWeight >= MinByteWeightForFlush)
                {
                    FlushCommandsImpl();
                }
            }
        }

        public void Restore()
        {
            if (Pipeline != null)
            {
                Gd.Api.CmdBindPipeline(CommandBuffer, Pbp, Pipeline.Get(Cbs).Value);
            }

            SignalCommandBufferChange();

            if (Pipeline != null && Pbp == PipelineBindPoint.Graphics)
            {
                DynamicState.ReplayIfDirty(Gd, CommandBuffer);
            }
        }

        public void FlushCommandsImpl()
        {
            // 处理所有批次查询
            ProcessQueryBatch(true);
            
            AutoFlush.RegisterFlush(DrawCount);
            EndRenderPass();

            foreach ((var queryPool, var index, _) in _activeQueries)
            {
                Gd.Api.CmdEndQuery(CommandBuffer, queryPool, index);
            }

            _byteWeight = 0;

            if (PreloadCbs != null)
            {
                PreloadCbs.Value.Dispose();
                PreloadCbs = null;
            }

            Gd.Barriers.Flush(Cbs, false, null, null);
            CommandBuffer = (Cbs = Gd.CommandBufferPool.ReturnAndRent(Cbs)).CommandBuffer;
            Gd.RegisterFlush();

            foreach (BufferHolder buffer in _activeBufferMirrors)
            {
                buffer.ClearMirrors();
            }

            _activeBufferMirrors.Clear();

            foreach ((var queryPool, var index, var isOcclusion) in _activeQueries)
            {
                bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
                
                if (_isTbdrPlatform && isOcclusion)
                {
                    isPrecise = false; // TBDR平台禁用精确查询
                }

                Gd.Api.CmdResetQueryPool(CommandBuffer, queryPool, index, 1);
                Gd.Api.CmdBeginQuery(CommandBuffer, queryPool, index, isPrecise ? QueryControlFlags.PreciseBit : 0);
            }

            Gd.ResetCounterPool();

            Restore();
        }

        public void RegisterActiveMirror(BufferHolder buffer)
        {
            _activeBufferMirrors.Add(buffer);
        }

        public void BeginQuery(BufferedQuery query, QueryPool pool, uint index, bool needsReset, bool isOcclusion, bool fromSamplePool)
        {
            if (needsReset)
            {
                EndRenderPass();

                Gd.Api.CmdResetQueryPool(CommandBuffer, pool, index, 1);

                if (fromSamplePool)
                {
                    Gd.ResetFutureCounters(CommandBuffer, AutoFlush.GetRemainingQueries());
                }
            }

            bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
            
            if (_isTbdrPlatform && isOcclusion)
            {
                isPrecise = false; // TBDR平台禁用精确查询
            }
            
            Gd.Api.CmdBeginQuery(CommandBuffer, pool, index, isPrecise ? QueryControlFlags.PreciseBit : 0);

            _activeQueries.Add((pool, index, isOcclusion));
        }

        public void EndQuery(QueryPool pool, uint index)
        {
            Gd.Api.CmdEndQuery(CommandBuffer, pool, index);

            for (int i = 0; i < _activeQueries.Count; i++)
            {
                if (_activeQueries[i].Item1.Handle == pool.Handle && _activeQueries[i].Item2 == index)
                {
                    _activeQueries.RemoveAt(i);
                    break;
                }
            }
        }

        public void CopyQueryResults(BufferedQuery query, uint index, bool includeTimestamp = false)
        {
            lock (_batchLock)
            {
                _queryBatchQueue.Enqueue((query, index, includeTimestamp));
                _batchQueryCount++;
                
                // 达到批次大小时处理批次
                if (_batchQueryCount >= _targetBatchSize)
                {
                    ProcessQueryBatch(false);
                }
            }
        }
        
        private void ProcessQueryBatch(bool forceFlush)
        {
            lock (_batchLock)
            {
                if (_batchQueryCount == 0) return;
                
                // 对于TBDR平台，更激进的批处理
                int targetSize = _isTbdrPlatform ? 
                    (forceFlush ? 1 : 4) : // TBDR平台更小的批次
                    (forceFlush ? 1 : 16); // 非TBDR平台正常批次
                
                if (!forceFlush && _batchQueryCount < targetSize)
                {
                    return;
                }
                
                // 分配批次时间线信号量值
                ulong batchTimelineValue = 0;
                if (Gd.SupportsTimelineSemaphores && Gd.TimelineSemaphore.Handle != 0)
                {
                    batchTimelineValue = Gd.GetNextTimelineValue();
                    _currentBatchTimelineValue = batchTimelineValue;
                    
                    var batchQueries = new List<BufferedQuery>();
                    
                    // 收集批次中的所有查询
                    while (_queryBatchQueue.Count > 0)
                    {
                        var (query, index, hasTimestamp) = _queryBatchQueue.Dequeue();
                        batchQueries.Add(query);
                        _pendingQueryCopies.Add((query, index, hasTimestamp));
                    }
                    
                    // 存储映射关系，以便后续通知查询
                    _batchTimelineMap[batchTimelineValue] = batchQueries;
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"TBDR: Batch of {batchQueries.Count} queries, timeline={batchTimelineValue}");
                    }
                }
                else
                {
                    // 没有时间线信号量支持，直接复制
                    while (_queryBatchQueue.Count > 0)
                    {
                        var (query, index, hasTimestamp) = _queryBatchQueue.Dequeue();
                        _pendingQueryCopies.Add((query, index, hasTimestamp));
                    }
                }
                
                _batchQueryCount = 0;
                
                // 如果强制刷新或需要刷新命令缓冲区
                if (forceFlush || (_pendingQueryCopies.Count > 0 && AutoFlush.RegisterPendingQuery()))
                {
                    // 对于TBDR平台，立即复制查询结果
                    if (_isTbdrPlatform && _pendingQueryCopies.Count > 0)
                    {
                        CopyPendingQuery();
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"TBDR: Immediately copying {_pendingQueryCopies.Count} queries");
                    }
                }
            }
        }

        protected override void SignalAttachmentChange()
        {
            if (AutoFlush.ShouldFlushAttachmentChange(DrawCount))
            {
                FlushCommandsImpl();
            }
        }

        protected override void SignalRenderPassEnd()
        {
            // 渲染过程结束时处理所有批次查询
            ProcessQueryBatch(true);
            CopyPendingQuery();
        }

        // 获取最后的时间戳
        public ulong GetLastTimestamp() => _lastTimestamp;
    }
}