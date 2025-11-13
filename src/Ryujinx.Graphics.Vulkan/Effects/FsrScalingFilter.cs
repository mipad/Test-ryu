using Ryujinx.Common; 
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Vulkan;
using System;
using System.Runtime.CompilerServices;
using Extent2D = Ryujinx.Graphics.GAL.Extents2D;
using VkFormat = Silk.NET.Vulkan.Format;
using GalFormat = Ryujinx.Graphics.GAL.Format;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal class FsrScalingFilter : IScalingFilter
    {
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private TextureView _intermediaryTexture;
        private readonly PipelineHelperShader _pipeline;
        private readonly ISampler _samplerLinear;
        private readonly IProgram _programFSRScaling;
        private readonly IProgram _programFSRSharpening;

        public float Level { get; set; }

        public FsrScalingFilter(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            Level = 0.5f; // 默认锐度级别

            _pipeline = new PipelineHelperShader(gd, device);
            _pipeline.Initialize();

            _samplerLinear = gd.CreateSampler(GAL.SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            // 创建 FSR 缩放着色器程序
            ResourceLayout fsrScalingResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Vertex, ResourceType.UniformBuffer, 1)  // 区域参数
                .Add(ResourceStages.Fragment, ResourceType.UniformBuffer, 2) // FSR常量
                .Add(ResourceStages.Fragment, ResourceType.TextureAndSampler, 0).Build();

            ResourceLayout fsrSharpeningResourceLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Vertex, ResourceType.UniformBuffer, 1)  // 区域参数
                .Add(ResourceStages.Fragment, ResourceType.UniformBuffer, 2) // RCAS常量
                .Add(ResourceStages.Fragment, ResourceType.TextureAndSampler, 0).Build();

            try
            {
                // 使用正确的 ShaderSource 构造函数
                _programFSRScaling = gd.CreateProgramWithMinimalLayout(new[]
                {
                    new ShaderSource(ReadSpirv("FsrScaling.vert.spv"), ShaderStage.Vertex, TargetLanguage.Spirv),
                    new ShaderSource(ReadSpirv("FsrScaling.frag.spv"), ShaderStage.Fragment, TargetLanguage.Spirv)
                }, fsrScalingResourceLayout);
                Logger.Info?.Print(LogClass.Gpu, "FSR scaling shader program created successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to create FSR scaling shader program: {ex.Message}");
                throw;
            }

            try
            {
                _programFSRSharpening = gd.CreateProgramWithMinimalLayout(new[]
                {
                    new ShaderSource(ReadSpirv("FsrScaling.vert.spv"), ShaderStage.Vertex, TargetLanguage.Spirv),
                    new ShaderSource(ReadSpirv("FsrSharpening.frag.spv"), ShaderStage.Fragment, TargetLanguage.Spirv)
                }, fsrSharpeningResourceLayout);
                Logger.Info?.Print(LogClass.Gpu, "FSR sharpening shader program created successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to create FSR sharpening shader program: {ex.Message}");
                throw;
            }
        }

        private static byte[] ReadSpirv(string fileName)
        {
            const string ShaderBinariesPath = "Ryujinx.Graphics.Vulkan/Shaders/SpirvBinaries";
            return EmbeddedResources.Read(string.Join('/', ShaderBinariesPath, fileName));
        }

        public void Run(
            TextureView view,
            CommandBufferScoped cbs,
            Auto<DisposableImageView> destinationTexture,
            VkFormat format,
            int width,
            int height,
            Extent2D source,
            Extent2D destination)
        {
            Logger.Info?.Print(LogClass.Gpu, $"FSR: Starting processing - Level: {Level}, Source: {source}, Destination: {destination}, Output: {width}x{height}");

            // 确保中间纹理存在
            if (_intermediaryTexture == null || 
                _intermediaryTexture.Info.Width != width || 
                _intermediaryTexture.Info.Height != height)
            {
                CreateIntermediaryTexture(width, height, view.Info.Format);
            }

            if (_intermediaryTexture == null)
            {
                Logger.Error?.Print(LogClass.Gpu, "FSR: Failed to create intermediary texture, falling back to bilinear");
                FallbackToBilinear(view, cbs, destinationTexture, format, width, height, source, destination);
                return;
            }

            try
            {
                // 计算 FSR 常量
                var fsrConstants = CalculateFsrConstants(source, destination, width, height);
                var rcasConstants = CalculateRcasConstants(width, height);

                // 创建目标 TextureView - 使用正确的格式转换
                var textureCreateInfo = new TextureCreateInfo(
                    width, height, 1, 1, 1, 1, 1, 1,
                    ConvertVkFormatToGalFormat(format),
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red,
                    SwizzleComponent.Green,
                    SwizzleComponent.Blue,
                    SwizzleComponent.Alpha);

                // 使用正确的 TextureView 构造函数，传递 VkFormat
                using var dstView = new TextureView(_gd, _device, destinationTexture, textureCreateInfo, format);

                // 执行 FSR 处理
                RunFsrProcessing(view, dstView, cbs, source, destination, fsrConstants, rcasConstants);
                
                Logger.Info?.Print(LogClass.Gpu, "FSR: Processing completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"FSR: Error during processing: {ex.Message}");
                Logger.Info?.Print(LogClass.Gpu, "FSR: Falling back to bilinear scaling");
                FallbackToBilinear(view, cbs, destinationTexture, format, width, height, source, destination);
            }
        }

        private GalFormat ConvertVkFormatToGalFormat(VkFormat vkFormat)
        {
            // 将 Vulkan Format 转换为 GAL Format
            switch (vkFormat)
            {
                case VkFormat.B8G8R8A8Unorm:
                    return GalFormat.B8G8R8A8Unorm;
                case VkFormat.R8G8B8A8Unorm:
                    return GalFormat.R8G8B8A8Unorm;
                case VkFormat.R8Unorm:
                    return GalFormat.R8Unorm;
                case VkFormat.R8G8Unorm:
                    return GalFormat.R8G8Unorm;
                case VkFormat.R16G16B16A16Sfloat:
                    return GalFormat.R16G16B16A16Float;
                case VkFormat.R32G32B32A32Sfloat:
                    return GalFormat.R32G32B32A32Float;
                // 添加更多格式映射...
                default:
                    Logger.Warning?.Print(LogClass.Gpu, $"FSR: Unsupported Vulkan format {vkFormat}, defaulting to R8G8B8A8Unorm");
                    return GalFormat.R8G8B8A8Unorm;
            }
        }

        private void RunFsrProcessing(
            TextureView src,
            TextureView dst,
            CommandBufferScoped cbs,
            Extent2D srcRegion,
            Extent2D dstRegion,
            ReadOnlySpan<float> fsrConstants,
            ReadOnlySpan<float> rcasConstants)
        {
            _pipeline.SetCommandBuffer(cbs);

            // 第一遍：FSR 缩放 (EASU)
            RunFsrScalingPass(src, _intermediaryTexture, cbs, srcRegion, dstRegion, fsrConstants);
            
            // 第二遍：FSR 锐化 (RCAS)
            RunFsrSharpeningPass(_intermediaryTexture, dst, cbs, 
                new Extent2D(0, 0, _intermediaryTexture.Width, _intermediaryTexture.Height),
                dstRegion, rcasConstants);

            _pipeline.Finish(_gd, cbs);
        }

        private void RunFsrScalingPass(
            TextureView src,
            TextureView dst,
            CommandBufferScoped cbs,
            Extent2D srcRegion,
            Extent2D dstRegion,
            ReadOnlySpan<float> fsrConstants)
        {
            Logger.Info?.Print(LogClass.Gpu, "FSR: Starting scaling pass");

            _pipeline.SetCommandBuffer(cbs);

            const int RegionBufferSize = 16;
            const int FsrConstantsSize = 64;

            ISampler sampler = _samplerLinear;
            _pipeline.SetTextureAndSampler(ShaderStage.Fragment, 0, src, sampler);

            Span<float> region = stackalloc float[RegionBufferSize / sizeof(float)];

            // 无分支优化计算纹理坐标
            float srcX1 = (float)srcRegion.X1 / src.Width;
            float srcX2 = (float)srcRegion.X2 / src.Width;
            float srcY1 = (float)srcRegion.Y1 / src.Height;
            float srcY2 = (float)srcRegion.Y2 / src.Height;

            int flipX = (dstRegion.X1 > dstRegion.X2) ? 1 : 0;
            int flipY = (dstRegion.Y1 > dstRegion.Y2) ? 1 : 0;

            region[0] = (1 - flipX) * srcX1 + flipX * srcX2;
            region[1] = flipX * srcX1 + (1 - flipX) * srcX2;
            region[2] = (1 - flipY) * srcY1 + flipY * srcY2;
            region[3] = flipY * srcY1 + (1 - flipY) * srcY2;

            using ScopedTemporaryBuffer regionBuffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, RegionBufferSize);
            regionBuffer.Holder.SetDataUnchecked<float>(regionBuffer.Offset, region);

            using ScopedTemporaryBuffer fsrConstantsBuffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, FsrConstantsSize);
            fsrConstantsBuffer.Holder.SetDataUnchecked<float>(fsrConstantsBuffer.Offset, fsrConstants);

            _pipeline.SetUniformBuffers(new[]
            {
                new BufferAssignment(1, regionBuffer.Range),
                new BufferAssignment(2, fsrConstantsBuffer.Range)
            });

            // 使用完全限定名解决 Viewport 歧义
            Span<Ryujinx.Graphics.GAL.Viewport> viewports = stackalloc Ryujinx.Graphics.GAL.Viewport[1];
            Rectangle<float> rect = new(
                MathF.Min(dstRegion.X1, dstRegion.X2),
                MathF.Min(dstRegion.Y1, dstRegion.Y2),
                MathF.Abs(dstRegion.X2 - dstRegion.X1),
                MathF.Abs(dstRegion.Y2 - dstRegion.Y1));

            viewports[0] = new Ryujinx.Graphics.GAL.Viewport(
                rect,
                ViewportSwizzle.PositiveX,
                ViewportSwizzle.PositiveY,
                ViewportSwizzle.PositiveZ,
                ViewportSwizzle.PositiveW,
                0f,
                1f);

            if (_programFSRScaling == null)
            {
                Logger.Error?.Print(LogClass.Gpu, "FSR: Scaling program is null");
                throw new InvalidOperationException("FSR scaling program is not available");
            }

            _pipeline.SetProgram(_programFSRScaling);
            _pipeline.SetRenderTarget(dst, (uint)dst.Width, (uint)dst.Height);
            
            // 修复颜色掩码参数类型
            _pipeline.SetRenderTargetColorMasks(new uint[] { 0xf });
            
            _pipeline.SetScissors(new[] { new Rectangle<int>(0, 0, dst.Width, dst.Height) });
            _pipeline.SetViewports(viewports);
            
            // 使用完全限定名解决 PrimitiveTopology 歧义
            _pipeline.SetPrimitiveTopology(Ryujinx.Graphics.GAL.PrimitiveTopology.TriangleStrip);
            _pipeline.Draw(4, 1, 0, 0);

            Logger.Info?.Print(LogClass.Gpu, "FSR: Scaling pass completed");
        }

        private void RunFsrSharpeningPass(
            TextureView src,
            TextureView dst,
            CommandBufferScoped cbs,
            Extent2D srcRegion,
            Extent2D dstRegion,
            ReadOnlySpan<float> rcasConstants)
        {
            Logger.Info?.Print(LogClass.Gpu, "FSR: Starting sharpening pass");

            _pipeline.SetCommandBuffer(cbs);

            const int RegionBufferSize = 16;
            const int RcasConstantsSize = 28;

            ISampler sampler = _samplerLinear;
            _pipeline.SetTextureAndSampler(ShaderStage.Fragment, 0, src, sampler);

            Span<float> region = stackalloc float[RegionBufferSize / sizeof(float)];

            float srcX1 = (float)srcRegion.X1 / src.Width;
            float srcX2 = (float)srcRegion.X2 / src.Width;
            float srcY1 = (float)srcRegion.Y1 / src.Height;
            float srcY2 = (float)srcRegion.Y2 / src.Height;

            int flipX = (dstRegion.X1 > dstRegion.X2) ? 1 : 0;
            int flipY = (dstRegion.Y1 > dstRegion.Y2) ? 1 : 0;

            region[0] = (1 - flipX) * srcX1 + flipX * srcX2;
            region[1] = flipX * srcX1 + (1 - flipX) * srcX2;
            region[2] = (1 - flipY) * srcY1 + flipY * srcY2;
            region[3] = flipY * srcY1 + (1 - flipY) * srcY2;

            using ScopedTemporaryBuffer regionBuffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, RegionBufferSize);
            regionBuffer.Holder.SetDataUnchecked<float>(regionBuffer.Offset, region);

            using ScopedTemporaryBuffer rcasConstantsBuffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, RcasConstantsSize);
            rcasConstantsBuffer.Holder.SetDataUnchecked<float>(rcasConstantsBuffer.Offset, rcasConstants);

            _pipeline.SetUniformBuffers(new[]
            {
                new BufferAssignment(1, regionBuffer.Range),
                new BufferAssignment(2, rcasConstantsBuffer.Range)
            });

            // 使用完全限定名解决 Viewport 歧义
            Span<Ryujinx.Graphics.GAL.Viewport> viewports = stackalloc Ryujinx.Graphics.GAL.Viewport[1];
            Rectangle<float> rect = new(
                MathF.Min(dstRegion.X1, dstRegion.X2),
                MathF.Min(dstRegion.Y1, dstRegion.Y2),
                MathF.Abs(dstRegion.X2 - dstRegion.X1),
                MathF.Abs(dstRegion.Y2 - dstRegion.Y1));

            viewports[0] = new Ryujinx.Graphics.GAL.Viewport(
                rect,
                ViewportSwizzle.PositiveX,
                ViewportSwizzle.PositiveY,
                ViewportSwizzle.PositiveZ,
                ViewportSwizzle.PositiveW,
                0f,
                1f);

            if (_programFSRSharpening == null)
            {
                Logger.Error?.Print(LogClass.Gpu, "FSR: Sharpening program is null");
                throw new InvalidOperationException("FSR sharpening program is not available");
            }

            _pipeline.SetProgram(_programFSRSharpening);
            _pipeline.SetRenderTarget(dst, (uint)dst.Width, (uint)dst.Height);
            
            // 修复颜色掩码参数类型
            _pipeline.SetRenderTargetColorMasks(new uint[] { 0xf });
            
            _pipeline.SetScissors(new[] { new Rectangle<int>(0, 0, dst.Width, dst.Height) });
            _pipeline.SetViewports(viewports);
            
            // 使用完全限定名解决 PrimitiveTopology 歧义
            _pipeline.SetPrimitiveTopology(Ryujinx.Graphics.GAL.PrimitiveTopology.TriangleStrip);
            _pipeline.Draw(4, 1, 0, 0);

            Logger.Info?.Print(LogClass.Gpu, "FSR: Sharpening pass completed");
        }

        private void FallbackToBilinear(
            TextureView view,
            CommandBufferScoped cbs,
            Auto<DisposableImageView> destinationTexture,
            VkFormat format,
            int width,
            int height,
            Extent2D source,
            Extent2D destination)
        {
            var textureCreateInfo = new TextureCreateInfo(
                width, height, 1, 1, 1, 1, 1, 1,
                ConvertVkFormatToGalFormat(format),
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha);

            // 使用正确的 TextureView 构造函数，传递 VkFormat
            using var dstView = new TextureView(_gd, _device, destinationTexture, textureCreateInfo, format);

            _gd.HelperShader.BlitColor(
                _gd, cbs, view, dstView,
                source,
                new Extent2D(destination.X1, destination.Y2, destination.X2, destination.Y1), // 注意 Y 坐标翻转
                true, true);
        }

        private void CreateIntermediaryTexture(int width, int height, GalFormat format)
        {
            Logger.Info?.Print(LogClass.Gpu, $"FSR: Creating intermediary texture {width}x{height} with format {format}");

            _intermediaryTexture?.Dispose();

            try
            {
                var info = new TextureCreateInfo(
                    width, height, 1, 1, 1, 1, 1, 1,
                    format,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red,
                    SwizzleComponent.Green,
                    SwizzleComponent.Blue,
                    SwizzleComponent.Alpha);

                _intermediaryTexture = _gd.CreateTexture(info) as TextureView;

                if (_intermediaryTexture == null)
                {
                    Logger.Error?.Print(LogClass.Gpu, "FSR: Failed to create intermediary texture - CreateTexture returned null");
                }
                else
                {
                    Logger.Info?.Print(LogClass.Gpu, $"FSR: Successfully created intermediary texture {_intermediaryTexture.Width}x{_intermediaryTexture.Height}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"FSR: Error creating intermediary texture: {ex.Message}");
                _intermediaryTexture = null;
            }
        }

        private float[] CalculateFsrConstants(Extent2D source, Extent2D destination, int width, int height)
        {
            Logger.Info?.Print(LogClass.Gpu, $"FSR: Calculating constants for source={source}, destination={destination}, output={width}x{height}");

            float viewportWidth = Math.Abs(source.X2 - source.X1);
            float viewportHeight = Math.Abs(source.Y2 - source.Y1);
            float viewportX = source.X1;
            float viewportY = source.Y1;

            float inputWidth = viewportWidth;
            float inputHeight = viewportHeight;
            float outputWidth = width;
            float outputHeight = height;

            Logger.Info?.Print(LogClass.Gpu, $"FSR: Viewport={viewportWidth}x{viewportHeight} at ({viewportX},{viewportY})");

            // 计算 EASU 常量
            Span<uint> con0 = stackalloc uint[4];
            Span<uint> con1 = stackalloc uint[4];
            Span<uint> con2 = stackalloc uint[4];
            Span<uint> con3 = stackalloc uint[4];

            FsrEasuConOffset(
                con0, con1, con2, con3,
                viewportWidth, viewportHeight,
                inputWidth, inputHeight,
                outputWidth, outputHeight,
                viewportX, viewportY);

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

            Logger.Info?.Print(LogClass.Gpu, "FSR: Constants calculated successfully");
            return constants;
        }

        private float[] CalculateRcasConstants(int width, int height)
        {
            // 计算 RCAS 常量
            Span<uint> rcasCon = stackalloc uint[4];
            FsrRcasCon(rcasCon, Level);

            Logger.Info?.Print(LogClass.Gpu, $"FSR: RCAS sharpness level = {Level}");

            float[] constants = new float[7];

            // RCAS 常量
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

        // FSR 常量计算器
        private static void FsrEasuConOffset(
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
            // 基于 FFX FSR 的 EASU 常量计算
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

        private static void FsrRcasCon(Span<uint> constants, float sharpness)
        {
            // 基于 FFX FSR 的 RCAS 常量计算
            // 锐化调整 - 将 [0,1] 范围转换为 RCAS 参数
            float sharp = 1.0f - sharpness * 0.99f;
            float sharpScale = sharp * 8.0f;

            // con0 - 锐化参数
            constants[0] = ToUint(sharpScale);
            constants[1] = ToUint(sharpScale);
            constants[2] = ToUint(sharpScale);
            constants[3] = ToUint(0.0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ToUint(float value)
        {
            return BitConverter.SingleToUInt32Bits(value);
        }

        public void Dispose()
        {
            _intermediaryTexture?.Dispose();
            _programFSRScaling?.Dispose();
            _programFSRSharpening?.Dispose();
            _samplerLinear?.Dispose();
            _pipeline?.Dispose();
        }
    }
}
