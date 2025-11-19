using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using Format = Silk.NET.Vulkan.Format;
using PolygonMode = Silk.NET.Vulkan.PolygonMode;

namespace Ryujinx.Graphics.Vulkan
{
    static class PipelineConverter
    {
        // 添加 Tile-based GPU 检测方法
        private static bool IsTileBasedGPU(VulkanRenderer gd)
        {
            return gd.IsTileBasedGPU;
        }

        // 添加 Tile 优化配置获取
        private static TileOptimizationConfig GetTileOptimizationConfig(VulkanRenderer gd)
        {
            return gd.TileOptimizationConfig;
        }

        // 判断是否应该优化颜色附件存储
        private static bool ShouldOptimizeColorStore(ProgramPipelineState state, int attachmentIndex, VulkanRenderer gd)
        {
            if (!IsTileBasedGPU(gd)) return false;

            var config = GetTileOptimizationConfig(gd);
            
            if (!config.OptimizeAttachmentOperations) return false;

            // 激进模式下优化所有颜色附件
            if (config.AggressiveStoreOpDontCare) return true;

            // 保守模式下只优化中间渲染目标
            // 这里简化处理，实际可能需要更复杂的逻辑
            return attachmentIndex < state.AttachmentEnable.Length - 1; // 不是最后一个附件
        }

        // 判断是否应该优化深度附件存储
        private static bool ShouldOptimizeDepthStore(ProgramPipelineState state, VulkanRenderer gd)
        {
            if (!IsTileBasedGPU(gd)) return false;

            var config = GetTileOptimizationConfig(gd);
            return config.OptimizeAttachmentOperations;
        }

        public static unsafe DisposableRenderPass ToRenderPass(this ProgramPipelineState state, VulkanRenderer gd, Device device)
        {
            const int MaxAttachments = Constants.MaxRenderTargets + 1;

            AttachmentDescription[] attachmentDescs = null;

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
            };

            AttachmentReference* attachmentReferences = stackalloc AttachmentReference[MaxAttachments];

            Span<int> attachmentIndices = stackalloc int[MaxAttachments];
            Span<Format> attachmentFormats = stackalloc Format[MaxAttachments];

            int attachmentCount = 0;
            int colorCount = 0;
            int maxColorAttachmentIndex = -1;

            for (int i = 0; i < state.AttachmentEnable.Length; i++)
            {
                if (state.AttachmentEnable[i])
                {
                    attachmentFormats[attachmentCount] = gd.FormatCapabilities.ConvertToVkFormat(state.AttachmentFormats[i], true);
                    attachmentIndices[attachmentCount++] = i;
                    colorCount++;
                    maxColorAttachmentIndex = i;
                }
            }

            if (state.DepthStencilEnable)
            {
                attachmentFormats[attachmentCount++] = gd.FormatCapabilities.ConvertToVkFormat(state.DepthStencilFormat, true);
            }

            // 检测是否为 Tile-based GPU
            bool isTileBasedGPU = IsTileBasedGPU(gd);
            var tileConfig = GetTileOptimizationConfig(gd);

            if (attachmentCount != 0)
            {
                attachmentDescs = new AttachmentDescription[attachmentCount];

                for (int i = 0; i < attachmentCount; i++)
                {
                    var loadOp = AttachmentLoadOp.Load;
                    var storeOp = AttachmentStoreOp.Store;
                    var stencilLoadOp = AttachmentLoadOp.Load;
                    var stencilStoreOp = AttachmentStoreOp.Store;
                    var initialLayout = ImageLayout.General;
                    var finalLayout = ImageLayout.General;

                    // Tile-based GPU 优化
                    if (isTileBasedGPU && tileConfig.OptimizeAttachmentOperations)
                    {
                        // 优化布局
                        if (tileConfig.UseAttachmentOptimalLayouts)
                        {
                            initialLayout = ImageLayout.AttachmentOptimal;
                            finalLayout = ImageLayout.AttachmentOptimal;
                        }

                        // 颜色附件优化
                        if (i < colorCount)
                        {
                            // 使用 Clear 而不是 Load 来避免内存读取
                            loadOp = AttachmentLoadOp.Clear;
                            
                            // 优化存储操作
                            if (ShouldOptimizeColorStore(state, i, gd))
                            {
                                storeOp = AttachmentStoreOp.DontCare;
                            }
                        }
                        // 深度模板附件优化
                        else if (state.DepthStencilEnable && i == attachmentCount - 1)
                        {
                            // 深度附件通常不需要存储到内存
                            if (ShouldOptimizeDepthStore(state, gd))
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
                        attachmentFormats[i],
                        TextureStorage.ConvertToSampleCountFlags(gd.Capabilities.SupportedSampleCounts, (uint)state.SamplesCount),
                        loadOp,           // 优化后的 LoadOp
                        storeOp,          // 优化后的 StoreOp
                        stencilLoadOp,
                        stencilStoreOp,
                        initialLayout,    // 优化后的初始布局
                        finalLayout);     // 优化后的最终布局
                }

                int colorAttachmentsCount = colorCount;

                if (colorAttachmentsCount > MaxAttachments - 1)
                {
                    colorAttachmentsCount = MaxAttachments - 1;
                }

                if (colorAttachmentsCount != 0)
                {
                    subpass.ColorAttachmentCount = (uint)maxColorAttachmentIndex + 1;
                    subpass.PColorAttachments = &attachmentReferences[0];

                    // Fill with VK_ATTACHMENT_UNUSED to cover any gaps.
                    for (int i = 0; i <= maxColorAttachmentIndex; i++)
                    {
                        subpass.PColorAttachments[i] = new AttachmentReference(Vk.AttachmentUnused, ImageLayout.Undefined);
                    }

                    for (int i = 0; i < colorAttachmentsCount; i++)
                    {
                        int bindIndex = attachmentIndices[i];

                        var imageLayout = ImageLayout.General;
                        if (isTileBasedGPU && tileConfig.UseAttachmentOptimalLayouts)
                        {
                            imageLayout = ImageLayout.ColorAttachmentOptimal;
                        }

                        subpass.PColorAttachments[bindIndex] = new AttachmentReference((uint)i, imageLayout);
                    }
                }

                if (state.DepthStencilEnable)
                {
                    uint dsIndex = (uint)attachmentCount - 1;

                    var imageLayout = ImageLayout.General;
                    if (isTileBasedGPU && tileConfig.UseAttachmentOptimalLayouts)
                    {
                        imageLayout = ImageLayout.DepthStencilAttachmentOptimal;
                    }

                    subpass.PDepthStencilAttachment = &attachmentReferences[MaxAttachments - 1];
                    *subpass.PDepthStencilAttachment = new AttachmentReference(dsIndex, imageLayout);
                }
            }

            // 使用优化后的子通道依赖
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

                return new DisposableRenderPass(gd.Api, device, renderPass);
            }
        }

        public static SubpassDependency CreateSubpassDependency(VulkanRenderer gd)
        {
            return CreateTileOptimizedSubpassDependency(gd);
        }

        // 创建 Tile 优化的子通道依赖
        private static SubpassDependency CreateTileOptimizedSubpassDependency(VulkanRenderer gd)
        {
            var (access, stages) = BarrierBatch.GetSubpassAccessSuperset(gd);
            
            var dependencyFlags = DependencyFlags.None;
            
            // Tile-based GPU 优化：使用区域依赖
            if (IsTileBasedGPU(gd) && GetTileOptimizationConfig(gd).OptimizeDependencies)
            {
                dependencyFlags = DependencyFlags.ByRegionBit;
                
                // 在 Tile 架构上，优化阶段和访问标志
                stages &= PipelineStageFlags.AllGraphicsBit; // 只保留图形相关阶段
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
                dependencyFlags); // 添加依赖标志
        }

        public unsafe static SubpassDependency2 CreateSubpassDependency2(VulkanRenderer gd)
        {
            var (access, stages) = BarrierBatch.GetSubpassAccessSuperset(gd);
            
            var dependencyFlags = DependencyFlags.None;
            
            // Tile-based GPU 优化：使用区域依赖
            if (IsTileBasedGPU(gd) && GetTileOptimizationConfig(gd).OptimizeDependencies)
            {
                dependencyFlags = DependencyFlags.ByRegionBit;
                
                // 在 Tile 架构上，优化阶段和访问标志
                stages &= PipelineStageFlags.AllGraphicsBit;
                access &= (AccessFlags.ColorAttachmentReadBit | 
                          AccessFlags.ColorAttachmentWriteBit |
                          AccessFlags.DepthStencilAttachmentReadBit | 
                          AccessFlags.DepthStencilAttachmentWriteBit);
            }
    
            return new SubpassDependency2(
                StructureType.SubpassDependency2,
                null,
                0,
                0,
                stages,
                stages,
                access,
                access,
                dependencyFlags); // 添加依赖标志
        }

        public static PipelineState ToVulkanPipelineState(this ProgramPipelineState state, VulkanRenderer gd)
        {
            PipelineState pipeline = new();
            pipeline.Initialize();

            // It is assumed that Dynamic State is enabled when this conversion is used.

            pipeline.CullMode = state.CullEnable ? state.CullMode.Convert() : CullModeFlags.None;

            pipeline.DepthBoundsTestEnable = false; // Not implemented.

            pipeline.DepthClampEnable = state.DepthClampEnable;

            pipeline.DepthTestEnable = state.DepthTest.TestEnable;
            pipeline.DepthWriteEnable = state.DepthTest.WriteEnable;
            pipeline.DepthCompareOp = state.DepthTest.Func.Convert();
            pipeline.DepthMode = state.DepthMode == DepthMode.MinusOneToOne;

            pipeline.FrontFace = state.FrontFace.Convert();

            pipeline.HasDepthStencil = state.DepthStencilEnable;
            pipeline.LineWidth = state.LineWidth;
            pipeline.LogicOpEnable = state.LogicOpEnable;
            pipeline.LogicOp = state.LogicOp.Convert();

            pipeline.PatchControlPoints = state.PatchControlPoints;
            pipeline.PolygonMode = PolygonMode.Fill; // Not implemented.
            pipeline.PrimitiveRestartEnable = state.PrimitiveRestartEnable;
            pipeline.RasterizerDiscardEnable = state.RasterizerDiscard;
            pipeline.SamplesCount = (uint)state.SamplesCount;

            // Tile-based GPU 优化：视口和裁剪框配置
            if (gd.Capabilities.SupportsMultiView && !IsTileBasedGPU(gd))
            {
                pipeline.ScissorsCount = Constants.MaxViewports;
                pipeline.ViewportsCount = Constants.MaxViewports;
            }
            else
            {
                // Tile-based GPU 上通常使用单个视口更高效
                pipeline.ScissorsCount = 1;
                pipeline.ViewportsCount = 1;
            }

            pipeline.DepthBiasEnable = state.BiasEnable != 0;

            // Stencil masks and ref are dynamic, so are 0 in the Vulkan pipeline.

            pipeline.StencilFrontFailOp = state.StencilTest.FrontSFail.Convert();
            pipeline.StencilFrontPassOp = state.StencilTest.FrontDpPass.Convert();
            pipeline.StencilFrontDepthFailOp = state.StencilTest.FrontDpFail.Convert();
            pipeline.StencilFrontCompareOp = state.StencilTest.FrontFunc.Convert();

            pipeline.StencilBackFailOp = state.StencilTest.BackSFail.Convert();
            pipeline.StencilBackPassOp = state.StencilTest.BackDpPass.Convert();
            pipeline.StencilBackDepthFailOp = state.StencilTest.BackDpFail.Convert();
            pipeline.StencilBackCompareOp = state.StencilTest.BackFunc.Convert();

            pipeline.StencilTestEnable = state.StencilTest.TestEnable;

            pipeline.Topology = gd.TopologyRemap(state.Topology).Convert();

            int vaCount = Math.Min(Constants.MaxVertexAttributes, state.VertexAttribCount);
            int vbCount = Math.Min(Constants.MaxVertexBuffers, state.VertexBufferCount);

            Span<int> vbScalarSizes = stackalloc int[vbCount];

            for (int i = 0; i < vaCount; i++)
            {
                var attribute = state.VertexAttribs[i];
                var bufferIndex = attribute.IsZero ? 0 : attribute.BufferIndex + 1;

                pipeline.Internal.VertexAttributeDescriptions[i] = new VertexInputAttributeDescription(
                    (uint)i,
                    (uint)bufferIndex,
                    gd.FormatCapabilities.ConvertToVertexVkFormat(attribute.Format),
                    (uint)attribute.Offset);

                if (!attribute.IsZero && bufferIndex < vbCount)
                {
                    vbScalarSizes[bufferIndex - 1] = Math.Max(attribute.Format.GetScalarSize(), vbScalarSizes[bufferIndex - 1]);
                }
            }

            int descriptorIndex = 1;
            pipeline.Internal.VertexBindingDescriptions[0] = new VertexInputBindingDescription(0, 0, VertexInputRate.Vertex);

            for (int i = 0; i < vbCount; i++)
            {
                var vertexBuffer = state.VertexBuffers[i];

                if (vertexBuffer.Enable)
                {
                    var inputRate = vertexBuffer.Divisor != 0 ? VertexInputRate.Instance : VertexInputRate.Vertex;

                    int alignedStride = vertexBuffer.Stride;

                    // Tile-based GPU 优化：使用针对 Tile 架构的对齐
                    if (gd.NeedsVertexBufferAlignmentForTile(vbScalarSizes[i], out int alignment))
                    {
                        alignedStride = BitUtils.AlignUp(vertexBuffer.Stride, alignment);
                    }
                    else if (gd.NeedsVertexBufferAlignment(vbScalarSizes[i], out alignment))
                    {
                        alignedStride = BitUtils.AlignUp(vertexBuffer.Stride, alignment);
                    }

                    // TODO: Support divisor > 1
                    pipeline.Internal.VertexBindingDescriptions[descriptorIndex++] = new VertexInputBindingDescription(
                        (uint)i + 1,
                        (uint)alignedStride,
                        inputRate);
                }
            }

            pipeline.VertexBindingDescriptionsCount = (uint)descriptorIndex;

            // NOTE: Viewports, Scissors are dynamic.

            for (int i = 0; i < Constants.MaxRenderTargets; i++)
            {
                var blend = state.BlendDescriptors[i];

                if (blend.Enable && state.ColorWriteMask[i] != 0)
                {
                    pipeline.Internal.ColorBlendAttachmentState[i] = new PipelineColorBlendAttachmentState(
                        blend.Enable,
                        blend.ColorSrcFactor.Convert(),
                        blend.ColorDstFactor.Convert(),
                        blend.ColorOp.Convert(),
                        blend.AlphaSrcFactor.Convert(),
                        blend.AlphaDstFactor.Convert(),
                        blend.AlphaOp.Convert(),
                        (ColorComponentFlags)state.ColorWriteMask[i]);
                }
                else
                {
                    pipeline.Internal.ColorBlendAttachmentState[i] = new PipelineColorBlendAttachmentState(
                        colorWriteMask: (ColorComponentFlags)state.ColorWriteMask[i]);
                }
            }

            int attachmentCount = 0;
            int maxColorAttachmentIndex = -1;
            uint attachmentIntegerFormatMask = 0;
            bool allFormatsFloatOrSrgb = true;

            for (int i = 0; i < Constants.MaxRenderTargets; i++)
            {
                if (state.AttachmentEnable[i])
                {
                    pipeline.Internal.AttachmentFormats[attachmentCount++] = gd.FormatCapabilities.ConvertToVkFormat(state.AttachmentFormats[i], true);
                    maxColorAttachmentIndex = i;

                    if (state.AttachmentFormats[i].IsInteger())
                    {
                        attachmentIntegerFormatMask |= 1u << i;
                    }

                    allFormatsFloatOrSrgb &= state.AttachmentFormats[i].IsFloatOrSrgb();
                }
            }

            if (state.DepthStencilEnable)
            {
                pipeline.Internal.AttachmentFormats[attachmentCount++] = gd.FormatCapabilities.ConvertToVkFormat(state.DepthStencilFormat, true);
            }

            pipeline.ColorBlendAttachmentStateCount = (uint)(maxColorAttachmentIndex + 1);
            pipeline.VertexAttributeDescriptionsCount = (uint)Math.Min(Constants.MaxVertexAttributes, state.VertexAttribCount);
            pipeline.Internal.AttachmentIntegerFormatMask = attachmentIntegerFormatMask;
            pipeline.Internal.LogicOpsAllowed = attachmentCount == 0 || !allFormatsFloatOrSrgb;

            return pipeline;
        }
    }
}