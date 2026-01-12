using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineFull : PipelineBase, IPipeline
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024;
        private const int MaxBatchSize = 128; // 最大批次大小
        private const int TargetBatchSizeTbdr = 48; // TBDR平台目标批次大小
        private const int TargetBatchSizeOther = 64; // 其他平台目标批次大小

        private readonly List<(QueryPool, uint, bool)> _activeQueries;
        private CounterQueueEvent _activeConditionalRender;

        private readonly List<QueryCopyOperation> _pendingQueryCopies;
        private readonly List<BufferHolder> _activeBufferMirrors;

        // 批量查询处理
        private readonly Queue<QueryCopyOperation> _queryBatchQueue = new();
        private int _batchQueryCount = 0;
        private readonly bool _isTbdrPlatform;
        private readonly int _targetBatchSize;
        private readonly object _batchLock = new();
        
        // 批量查询管理器
        private readonly BufferedQueryBatchManager _queryBatchManager;

        private ulong _byteWeight;

        private readonly List<BufferHolder> _backingSwaps;

        public PipelineFull(VulkanRenderer gd, Device device) : base(gd, device)
        {
            _activeQueries = new List<(QueryPool, uint, bool)>();
            _pendingQueryCopies = new List<QueryCopyOperation>();
            _backingSwaps = new List<BufferHolder>();
            _activeBufferMirrors = new List<BufferHolder>();

            CommandBuffer = (Cbs = gd.CommandBufferPool.Rent()).CommandBuffer;

            IsMainPipeline = true;
            
            _isTbdrPlatform = gd.IsTBDR;
            _targetBatchSize = _isTbdrPlatform ? TargetBatchSizeTbdr : TargetBatchSizeOther;
            
            // 创建批量查询管理器
            _queryBatchManager = new BufferedQueryBatchManager(gd, device, _isTbdrPlatform);
            
            if (_isTbdrPlatform)
            {
                Logger.Info?.Print(LogClass.Gpu, 
                    $"TBDR pipeline initialized with batch size: {_targetBatchSize}");
            }
        }

        private unsafe void CopyPendingQuery()
        {
            if (_pendingQueryCopies.Count == 0) return;
            
            // 使用批量管理器复制所有查询结果
            _queryBatchManager.CopyBatchResults(
                Cbs.CommandBuffer,
                _pendingQueryCopies);
            
            if (_isTbdrPlatform && _pendingQueryCopies.Count > 1)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Copied {_pendingQueryCopies.Count} queries in batch on TBDR platform");
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
                // 实现条件渲染结束
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
                        // 实现条件渲染
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
            // 处理所有批次查询
            ProcessQueryBatch(true);
            
            AutoFlush.RegisterFlush(DrawCount);
            EndRenderPass();

            // 结束所有活跃查询
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

            // 清理镜像缓冲区
            foreach (BufferHolder buffer in _activeBufferMirrors)
            {
                buffer.ClearMirrors();
            }
            _activeBufferMirrors.Clear();

            // 重新开始所有查询
            foreach ((var queryPool, var index, var isOcclusion) in _activeQueries)
            {
                bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
                
                // TBDR平台：禁用精确查询以提升性能
                if (_isTbdrPlatform && isOcclusion)
                {
                    isPrecise = false;
                }

                Gd.Api.CmdResetQueryPool(CommandBuffer, queryPool, index, 1);
                Gd.Api.CmdBeginQuery(CommandBuffer, queryPool, index, 
                    isPrecise ? QueryControlFlags.PreciseBit : 0);
            }

            // 重置计数器池
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
            
            // TBDR平台优化：禁用精确查询
            if (_isTbdrPlatform && isOcclusion)
            {
                isPrecise = false;
            }
            
            Gd.Api.CmdBeginQuery(CommandBuffer, pool, index, 
                isPrecise ? QueryControlFlags.PreciseBit : 0);

            _activeQueries.Add((pool, index, isOcclusion));
        }

        public void EndQuery(QueryPool pool, uint index)
        {
            Gd.Api.CmdEndQuery(CommandBuffer, pool, index);

            // 从活跃查询列表中移除
            for (int i = 0; i < _activeQueries.Count; i++)
            {
                if (_activeQueries[i].Item1.Handle == pool.Handle && 
                    _activeQueries[i].Item2 == index)
                {
                    _activeQueries.RemoveAt(i);
                    break;
                }
            }
        }

        public void CopyQueryResults(BufferedQuery query, uint index)
        {
            lock (_batchLock)
            {
                // 获取查询的复制操作信息
                var copyOp = query.GetCopyOperation();
                copyOp.QueryIndex = index; // 确保使用正确的索引
                
                _queryBatchQueue.Enqueue(copyOp);
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
            List<QueryCopyOperation> batchOperations = new List<QueryCopyOperation>();
            
            lock (_batchLock)
            {
                if (_batchQueryCount == 0) return;
                
                // 收集批次中的所有操作
                while (_queryBatchQueue.Count > 0 && 
                      (forceFlush || batchOperations.Count < _targetBatchSize))
                {
                    batchOperations.Add(_queryBatchQueue.Dequeue());
                }
                
                _batchQueryCount -= batchOperations.Count;
                
                if (batchOperations.Count > 0)
                {
                    // 添加到待处理列表
                    _pendingQueryCopies.AddRange(batchOperations);
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"TBDR: Collected batch of {batchOperations.Count} queries");
                    }
                    
                    // 如果强制刷新或需要刷新命令缓冲区
                    if (forceFlush || AutoFlush.RegisterPendingQuery())
                    {
                        // 在实际渲染中，我们会立即复制
                        if (forceFlush)
                        {
                            CopyPendingQuery();
                        }
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

        protected override unsafe void SignalRenderPassEnd()
        {
            // 渲染过程结束时处理所有批次查询
            ProcessQueryBatch(true);
            CopyPendingQuery();
        }
        
        public new void Dispose()
        {
            base.Dispose();
            
            // 清理批量查询管理器
            _queryBatchManager?.Dispose();
            
            // 清理查询批次队列
            _queryBatchQueue.Clear();
            _pendingQueryCopies.Clear();
            _activeQueries.Clear();
        }
    }
}