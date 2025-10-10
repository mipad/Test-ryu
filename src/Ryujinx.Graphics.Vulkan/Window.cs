using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Effects;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using VkFormat = Silk.NET.Vulkan.Format;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Ryujinx.Graphics.Vulkan
{
    class Window : WindowBase, IDisposable
    {
        private const int SurfaceWidth = 1280;
        private const int SurfaceHeight = 720;

        private readonly VulkanRenderer _gd;
        private readonly PhysicalDevice _physicalDevice;
        private readonly Device _device;
        private SwapchainKHR _swapchain;
        private SurfaceKHR _surface;

        private Image[] _swapchainImages;
        private TextureView[] _swapchainImageViews;

        private VkSemaphore[] _imageAvailableSemaphores;
        private VkSemaphore[] _renderFinishedSemaphores;

        private int _frameIndex;

        private int _width;
        private int _height;
        private bool _vsyncEnabled;
        private bool _swapchainIsDirty;
        private VkFormat _format;
        private AntiAliasing _currentAntiAliasing;
        private bool _updateEffect;
        private IPostProcessingEffect _effect;
        private IScalingFilter _scalingFilter;
        private bool _isLinear;
        private float _scalingFilterLevel;
        private bool _updateScalingFilter;
        private ScalingFilter _currentScalingFilter;
        private bool _colorSpacePassthroughEnabled;

        // Mali GPU检测和状态
        private bool _isMaliGpu;
        private bool _maliSupportsAdvancedScaling;
        private string _gpuName;

        // Android特定：交换链恢复计数器
#if ANDROID
        private int _swapchainRecoveryAttempts = 0;
        private const int MaxSwapchainRecoveryAttempts = 3;
#endif

        public unsafe Window(VulkanRenderer gd, SurfaceKHR surface, PhysicalDevice physicalDevice, Device device)
        {
            _gd = gd;
            _physicalDevice = physicalDevice;
            _device = device;
            _surface = surface;

            // 检测GPU类型和功能
            DetectGpuFeatures();

            CreateSwapchain();
        }

        private unsafe void DetectGpuFeatures()
        {
            _gd.Api.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
            
            // 修复编译错误：正确获取GPU名称
            _gpuName = Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName) ?? "Unknown";
            Logger.Info?.Print(LogClass.Gpu, $"GPU detected: {_gpuName}");

            // 检测Mali GPU
            _isMaliGpu = _gpuName.Contains("Mali", StringComparison.OrdinalIgnoreCase) || 
                         _gpuName.Contains("ARM", StringComparison.OrdinalIgnoreCase);

            if (_isMaliGpu)
            {
                Logger.Info?.Print(LogClass.Gpu, "Mali GPU detected, applying compatibility fixes");
                
                // 检查Mali GPU的Storage Image支持
                _gd.Api.GetPhysicalDeviceFormatProperties(_physicalDevice, VkFormat.B8G8R8A8Unorm, out var formatProps);
                bool supportsStorage = formatProps.OptimalTilingFeatures.HasFlag(FormatFeatureFlags.StorageImageBit);
                
                Logger.Info?.Print(LogClass.Gpu, $"Mali GPU Storage Image support: {supportsStorage}");
                
                // 根据Mali型号决定高级缩放器支持
                _maliSupportsAdvancedScaling = CheckMaliAdvancedScalingSupport(properties, supportsStorage);
                
                if (!_maliSupportsAdvancedScaling)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Mali GPU: Advanced scaling filters (FSR/Area) disabled due to compatibility issues");
                }
            }
            else
            {
                _maliSupportsAdvancedScaling = true; // 非Mali GPU默认支持
            }

            // 记录设备限制
            Logger.Debug?.Print(LogClass.Gpu, $"Max storage image dimensions: {properties.Limits.MaxImageDimension2D}");
        }

        private bool CheckMaliAdvancedScalingSupport(PhysicalDeviceProperties properties, bool supportsStorage)
        {
            if (!supportsStorage)
            {
                return false;
            }

            // 根据Mali型号决定支持程度
            string lowerName = _gpuName.ToLowerInvariant();
            
            // 较旧的Mali型号可能不支持或支持不完善
            if (lowerName.Contains("mali-t") || 
                lowerName.Contains("mali-g51") || 
                lowerName.Contains("mali-g52") ||
                lowerName.Contains("mali-4") ||
                lowerName.Contains("mali-3"))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Older Mali GPU model detected: {_gpuName}, advanced scaling may be unstable");
                return false;
            }

            // 较新的Mali型号应该支持
            if (lowerName.Contains("mali-g") && 
               (lowerName.Contains("mali-g71") || 
                lowerName.Contains("mali-g72") ||
                lowerName.Contains("mali-g76") ||
                lowerName.Contains("mali-g77") ||
                lowerName.Contains("mali-g78") ||
                lowerName.Contains("mali-g710")))
            {
                return true;
            }

            // 默认情况下，对于未知的Mali型号，我们保守地禁用高级缩放
            Logger.Warning?.Print(LogClass.Gpu, $"Unknown Mali GPU model: {_gpuName}, disabling advanced scaling for safety");
            return false;
        }

        private void RecreateSwapchain()
        {
            var oldSwapchain = _swapchain;
            _swapchainIsDirty = false;

            for (int i = 0; i < _swapchainImageViews.Length; i++)
            {
                _swapchainImageViews[i]?.Dispose();
            }

            // Destroy old Swapchain.
            _gd.Api.DeviceWaitIdle(_device);

            unsafe
            {
                for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                {
                    _gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                }

                for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                {
                    _gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                }
            }

            _gd.SwapchainApi.DestroySwapchain(_device, oldSwapchain, Span<AllocationCallbacks>.Empty);

            CreateSwapchain();
        }

        internal void SetSurface(SurfaceKHR surface)
        {
            _surface = surface;
            RecreateSwapchain();
        }

        private unsafe bool TryCreateSwapchain(out string errorMessage)
        {
            errorMessage = null;
            
#if ANDROID
            if (_swapchainRecoveryAttempts >= MaxSwapchainRecoveryAttempts)
            {
                errorMessage = "Maximum swapchain recovery attempts reached";
                return false;
            }
            
            _swapchainRecoveryAttempts++;
#endif

            try
            {
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities);

                uint surfaceFormatsCount;
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, null);

                var surfaceFormats = new SurfaceFormatKHR[surfaceFormatsCount];
                fixed (SurfaceFormatKHR* pSurfaceFormats = surfaceFormats)
                {
                    _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, pSurfaceFormats);
                }

                uint presentModesCount;
                _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, null);

                var presentModes = new PresentModeKHR[presentModesCount];
                fixed (PresentModeKHR* pPresentModes = presentModes)
                {
                    _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, pPresentModes);
                }

                uint imageCount = capabilities.MinImageCount + 1;
                if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
                {
                    imageCount = capabilities.MaxImageCount;
                }

                // 对于Mali GPU和高级缩放器，使用标准颜色空间而不是直通
                bool useStandardColorSpace = _isMaliGpu && 
                    (_currentScalingFilter == ScalingFilter.Fsr || _currentScalingFilter == ScalingFilter.Area);
                
                var surfaceFormat = ChooseSwapSurfaceFormat(surfaceFormats, _colorSpacePassthroughEnabled && !useStandardColorSpace);
                var extent = ChooseSwapExtent(capabilities);

                _width = (int)extent.Width;
                _height = (int)extent.Height;
                _format = surfaceFormat.Format;

                var oldSwapchain = _swapchain;
                CurrentTransform = capabilities.CurrentTransform;

                // 动态确定图像使用标志
                var imageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit;
                
                // 检查是否需要StorageBit用于高级缩放器
                bool needsStorageForScaling = (_currentScalingFilter == ScalingFilter.Fsr || 
                                             _currentScalingFilter == ScalingFilter.Area) &&
                                             _maliSupportsAdvancedScaling;
                
#if ANDROID
                // 在Android上，只有当需要高级缩放器且Mali GPU支持时才包含StorageBit
                if (needsStorageForScaling)
                {
                    imageUsage |= ImageUsageFlags.StorageBit;
                    Logger.Debug?.Print(LogClass.Gpu, "Including StorageBit for advanced scaling filter");
                }
                else if (_isMaliGpu && (_currentScalingFilter == ScalingFilter.Fsr || _currentScalingFilter == ScalingFilter.Area))
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Mali GPU: Advanced scaling requested but not supported, StorageBit excluded");
                }
                else
                {
                    Logger.Debug?.Print(LogClass.Gpu, "Using optimized swapchain usage flags (no StorageBit)");
                }
#else
                // 非Android平台始终包含StorageBit以获得最佳兼容性
                imageUsage |= ImageUsageFlags.StorageBit;
#endif

                var swapchainCreateInfo = new SwapchainCreateInfoKHR
                {
                    SType = StructureType.SwapchainCreateInfoKhr,
                    Surface = _surface,
                    MinImageCount = imageCount,
                    ImageFormat = surfaceFormat.Format,
                    ImageColorSpace = surfaceFormat.ColorSpace,
                    ImageExtent = extent,
                    ImageUsage = imageUsage,
                    ImageSharingMode = SharingMode.Exclusive,
                    ImageArrayLayers = 1,
                    PreTransform = capabilities.CurrentTransform,
                    CompositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
                    PresentMode = ChooseSwapPresentMode(presentModes, _vsyncEnabled),
                    Clipped = true,
                };

                // Mali GPU特定：使用更兼容的设置
                if (_isMaliGpu)
                {
                    // 确保使用标准的预变换
                    if (swapchainCreateInfo.PreTransform != SurfaceTransformFlagsKHR.IdentityBitKhr)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, "Mali GPU: Ensuring compatible pre-transform");
                        // 某些Mali设备对非Identity变换支持不佳
                        if (capabilities.SupportedTransforms.HasFlag(SurfaceTransformFlagsKHR.IdentityBitKhr))
                        {
                            swapchainCreateInfo.PreTransform = SurfaceTransformFlagsKHR.IdentityBitKhr;
                        }
                    }
                }

                var textureCreateInfo = new TextureCreateInfo(
                    _width,
                    _height,
                    1,
                    1,
                    1,
                    1,
                    1,
                    1,
                    FormatTable.GetFormat(surfaceFormat.Format),
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red,
                    SwizzleComponent.Green,
                    SwizzleComponent.Blue,
                    SwizzleComponent.Alpha);

                Result result = _gd.SwapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain);
                
                if (result != Result.Success)
                {
                    // 尝试备用格式和设置
#if ANDROID
                    Logger.Warning?.Print(LogClass.Gpu, $"Swapchain creation failed: {result}, trying fallback options");
                    
                    // 回退策略1：尝试使用B8G8R8A8Unorm格式
                    surfaceFormat.Format = VkFormat.B8G8R8A8Unorm;
                    swapchainCreateInfo.ImageFormat = surfaceFormat.Format;
                    result = _gd.SwapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain);
                    
                    if (result != Result.Success)
                    {
                        // 回退策略2：移除StorageBit
                        Logger.Warning?.Print(LogClass.Gpu, $"Swapchain creation failed with fallback format: {result}, removing StorageBit");
                        swapchainCreateInfo.ImageUsage &= ~ImageUsageFlags.StorageBit;
                        result = _gd.SwapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain);
                        
                        if (result != Result.Success)
                        {
                            errorMessage = $"Swapchain creation failed after all fallback attempts: {result}";
                            return false;
                        }
                        else
                        {
                            Logger.Info?.Print(LogClass.Gpu, "Swapchain created successfully without StorageBit");
                        }
                    }
#else
                    errorMessage = $"Swapchain creation failed: {result}";
                    return false;
#endif
                }

                _gd.SwapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, null);
                _swapchainImages = new Image[imageCount];
                fixed (Image* pSwapchainImages = _swapchainImages)
                {
                    _gd.SwapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, pSwapchainImages);
                }

                _swapchainImageViews = new TextureView[imageCount];
                for (int i = 0; i < _swapchainImageViews.Length; i++)
                {
                    _swapchainImageViews[i] = CreateSwapchainImageView(_swapchainImages[i], surfaceFormat.Format, textureCreateInfo);
                }

                var semaphoreCreateInfo = new SemaphoreCreateInfo
                {
                    SType = StructureType.SemaphoreCreateInfo,
                };

                _imageAvailableSemaphores = new VkSemaphore[imageCount];
                for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                {
                    _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _imageAvailableSemaphores[i]).ThrowOnError();
                }

                _renderFinishedSemaphores = new VkSemaphore[imageCount];
                for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                {
                    _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _renderFinishedSemaphores[i]).ThrowOnError();
                }

#if ANDROID
                _swapchainRecoveryAttempts = 0; // 重置恢复计数器
#endif
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private unsafe void CreateSwapchain()
        {
            if (!TryCreateSwapchain(out string error))
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to create swapchain: {error}");
                
#if ANDROID
                // Android特定：尝试延迟恢复
                Logger.Info?.Print(LogClass.Gpu, "Will retry swapchain creation after delay");
                Thread.Sleep(100); // 短暂延迟后重试
                if (!TryCreateSwapchain(out error))
                {
                    throw new InvalidOperationException($"Unable to create swapchain: {error}");
                }
#else
                throw new InvalidOperationException($"Unable to create swapchain: {error}");
#endif
            }
        }

        private unsafe TextureView CreateSwapchainImageView(Image swapchainImage, VkFormat format, TextureCreateInfo info)
        {
            var componentMapping = new ComponentMapping(
                ComponentSwizzle.R,
                ComponentSwizzle.G,
                ComponentSwizzle.B,
                ComponentSwizzle.A);

            var aspectFlags = ImageAspectFlags.ColorBit;
            var subresourceRange = new ImageSubresourceRange(aspectFlags, 0, 1, 0, 1);

            var imageCreateInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = swapchainImage,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = componentMapping,
                SubresourceRange = subresourceRange,
            };

            var result = _gd.Api.CreateImageView(_device, in imageCreateInfo, null, out var imageView);
            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to create swapchain image view: {result}");
                result.ThrowOnError();
                return null;
            }

            return new TextureView(_gd, _device, new DisposableImageView(_gd.Api, _device, imageView), info, format);
        }

        private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats, bool colorSpacePassthroughEnabled)
        {
            // 定义标准颜色空间常量
            const ColorSpaceKHR StandardColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
            const VkFormat PreferredFormat = VkFormat.B8G8R8A8Unorm;

            // 空数组检查 - 返回安全默认值
            if (availableFormats.Length == 0)
            {
                return new SurfaceFormatKHR(PreferredFormat, StandardColorSpace);
            }

            // 特殊格式处理
            if (availableFormats.Length == 1 && availableFormats[0].Format == VkFormat.Undefined)
            {
                return new SurfaceFormatKHR(PreferredFormat, StandardColorSpace);
            }

            // Android特定：优先选择兼容格式
#if ANDROID
            // 在Android上优先选择B8G8R8A8Unorm格式
            foreach (var format in availableFormats)
            {
                if (format.Format == VkFormat.B8G8R8A8Unorm || 
                    format.Format == VkFormat.R8G8B8A8Unorm)
                {
                    return format;
                }
            }
#endif

            // 修复：使用正确的颜色空间枚举值
            if (colorSpacePassthroughEnabled)
            {
                // 优先选择PassThrough格式
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && 
                        format.ColorSpace == ColorSpaceKHR.SpacePassThroughExt)
                    {
                        return format;
                    }
                }
                
                // 其次选择标准SRGB格式
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && 
                        format.ColorSpace == StandardColorSpace)
                    {
                        return format;
                    }
                }
            }
            else
            {
                // 标准模式下优先选择SRGB格式
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && 
                        format.ColorSpace == StandardColorSpace)
                    {
                        return format;
                    }
                }
            }

            // 没有匹配时返回第一个可用格式
            return availableFormats[0];
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supportedFlags)
        {
            if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.OpaqueBitKhr))
            {
                return CompositeAlphaFlagsKHR.OpaqueBitKhr;
            }
            else if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.PreMultipliedBitKhr))
            {
                return CompositeAlphaFlagsKHR.PreMultipliedBitKhr;
            }
            else
            {
                return CompositeAlphaFlagsKHR.InheritBitKhr;
            }
        }

        private static PresentModeKHR ChooseSwapPresentMode(PresentModeKHR[] availablePresentModes, bool vsyncEnabled)
        {
            if (availablePresentModes.Length == 0)
            {
                return PresentModeKHR.FifoKhr; // 安全默认值
            }
            
            // Android特定：使用更稳定的呈现模式
#if ANDROID
            // 在Android上优先使用FIFO模式，更稳定
            if (availablePresentModes.Contains(PresentModeKHR.FifoKhr))
            {
                return PresentModeKHR.FifoKhr;
            }
#endif
            
            if (!vsyncEnabled && availablePresentModes.Contains(PresentModeKHR.ImmediateKhr))
            {
                return PresentModeKHR.ImmediateKhr;
            }
            else if (availablePresentModes.Contains(PresentModeKHR.MailboxKhr))
            {
                return PresentModeKHR.MailboxKhr;
            }
            else
            {
                return PresentModeKHR.FifoKhr;
            }
        }

        public static Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                return capabilities.CurrentExtent;
            }

            uint width = Math.Max(capabilities.MinImageExtent.Width, Math.Min(capabilities.MaxImageExtent.Width, SurfaceWidth));
            uint height = Math.Max(capabilities.MinImageExtent.Height, Math.Min(capabilities.MaxImageExtent.Height, SurfaceHeight));

            return new Extent2D(width, height);
        }

        public unsafe override void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            try
            {
                _gd.PipelineInternal.AutoFlush.Present();

                uint nextImage = 0;
                int semaphoreIndex = _frameIndex++ % _imageAvailableSemaphores.Length;

                while (true)
                {
                    var acquireResult = _gd.SwapchainApi.AcquireNextImage(
                        _device,
                        _swapchain,
                        1000000000, // 1秒超时，避免无限等待
                        _imageAvailableSemaphores[semaphoreIndex],
                        new Fence(),
                        ref nextImage);

                    if (acquireResult == Result.ErrorOutOfDateKhr ||
                        acquireResult == Result.SuboptimalKhr ||
                        _swapchainIsDirty)
                    {
                        Logger.Info?.Print(LogClass.Gpu, "Swapchain out of date, recreating...");
                        RecreateSwapchain();
                        semaphoreIndex = (_frameIndex - 1) % _imageAvailableSemaphores.Length;
                    }
                    else if(acquireResult == Result.ErrorSurfaceLostKhr)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, "Surface lost, recreating...");
                        _gd.RecreateSurface();
                    }
                    else if (acquireResult == Result.Timeout)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, "AcquireNextImage timeout, retrying...");
                        continue;
                    }
                    else
                    {
                        acquireResult.ThrowOnError();
                        break;
                    }
                }

                var swapchainImage = _swapchainImages[nextImage];

                _gd.FlushAllCommands();

                var cbs = _gd.CommandBufferPool.Rent();

                Transition(
                    cbs.CommandBuffer,
                    swapchainImage,
                    0,
                    AccessFlags.TransferWriteBit,
                    ImageLayout.Undefined,
                    ImageLayout.General);

                var view = (TextureView)texture;

                UpdateEffect();

                if (_effect != null)
                {
                    view = _effect.Run(view, cbs, _width, _height);
                }

                int srcX0, srcX1, srcY0, srcY1;

                if (crop.Left == 0 && crop.Right == 0)
                {
                    srcX0 = 0;
                    srcX1 = view.Width;
                }
                else
                {
                    srcX0 = crop.Left;
                    srcX1 = crop.Right;
                }

                if (crop.Top == 0 && crop.Bottom == 0)
                {
                    srcY0 = 0;
                    srcY1 = view.Height;
                }
                else
                {
                    srcY0 = crop.Top;
                    srcY1 = crop.Bottom;
                }

                if (ScreenCaptureRequested)
                {
                    if (_effect != null)
                    {
                        _gd.CommandBufferPool.Return(
                            cbs,
                            null,
                            stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit },
                            null);
                        _gd.FlushAllCommands();
                        cbs.GetFence().Wait();
                        cbs = _gd.CommandBufferPool.Rent();
                    }

                    CaptureFrame(view, srcX0, srcY0, srcX1 - srcX0, srcY1 - srcY0, view.Info.Format.IsBgr(), crop.FlipX, crop.FlipY);

                    ScreenCaptureRequested = false;
                }

                float ratioX = crop.IsStretched ? 1.0f : MathF.Min(1.0f, _height * crop.AspectRatioX / (_width * crop.AspectRatioY));
                float ratioY = crop.IsStretched ? 1.0f : MathF.Min(1.0f, _width * crop.AspectRatioY / (_height * crop.AspectRatioX));

                int dstWidth = (int)(_width * ratioX);
                int dstHeight = (int)(_height * ratioY);

                int dstPaddingX = (_width - dstWidth) / 2;
                int dstPaddingY = (_height - dstHeight) / 2;

                int dstX0 = crop.FlipX ? _width - dstPaddingX : dstPaddingX;
                int dstX1 = crop.FlipX ? dstPaddingX : _width - dstPaddingX;

                int dstY0 = crop.FlipY ? dstPaddingY : _height - dstPaddingY;
                int dstY1 = crop.FlipY ? _height - dstPaddingY : dstPaddingY;

                // Mali GPU特定：检查缩放器兼容性
                bool useScalingFilter = _scalingFilter != null;
                if (_isMaliGpu && useScalingFilter && !_maliSupportsAdvancedScaling)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Mali GPU: Advanced scaling filter requested but not supported, falling back to bilinear");
                    useScalingFilter = false;
                    _isLinear = true;
                }

                if (useScalingFilter)
                {
                    try
                    {
                        _scalingFilter.Run(
                            view,
                            cbs,
                            _swapchainImageViews[nextImage].GetImageViewForAttachment(),
                            _format,
                            _width,
                            _height,
                            new Extents2D(srcX0, srcY0, srcX1, srcY1),
                            new Extents2D(dstX0, dstY0, dstX1, dstY1)
                            );
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Gpu, $"Scaling filter failed: {ex.Message}, falling back to bilinear");
                        // 回退到双线性过滤
                        _gd.HelperShader.BlitColor(
                            _gd,
                            cbs,
                            view,
                            _swapchainImageViews[nextImage],
                            new Extents2D(srcX0, srcY0, srcX1, srcY1),
                            new Extents2D(dstX0, dstY1, dstX1, dstY0),
                            true, // 使用线性过滤
                            true);
                    }
                }
                else
                {
                    _gd.HelperShader.BlitColor(
                        _gd,
                        cbs,
                        view,
                        _swapchainImageViews[nextImage],
                        new Extents2D(srcX0, srcY0, srcX1, srcY1),
                        new Extents2D(dstX0, dstY1, dstX1, dstY0),
                        _isLinear,
                        true);
                }

                Transition(
                    cbs.CommandBuffer,
                    swapchainImage,
                    0,
                    0,
                    ImageLayout.General,
                    ImageLayout.PresentSrcKhr);

                _gd.CommandBufferPool.Return(
                    cbs,
                    stackalloc[] { _imageAvailableSemaphores[semaphoreIndex] },
                    stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit },
                    stackalloc[] { _renderFinishedSemaphores[semaphoreIndex] });

                // TODO: Present queue.
                var semaphore = _renderFinishedSemaphores[semaphoreIndex];
                var swapchain = _swapchain;

                Result result;

                var presentInfo = new PresentInfoKHR
                {
                    SType = StructureType.PresentInfoKhr,
                    WaitSemaphoreCount = 1,
                    PWaitSemaphores = &semaphore,
                    SwapchainCount = 1,
                    PSwapchains = &swapchain,
                    PImageIndices = &nextImage,
                    PResults = &result,
                };

                lock (_gd.QueueLock)
                {
                    _gd.SwapchainApi.QueuePresent(_gd.Queue, in presentInfo);
                }

                if (result != Result.Success && result != Result.SuboptimalKhr)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"QueuePresent returned: {result}");
                }

                //While this does nothing in most cases, it's useful to notify the end of the frame.
                swapBuffersCallback?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Present failed: {ex.Message}");
                
                // Android特定：尝试恢复
#if ANDROID
                Logger.Info?.Print(LogClass.Gpu, "Attempting swapchain recovery after present failure");
                _swapchainIsDirty = true;
                Thread.Sleep(16); // 延迟一帧
#else
                throw;
#endif
            }
        }

        public override void SetAntiAliasing(AntiAliasing effect)
        {
            if (_currentAntiAliasing == effect && _effect != null)
            {
                return;
            }

            _currentAntiAliasing = effect;

            _updateEffect = true;
        }

        public override void SetScalingFilter(ScalingFilter type)
        {
            if (_currentScalingFilter == type && _effect != null)
            {
                return;
            }

            // Mali GPU特定：检查高级缩放器支持
            if (_isMaliGpu && (type == ScalingFilter.Fsr || type == ScalingFilter.Area) && !_maliSupportsAdvancedScaling)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Mali GPU: {type} scaling filter not supported, using bilinear instead");
                type = ScalingFilter.Bilinear;
            }

            _currentScalingFilter = type;

            _updateScalingFilter = true;
            _swapchainIsDirty = true; // 缩放器改变可能需要重新创建交换链
        }

        public override void SetColorSpacePassthrough(bool colorSpacePassthroughEnabled)
        {
            _colorSpacePassthroughEnabled = colorSpacePassthroughEnabled;
            _swapchainIsDirty = true;
        }

        private void UpdateEffect()
        {
            if (_updateEffect)
            {
                _updateEffect = false;

                try
                {
                    switch (_currentAntiAliasing)
                    {
                        case AntiAliasing.Fxaa:
                            _effect?.Dispose();
                            _effect = new FxaaPostProcessingEffect(_gd, _device);
                            break;
                        case AntiAliasing.None:
                            _effect?.Dispose();
                            _effect = null;
                            break;
                        case AntiAliasing.SmaaLow:
                        case AntiAliasing.SmaaMedium:
                        case AntiAliasing.SmaaHigh:
                        case AntiAliasing.SmaaUltra:
                            var quality = _currentAntiAliasing - AntiAliasing.SmaaLow;
                            if (_effect is SmaaPostProcessingEffect smaa)
                            {
                                smaa.Quality = quality;
                            }
                            else
                            {
                                _effect?.Dispose();
                                _effect = new SmaaPostProcessingEffect(_gd, _device, quality);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Failed to update anti-aliasing effect: {ex.Message}");
                    // 回退到无抗锯齿
                    _effect?.Dispose();
                    _effect = null;
                    _currentAntiAliasing = AntiAliasing.None;
                }
            }

            if (_updateScalingFilter)
            {
                _updateScalingFilter = false;

                try
                {
                    switch (_currentScalingFilter)
                    {
                        case ScalingFilter.Bilinear:
                        case ScalingFilter.Nearest:
                            _scalingFilter?.Dispose();
                            _scalingFilter = null;
                            _isLinear = _currentScalingFilter == ScalingFilter.Bilinear;
                            break;
                        case ScalingFilter.Fsr:
                            if (_scalingFilter is not FsrScalingFilter)
                            {
                                _scalingFilter?.Dispose();
                                _scalingFilter = new FsrScalingFilter(_gd, _device);
                            }

                            _scalingFilter.Level = _scalingFilterLevel;
                            break;
                        case ScalingFilter.Area:
                            if (_scalingFilter is not AreaScalingFilter)
                            {
                                _scalingFilter?.Dispose();
                                _scalingFilter = new AreaScalingFilter(_gd, _device);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Failed to update scaling filter: {ex.Message}");
                    // 回退到双线性过滤
                    _scalingFilter?.Dispose();
                    _scalingFilter = null;
                    _isLinear = true;
                    _currentScalingFilter = ScalingFilter.Bilinear;
                }
            }
        }

        public override void SetScalingFilterLevel(float level)
        {
            _scalingFilterLevel = level;
            _updateScalingFilter = true;
        }

        private unsafe void Transition(
            CommandBuffer commandBuffer,
            Image image,
            AccessFlags srcAccess,
            AccessFlags dstAccess,
            ImageLayout srcLayout,
            ImageLayout dstLayout)
        {
            var subresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = srcLayout,
                NewLayout = dstLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                in barrier);
        }

        private void CaptureFrame(TextureView texture, int x, int y, int width, int height, bool isBgra, bool flipX, bool flipY)
        {
            byte[] bitmap = texture.GetData(x, y, width, height);

            _gd.OnScreenCaptured(new ScreenCaptureImageInfo(width, height, isBgra, bitmap, flipX, flipY));
        }

        public override void SetSize(int width, int height)
        {
            // We don't need to use width and height as we can get the size from the surface.
            _swapchainIsDirty = true;
        }

        public override void ChangeVSyncMode(bool vsyncEnabled)
        {
            _vsyncEnabled = vsyncEnabled;
            _swapchainIsDirty = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                unsafe
                {
                    for (int i = 0; i < _swapchainImageViews.Length; i++)
                    {
                        _swapchainImageViews[i]?.Dispose();
                    }

                    for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                    {
                        _gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                    }

                    for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                    {
                        _gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                    }

                    _gd.SwapchainApi.DestroySwapchain(_device, _swapchain, null);
                }

                _effect?.Dispose();
                _scalingFilter?.Dispose();
            }
        }

        public override void Dispose()
        {
            Dispose(true);
        }
    }
}
