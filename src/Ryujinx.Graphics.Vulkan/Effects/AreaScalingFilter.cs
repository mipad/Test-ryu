using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using System.IO;
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

            // 修改这里：使用正确的路径
            byte[] scalingShader = LoadShaderFromFile("AreaScaling.spv");

            if (scalingShader == null || scalingShader.Length == 0)
            {
                Logger.Error?.Print(LogClass.Gpu, "Failed to load AreaScaling.spv shader");
                return;
            }

            Logger.Info?.Print(LogClass.Gpu, $"Area scaling shader loaded successfully, size: {scalingShader.Length} bytes");

            var scalingResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

            _sampler = _renderer.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            _scalingProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(scalingShader, ShaderStage.Compute, TargetLanguage.Spirv),
            }, scalingResourceLayout);

            if (_scalingProgram == null)
            {
                Logger.Error?.Print(LogClass.Gpu, "Failed to create scaling program");
            }
            else
            {
                Logger.Info?.Print(LogClass.Gpu, "Area scaling program created successfully");
            }
        }

        // 添加从文件系统加载着色器的方法
        private byte[] LoadShaderFromFile(string shaderPath)
        {
            try
            {
                // 首先尝试从assets加载（Android）- 使用直接路径
                byte[] assetShader = ShaderLoader.LoadShaderFromAssets(shaderPath);
                if (assetShader != null && assetShader.Length > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"Successfully loaded shader from assets: {shaderPath}, size: {assetShader.Length} bytes");
                    return assetShader;
                }

                // 然后尝试从应用数据目录加载
                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ryujinx", shaderPath);
                if (File.Exists(appDataPath))
                {
                    Logger.Info?.Print(LogClass.Gpu, $"Loading shader from app data: {appDataPath}");
                    return File.ReadAllBytes(appDataPath);
                }

                // 最后回退到嵌入式资源
                Logger.Info?.Print(LogClass.Gpu, $"Falling back to embedded resource for shader: {shaderPath}");
                return EmbeddedResources.Read($"Ryujinx.Graphics.Vulkan/Effects/Shaders/{shaderPath}");
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
            Logger.Info?.Print(LogClass.Gpu, $"AreaScalingFilter.Run called - width: {width}, height: {height}");
            Logger.Info?.Print(LogClass.Gpu, $"Source: X1={source.X1}, X2={source.X2}, Y1={source.Y1}, Y2={source.Y2}");
            Logger.Info?.Print(LogClass.Gpu, $"Destination: X1={destination.X1}, X2={destination.X2}, Y1={destination.Y1}, Y2={destination.Y2}");

            if (_scalingProgram == null) 
            {
                Logger.Warning?.Print(LogClass.Gpu, "Area scaling program not initialized, skipping filter");
                return;
            }

            if (view == null)
            {
                Logger.Error?.Print(LogClass.Gpu, "Input texture view is null");
                return;
            }

            if (destinationTexture == null)
            {
                Logger.Error?.Print(LogClass.Gpu, "Destination texture is null");
                return;
            }

            try
            {
                _pipeline.SetCommandBuffer(cbs);
                Logger.Info?.Print(LogClass.Gpu, "Command buffer set");

                _pipeline.SetProgram(_scalingProgram);
                Logger.Info?.Print(LogClass.Gpu, "Program set");

                _pipeline.SetTextureAndSampler(ShaderStage.Compute, 1, view, _sampler);
                Logger.Info?.Print(LogClass.Gpu, "Texture and sampler set");

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

                Logger.Info?.Print(LogClass.Gpu, $"Dimensions buffer: [{string.Join(", ", dimensionsBuffer.ToArray())}]");

                int rangeSize = dimensionsBuffer.Length * sizeof(float);
                Logger.Info?.Print(LogClass.Gpu, $"Range size: {rangeSize} bytes");

                using var buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, rangeSize);
                buffer.Holder.SetDataUnchecked(buffer.Offset, dimensionsBuffer);
                Logger.Info?.Print(LogClass.Gpu, "Buffer data set");

                int threadGroupWorkRegionDim = 16;
                int dispatchX = (width + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
                int dispatchY = (height + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

                Logger.Info?.Print(LogClass.Gpu, $"Dispatch: X={dispatchX}, Y={dispatchY}, Z=1");

                _pipeline.SetUniformBuffers(stackalloc[] { new BufferAssignment(2, buffer.Range) });
                Logger.Info?.Print(LogClass.Gpu, "Uniform buffers set");

                _pipeline.SetImage(0, destinationTexture);
                Logger.Info?.Print(LogClass.Gpu, "Image set");

                _pipeline.DispatchCompute(dispatchX, dispatchY, 1);
                Logger.Info?.Print(LogClass.Gpu, "DispatchCompute executed");

                _pipeline.ComputeBarrier();
                Logger.Info?.Print(LogClass.Gpu, "Compute barrier executed");

                _pipeline.Finish();
                Logger.Info?.Print(LogClass.Gpu, "Pipeline finished");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Error in AreaScalingFilter.Run: {ex.Message}");
                Logger.Error?.Print(LogClass.Gpu, $"Stack trace: {ex.StackTrace}");
            }
        }
    }
}
