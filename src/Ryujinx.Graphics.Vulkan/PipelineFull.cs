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

        // 批量查询处理
        private readonly Queue<QueryBatch> _queryBatchQueue = new();
        private int _batchQueryCount = 0;
        private readonly bool _isTbdrPlatform;
        private readonly int _targetBatchSize;
        private readonly object _batchLock = new();
        
        // 批量结果缓冲区管理
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
            // 批量复制所有查询结果
            foreach (var (query, index) in _pendingQueryCopies)
            {
                query.PoolCopy(Cbs, index);
            }

            _pendingQueryCopies.Clear();
        }
        
        // 新的批量复制方法
        private void CopyPendingBatchQueries()
        {
            // 处理所有待处理的批量查询
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
            
            // 标记相关的查询结果为准备就绪
            foreach (var queries in _batchQueriesToMarkReady.Values)
            {
                foreach (var query in queries)
                {
                    query.MarkBatchResultReady();
                }
            }
            
            _pendingBatchCopies.Clear();
            _batchQueriesToMarkReady.Clear();
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

            // 批量重置所有活动查询 - 16个一组
            BatchResetActiveQueries();
            
            Gd.ResetCounterPool();

            Restore();
        }
        
        // 批量重置活动查询的方法
        private void BatchResetActiveQueries()
        {
            // 按查询池和类型分组
            var groupedQueries = new Dictionary<CounterType, Dictionary<QueryPool, List<uint>>>();
            
            foreach ((var queryPool, var index, var isOcclusion) in _activeQueries)
            {
                // 确定查询类型（这里需要根据查询池确定类型）
                // 在实际实现中，需要从查询池映射到类型
                CounterType type = GetCounterTypeFromPool(queryPool, isOcclusion);
                
                if (!groupedQueries.ContainsKey(type))
                {
                    groupedQueries[type] = new Dictionary<QueryPool, List<uint>>();
                }
                
                if (!groupedQueries[type].ContainsKey(queryPool))
                {
                    groupedQueries[type][queryPool] = new List<uint>();
                }
                
                groupedQueries[type][queryPool].Add(index);
            }
            
            // 对每个类型执行批量重置
            foreach (var typeGroup in groupedQueries)
            {
                CounterType type = typeGroup.Key;
                
                // 收集所有索引并排序
                var allIndices = new List<uint>();
                foreach (var poolIndices in typeGroup.Value.Values)
                {
                    allIndices.AddRange(poolIndices);
                }
                
                allIndices.Sort();
                
                // 以16个为一组创建重置范围
                for (int i = 0; i < allIndices.Count; i += 16)
                {
                    uint startIndex = allIndices[i];
                    uint count = (uint)Math.Min(16, allIndices.Count - i);
                    
                    // 添加到批量重置队列
                    BufferedQuery.AddResetRange(type, startIndex, count);
                }
                
                // 执行批量重置
                BufferedQuery.BatchReset(CommandBuffer, type);
                
                if (_isTbdrPlatform && allIndices.Count > 0)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"TBDR: Batch reset {allIndices.Count} queries for {type} in groups of 16");
                }
            }

            // 开始每个查询（保持原有逻辑）
            foreach ((var queryPool, var index, var isOcclusion) in _activeQueries)
            {
                bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
                
                if (_isTbdrPlatform && isOcclusion)
                {
                    isPrecise = false;
                }

                Gd.Api.CmdBeginQuery(CommandBuffer, queryPool, index, isPrecise ? QueryControlFlags.PreciseBit : 0);
            }
        }
        
        // 辅助方法：从查询池获取计数器类型
        private CounterType GetCounterTypeFromPool(QueryPool pool, bool isOcclusion)
        {
            // 这里需要实现从查询池到计数器类型的映射
            // 在实际实现中，可能需要维护一个映射表
            
            // 简化实现：根据是否为遮挡查询返回类型
            return isOcclusion ? CounterType.SamplesPassed : CounterType.PrimitivesGenerated;
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

                // 对于需要重置的查询，添加到批量重置队列
                var type = query.GetCounterType();
                BufferedQuery.AddResetRange(type, index, 1);
                
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
                // 使用批量处理
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
                        
                        _pendingBatchCopies[counterType].Add(batch);
                        _batchQueriesToMarkReady[counterType].Add(query);
                        _batchQueryCount++;
                        
                        // 达到批次大小时处理
                        if (_batchQueryCount >= _targetBatchSize)
                        {
                            ProcessQueryBatch(false);
                        }
                    }
                    return;
                }
            }
            
            // 回退到单个查询处理
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
                    // 先处理批量查询
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
                    
                    // 执行复制
                    CopyPendingBatchQueries();
                    CopyPendingQuery();
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
            // 渲染过程结束时处理所有批次查询
            ProcessQueryBatch(true);
            CopyPendingBatchQueries();
            CopyPendingQuery();
        }
    }
}