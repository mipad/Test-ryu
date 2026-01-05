using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineFull : PipelineBase, IPipeline
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024; // MiB
        
        // TBDR架构优化参数
        private const int TbdrQueryBatchSize = 16;
        private const int TbdrQueryFlushThreshold = 24;
        private const int TbdrQueryDelayMs = 2;

        private readonly List<(QueryPool, bool)> _activeQueries;
        private CounterQueueEvent _activeConditionalRender;

        private readonly List<BufferedQuery> _pendingQueryCopies;
        private readonly List<BufferHolder> _activeBufferMirrors;

        // TBDR架构优化字段
        private readonly List<BufferedQuery> _deferredQueries = new();
        private int _deferredQueryCount = 0;
        private readonly bool _isTbdrPlatform;
        private DateTime _lastFlushTime = DateTime.UtcNow;
        private int _consecutiveTimeouts = 0;
        private int _adaptiveDelayMs = 1;
        private int _queryFlushCounter = 0;

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
            
            // 使用IsTBDR判断是否为移动平台
            _isTbdrPlatform = gd.IsTBDR;
            
            if (_isTbdrPlatform)
            {
                Logger.Info?.Print(LogClass.Gpu, "Running on TBDR architecture (ARM Mali/Qualcomm), enabling mobile optimizations");
            }
        }

        private void CopyPendingQuery()
        {
            if (_isTbdrPlatform && _pendingQueryCopies.Count > 8)
            {
                System.Threading.Thread.Sleep(_adaptiveDelayMs);
            }
            
            foreach (var query in _pendingQueryCopies)
            {
                query.PoolCopy(Cbs);
            }

            _pendingQueryCopies.Clear();
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
                        return false;
                    }

                    if (Gd.Capabilities.SupportsConditionalRendering)
                    {
                    }

                    _activeConditionalRender = evt;
                    return true;
                }
            }

            FlushPendingQuery();
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
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
            if (_isTbdrPlatform && _pendingQueryCopies.Count > 0)
            {
                System.Threading.Thread.Sleep(_adaptiveDelayMs);
                
                _queryFlushCounter++;
                if (_queryFlushCounter % 4 == 0 && _adaptiveDelayMs > 1)
                {
                    System.Threading.Thread.Sleep(_adaptiveDelayMs * 2);
                }
            }
            
            AutoFlush.RegisterFlush(DrawCount);
            EndRenderPass();

            foreach ((var queryPool, _) in _activeQueries)
            {
                Gd.Api.CmdEndQuery(CommandBuffer, queryPool, 0);
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

            foreach ((var queryPool, var isOcclusion) in _activeQueries)
            {
                bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;

                Gd.Api.CmdResetQueryPool(CommandBuffer, queryPool, 0, 1);
                Gd.Api.CmdBeginQuery(CommandBuffer, queryPool, 0, isPrecise ? QueryControlFlags.PreciseBit : 0);
            }

            Gd.ResetCounterPool();

            Restore();
            
            _queryFlushCounter = 0;
        }

        public void RegisterActiveMirror(BufferHolder buffer)
        {
            _activeBufferMirrors.Add(buffer);
        }

        public void BeginQuery(BufferedQuery query, QueryPool pool, bool needsReset, bool isOcclusion, bool fromSamplePool)
        {
            if (needsReset)
            {
                EndRenderPass();

                Gd.Api.CmdResetQueryPool(CommandBuffer, pool, 0, 1);

                if (fromSamplePool)
                {
                    Gd.ResetFutureCounters(CommandBuffer, AutoFlush.GetRemainingQueries());
                }
            }

            bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
            
            if (_isTbdrPlatform && isPrecise)
            {
                isPrecise = false;
                Logger.Debug?.Print(LogClass.Gpu, "TBDR platform: Disabling precise occlusion query for performance");
            }
            
            Gd.Api.CmdBeginQuery(CommandBuffer, pool, 0, isPrecise ? QueryControlFlags.PreciseBit : 0);

            _activeQueries.Add((pool, isOcclusion));
        }

        public void EndQuery(QueryPool pool)
        {
            Gd.Api.CmdEndQuery(CommandBuffer, pool, 0);

            for (int i = 0; i < _activeQueries.Count; i++)
            {
                if (_activeQueries[i].Item1.Handle == pool.Handle)
                {
                    _activeQueries.RemoveAt(i);
                    break;
                }
            }
        }

        public void CopyQueryResults(BufferedQuery query)
        {
            if (_isTbdrPlatform)
            {
                _deferredQueries.Add(query);
                _deferredQueryCount++;
                
                if (_consecutiveTimeouts > 3)
                {
                    _adaptiveDelayMs = Math.Min(_adaptiveDelayMs * 2, 20);
                    _consecutiveTimeouts = 0;
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"TBDR platform: Increasing adaptive delay to {_adaptiveDelayMs}ms due to timeouts");
                }
                
                bool shouldFlush = false;
                
                if (_deferredQueryCount >= TbdrQueryBatchSize)
                {
                    shouldFlush = true;
                }
                else
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastFlushTime).TotalMilliseconds > 33)
                    {
                        shouldFlush = true;
                    }
                }
                
                if (shouldFlush)
                {
                    foreach (var deferredQuery in _deferredQueries)
                    {
                        _pendingQueryCopies.Add(deferredQuery);
                    }
                    _deferredQueries.Clear();
                    _deferredQueryCount = 0;
                    
                    System.Threading.Thread.Sleep(TbdrQueryDelayMs);
                    
                    if (_pendingQueryCopies.Count >= TbdrQueryFlushThreshold || 
                        AutoFlush.RegisterPendingQuery())
                    {
                        _lastFlushTime = DateTime.UtcNow;
                        FlushCommandsImpl();
                    }
                }
                else
                {
                    return;
                }
            }
            else
            {
                _pendingQueryCopies.Add(query);

                if (AutoFlush.RegisterPendingQuery())
                {
                    FlushCommandsImpl();
                }
            }
        }
        
        public void NotifyQueryTimeout()
        {
            if (_isTbdrPlatform)
            {
                _consecutiveTimeouts++;
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"TBDR platform query timeout #{_consecutiveTimeouts}, adaptive delay: {_adaptiveDelayMs}ms");
                
                if (_consecutiveTimeouts > 10)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        "High query timeout rate on TBDR platform. Consider reducing graphical settings.");
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
            if (_isTbdrPlatform && _deferredQueryCount > 0)
            {
                foreach (var deferredQuery in _deferredQueries)
                {
                    _pendingQueryCopies.Add(deferredQuery);
                }
                _deferredQueries.Clear();
                _deferredQueryCount = 0;
                
                if (_pendingQueryCopies.Count > 0)
                {
                    System.Threading.Thread.Sleep(1);
                }
            }
            
            CopyPendingQuery();
        }
        
        public void FlushDeferredQueries()
        {
            if (_isTbdrPlatform && _deferredQueryCount > 0)
            {
                Logger.Debug?.Print(LogClass.Gpu, $"Flushing {_deferredQueryCount} deferred queries on TBDR platform");
                
                foreach (var deferredQuery in _deferredQueries)
                {
                    _pendingQueryCopies.Add(deferredQuery);
                }
                _deferredQueries.Clear();
                _deferredQueryCount = 0;
                
                if (_pendingQueryCopies.Count > 0)
                {
                    FlushCommandsImpl();
                }
            }
        }
        
        public bool IsTbdrPlatform => _isTbdrPlatform;
    }
}
