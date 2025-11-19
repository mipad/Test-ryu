using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Vulkan
{
    internal class BarrierBatch : IDisposable
    {
        private const int MaxBarriersPerCall = 16;

        private const AccessFlags BaseAccess = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
        private const AccessFlags BufferAccess = AccessFlags.IndexReadBit | AccessFlags.VertexAttributeReadBit | AccessFlags.UniformReadBit;
        private const AccessFlags CommandBufferAccess = AccessFlags.IndirectCommandReadBit;

        private readonly VulkanRenderer _gd;

        private readonly NativeArray<MemoryBarrier> _memoryBarrierBatch = new(MaxBarriersPerCall);
        private readonly NativeArray<BufferMemoryBarrier> _bufferBarrierBatch = new(MaxBarriersPerCall);
        private readonly NativeArray<ImageMemoryBarrier> _imageBarrierBatch = new(MaxBarriersPerCall);

        private readonly List<BarrierWithStageFlags<MemoryBarrier, int>> _memoryBarriers = [];
        private readonly List<BarrierWithStageFlags<BufferMemoryBarrier, int>> _bufferBarriers = [];
        private readonly List<BarrierWithStageFlags<ImageMemoryBarrier, TextureStorage>> _imageBarriers = [];
        private int _queuedBarrierCount;

        private enum IncoherentBarrierType
        {
            None,
            Texture,
            All,
            CommandBuffer
        }

        private bool _feedbackLoopActive;
        private PipelineStageFlags _incoherentBufferWriteStages;
        private PipelineStageFlags _incoherentTextureWriteStages;
        private PipelineStageFlags _extraStages;
        private IncoherentBarrierType _queuedIncoherentBarrier;
        private bool _queuedFeedbackLoopBarrier;

        public BarrierBatch(VulkanRenderer gd)
        {
            _gd = gd;
        }

        // 添加 Tile-based GPU 检测
        private bool IsTileBasedGPU => _gd.IsTileBasedGPU;
        private TileOptimizationConfig TileConfig => _gd.TileOptimizationConfig;

        public static (AccessFlags Access, PipelineStageFlags Stages) GetSubpassAccessSuperset(VulkanRenderer gd)
        {
            AccessFlags access = BufferAccess;
            PipelineStageFlags stages = PipelineStageFlags.AllGraphicsBit;

            if (gd.TransformFeedbackApi != null)
            {
                access |= AccessFlags.TransformFeedbackWriteBitExt;
                stages |= PipelineStageFlags.TransformFeedbackBitExt;
            }

            // Tile-based GPU 优化：减少不必要的访问标志
            if (gd.IsTileBasedGPU && gd.TileOptimizationConfig.OptimizeBarriers)
            {
                // 在 Tile 架构上，使用更精确的访问模式
                access = AccessFlags.ColorAttachmentWriteBit | 
                         AccessFlags.DepthStencilAttachmentWriteBit;
                         
                stages = PipelineStageFlags.ColorAttachmentOutputBit | 
                         PipelineStageFlags.EarlyFragmentTestsBit | 
                         PipelineStageFlags.LateFragmentTestsBit;
            }

            return (access, stages);
        }

        // 添加 Tile 优化的阶段标志获取
        private PipelineStageFlags GetTileOptimizedStages(PipelineStageFlags stages, bool inRenderPass)
        {
            if (!IsTileBasedGPU || !TileConfig.OptimizeBarriers) return stages;
            
            var optimizedStages = stages;
            
            // 移除在 Tile 架构上不必要的阶段
            optimizedStages &= ~PipelineStageFlags.HostBit;
            optimizedStages &= ~PipelineStageFlags.AllCommandsBit;
            
            if (inRenderPass)
            {
                // 在渲染通道内，限制为图形相关阶段
                optimizedStages &= PipelineStageFlags.AllGraphicsBit;
            }
            
            return optimizedStages;
        }

        // 添加 Tile 优化的访问标志获取
        private AccessFlags GetTileOptimizedAccess(AccessFlags access, bool inRenderPass)
        {
            if (!IsTileBasedGPU || !TileConfig.OptimizeBarriers) return access;
            
            var optimizedAccess = access;
            
            // 移除在 Tile 架构上不必要的访问标志
            optimizedAccess &= ~AccessFlags.MemoryReadBit;
            optimizedAccess &= ~AccessFlags.MemoryWriteBit;
            
            if (inRenderPass)
            {
                // 在渲染通道内，限制为附件相关访问
                optimizedAccess &= (AccessFlags.ColorAttachmentReadBit | 
                                   AccessFlags.ColorAttachmentWriteBit |
                                   AccessFlags.DepthStencilAttachmentReadBit | 
                                   AccessFlags.DepthStencilAttachmentWriteBit);
            }
            
            return optimizedAccess;
        }

        private readonly record struct StageFlags : IEquatable<StageFlags>
        {
            public readonly PipelineStageFlags Source;
            public readonly PipelineStageFlags Dest;

            public StageFlags(PipelineStageFlags source, PipelineStageFlags dest)
            {
                Source = source;
                Dest = dest;
            }
        }

        private readonly struct BarrierWithStageFlags<T, T2> where T : unmanaged
        {
            public readonly StageFlags Flags;
            public readonly T Barrier;
            public readonly T2 Resource;

            public BarrierWithStageFlags(StageFlags flags, T barrier)
            {
                Flags = flags;
                Barrier = barrier;
                Resource = default;
            }

            public BarrierWithStageFlags(PipelineStageFlags srcStageFlags, PipelineStageFlags dstStageFlags, T barrier, T2 resource)
            {
                Flags = new StageFlags(srcStageFlags, dstStageFlags);
                Barrier = barrier;
                Resource = resource;
            }
        }

        private void QueueBarrier<T, T2>(List<BarrierWithStageFlags<T, T2>> list, T barrier, T2 resource, PipelineStageFlags srcStageFlags, PipelineStageFlags dstStageFlags) where T : unmanaged
        {
            // Tile-based GPU 优化：优化阶段和访问标志
            if (IsTileBasedGPU && TileConfig.OptimizeBarriers)
            {
                srcStageFlags = GetTileOptimizedStages(srcStageFlags, false);
                dstStageFlags = GetTileOptimizedStages(dstStageFlags, false);
                
                // 优化屏障的访问标志
                if (barrier is ImageMemoryBarrier imageBarrier)
                {
                    imageBarrier.SrcAccessMask = GetTileOptimizedAccess(imageBarrier.SrcAccessMask, false);
                    imageBarrier.DstAccessMask = GetTileOptimizedAccess(imageBarrier.DstAccessMask, false);
                }
                else if (barrier is BufferMemoryBarrier bufferBarrier)
                {
                    bufferBarrier.SrcAccessMask = GetTileOptimizedAccess(bufferBarrier.SrcAccessMask, false);
                    bufferBarrier.DstAccessMask = GetTileOptimizedAccess(bufferBarrier.DstAccessMask, false);
                }
                else if (barrier is MemoryBarrier memoryBarrier)
                {
                    memoryBarrier.SrcAccessMask = GetTileOptimizedAccess(memoryBarrier.SrcAccessMask, false);
                    memoryBarrier.DstAccessMask = GetTileOptimizedAccess(memoryBarrier.DstAccessMask, false);
                }
            }

            list.Add(new BarrierWithStageFlags<T, T2>(srcStageFlags, dstStageFlags, barrier, resource));
            _queuedBarrierCount++;
        }

        public void QueueBarrier(MemoryBarrier barrier, PipelineStageFlags srcStageFlags, PipelineStageFlags dstStageFlags)
        {
            QueueBarrier(_memoryBarriers, barrier, default, srcStageFlags, dstStageFlags);
        }

        public void QueueBarrier(BufferMemoryBarrier barrier, PipelineStageFlags srcStageFlags, PipelineStageFlags dstStageFlags)
        {
            QueueBarrier(_bufferBarriers, barrier, default, srcStageFlags, dstStageFlags);
        }

        public void QueueBarrier(ImageMemoryBarrier barrier, TextureStorage resource, PipelineStageFlags srcStageFlags, PipelineStageFlags dstStageFlags)
        {
            QueueBarrier(_imageBarriers, barrier, resource, srcStageFlags, dstStageFlags);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void FlushMemoryBarrier(ShaderCollection program, bool inRenderPass)
        {
            if (_queuedIncoherentBarrier > IncoherentBarrierType.None)
            {
                // We should emit a memory barrier if there's a write access in the program (current program, or program since last barrier)
                bool hasTextureWrite = _incoherentTextureWriteStages != PipelineStageFlags.None;
                bool hasBufferWrite = _incoherentBufferWriteStages != PipelineStageFlags.None;
                bool hasBufferBarrier = _queuedIncoherentBarrier > IncoherentBarrierType.Texture;

                if (hasTextureWrite || (hasBufferBarrier && hasBufferWrite))
                {
                    AccessFlags access = BaseAccess;

                    PipelineStageFlags stages = inRenderPass ? PipelineStageFlags.AllGraphicsBit : PipelineStageFlags.AllCommandsBit;

                    // Tile-based GPU 优化：优化访问标志和阶段
                    if (IsTileBasedGPU && TileConfig.OptimizeBarriers)
                    {
                        stages = GetTileOptimizedStages(stages, inRenderPass);
                        access = GetTileOptimizedAccess(access, inRenderPass);
                    }

                    if (hasBufferBarrier && hasBufferWrite)
                    {
                        access |= BufferAccess;

                        if (_gd.TransformFeedbackApi != null)
                        {
                            access |= AccessFlags.TransformFeedbackWriteBitExt;
                            stages |= PipelineStageFlags.TransformFeedbackBitExt;
                        }
                    }

                    if (_queuedIncoherentBarrier == IncoherentBarrierType.CommandBuffer)
                    {
                        access |= CommandBufferAccess;
                        stages |= PipelineStageFlags.DrawIndirectBit;
                    }

                    MemoryBarrier barrier = new()
                    {
                        SType = StructureType.MemoryBarrier,
                        SrcAccessMask = access,
                        DstAccessMask = access
                    };

                    QueueBarrier(barrier, stages, stages);

                    _incoherentTextureWriteStages = program?.IncoherentTextureWriteStages ?? PipelineStageFlags.None;

                    if (_queuedIncoherentBarrier > IncoherentBarrierType.Texture)
                    {
                        if (program != null)
                        {
                            _incoherentBufferWriteStages = program.IncoherentBufferWriteStages | _extraStages;
                        }
                        else
                        {
                            _incoherentBufferWriteStages = PipelineStageFlags.None;
                        }
                    }

                    _queuedIncoherentBarrier = IncoherentBarrierType.None;
                    _queuedFeedbackLoopBarrier = false;
                }
                else if (_feedbackLoopActive && _queuedFeedbackLoopBarrier)
                {
                    // Feedback loop barrier.

                    MemoryBarrier barrier = new()
                    {
                        SType = StructureType.MemoryBarrier,
                        SrcAccessMask = AccessFlags.ShaderWriteBit,
                        DstAccessMask = AccessFlags.ShaderReadBit
                    };

                    // Tile-based GPU 优化：优化反馈循环屏障
                    if (IsTileBasedGPU && TileConfig.OptimizeBarriers)
                    {
                        barrier.SrcAccessMask = GetTileOptimizedAccess(barrier.SrcAccessMask, true);
                        barrier.DstAccessMask = GetTileOptimizedAccess(barrier.DstAccessMask, true);
                    }

                    QueueBarrier(barrier, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.AllGraphicsBit);

                    _queuedFeedbackLoopBarrier = false;
                }

                _feedbackLoopActive = false;
            }
        }

        public unsafe void Flush(CommandBufferScoped cbs, bool inRenderPass, RenderPassHolder rpHolder, Action endRenderPass)
        {
            Flush(cbs, null, false, inRenderPass, rpHolder, endRenderPass);
        }

        public unsafe void Flush(CommandBufferScoped cbs, ShaderCollection program, bool feedbackLoopActive, bool inRenderPass, RenderPassHolder rpHolder, Action endRenderPass)
        {
            if (program != null)
            {
                _incoherentBufferWriteStages |= program.IncoherentBufferWriteStages | _extraStages;
                _incoherentTextureWriteStages |= program.IncoherentTextureWriteStages;
            }

            _feedbackLoopActive |= feedbackLoopActive;

            FlushMemoryBarrier(program, inRenderPass);

            if (!inRenderPass && rpHolder != null)
            {
                // Render pass is about to begin. Queue any fences that normally interrupt the pass.
                rpHolder.InsertForcedFences(cbs);
            }

            while (_queuedBarrierCount > 0)
            {
                int memoryCount = 0;
                int bufferCount = 0;
                int imageCount = 0;

                bool hasBarrier = false;
                StageFlags flags = default;

                static void AddBarriers<T, T2>(
                    Span<T> target,
                    ref int queuedBarrierCount,
                    ref bool hasBarrier,
                    ref StageFlags flags,
                    ref int count,
                    List<BarrierWithStageFlags<T, T2>> list) where T : unmanaged
                {
                    int firstMatch = -1;
                    int end = list.Count;

                    for (int i = 0; i < list.Count; i++)
                    {
                        BarrierWithStageFlags<T, T2> barrier = list[i];

                        if (!hasBarrier)
                        {
                            flags = barrier.Flags;
                            hasBarrier = true;

                            target[count++] = barrier.Barrier;
                            queuedBarrierCount--;
                            firstMatch = i;

                            if (count >= target.Length)
                            {
                                end = i + 1;
                                break;
                            }
                        }
                        else
                        {
                            if (flags.Equals(barrier.Flags))
                            {
                                target[count++] = barrier.Barrier;
                                queuedBarrierCount--;

                                if (firstMatch == -1)
                                {
                                    firstMatch = i;
                                }

                                if (count >= target.Length)
                                {
                                    end = i + 1;
                                    break;
                                }
                            }
                            else
                            {
                                // Delete consumed barriers from the first match to the current non-match.
                                if (firstMatch != -1)
                                {
                                    int deleteCount = i - firstMatch;
                                    list.RemoveRange(firstMatch, deleteCount);
                                    i -= deleteCount;

                                    firstMatch = -1;
                                    end = list.Count;
                                }
                            }
                        }
                    }

                    if (firstMatch == 0 && end == list.Count)
                    {
                        list.Clear();
                    }
                    else if (firstMatch != -1)
                    {
                        int deleteCount = end - firstMatch;

                        list.RemoveRange(firstMatch, deleteCount);
                    }
                }

                if (inRenderPass && _imageBarriers.Count > 0)
                {
                    // Image barriers queued in the batch are meant to be globally scoped,
                    // but inside a render pass they're scoped to just the range of the render pass.

                    // Tile-based GPU 优化：特殊的图像屏障处理
                    if (IsTileBasedGPU && TileConfig.OptimizeBarriers)
                    {
                        if (TryConvertImageBarriersForTile(_imageBarriers, rpHolder))
                        {
                            // 成功转换为 Tile 优化的屏障，不需要结束渲染通道
                        }
                        else if (!_gd.IsMoltenVk)
                        {
                            // 必须结束渲染通道的情况
                            endRenderPass();
                            inRenderPass = false;
                        }
                    }
                    else
                    {
                        // 原有的非 Tile 优化处理逻辑
                        // On MoltenVK, we just break the rules and always use image barrier.
                        // On desktop GPUs, all barriers are globally scoped, so we just replace it with a generic memory barrier.
                        // Generally, we want to avoid this from happening in the future, so flag the texture to immediately
                        // emit a barrier whenever the current render pass is bound again.

                        bool anyIsNonAttachment = false;

                        foreach (BarrierWithStageFlags<ImageMemoryBarrier, TextureStorage> barrier in _imageBarriers)
                        {
                            // If the binding is an attachment, don't add it as a forced fence.
                            bool isAttachment = rpHolder.ContainsAttachment(barrier.Resource);

                            if (!isAttachment)
                            {
                                rpHolder.AddForcedFence(barrier.Resource, barrier.Flags.Dest);
                                anyIsNonAttachment = true;
                            }
                        }

                        if (_gd.IsTBDR)
                        {
                            if (!_gd.IsMoltenVk)
                            {
                                if (!anyIsNonAttachment)
                                {
                                    // This case is a feedback loop. To prevent this from causing an absolute performance disaster,
                                    // remove the barriers entirely.
                                    // If this is not here, there will be a lot of single draw render passes.
                                    // TODO: explicit handling for feedback loops, likely outside this class.

                                    _queuedBarrierCount -= _imageBarriers.Count;
                                    _imageBarriers.Clear();
                                }
                                else
                                {
                                    // TBDR GPUs are sensitive to barriers, so we need to end the pass to ensure the data is available.
                                    // Metal already has hazard tracking so MVK doesn't need this.
                                    endRenderPass();
                                    inRenderPass = false;
                                }
                            }
                        }
                        else
                        {
                            // Generic pipeline memory barriers will work for desktop GPUs.
                            // They do require a few more access flags on the subpass dependency, though.
                            foreach (var barrier in _imageBarriers)
                            {
                                _memoryBarriers.Add(new BarrierWithStageFlags<MemoryBarrier, int>(
                                    barrier.Flags,
                                    new MemoryBarrier()
                                    {
                                        SType = StructureType.MemoryBarrier,
                                        SrcAccessMask = barrier.Barrier.SrcAccessMask,
                                        DstAccessMask = barrier.Barrier.DstAccessMask
                                    }));
                            }

                            _imageBarriers.Clear();
                        }
                    }
                }

                if (inRenderPass && _memoryBarriers.Count > 0)
                {
                    PipelineStageFlags allFlags = PipelineStageFlags.None;

                    foreach (var barrier in _memoryBarriers)
                    {
                        allFlags |= barrier.Flags.Dest;
                    }

                    // Tile-based GPU 优化：检查是否支持渲染通道内屏障
                    if (IsTileBasedGPU)
                    {
                        if (!_gd.SupportsRenderPassBarrier(allFlags))
                        {
                            endRenderPass();
                            inRenderPass = false;
                        }
                    }
                    else if (allFlags.HasFlag(PipelineStageFlags.DrawIndirectBit) || !_gd.SupportsRenderPassBarrier(allFlags))
                    {
                        endRenderPass();
                        inRenderPass = false;
                    }
                }

                AddBarriers(_memoryBarrierBatch.AsSpan(), ref _queuedBarrierCount, ref hasBarrier, ref flags, ref memoryCount, _memoryBarriers);
                AddBarriers(_bufferBarrierBatch.AsSpan(), ref _queuedBarrierCount, ref hasBarrier, ref flags, ref bufferCount, _bufferBarriers);
                AddBarriers(_imageBarrierBatch.AsSpan(), ref _queuedBarrierCount, ref hasBarrier, ref flags, ref imageCount, _imageBarriers);

                if (hasBarrier)
                {
                    PipelineStageFlags srcStageFlags = flags.Source;
                    PipelineStageFlags dstStageFlags = flags.Dest;

                    // Tile-based GPU 优化：调整阶段标志
                    if (IsTileBasedGPU && TileConfig.OptimizeBarriers)
                    {
                        srcStageFlags = GetTileOptimizedStages(srcStageFlags, inRenderPass);
                        dstStageFlags = GetTileOptimizedStages(dstStageFlags, inRenderPass);
                    }

                    if (inRenderPass)
                    {
                        // 在渲染通道内，屏障阶段只能来自光栅化
                        srcStageFlags &= ~PipelineStageFlags.ComputeShaderBit;
                    }

                    _gd.Api.CmdPipelineBarrier(
                        cbs.CommandBuffer,
                        srcStageFlags,
                        dstStageFlags,
                        0,
                        (uint)memoryCount,
                        _memoryBarrierBatch.Pointer,
                        (uint)bufferCount,
                        _bufferBarrierBatch.Pointer,
                        (uint)imageCount,
                        _imageBarrierBatch.Pointer);
                }
            }
        }

        // Tile-based GPU 优化：尝试转换图像屏障
        private bool TryConvertImageBarriersForTile(List<BarrierWithStageFlags<ImageMemoryBarrier, TextureStorage>> imageBarriers, RenderPassHolder rpHolder)
        {
            if (!IsTileBasedGPU || !TileConfig.PreferMemoryBarriers) return false;

            bool allConvertible = true;

            foreach (var barrier in imageBarriers)
            {
                // 检查是否可以将图像屏障转换为内存屏障
                if (!CanConvertImageToMemoryBarrier(barrier, rpHolder))
                {
                    allConvertible = false;
                    break;
                }
            }

            if (allConvertible)
            {
                ConvertImageBarriersToMemoryBarriers(imageBarriers);
                return true;
            }

            return false;
        }

        // 检查图像屏障是否可以转换为内存屏障
        private bool CanConvertImageToMemoryBarrier(BarrierWithStageFlags<ImageMemoryBarrier, TextureStorage> barrier, RenderPassHolder rpHolder)
        {
            // 如果图像是当前渲染通道的附件，可能需要特殊处理
            if (rpHolder != null && rpHolder.ContainsAttachment(barrier.Resource))
            {
                // 检查访问模式是否适合转换
                var srcAccess = barrier.Barrier.SrcAccessMask;
                var dstAccess = barrier.Barrier.DstAccessMask;
                
                // 如果是附件写入，可能不适合转换
                if ((srcAccess & (AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit)) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        // 将图像屏障转换为内存屏障
        private void ConvertImageBarriersToMemoryBarriers(List<BarrierWithStageFlags<ImageMemoryBarrier, TextureStorage>> imageBarriers)
        {
            foreach (var barrier in imageBarriers)
            {
                var memoryBarrier = new MemoryBarrier()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = GetTileOptimizedAccess(barrier.Barrier.SrcAccessMask, true),
                    DstAccessMask = GetTileOptimizedAccess(barrier.Barrier.DstAccessMask, true)
                };
                
                _memoryBarriers.Add(new BarrierWithStageFlags<MemoryBarrier, int>(
                    barrier.Flags, memoryBarrier));
            }

            _queuedBarrierCount -= imageBarriers.Count;
            imageBarriers.Clear();
        }

        private void QueueIncoherentBarrier(IncoherentBarrierType type)
        {
            if (type > _queuedIncoherentBarrier)
            {
                _queuedIncoherentBarrier = type;
            }

            _queuedFeedbackLoopBarrier = true;
        }

        public void QueueTextureBarrier()
        {
            QueueIncoherentBarrier(IncoherentBarrierType.Texture);
        }

        public void QueueMemoryBarrier()
        {
            QueueIncoherentBarrier(IncoherentBarrierType.All);
        }

        public void QueueCommandBufferBarrier()
        {
            QueueIncoherentBarrier(IncoherentBarrierType.CommandBuffer);
        }

        public void EnableTfbBarriers(bool enable)
        {
            if (enable)
            {
                _extraStages |= PipelineStageFlags.TransformFeedbackBitExt;
            }
            else
            {
                _extraStages &= ~PipelineStageFlags.TransformFeedbackBitExt;
            }
        }

        public void Dispose()
        {
            _memoryBarrierBatch.Dispose();
            _bufferBarrierBatch.Dispose();
            _imageBarrierBatch.Dispose();
        }
    }
}