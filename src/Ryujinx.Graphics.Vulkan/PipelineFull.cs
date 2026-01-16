using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineFull : PipelineBase, IPipeline
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024;

        private readonly List<(QueryPool, uint, bool)> _activeQueries;
        private CounterQueueEvent _activeConditionalRender;

        private readonly List<(BufferedQuery, uint)> _pendingQueryCopies;
        private readonly List<BufferHolder> _activeBufferMirrors;

        // 批量查询处理 - 参考Skyline的批处理机制
        private readonly Queue<(BufferedQuery, uint)> _queryBatchQueue = new();
        private int _batchQueryCount = 0;
        private readonly bool _isTbdrPlatform;
        private readonly int _targetBatchSize;
        private readonly object _batchLock = new();
        
        // 批次时间线信号量映射
        private readonly Dictionary<ulong, List<BufferedQuery>> _batchTimelineMap = new();
        private ulong _currentBatchTimelineValue = 0;
        
        // 查询状态追踪
        private readonly Dictionary<(QueryPool, uint), bool> _queryActiveState = new();
        private int _totalQueriesStarted = 0;
        private int _totalQueriesEnded = 0;

        private ulong _byteWeight;

        private readonly List<BufferHolder> _backingSwaps;

        public PipelineFull(VulkanRenderer gd, Device device) : base(gd, device)
        {
            _activeQueries = [];
            _pendingQueryCopies = [];
            _backingSwaps = [];
            _activeBufferMirrors = [];

            CommandBuffer = (Cbs = gd.CommandBufferPool.Rent()).CommandBuffer;

            IsMainPipeline = true;
            
            _isTbdrPlatform = gd.IsTBDR;
            _targetBatchSize = _isTbdrPlatform ? 32 : 64;
            
            if (_isTbdrPlatform)
            {
                Logger.Info?.Print(LogClass.Gpu, "TBDR platform: Using batch query processing with target size " + _targetBatchSize);
            }
            
            Logger.Debug?.Print(LogClass.Gpu, "PipelineFull initialized");
        }

        private void CopyPendingQuery()
        {
            // 批量复制所有查询结果
            int copyCount = _pendingQueryCopies.Count;
            
            if (copyCount > 0)
            {
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"PipelineFull CopyPendingQuery: Copying {copyCount} queries");
                }
                
                foreach (var (query, index) in _pendingQueryCopies)
                {
                    query.BatchCopy(Cbs, index, _currentBatchTimelineValue);
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
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"PipelineFull timeline signal submitted: Value={_currentBatchTimelineValue}");
                    }
                    
                    _currentBatchTimelineValue = 0;
                }
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
                // 条件渲染结束逻辑
            }

            _activeConditionalRender?.ReleaseHostAccess();
            _activeConditionalRender = null;
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    "PipelineFull EndHostConditionalRendering");
            }
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            if (value is CounterQueueEvent evt)
            {
                if (compare == 0 && evt.Type == CounterType.SamplesPassed && evt.ClearCounter)
                {
                    if (!value.ReserveForHostAccess())
                    {
                        if (_isTbdrPlatform)
                        {
                            Logger.Debug?.Print(LogClass.Gpu, 
                                "PipelineFull TryHostConditionalRendering: Failed to reserve host access");
                        }
                        return false;
                    }

                    if (Gd.Capabilities.SupportsConditionalRendering)
                    {
                        // 条件渲染开始逻辑
                    }

                    _activeConditionalRender = evt;
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            "PipelineFull TryHostConditionalRendering: Conditional rendering enabled");
                    }
                    return true;
                }
            }

            FlushPendingQuery();
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    "PipelineFull TryHostConditionalRendering: Fallback to flush");
            }
            
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            FlushPendingQuery();
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    "PipelineFull TryHostConditionalRendering with compare event");
            }
            
            return false;
        }

        private void FlushPendingQuery()
        {
            if (AutoFlush.ShouldFlushQuery())
            {
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        "PipelineFull FlushPendingQuery: Auto-flushing commands");
                }
                
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
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"PipelineFull FlushCommandsIfWeightExceeding: Weight={_byteWeight}, Flushing");
                    }
                    
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

            // 结束所有活动的查询
            foreach ((var queryPool, var index, _) in _activeQueries)
            {
                Gd.Api.CmdEndQuery(CommandBuffer, queryPool, index);
                _totalQueriesEnded++;
                
                // 更新查询状态
                _queryActiveState[(queryPool, index)] = false;
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

            // 重新开始所有活动的查询
            foreach ((var queryPool, var index, var isOcclusion) in _activeQueries)
            {
                bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
                
                if (_isTbdrPlatform && isOcclusion)
                {
                    isPrecise = false;
                }

                Gd.Api.CmdResetQueryPool(CommandBuffer, queryPool, index, 1);
                Gd.Api.CmdBeginQuery(CommandBuffer, queryPool, index, isPrecise ? QueryControlFlags.PreciseBit : 0);
                _totalQueriesStarted++;
                
                // 更新查询状态
                _queryActiveState[(queryPool, index)] = true;
            }

            Gd.ResetCounterPool();

            Restore();
            
            // 记录统计信息
            if (_isTbdrPlatform && (_totalQueriesStarted % 100 == 0 || _totalQueriesEnded % 100 == 0))
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"PipelineFull query stats: Started={_totalQueriesStarted}, Ended={_totalQueriesEnded}, Active={_activeQueries.Count}");
            }
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
                    int remainingQueries = AutoFlush.GetRemainingQueries();
                    Gd.ResetFutureCounters(CommandBuffer, remainingQueries);
                    
                    if (_isTbdrPlatform && remainingQueries > 0)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"PipelineFull BeginQuery reset future counters: Count={remainingQueries}");
                    }
                }
                
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"PipelineFull BeginQuery with reset: Pool={pool.Handle}, Index={index}, Occlusion={isOcclusion}");
                }
            }

            bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
            
            if (_isTbdrPlatform && isOcclusion)
            {
                isPrecise = false;
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"PipelineFull BeginQuery on TBDR: Disabling precise occlusion");
            }
            
            Gd.Api.CmdBeginQuery(CommandBuffer, pool, index, isPrecise ? QueryControlFlags.PreciseBit : 0);
            _totalQueriesStarted++;

            _activeQueries.Add((pool, index, isOcclusion));
            _queryActiveState[(pool, index)] = true;
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"PipelineFull BeginQuery: ActiveQueries={_activeQueries.Count}");
            }
        }

        public void EndQuery(QueryPool pool, uint index)
        {
            Gd.Api.CmdEndQuery(CommandBuffer, pool, index);
            _totalQueriesEnded++;

            for (int i = 0; i < _activeQueries.Count; i++)
            {
                if (_activeQueries[i].Item1.Handle == pool.Handle && _activeQueries[i].Item2 == index)
                {
                    _activeQueries.RemoveAt(i);
                    _queryActiveState[(pool, index)] = false;
                    break;
                }
            }
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"PipelineFull EndQuery: Pool={pool.Handle}, Index={index}, RemainingActive={_activeQueries.Count}");
            }
        }

        public void CopyQueryResults(BufferedQuery query, uint index)
        {
            lock (_batchLock)
            {
                _queryBatchQueue.Enqueue((query, index));
                _batchQueryCount++;
                
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"PipelineFull CopyQueryResults queued: Type={query.GetType()}, Index={index}, BatchCount={_batchQueryCount}");
                }
                
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
                    (forceFlush ? 1 : 8) :
                    (forceFlush ? 1 : 32);
                
                if (!forceFlush && _batchQueryCount < targetSize)
                {
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"PipelineFull ProcessQueryBatch: Not enough queries ({_batchQueryCount} < {targetSize}), deferring");
                    }
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
                        var (query, index) = _queryBatchQueue.Dequeue();
                        batchQueries.Add(query);
                        _pendingQueryCopies.Add((query, index));
                    }
                    
                    // 存储映射关系，以便后续通知查询
                    _batchTimelineMap[batchTimelineValue] = batchQueries;
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"TBDR: Processing batch of {batchQueries.Count} queries, timeline={batchTimelineValue}");
                    }
                }
                else
                {
                    // 没有时间线信号量支持，直接复制
                    while (_queryBatchQueue.Count > 0)
                    {
                        var (query, index) = _queryBatchQueue.Dequeue();
                        _pendingQueryCopies.Add((query, index));
                    }
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"TBDR: Processing batch of {_pendingQueryCopies.Count} queries (no timeline)");
                    }
                }
                
                _batchQueryCount = 0;
                
                // 如果强制刷新或需要刷新命令缓冲区
                if (forceFlush || (_pendingQueryCopies.Count > 0 && AutoFlush.RegisterPendingQuery()))
                {
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"TBDR: Processing batch of {_pendingQueryCopies.Count} queries, forceFlush={forceFlush}");
                    }
                    
                    // 立即执行复制
                    CopyPendingQuery();
                }
            }
        }

        protected override void SignalAttachmentChange()
        {
            if (AutoFlush.ShouldFlushAttachmentChange(DrawCount))
            {
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        "PipelineFull SignalAttachmentChange: Flushing commands");
                }
                
                FlushCommandsImpl();
            }
        }

        protected override void SignalRenderPassEnd()
        {
            // 渲染过程结束时处理所有批次查询
            if (_isTbdrPlatform && (_batchQueryCount > 0 || _pendingQueryCopies.Count > 0))
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"PipelineFull SignalRenderPassEnd: Processing queries (Batch={_batchQueryCount}, Pending={_pendingQueryCopies.Count})");
            }
            
            ProcessQueryBatch(true);
            CopyPendingQuery();
        }
        
        // 获取查询统计信息
        public void LogQueryStatistics()
        {
            Logger.Info?.Print(LogClass.Gpu, 
                $"PipelineFull Query Statistics: " +
                $"Started={_totalQueriesStarted}, " +
                $"Ended={_totalQueriesEnded}, " +
                $"Active={_activeQueries.Count}, " +
                $"BatchQueue={_batchQueryCount}, " +
                $"PendingCopies={_pendingQueryCopies.Count}");
        }
    }
}