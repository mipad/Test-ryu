using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using Extent2D = Ryujinx.Graphics.GAL.Extents2D;
using Format = Silk.NET.Vulkan.Format;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal class MmpxScalingFilter : IScalingFilter
    {
        private readonly VulkanRenderer _renderer;
        private PipelineHelperShader _pipeline;
        private ISampler _sampler;
        private ShaderCollection _scalingProgram;
        private Device _device;

        public float Level { get; set; }

        public MmpxScalingFilter(VulkanRenderer renderer, Device device)
        {
            _device = device;
            _renderer = renderer;
            Initialize();
        }

        public void Dispose()
        {
            _pipeline?.Dispose();
            _scalingProgram?.Dispose();
            _sampler?.Dispose();
        }

        public void Initialize()
        {
            _pipeline = new PipelineHelperShader(_renderer, _device);
            _pipeline.Initialize();

            // 加载编译好的 MMPX SPIR-V 着色器
            byte[] scalingShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/MmpxScaling.spv");

            // 创建资源布局
            var scalingResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)  // Dimensions uniform
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)  // Source texture
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true)  // Output image
                .Build();

            // 创建线性采样器
            _sampler = _renderer.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            // 创建着色器程序
            _scalingProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(scalingShader, ShaderStage.Compute, TargetLanguage.Spirv),
            }, scalingResourceLayout);
        }

        public void Run(
            TextureView view,
            CommandBufferScoped cbs,
            Auto<DisposableImageView> destinationTexture,
            Format format,
            int width,
            int height,
            Extent2D source,
            Extent2D destination)
        {
            _pipeline.SetCommandBuffer(cbs);
            _pipeline.SetProgram(_scalingProgram);
            
            // 绑定输入纹理和采样器
            _pipeline.SetTextureAndSampler(ShaderStage.Compute, 1, view, _sampler);

            // 准备维度统一缓冲区数据
            ReadOnlySpan<float> dimensionsBuffer = stackalloc float[]
            {
                source.X1,
                source.X2,
                source.Y1,
                source.Y2,
                destination.X1,
                destination.X2,
                destination.Y1,
                destination.Y2,
            };

            // 创建并设置统一缓冲区
            int rangeSize = dimensionsBuffer.Length * sizeof(float);
            using var buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, rangeSize);
            buffer.Holder.SetDataUnchecked(buffer.Offset, dimensionsBuffer);

            // 计算调度组数量
            int threadGroupWorkRegionDim = 16;
            int dispatchX = (width + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchY = (height + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

            // 设置统一缓冲区和输出图像
            _pipeline.SetUniformBuffers(stackalloc[] { new BufferAssignment(2, buffer.Range) });
            _pipeline.SetImage(0, destinationTexture);
            
            // 调度计算着色器
            _pipeline.DispatchCompute(dispatchX, dispatchY, 1);
            _pipeline.ComputeBarrier();
            _pipeline.Finish();
        }
    }
}
