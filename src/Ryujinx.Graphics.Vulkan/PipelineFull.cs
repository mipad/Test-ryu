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

        private readonly Queue<QueryBatch> _queryBatchQueue = new();
        private int _batchQueryCount = 0;
        private readonly bool _isTbdrPlatform;
        private readonly int _targetBatchSize;
        private readonly object _batchLock = new();
        
        private readonly Dictionary<CounterType, List<QueryBatch>> _pendingBatchCopies = new();
        private readonly Dictionary<CounterType, List<BufferedQuery>> _batchQueriesToMarkReady = new();

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
                Logger.Info?.Print(LogClass.Gpu, "TBDR platform: Using optimized batch query processing");
            }
        }

        private void CopyPendingQuery()
        {
            if (_pendingQueryCopies.Count > 0)
            {
                foreach (var (query, index) in _pendingQueryCopies)
                {
                    query.PoolCopy(Cbs, index);
                }

                _pendingQueryCopies.Clear();
                
                if (_isTbdrPlatform)
                {
                    Gd.Barriers.Flush(Cbs, false, null, null);
                }
            }
        }
        
        private void CopyPendingBatchQueries()
        {
            if (_pendingBatchCopies.Count > 0)
            {
                foreach (var batches in _pendingBatchCopies.Values)
                {
                    foreach (var batch in batches)
                    {
                        if (batch.Count > 0 && batch.QueryPool.Handle != 0)
                        {
                            BufferedQuery.CopyBatch(Gd.Api, Cbs, batch);
                            
                            if (_isTbdrPlatform)
                            {
                                Logger.Debug?.Print(LogClass.Gpu, 
                                    $"TBDR: Executed batch copy: pool={batch.QueryPool.Handle:X}, " +
                                    $"start={batch.StartIndex}, count={batch.Count}, " +
                                    $"offset={batch.ResultOffset}");
                            }
                        }
                    }
                }
                
                foreach (var queries in _batchQueriesToMarkReady.Values)
                {
                    foreach (var query in queries)
                    {
                        query.MarkBatchResultReady();
                    }
                }
                
                if (_isTbdrPlatform)
                {
                    unsafe
                    {
                        MemoryBarrier memoryBarrier = new()
                        {
                            SType = StructureType.MemoryBarrier,
                            SrcAccessMask = AccessFlags.TransferWriteBit,
                            DstAccessMask = AccessFlags.HostReadBit
                        };
                        
                        Gd.Api.CmdPipelineBarrier(
                            Cbs.CommandBuffer,
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
                
                _pendingBatchCopies.Clear();
                _batchQueriesToMarkReady.Clear();
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
                    isPrecise = false;
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
                isPrecise = false;
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

        public void CopyQueryResults(BufferedQuery query, uint index)
        {
            if (_isTbdrPlatform && query.GetBatchInfo().QueryPool.Handle != 0)
            {
                var batch = query.GetBatchInfo();
                if (batch.QueryPool.Handle != 0 && batch.Count > 0)
                {
                    lock (_batchLock)
                    {
                        var counterType = query.GetCounterType();
                        if (!_pendingBatchCopies.ContainsKey(counterType))
                        {
                            _pendingBatchCopies[counterType] = new List<QueryBatch>();
                            _batchQueriesToMarkReady[counterType] = new List<BufferedQuery>();
                        }
                        
                        bool exists = false;
                        foreach (var existing in _pendingBatchCopies[counterType])
                        {
                            if (existing.QueryPool.Handle == batch.QueryPool.Handle &&
                                existing.StartIndex == batch.StartIndex &&
                                existing.ResultBuffer.Handle == batch.ResultBuffer.Handle &&
                                existing.ResultOffset == batch.ResultOffset)
                            {
                                exists = true;
                                break;
                            }
                        }
                        
                        if (!exists)
                        {
                            _pendingBatchCopies[counterType].Add(batch);
                            _batchQueriesToMarkReady[counterType].Add(query);
                            _batchQueryCount++;
                            
                            if (_batchQueryCount >= _targetBatchSize)
                            {
                                ProcessQueryBatch(false);
                            }
                        }
                    }
                    return;
                }
            }
            
            lock (_batchLock)
            {
                _pendingQueryCopies.Add((query, index));
                _batchQueryCount++;
                
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
                
                bool shouldFlush = forceFlush || 
                    (_pendingBatchCopies.Count > 0 && AutoFlush.RegisterPendingQuery()) ||
                    (_pendingQueryCopies.Count > 0 && AutoFlush.RegisterPendingQuery());
                
                if (shouldFlush)
                {
                    if (_pendingBatchCopies.Count > 0)
                    {
                        if (_isTbdrPlatform)
                        {
                            int batchCount = 0;
                            int queryCount = 0;
                            foreach (var batches in _pendingBatchCopies.Values)
                            {
                                batchCount += batches.Count;
                                foreach (var batch in batches)
                                {
                                    queryCount += (int)batch.Count;
                                }
                            }
                            
                            Logger.Debug?.Print(LogClass.Gpu, 
                                $"TBDR: Processing {batchCount} batches, {queryCount} queries");
                        }
                    }
                    
                    CopyPendingBatchQueries();
                    CopyPendingQuery();
                    
                    if (_isTbdrPlatform)
                    {
                        EndRenderPass();
                        unsafe
                        {
                            // 修正：使用正确的API
                            Gd.Api.EndCommandBuffer(Cbs.CommandBuffer);
                            
                            // 创建临时变量以获取指针
                            CommandBuffer commandBuffer = Cbs.CommandBuffer;
                            var submitInfo = new SubmitInfo
                            {
                                SType = StructureType.SubmitInfo,
                                CommandBufferCount = 1,
                                PCommandBuffers = &commandBuffer
                            };
                            
                            Gd.Api.QueueSubmit(Gd.Queue, 1, &submitInfo, default);
                            Gd.Api.QueueWaitIdle(Gd.Queue);
                            
                            // 修正：使用正确的API
                            var beginInfo = new CommandBufferBeginInfo
                            {
                                SType = StructureType.CommandBufferBeginInfo,
                                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
                            };
                            
                            Gd.Api.BeginCommandBuffer(commandBuffer, beginInfo);
                            
                            Logger.Debug?.Print(LogClass.Gpu, "TBDR: Flushed and waited for query batch completion");
                        }
                    }
                }
                
                _batchQueryCount = 0;
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
            ProcessQueryBatch(true);
            CopyPendingBatchQueries();
            CopyPendingQuery();
        }
        
        public void OptimizeBatchQueries()
        {
            if (!_isTbdrPlatform) return;
            
            var counters = Gd.GetCounters();
            if (counters == null) return;
            
            var allBatches = counters.CollectAllBatchQueries();
            
            if (allBatches.Count > 0)
            {
                var optimizedBatches = OptimizeBatchGroups(allBatches);
                
                lock (_batchLock)
                {
                    _pendingBatchCopies.Clear();
                    _batchQueriesToMarkReady.Clear();
                    foreach (var batch in optimizedBatches)
                    {
                        var counterType = GetCounterTypeFromPool(batch.QueryPool);
                        if (!_pendingBatchCopies.ContainsKey(counterType))
                        {
                            _pendingBatchCopies[counterType] = new List<QueryBatch>();
                            _batchQueriesToMarkReady[counterType] = new List<BufferedQuery>();
                        }
                        _pendingBatchCopies[counterType].Add(batch);
                    }
                }
                
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"TBDR: Optimized {allBatches.Count} batches into {optimizedBatches.Count} groups");
            }
        }
        
        private List<QueryBatch> OptimizeBatchGroups(List<QueryBatch> batches)
        {
            var optimized = new List<QueryBatch>();
            
            batches.Sort((a, b) => 
            {
                int poolCompare = a.QueryPool.Handle.CompareTo(b.QueryPool.Handle);
                if (poolCompare != 0) return poolCompare;
                return a.ResultBuffer.Handle.CompareTo(b.ResultBuffer.Handle);
            });
            
            int i = 0;
            while (i < batches.Count)
            {
                var current = batches[i];
                int j = i + 1;
                
                while (j < batches.Count && 
                       batches[j].QueryPool.Handle == current.QueryPool.Handle &&
                       batches[j].ResultBuffer.Handle == current.ResultBuffer.Handle &&
                       batches[j].Is64Bit == current.Is64Bit &&
                       batches[j].StartIndex == current.StartIndex + current.Count &&
                       batches[j].ResultOffset == current.ResultOffset + 
                        (ulong)(current.Is64Bit ? sizeof(long) : sizeof(int)) * current.Count)
                {
                    current = new QueryBatch(
                        current.QueryPool,
                        current.StartIndex,
                        current.Count + batches[j].Count,
                        current.ResultBuffer,
                        current.ResultOffset,
                        current.Is64Bit);
                    j++;
                }
                
                optimized.Add(current);
                i = j;
            }
            
            return optimized;
        }
        
        private CounterType GetCounterTypeFromPool(QueryPool pool)
        {
            return CounterType.SamplesPassed;
        }
    }
}