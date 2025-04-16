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

        private readonly List<(QueryPool, bool)> _activeQueries;
        private CounterQueueEvent _activeConditionalRender;

        private readonly List<BufferedQuery> _pendingQueryCopies;
        private readonly List<BufferHolder> _activeBufferMirrors;

        private ulong _byteWeight;

        private readonly List<BufferHolder> _backingSwaps;

        public PipelineFull(VulkanRenderer gd, Device device) : base(gd, device)
        {
            _activeQueries = new List<(QueryPool, bool)>();
            _pendingQueryCopies = new();
            _backingSwaps = new();
            _activeBufferMirrors = new();

            CommandBuffer = (Cbs = gd.CommandBufferPool.Rent()).CommandBuffer;

            IsMainPipeline = true;
        }

        private void CopyPendingQuery()
        {
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
                // We can't use CmdClearAttachments if not writing all components,
                // because on Vulkan, the pipeline state does not affect clears.
                // On proprietary Adreno drivers, CmdClearAttachments appears to execute out of order, so it's better to not use it at all.
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

                // TODO: Clear only the specified layer.
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
                // We can't use CmdClearAttachments if not clearing all (mask is all ones, 0xFF) or none (mask is 0) of the stencil bits,
                // because on Vulkan, the pipeline state does not affect clears.
                // On proprietary Adreno drivers, CmdClearAttachments appears to execute out of order, so it's better to not use it at all.
                var dstTexture = FramebufferParams.GetDepthStencilView();
                if (dstTexture == null)
                {
                    return;
                }

                // TODO: Clear only the specified layer.
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
                // Gd.ConditionalRenderingApi.CmdEndConditionalRendering(CommandBuffer);
            }
            else
            {
                // throw new NotSupportedException();
            }

            _activeConditionalRender?.ReleaseHostAccess();
            _activeConditionalRender = null;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            // Compare an event and a constant value.
            if (value is CounterQueueEvent evt)
            {
                // Easy host conditional rendering when the check matches what GL can do:
                //  - Event is of type samples passed.
                //  - Result is not a combination of multiple queries.
                //  - Comparing against 0.
                //  - Event has not already been flushed.

                if (compare == 0 && evt.Type == CounterType.SamplesPassed && evt.ClearCounter)
                {
                    if (!value.ReserveForHostAccess())
                    {
                        // If the event has been flushed, then just use the values on the CPU.
                        // The query object may already be repurposed for another draw (eg. begin + end).
                        return false;
                    }

                    if (Gd.Capabilities.SupportsConditionalRendering)
                    {
                        // var buffer = evt.GetBuffer().Get(Cbs, 0, sizeof(long)).Value;
                        // var flags = isEqual ? ConditionalRenderingFlagsEXT.InvertedBitExt : 0;

                        // var conditionalRenderingBeginInfo = new ConditionalRenderingBeginInfoEXT
                        // {
                        //     SType = StructureType.ConditionalRenderingBeginInfoExt,
                        //     Buffer = buffer,
                        //     Flags = flags,
                        // };

                        // Gd.ConditionalRenderingApi.CmdBeginConditionalRendering(CommandBuffer, conditionalRenderingBeginInfo);
                    }

                    _activeConditionalRender = evt;
                    return true;
                }
            }

            // The GPU will flush the queries to CPU and evaluate the condition there instead.

            FlushPendingQuery(); // The thread will be stalled manually flushing the counter, so flush commands now.
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            FlushPendingQuery(); // The thread will be stalled manually flushing the counter, so flush commands now.
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
                // Since we can only free memory after the command buffer that uses a given resource was executed,
                // keeping the command buffer might cause a high amount of memory to be in use.
                // To prevent that, we force submit command buffers if the memory usage by resources
                // in use by the current command buffer is above a given limit, and those resources were disposed.
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
            
        unsafe
{
    try
    {
        var shaderStages = new PipelineShaderStageCreateInfo[2];
    
    // 顶点着色器阶段
    shaderStages[0] = new PipelineShaderStageCreateInfo
    {
        SType = StructureType.PipelineShaderStageCreateInfo,
        Stage = ShaderStageFlags.VertexBit,
        Module = vertexShaderModule,  // 替换为实际的顶点着色器模块
        PName = (byte*)SilkMarshal.StringToPtr("main")
    };

    // 片段着色器阶段
    shaderStages[1] = new PipelineShaderStageCreateInfo
    {
        SType = StructureType.PipelineShaderStageCreateInfo,
        Stage = ShaderStageFlags.FragmentBit,
        Module = fragmentShaderModule, // 替换为实际的片段着色器模块
        PName = (byte*)SilkMarshal.StringToPtr("main")
    };

    // -------------------------------
    // 2. 顶点输入配置（示例：描述顶点数据格式）
    // -------------------------------
    var vertexInputInfo = new PipelineVertexInputStateCreateInfo
    {
        SType = StructureType.PipelineVertexInputStateCreateInfo,
        VertexBindingDescriptionCount = 1,
        PVertexBindingDescriptions = new VertexInputBindingDescription[]
        {
            new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = 5 * sizeof(float), // 例如：位置(x,y,z) + 纹理坐标(u,v)
                InputRate = VertexInputRate.Vertex
            }
        },
        VertexAttributeDescriptionCount = 2,
        PVertexAttributeDescriptions = new VertexInputAttributeDescription[]
        {
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 0,
                Format = Format.R32G32B32Sfloat, // 位置(x,y,z)
                Offset = 0
            },
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 1,
                Format = Format.R32G32Sfloat,    // 纹理坐标(u,v)
                Offset = 3 * sizeof(float)
            }
        }
    };

    // -------------------------------
    // 3. 输入装配配置（示例：三角形列表）
    // -------------------------------
    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
    {
        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
        Topology = PrimitiveTopology.TriangleList,
        PrimitiveRestartEnable = false
    };

    // -------------------------------
    // 4. 视口和裁剪矩形配置
    // -------------------------------
    var viewport = new Viewport
    {
        X = 0,
        Y = 0,
        Width = Gd.Window.Width,   // 视口宽度
        Height = Gd.Window.Height, // 视口高度
        MinDepth = 0.0f,
        MaxDepth = 1.0f
    };

    var scissor = new Rect2D
    {
        Offset = new Offset2D { X = 0, Y = 0 },
        Extent = new Extent2D { Width = Gd.Window.Width, Height = Gd.Window.Height }
    };

    var viewportState = new PipelineViewportStateCreateInfo
    {
        SType = StructureType.PipelineViewportStateCreateInfo,
        ViewportCount = 1,
        PViewports = &viewport,
        ScissorCount = 1,
        PScissors = &scissor
    };

    // -------------------------------
    // 5. 光栅化配置
    // -------------------------------
    var rasterizer = new PipelineRasterizationStateCreateInfo
    {
        SType = StructureType.PipelineRasterizationStateCreateInfo,
        DepthClampEnable = false,
        RasterizerDiscardEnable = false,
        PolygonMode = PolygonMode.Fill,
        LineWidth = 1.0f,
        CullMode = CullModeFlags.BackBit,
        FrontFace = FrontFace.Clockwise,
        DepthBiasEnable = false
    };

    // -------------------------------
    // 6. 多重采样配置
    // -------------------------------
    var multisampling = new PipelineMultisampleStateCreateInfo
    {
        SType = StructureType.PipelineMultisampleStateCreateInfo,
        SampleShadingEnable = false,
        RasterizationSamples = SampleCountFlags.Count1Bit
    };

    // -------------------------------
    // 7. 颜色混合配置（示例：启用Alpha混合）
    // -------------------------------
    var colorBlendAttachment = new PipelineColorBlendAttachmentState
    {
        ColorWriteMask = ColorComponentFlags.RBit | 
                         ColorComponentFlags.GBit | 
                         ColorComponentFlags.BBit | 
                         ColorComponentFlags.ABit,
        BlendEnable = true,
        SrcColorBlendFactor = BlendFactor.SrcAlpha,
        DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
        ColorBlendOp = BlendOp.Add,
        SrcAlphaBlendFactor = BlendFactor.One,
        DstAlphaBlendFactor = BlendFactor.Zero,
        AlphaBlendOp = BlendOp.Add
    };

    var colorBlending = new PipelineColorBlendStateCreateInfo
    {
        SType = StructureType.PipelineColorBlendStateCreateInfo,
        LogicOpEnable = false,
        LogicOp = LogicOp.Copy,
        AttachmentCount = 1,
        PAttachments = &colorBlendAttachment,
        BlendConstants = new float[] { 0.0f, 0.0f, 0.0f, 0.0f }
    };

    // -------------------------------
    // 8. 动态状态配置（可选：允许运行时修改视口/裁剪）
    // -------------------------------
    var dynamicStates = new DynamicState[]
    {
        DynamicState.Viewport,
        DynamicState.Scissor
    };

    var dynamicState = new PipelineDynamicStateCreateInfo
    {
        SType = StructureType.PipelineDynamicStateCreateInfo,
        DynamicStateCount = (uint)dynamicStates.Length,
        PDynamicStates = (DynamicState*)SilkMarshal.MarshalToPtr(dynamicStates)
    };

    // -------------------------------
    // 9. 组装完整的管线配置信息
    // -------------------------------
    var pipelineCreateInfo = new GraphicsPipelineCreateInfo
    {
        SType = StructureType.GraphicsPipelineCreateInfo,
        StageCount = (uint)shaderStages.Length,
        PStages = (PipelineShaderStageCreateInfo*)SilkMarshal.MarshalToPtr(shaderStages),
        PVertexInputState = &vertexInputInfo,
        PInputAssemblyState = &inputAssembly,
        PViewportState = &viewportState,
        PRasterizationState = &rasterizer,
        PMultisampleState = &multisampling,
        PColorBlendState = &colorBlending,
        PDynamicState = &dynamicState,
        Layout = _pipelineLayout,  // 假设已创建管线布局
        RenderPass = _renderPass,  // 假设已创建渲染流程
        Subpass = 0,
        BasePipelineHandle = default,
        BasePipelineIndex = -1
    };

    // -------------------------------
    // 10. 添加ARM扩展配置（之前已实现）
    // -------------------------------
    if (Gd.Capabilities.SupportsRasterizationOrderAttachmentAccess)
    {
        var rasterizationOrderInfo = new RasterizationOrderAttachmentAccessCreateInfoARM
        {
            SType = StructureType.RasterizationOrderAttachmentAccessCreateInfoArm,
            RasterizationOrder = RasterizationOrderARM.AttachmentAccessOrderedARM
        };

        rasterizationOrderInfo.PNext = pipelineCreateInfo.PNext;
        pipelineCreateInfo.PNext = &rasterizationOrderInfo;
    }

    // -------------------------------
    // 11. 调用Vulkan API创建管线
    // -------------------------------
    fixed (Pipeline* pPipeline = &_pipeline)
    {
        Gd.Api.CreateGraphicsPipelines(
            Gd.Device,
            default(PipelineCache),
            1,
            &pipelineCreateInfo,
            null,
            pPipeline
        ).ThrowOnError();
    }

 finally
        {
            // 释放所有非托管内存
            SilkMarshal.Free((nint)shaderStages[0].PName);
            SilkMarshal.Free((nint)shaderStages[1].PName);
            SilkMarshal.Free((nint)pipelineCreateInfo.PStages);
            SilkMarshal.Free((nint)dynamicState.PDynamicStates);
        }
    }
}
    
            SignalCommandBufferChange();

            if (Pipeline != null && Pbp == PipelineBindPoint.Graphics)
            {
                DynamicState.ReplayIfDirty(Gd, CommandBuffer);
            }
        }

        public void FlushCommandsImpl()
        {
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

            // Restore per-command buffer state.
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
                    // Try reset some additional queries in advance.

                    Gd.ResetFutureCounters(CommandBuffer, AutoFlush.GetRemainingQueries());
                }
            }

            bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
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
            _pendingQueryCopies.Add(query);

            if (AutoFlush.RegisterPendingQuery())
            {
                FlushCommandsImpl();
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
            CopyPendingQuery();
        }
    }
}
