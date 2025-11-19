using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Graphics.Vulkan
{
    internal class RenderPassHolder
    {
        private readonly struct FramebufferCacheKey : IRefEquatable<FramebufferCacheKey>
        {
            private readonly uint _width;
            private readonly uint _height;
            private readonly uint _layers;

            public FramebufferCacheKey(uint width, uint height, uint layers)
            {
                _width = width;
                _height = height;
                _layers = layers;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_width, _height, _layers);
            }

            public bool Equals(ref FramebufferCacheKey other)
            {
                return other._width == _width && other._height == _height && other._layers == _layers;
            }
        }

        private readonly record struct ForcedFence(TextureStorage Texture, PipelineStageFlags StageFlags);

        private readonly TextureView[] _textures;
        private readonly Auto<DisposableRenderPass> _renderPass;
        private readonly HashTableSlim<FramebufferCacheKey, Auto<DisposableFramebuffer>> _framebuffers;
        private readonly RenderPassCacheKey _key;
        private readonly List<ForcedFence> _forcedFences;

        // 添加 Tile 优化相关字段
        private readonly bool _isTileBasedGPU;
        private readonly TileOptimizationConfig _tileConfig;

        public unsafe RenderPassHolder(VulkanRenderer gd, Device device, RenderPassCacheKey key, FramebufferParams fb)
        {
            // 初始化 Tile 优化相关字段
            _isTileBasedGPU = gd.IsTileBasedGPU;
            _tileConfig = gd.TileOptimizationConfig;

            // Create render pass using framebuffer params.

            const int MaxAttachments = Constants.MaxRenderTargets + 1;

            AttachmentDescription[] attachmentDescs = null;

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
            };

            AttachmentReference* attachmentReferences = stackalloc AttachmentReference[MaxAttachments];

            var hasFramebuffer = fb != null;

            if (hasFramebuffer && fb.AttachmentsCount != 0)
            {
                attachmentDescs = new AttachmentDescription[fb.AttachmentsCount];

                for (int i = 0; i < fb.AttachmentsCount; i++)
                {
                    var loadOp = AttachmentLoadOp.Load;
                    var storeOp = AttachmentStoreOp.Store;
                    var stencilLoadOp = AttachmentLoadOp.Load;
                    var stencilStoreOp = AttachmentStoreOp.Store;
                    var initialLayout = ImageLayout.General;
                    var finalLayout = ImageLayout.General;

                    // Tile-based GPU 优化
                    if (_isTileBasedGPU && _tileConfig.OptimizeAttachmentOperations)
                    {
                        // 优化布局
                        if (_tileConfig.UseAttachmentOptimalLayouts)
                        {
                            initialLayout = ImageLayout.AttachmentOptimal;
                            finalLayout = ImageLayout.AttachmentOptimal;
                        }

                        // 颜色附件优化
                        if (i < fb.ColorAttachmentsCount)
                        {
                            // 使用 Clear 而不是 Load 来避免内存读取
                            loadOp = AttachmentLoadOp.Clear;
                            
                            // 优化存储操作
                            if (ShouldOptimizeColorStore(fb, i))
                            {
                                storeOp = AttachmentStoreOp.DontCare;
                            }
                        }
                        // 深度模板附件优化
                        else if (fb.HasDepthStencil && i == fb.AttachmentsCount - 1)
                        {
                            // 深度附件通常不需要存储到内存
                            if (_tileConfig.AggressiveStoreOpDontCare)
                            {
                                storeOp = AttachmentStoreOp.DontCare;
                                stencilStoreOp = AttachmentStoreOp.DontCare;
                            }
                            
                            // 使用 Clear 优化深度加载
                            loadOp = AttachmentLoadOp.Clear;
                            stencilLoadOp = AttachmentLoadOp.Clear;
                        }
                    }

                    attachmentDescs[i] = new AttachmentDescription(
                        0,
                        fb.AttachmentFormats[i],
                        TextureStorage.ConvertToSampleCountFlags(gd.Capabilities.SupportedSampleCounts, fb.AttachmentSamples[i]),
                        loadOp,           // 优化后的 LoadOp
                        storeOp,          // 优化后的 StoreOp
                        stencilLoadOp,
                        stencilStoreOp,
                        initialLayout,    // 优化后的初始布局
                        finalLayout);     // 优化后的最终布局
                }

                int colorAttachmentsCount = fb.ColorAttachmentsCount;

                if (colorAttachmentsCount > MaxAttachments - 1)
                {
                    colorAttachmentsCount = MaxAttachments - 1;
                }

                if (colorAttachmentsCount != 0)
                {
                    int maxAttachmentIndex = fb.MaxColorAttachmentIndex;
                    subpass.ColorAttachmentCount = (uint)maxAttachmentIndex + 1;
                    subpass.PColorAttachments = &attachmentReferences[0];

                    // Fill with VK_ATTACHMENT_UNUSED to cover any gaps.
                    for (int i = 0; i <= maxAttachmentIndex; i++)
                    {
                        subpass.PColorAttachments[i] = new AttachmentReference(Vk.AttachmentUnused, ImageLayout.Undefined);
                    }

                    for (int i = 0; i < colorAttachmentsCount; i++)
                    {
                        int bindIndex = fb.AttachmentIndices[i];

                        var imageLayout = ImageLayout.General;
                        if (_isTileBasedGPU && _tileConfig.UseAttachmentOptimalLayouts)
                        {
                            imageLayout = ImageLayout.ColorAttachmentOptimal;
                        }

                        subpass.PColorAttachments[bindIndex] = new AttachmentReference((uint)i, imageLayout);
                    }
                }

                if (fb.HasDepthStencil)
                {
                    uint dsIndex = (uint)fb.AttachmentsCount - 1;

                    var imageLayout = ImageLayout.General;
                    if (_isTileBasedGPU && _tileConfig.UseAttachmentOptimalLayouts)
                    {
                        imageLayout = ImageLayout.DepthStencilAttachmentOptimal;
                    }

                    subpass.PDepthStencilAttachment = &attachmentReferences[MaxAttachments - 1];
                    *subpass.PDepthStencilAttachment = new AttachmentReference(dsIndex, imageLayout);
                }
            }

            // 使用 Tile 优化的子通道依赖
            var subpassDependency = CreateTileOptimizedSubpassDependency(gd);

            fixed (AttachmentDescription* pAttachmentDescs = attachmentDescs)
            {
                var renderPassCreateInfo = new RenderPassCreateInfo
                {
                    SType = StructureType.RenderPassCreateInfo,
                    PAttachments = pAttachmentDescs,
                    AttachmentCount = attachmentDescs != null ? (uint)attachmentDescs.Length : 0,
                    PSubpasses = &subpass,
                    SubpassCount = 1,
                    PDependencies = &subpassDependency,
                    DependencyCount = 1,
                };

                gd.Api.CreateRenderPass(device, in renderPassCreateInfo, null, out var renderPass).ThrowOnError();

                _renderPass = new Auto<DisposableRenderPass>(new DisposableRenderPass(gd.Api, device, renderPass));
            }

            _framebuffers = new HashTableSlim<FramebufferCacheKey, Auto<DisposableFramebuffer>>();

            // Register this render pass with all render target views.

            var textures = fb.GetAttachmentViews();

            foreach (var texture in textures)
            {
                texture.AddRenderPass(key, this);
            }

            _textures = textures;
            _key = key;

            _forcedFences = new List<ForcedFence>();
        }

        // 判断是否应该优化颜色附件存储
        private bool ShouldOptimizeColorStore(FramebufferParams fb, int attachmentIndex)
        {
            if (!_isTileBasedGPU || !_tileConfig.OptimizeAttachmentOperations) return false;

            // 激进模式下优化所有颜色附件
            if (_tileConfig.AggressiveStoreOpDontCare) return true;

            // 保守模式下只优化中间渲染目标
            return attachmentIndex < fb.ColorAttachmentsCount - 1; // 不是最后一个颜色附件
        }

        // 创建 Tile 优化的子通道依赖
        private SubpassDependency CreateTileOptimizedSubpassDependency(VulkanRenderer gd)
        {
            var (access, stages) = BarrierBatch.GetSubpassAccessSuperset(gd);
            
            var dependencyFlags = DependencyFlags.None;
            
            // Tile-based GPU 优化：使用区域依赖
            if (_isTileBasedGPU && _tileConfig.OptimizeDependencies)
            {
                dependencyFlags = DependencyFlags.ByRegionBit;
                
                // 在 Tile 架构上，优化阶段和访问标志
                stages &= PipelineStageFlags.AllGraphicsBit;
                access &= (AccessFlags.ColorAttachmentReadBit | 
                          AccessFlags.ColorAttachmentWriteBit |
                          AccessFlags.DepthStencilAttachmentReadBit | 
                          AccessFlags.DepthStencilAttachmentWriteBit);
            }
    
            return new SubpassDependency(
                0,
                0,
                stages,
                stages,
                access,
                access,
                dependencyFlags);
        }

        public Auto<DisposableFramebuffer> GetFramebuffer(VulkanRenderer gd, CommandBufferScoped cbs, FramebufferParams fb)
        {
            var key = new FramebufferCacheKey(fb.Width, fb.Height, fb.Layers);

            if (!_framebuffers.TryGetValue(ref key, out Auto<DisposableFramebuffer> result))
            {
                result = fb.Create(gd.Api, cbs, _renderPass);

                _framebuffers.Add(ref key, result);
            }

            return result;
        }

        public Auto<DisposableRenderPass> GetRenderPass()
        {
            return _renderPass;
        }

        public void AddForcedFence(TextureStorage storage, PipelineStageFlags stageFlags)
        {
            if (!_forcedFences.Any(fence => fence.Texture == storage))
            {
                _forcedFences.Add(new ForcedFence(storage, stageFlags));
            }
        }

        public void InsertForcedFences(CommandBufferScoped cbs)
        {
            if (_forcedFences.Count > 0)
            {
                _forcedFences.RemoveAll((entry) =>
                {
                    if (entry.Texture.Disposed)
                    {
                        return true;
                    }

                    entry.Texture.QueueWriteToReadBarrier(cbs, AccessFlags.ShaderReadBit, entry.StageFlags);

                    return false;
                });
            }
        }

        public bool ContainsAttachment(TextureStorage storage)
        {
            return _textures.Any(view => view.Storage == storage);
        }

        public void Dispose()
        {
            // Dispose all framebuffers.

            foreach (var fb in _framebuffers.Values)
            {
                fb.Dispose();
            }

            // Notify all texture views that this render pass has been disposed.

            foreach (var texture in _textures)
            {
                texture.RemoveRenderPass(_key);
            }

            // Dispose render pass.

            _renderPass.Dispose();
        }
    }
}