using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using System.Runtime.CompilerServices;
using Extent2D = Ryujinx.Graphics.GAL.Extents2D;
using Format = Silk.NET.Vulkan.Format;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal class FsrScalingFilter : IScalingFilter
    {
        private readonly VulkanRenderer _renderer;
        private PipelineHelperShader _pipeline;
        private ISampler _sampler;
        private ShaderCollection _scalingProgram;
        private ShaderCollection _sharpeningProgram;
        private float _sharpeningLevel = 1;
        private Device _device;
        private TextureView _intermediaryTexture;

        // 移除单独的顶点着色器程序，使用合并的程序

        public float Level
        {
            get => _sharpeningLevel;
            set
            {
                _sharpeningLevel = MathF.Max(0.01f, value);
            }
        }

        public FsrScalingFilter(VulkanRenderer renderer, Device device)
        {
            _device = device;
            _renderer = renderer;

            Initialize();
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _scalingProgram?.Dispose();
            _sharpeningProgram?.Dispose();
            _sampler?.Dispose();
            _intermediaryTexture?.Dispose();
        }

        public void Initialize()
        {
            _pipeline = new PipelineHelperShader(_renderer, _device);
            _pipeline.Initialize();

            // 读取着色器文件
            var vertexShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/FsrScaling.vert.spv");
            var scalingShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/FsrScaling.frag.spv");
            var sharpeningShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/FsrSharpening.frag.spv");

            // 创建合并的着色器程序资源布局（顶点+片段）
            var scalingResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Vertex, ResourceType.UniformBuffer, 1)   // 区域参数
                .Add(ResourceStages.Fragment, ResourceType.UniformBuffer, 2) // FSR常量
                .Add(ResourceStages.Fragment, ResourceType.TextureAndSampler, 1) // 输入纹理
                .Build();

            var sharpeningResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Vertex, ResourceType.UniformBuffer, 1)   // 区域参数
                .Add(ResourceStages.Fragment, ResourceType.UniformBuffer, 2) // RCAS常量
                .Add(ResourceStages.Fragment, ResourceType.TextureAndSampler, 1) // 输入纹理
                .Build();

            _sampler = _renderer.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            // 创建合并的着色器程序（顶点+片段）
            _scalingProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(vertexShader, ShaderStage.Vertex, TargetLanguage.Spirv),
                new ShaderSource(scalingShader, ShaderStage.Fragment, TargetLanguage.Spirv),
            }, scalingResourceLayout);

            _sharpeningProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(vertexShader, ShaderStage.Vertex, TargetLanguage.Spirv),
                new ShaderSource(sharpeningShader, ShaderStage.Fragment, TargetLanguage.Spirv),
            }, sharpeningResourceLayout);
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
            // 创建中间纹理（如果需要）
            if (_intermediaryTexture == null
                || _intermediaryTexture.Info.Width != width
                || _intermediaryTexture.Info.Height != height
                || !_intermediaryTexture.Info.Equals(view.Info))
            {
                var originalInfo = view.Info;

                var info = new TextureCreateInfo(
                    width,
                    height,
                    originalInfo.Depth,
                    originalInfo.Levels,
                    originalInfo.Samples,
                    originalInfo.BlockWidth,
                    originalInfo.BlockHeight,
                    originalInfo.BytesPerPixel,
                    originalInfo.Format,
                    originalInfo.DepthStencilMode,
                    originalInfo.Target,
                    originalInfo.SwizzleR,
                    originalInfo.SwizzleG,
                    originalInfo.SwizzleB,
                    originalInfo.SwizzleA);
                _intermediaryTexture?.Dispose();
                _intermediaryTexture = _renderer.CreateTexture(info) as TextureView;
            }

            _pipeline.SetCommandBuffer(cbs);

            // 第一遍：缩放
            RunScalingPass(view, width, height, source, destination, cbs);

            // 第二遍：锐化
            RunSharpeningPass(width, height, destinationTexture, cbs);

            _pipeline.Finish();
        }

        private void RunScalingPass(TextureView view, int width, int height, Extent2D source, Extent2D destination, CommandBufferScoped cbs)
        {
            // 使用合并的着色器程序
            _pipeline.SetProgram(_scalingProgram);
            _pipeline.SetTextureAndSampler(ShaderStage.Fragment, 1, view, _sampler);

            // 计算FSR EASU常量 - 使用数组避免Span作用域问题
            float[] constants = CalculateFsrConstants(source, destination, width, height);

            // 创建统一缓冲区
            int bufferSize = constants.Length * sizeof(float);
            using var buffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, bufferSize);
            buffer.Holder.SetDataUnchecked(buffer.Offset, constants);

            _pipeline.SetUniformBuffers(stackalloc[] { new BufferAssignment(2, buffer.Range) });
            
            // 使用TextureView版本的SetRenderTarget
            _pipeline.SetRenderTarget(_intermediaryTexture);
            
            // 设置视口和裁剪
            SetViewportAndScissor(width, height);
            
            // 绘制全屏四边形（3个顶点）
            _pipeline.Draw(3, 1, 0, 0);
        }

        private void RunSharpeningPass(int width, int height, Auto<DisposableImageView> destinationTexture, CommandBufferScoped cbs)
        {
            // 使用合并的着色器程序
            _pipeline.SetProgram(_sharpeningProgram);
            _pipeline.SetTextureAndSampler(ShaderStage.Fragment, 1, _intermediaryTexture, _sampler);

            // 计算RCAS锐化常量 - 使用数组避免Span作用域问题
            float[] sharpeningBufferData = CalculateRcasConstants(width, height);

            int bufferSize = sharpeningBufferData.Length * sizeof(float);
            using var sharpeningBuffer = _renderer.BufferManager.ReserveOrCreate(_renderer, cbs, bufferSize);
            sharpeningBuffer.Holder.SetDataUnchecked(sharpeningBuffer.Offset, sharpeningBufferData);

            _pipeline.SetUniformBuffers(stackalloc[] { new BufferAssignment(2, sharpeningBuffer.Range) });
            
            // 使用Auto<DisposableImageView>版本的SetRenderTarget
            _pipeline.SetRenderTarget(destinationTexture);
            
            // 设置视口和裁剪
            SetViewportAndScissor(width, height);
            
            // 绘制全屏四边形
            _pipeline.Draw(3, 1, 0, 0);
        }

        // 修改返回类型为数组，避免Span作用域问题
        private float[] CalculateFsrConstants(Extent2D source, Extent2D destination, int width, int height)
        {
            // 计算视口参数
            float viewportWidth = Math.Abs(source.X2 - source.X1);
            float viewportHeight = Math.Abs(source.Y2 - source.Y1);
            float viewportX = source.X1;
            float viewportY = source.Y1;
            
            float inputWidth = viewportWidth;
            float inputHeight = viewportHeight;
            float outputWidth = width;
            float outputHeight = height;

            // 计算EASU常量
            Span<uint> con0 = stackalloc uint[4];
            Span<uint> con1 = stackalloc uint[4]; 
            Span<uint> con2 = stackalloc uint[4];
            Span<uint> con3 = stackalloc uint[4];
            
            FsrConstantsCalculator.FsrEasuConOffset(
                con0, con1, con2, con3,
                viewportWidth, viewportHeight,
                inputWidth, inputHeight, 
                outputWidth, outputHeight,
                viewportX, viewportY);

            // 使用数组而不是栈分配的Span
            float[] constants = new float[16];
            
            // con0
            constants[0] = BitConverter.UInt32BitsToSingle(con0[0]);
            constants[1] = BitConverter.UInt32BitsToSingle(con0[1]);
            constants[2] = BitConverter.UInt32BitsToSingle(con0[2]);
            constants[3] = BitConverter.UInt32BitsToSingle(con0[3]);
            
            // con1
            constants[4] = BitConverter.UInt32BitsToSingle(con1[0]);
            constants[5] = BitConverter.UInt32BitsToSingle(con1[1]);
            constants[6] = BitConverter.UInt32BitsToSingle(con1[2]);
            constants[7] = BitConverter.UInt32BitsToSingle(con1[3]);
            
            // con2
            constants[8] = BitConverter.UInt32BitsToSingle(con2[0]);
            constants[9] = BitConverter.UInt32BitsToSingle(con2[1]);
            constants[10] = BitConverter.UInt32BitsToSingle(con2[2]);
            constants[11] = BitConverter.UInt32BitsToSingle(con2[3]);
            
            // con3
            constants[12] = BitConverter.UInt32BitsToSingle(con3[0]);
            constants[13] = BitConverter.UInt32BitsToSingle(con3[1]);
            constants[14] = BitConverter.UInt32BitsToSingle(con3[2]);
            constants[15] = BitConverter.UInt32BitsToSingle(con3[3]);

            return constants;
        }

        // 修改返回类型为数组，避免Span作用域问题
        private float[] CalculateRcasConstants(int width, int height)
        {
            // 计算RCAS常量
            Span<uint> rcasCon = stackalloc uint[4];
            FsrConstantsCalculator.FsrRcasCon(rcasCon, Level);

            float[] constants = new float[7];
            
            // RCAS常量
            constants[0] = BitConverter.UInt32BitsToSingle(rcasCon[0]); // sharpness
            constants[1] = BitConverter.UInt32BitsToSingle(rcasCon[1]);
            constants[2] = BitConverter.UInt32BitsToSingle(rcasCon[2]);
            constants[3] = BitConverter.UInt32BitsToSingle(rcasCon[3]);
            
            // 纹理尺寸
            constants[4] = width;
            constants[5] = height;
            constants[6] = 0.0f; // 填充

            return constants;
        }

        private void SetViewportAndScissor(int width, int height)
        {
            // 设置视口为整个纹理
            _pipeline.SetViewport(0, 0, width, height);
            _pipeline.SetScissor(0, 0, width, height);
        }

        /// <summary>
        /// FSR常量计算器 - 基于yuzu的FSR实现
        /// </summary>
        private static class FsrConstantsCalculator
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static uint ToUint(float value)
            {
                return BitConverter.SingleToUInt32Bits(value);
            }

            public static void FsrEasuConOffset(
                Span<uint> con0,
                Span<uint> con1, 
                Span<uint> con2,
                Span<uint> con3,
                float viewportWidth,
                float viewportHeight,
                float inputImageWidth,
                float inputImageHeight,
                float outputImageWidth, 
                float outputImageHeight,
                float viewportX,
                float viewportY)
            {
                // 基于FFX FSR的EASU常量计算
                float srcW = viewportWidth;
                float srcH = viewportHeight;
                float dstW = outputImageWidth;
                float dstH = outputImageHeight;
                
                const float kEPS = 1e-6f;
                
                float rcpW = 1.0f / srcW;
                float rcpH = 1.0f / srcH;
                
                float translateX = -viewportX * rcpW;
                float translateY = -viewportY * rcpH;
                
                float scaleX = dstW * rcpW;
                float scaleY = dstH * rcpH;
                
                float subpixelX = 0.5f * rcpW;
                float subpixelY = 0.5f * rcpH;
                
                float kernelSize = 2.0f;
                float rcpTextureSizeX = 1.0f / inputImageWidth;
                float rcpTextureSizeY = 1.0f / inputImageHeight;

                // con0 - 视口边界
                con0[0] = ToUint(viewportX * rcpTextureSizeX);
                con0[1] = ToUint(viewportY * rcpTextureSizeY);
                con0[2] = ToUint((viewportX + srcW) * rcpTextureSizeX);
                con0[3] = ToUint((viewportY + srcH) * rcpTextureSizeY);

                // con1 - 缩放参数
                con1[0] = ToUint(scaleX);
                con1[1] = ToUint(scaleY);
                con1[2] = ToUint(scaleX * kernelSize - 0.5f - subpixelX);
                con1[3] = ToUint(scaleY * kernelSize - 0.5f - subpixelY);

                // con2 - 子像素偏移
                con2[0] = ToUint(0.0f);
                con2[1] = ToUint(0.0f);
                con2[2] = ToUint(subpixelX);
                con2[3] = ToUint(subpixelY);

                // con3 - 倒数参数
                con3[0] = ToUint(rcpW);
                con3[1] = ToUint(rcpH);
                con3[2] = ToUint(rcpTextureSizeX);
                con3[3] = ToUint(rcpTextureSizeY);
            }

            public static void FsrRcasCon(Span<uint> constants, float sharpness)
            {
                // 基于FFX FSR的RCAS常量计算
                // 锐化调整 - 将[0,1]范围转换为RCAS参数
                float sharp = 1.0f - sharpness * 0.99f;
                float sharpScale = sharp * 8.0f;

                // con0 - 锐化参数
                constants[0] = ToUint(sharpScale);
                constants[1] = ToUint(sharpScale);
                constants[2] = ToUint(sharpScale);
                constants[3] = ToUint(0.0f);
            }
        }
    }
}