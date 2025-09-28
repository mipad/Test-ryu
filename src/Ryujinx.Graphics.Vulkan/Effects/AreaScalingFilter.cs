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
    internal class AreaScalingFilter : IScalingFilter
    {
        private readonly VulkanRenderer _renderer;
        private PipelineHelperShader _pipeline;
        private ISampler _sampler;
        private ShaderCollection _scalingProgram;
        private Device _device;

        public float Level { get; set; }

        public AreaScalingFilter(VulkanRenderer renderer, Device device)
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

            // 修改这里：从嵌入式资源改为从文件系统加载
            byte[] scalingShader = LoadShaderFromFile("Shaders/AreaScaling.spv");

            var scalingResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

            _sampler = _renderer.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            _scalingProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(scalingShader, ShaderStage.Compute, TargetLanguage.Spirv),
            }, scalingResourceLayout);
        }

        // 添加从文件系统加载着色器的方法
        private byte[] LoadShaderFromFile(string shaderPath)
        {
            try
            {
                // 首先尝试从应用数据目录加载
                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ryujinx", shaderPath);
                if (File.Exists(appDataPath))
                {
                    return File.ReadAllBytes(appDataPath);
                }

                // 然后尝试从assets加载（Android）
                string assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", shaderPath);
                if (File.Exists(assetsPath))
                {
                    return File.ReadAllBytes(assetsPath);
                }

                // 最后回退到嵌入式资源
                return EmbeddedResources.Read($"Ryujinx.Graphics.Vulkan/Effects/{shaderPath}");
            }
            catch (Exception ex)
            {
                // 如果所有方法都失败，记录错误并返回空数组
                Logger.Error?.Print(LogClass.Gpu, $"Failed to load shader {shaderPath}: {ex.Message}");
                return Array.Empty<byte>();
            }
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
            if (_scalingProgram == null) return;

            _pipeline.SetCommandBuffer(cbs);
            _pipeline.SetProgram(_scalingProgram);
            _pipeline.SetTextureAndSampler(ShaderStage.Compute, 1, view, _sampler);

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

            int rangeSize = dimensionsBuffer.Length * sizeof(float);
            using var buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, rangeSize);
            buffer.Holder.SetDataUnchecked(buffer.Offset, dimensionsBuffer);

            int threadGroupWorkRegionDim = 16;
            int dispatchX = (width + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchY = (height + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

            _pipeline.SetUniformBuffers(stackalloc[] { new BufferAssignment(2, buffer.Range) });
            _pipeline.SetImage(0, destinationTexture);
            _pipeline.DispatchCompute(dispatchX, dispatchY, 1);
            _pipeline.ComputeBarrier();

            _pipeline.Finish();
        }
    }
}
