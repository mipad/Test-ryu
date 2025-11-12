using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        // Push constant 结构体
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct EasuPushConstants
        {
            public Vector4 Con0;
            public Vector4 Con1;
            public Vector4 Con2;
            public Vector4 Con3;
        }

        [StructLayout(LayoutKind.Sequential, Size = 32)]
        private struct RcasPushConstants
        {
            public Vector4 Con0;
            public Vector3 Con1;
            public float Padding;
        }

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

            // 创建使用push constants的资源布局
            var scalingResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Fragment, ResourceType.TextureAndSampler, 0) // 输入纹理
                .Build();

            var sharpeningResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Fragment, ResourceType.TextureAndSampler, 0) // 输入纹理
                .Build();

            _sampler = _renderer.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            // 创建着色器程序，指定push constant大小
            _scalingProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(vertexShader, ShaderStage.Vertex, TargetLanguage.Spirv),
                new ShaderSource(scalingShader, ShaderStage.Fragment, TargetLanguage.Spirv),
            }, scalingResourceLayout, pushConstantSize: 64); // EASU需要64字节

            _sharpeningProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(vertexShader, ShaderStage.Vertex, TargetLanguage.Spirv),
                new ShaderSource(sharpeningShader, ShaderStage.Fragment, TargetLanguage.Spirv),
            }, sharpeningResourceLayout, pushConstantSize: 32); // RCAS需要32字节
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
            _pipeline.SetProgram(_scalingProgram);
            _pipeline.SetTextureAndSampler(ShaderStage.Fragment, 0, view, _sampler);

            // 计算FSR EASU常量
            var constants = CalculateFsrConstants(source, destination, width, height);

            // 使用push constants传递数据
            _pipeline.PushConstants(cbs.CommandBuffer, constants);

            _pipeline.SetRenderTarget(_intermediaryTexture);
            SetViewportAndScissor(width, height);
            _pipeline.Draw(3, 1, 0, 0);
        }

        private void RunSharpeningPass(int width, int height, Auto<DisposableImageView> destinationTexture, CommandBufferScoped cbs)
        {
            _pipeline.SetProgram(_sharpeningProgram);
            _pipeline.SetTextureAndSampler(ShaderStage.Fragment, 0, _intermediaryTexture, _sampler);

            // 计算RCAS锐化常量
            var constants = CalculateRcasConstants(width, height);

            // 使用push constants传递数据
            _pipeline.PushConstants(cbs.CommandBuffer, constants);

            _pipeline.SetRenderTarget(destinationTexture);
            SetViewportAndScissor(width, height);
            _pipeline.Draw(3, 1, 0, 0);
        }

        private EasuPushConstants CalculateFsrConstants(Extent2D source, Extent2D destination, int width, int height)
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

            return new EasuPushConstants
            {
                Con0 = new Vector4(
                    BitConverter.UInt32BitsToSingle(con0[0]),
                    BitConverter.UInt32BitsToSingle(con0[1]),
                    BitConverter.UInt32BitsToSingle(con0[2]),
                    BitConverter.UInt32BitsToSingle(con0[3])),
                Con1 = new Vector4(
                    BitConverter.UInt32BitsToSingle(con1[0]),
                    BitConverter.UInt32BitsToSingle(con1[1]),
                    BitConverter.UInt32BitsToSingle(con1[2]),
                    BitConverter.UInt32BitsToSingle(con1[3])),
                Con2 = new Vector4(
                    BitConverter.UInt32BitsToSingle(con2[0]),
                    BitConverter.UInt32BitsToSingle(con2[1]),
                    BitConverter.UInt32BitsToSingle(con2[2]),
                    BitConverter.UInt32BitsToSingle(con2[3])),
                Con3 = new Vector4(
                    BitConverter.UInt32BitsToSingle(con3[0]),
                    BitConverter.UInt32BitsToSingle(con3[1]),
                    BitConverter.UInt32BitsToSingle(con3[2]),
                    BitConverter.UInt32BitsToSingle(con3[3]))
            };
        }

        private RcasPushConstants CalculateRcasConstants(int width, int height)
        {
            // 计算RCAS常量
            Span<uint> rcasCon = stackalloc uint[4];
            FsrConstantsCalculator.FsrRcasCon(rcasCon, Level);

            return new RcasPushConstants
            {
                Con0 = new Vector4(
                    BitConverter.UInt32BitsToSingle(rcasCon[0]),
                    BitConverter.UInt32BitsToSingle(rcasCon[1]),
                    BitConverter.UInt32BitsToSingle(rcasCon[2]),
                    BitConverter.UInt32BitsToSingle(rcasCon[3])),
                Con1 = new Vector3(width, height, 0.0f),
                Padding = 0.0f
            };
        }

        private void SetViewportAndScissor(int width, int height)
        {
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

    // 简单的Vector3/Vector4结构体用于数据传递
    [StructLayout(LayoutKind.Sequential)]
    public struct Vector3
    {
        public float X, Y, Z;
        
        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector4
    {
        public float X, Y, Z, W;
        
        public Vector4(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
    }
}