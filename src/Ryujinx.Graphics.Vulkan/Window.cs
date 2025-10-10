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

            Logger.Info?.Print(LogClass.Gpu, "Window constructor started");
            
            // 检测GPU类型和功能
            DetectGpuFeatures();

            CreateSwapchain();
            
            Logger.Info?.Print(LogClass.Gpu, "Window constructor completed");
        }

        private unsafe void DetectGpuFeatures()
        {
            Logger.Debug?.Print(LogClass.Gpu, "Detecting GPU features...");
            
            _gd.Api.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
            
            // 修复编译错误：正确获取GPU名称
            _gpuName = Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName) ?? "Unknown";
            Logger.Info?.Print(LogClass.Gpu, $"GPU detected: {_gpuName}");

            // 检测Mali GPU
            _isMaliGpu = _gpuName.Contains("Mali", StringComparison.OrdinalIgnoreCase) || 
                         _gpuName.Contains("ARM", StringComparison.OrdinalIgnoreCase);

            if (_isMaliGpu)
            {
                Logger.Info?.Print(LogClass.Gpu, "Mali GPU detected, will attempt advanced scaling filters");
                
                // 检查Mali GPU的Storage Image支持
                _gd.Api.GetPhysicalDeviceFormatProperties(_physicalDevice, VkFormat.B8G8R8A8Unorm, out var formatProps);
                bool supportsStorage = formatProps.OptimalTilingFeatures.HasFlag(FormatFeatureFlags.StorageImageBit);
                
                Logger.Info?.Print(LogClass.Gpu, $"Mali GPU Storage Image support: {supportsStorage}");
                
                // 对于Mali GPU，我们仍然允许尝试高级缩放器，但会在运行时处理失败
                _maliSupportsAdvancedScaling = supportsStorage;
                
                if (!_maliSupportsAdvancedScaling)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Mali GPU: No Storage Image support, advanced scaling filters disabled");
                }
                else
                {
                    Logger.Info?.Print(LogClass.Gpu, "Mali GPU: Storage Image supported, advanced scaling filters enabled");
                }
            }
            else
            {
                _maliSupportsAdvancedScaling = true; // 非Mali GPU默认支持
            }

            // 记录设备限制
            Logger.Debug?.Print(LogClass.Gpu, $"Max storage image dimensions: {properties.Limits.MaxImageDimension2D}");
            Logger.Debug?.Print(LogClass.Gpu, "GPU feature detection completed");
        }

        private void RecreateSwapchain()
        {
            Logger.Info?.Print(LogClass.Gpu, "Recreating swapchain started");
            
            var oldSwapchain = _swapchain;
            _swapchainIsDirty = false;

            Logger.Debug?.Print(LogClass.Gpu, $"Disposing {_swapchainImageViews.Length} swapchain image views");
            for (int i = 0; i < _swapchainImageViews.Length; i++)
            {
                _swapchainImageViews[i]?.Dispose();
            }

            // Destroy old Swapchain.
            Logger.Debug?.Print(LogClass.Gpu, "Waiting for device idle");
            _gd.Api.DeviceWaitIdle(_device);

            unsafe
            {
                Logger.Debug?.Print(LogClass.Gpu, $"Destroying {_imageAvailableSemaphores.Length} image available semaphores");
                for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                {
                    _gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                }

                Logger.Debug?.Print(LogClass.Gpu, $"Destroying {_renderFinishedSemaphores.Length} render finished semaphores");
                for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                {
                    _gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                }
            }

            Logger.Debug?.Print(LogClass.Gpu, "Destroying old swapchain");
            _gd.SwapchainApi.DestroySwapchain(_device, oldSwapchain, Span<AllocationCallbacks>.Empty);

            CreateSwapchain();
            
            Logger.Info?.Print(LogClass.Gpu, "Recreating swapchain completed");
        }

        internal void SetSurface(SurfaceKHR surface)
        {
            Logger.Info?.Print(LogClass.Gpu, "Setting new surface");
            _surface = surface;
            RecreateSwapchain();
        }

        private unsafe bool TryCreateSwapchain(out string errorMessage)
        {
            Logger.Debug?.Print(LogClass.Gpu, "TryCreateSwapchain started");
            errorMessage = null;
            
#if ANDROID
            if (_swapchainRecoveryAttempts >= MaxSwapchainRecoveryAttempts)
            {
                errorMessage = "Maximum swapchain recovery attempts reached";
                Logger.Error?.Print(LogClass.Gpu, errorMessage);
                return false;
            }
            
            _swapchainRecoveryAttempts++;
            Logger.Debug?.Print(LogClass.Gpu, $"Swapchain recovery attempt: {_swapchainRecoveryAttempts}/{MaxSwapchainRecoveryAttempts}");
#endif

            try
            {
                Logger.Debug?.Print(LogClass.Gpu, "Getting surface capabilities");
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities);

                uint surfaceFormatsCount;
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, null);
                Logger.Debug?.Print(LogClass.Gpu, $"Available surface formats: {surfaceFormatsCount}");

                var surfaceFormats = new SurfaceFormatKHR[surfaceFormatsCount];
                fixed (SurfaceFormatKHR* pSurfaceFormats = surfaceFormats)
                {
                    _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, pSurfaceFormats);
                }

                uint presentModesCount;
                _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, null);
                Logger.Debug?.Print(LogClass.Gpu, $"Available present modes: {presentModesCount}");

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
                Logger.Debug?.Print(LogClass.Gpu, $"Swapchain image count: {imageCount} (min: {capabilities.MinImageCount}, max: {capabilities.MaxImageCount})");

                // 对于Mali GPU和高级缩放器，使用标准颜色空间而不是直通
                bool useStandardColorSpace = _isMaliGpu && 
                    (_currentScalingFilter == ScalingFilter.Fsr || _currentScalingFilter == ScalingFilter.Area);
                
                var surfaceFormat = ChooseSwapSurfaceFormat(surfaceFormats, _colorSpacePassthroughEnabled && !useStandardColorSpace);
                var extent = ChooseSwapExtent(capabilities);

                _width = (int)extent.Width;
                _height = (int)extent.Height;
                _format = surfaceFormat.Format;

                Logger.Info?.Print(LogClass.Gpu, $"Swapchain dimensions: {_width}x{_height}, format: {surfaceFormat.Format}, color space: {surfaceFormat.ColorSpace}");

                var oldSwapchain = _swapchain;
                CurrentTransform = capabilities.CurrentTransform;

                // 动态确定图像使用标志
                var imageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit;
                
                // 检查是否需要StorageBit用于高级缩放器
                bool needsStorageForScaling = (_currentScalingFilter == ScalingFilter.Fsr || 
                                             _currentScalingFilter == ScalingFilter.Area) &&
                                             _maliSupportsAdvancedScaling;
                
                Logger.Debug?.Print(LogClass.Gpu, $"Current scaling filter: {_currentScalingFilter}, needs storage: {needsStorageForScaling}, Mali supports: {_maliSupportsAdvancedScaling}");
                
#if ANDROID
                // 在Android上，只有当需要高级缩放器且Mali GPU支持时才包含StorageBit
                if (needsStorageForScaling)
                {
                    imageUsage |= ImageUsageFlags.StorageBit;
                    Logger.Debug?.Print(LogClass.Gpu, "Including StorageBit for advanced scaling filter");
                }
                else if (_isMaliGpu && (_currentScalingFilter == ScalingFilter.Fsr || _currentScalingFilter == ScalingFilter.Area))
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Mali GPU: Advanced scaling requested but StorageBit not supported, using fallback");
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

                Logger.Debug?.Print(LogClass.Gpu, $"Swapchain create info - PreTransform: {swapchainCreateInfo.PreTransform}, PresentMode: {swapchainCreateInfo.PresentMode}");

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
                            Logger.Debug?.Print(LogClass.Gpu, "Mali GPU: Changed pre-transform to Identity");
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

                Logger.Debug?.Print(LogClass.Gpu, "Creating swapchain...");
                Result result = _gd.SwapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain);
                
                if (result != Result.Success)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Initial swapchain creation failed: {result}");
                    
                    // 尝试备用格式和设置
#if ANDROID
                    Logger.Warning?.Print(LogClass.Gpu, $"Swapchain creation failed: {result}, trying fallback options");
                    
                    // 回退策略1：尝试使用B8G8R8A8Unorm格式
                    surfaceFormat.Format = VkFormat.B8G8R8A8Unorm;
                    swapchainCreateInfo.ImageFormat = surfaceFormat.Format;
                    Logger.Debug?.Print(LogClass.Gpu, "Trying fallback format: B8G8R8A8Unorm");
                    result = _gd.SwapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain);
                    
                    if (result != Result.Success)
                    {
                        Logger.Error?.Print(LogClass.Gpu, $"Swapchain creation failed with fallback format: {result}");
                        
                        // 回退策略2：移除StorageBit
                        Logger.Warning?.Print(LogClass.Gpu, $"Swapchain creation failed with fallback format: {result}, removing StorageBit");
                        swapchainCreateInfo.ImageUsage &= ~ImageUsageFlags.StorageBit;
                        result = _gd.SwapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain);
                        
                        if (result != Result.Success)
                        {
                            errorMessage = $"Swapchain creation failed after all fallback attempts: {result}";
                            Logger.Error?.Print(LogClass.Gpu, errorMessage);
                            return false;
                        }
                        else
                        {
                            Logger.Info?.Print(LogClass.Gpu, "Swapchain created successfully without StorageBit");
                        }
                    }
#else
                    errorMessage = $"Swapchain creation failed: {result}";
                    Logger.Error?.Print(LogClass.Gpu, errorMessage);
                    return false;
#endif
                }
                else
                {
                    Logger.Debug?.Print(LogClass.Gpu, "Swapchain created successfully");
                }

                _gd.SwapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, null);
                _swapchainImages = new Image[imageCount];
                fixed (Image* pSwapchainImages = _swapchainImages)
                {
                    _gd.SwapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, pSwapchainImages);
                }

                Logger.Debug?.Print(LogClass.Gpu, $"Retrieved {_swapchainImages.Length} swapchain images");

                _swapchainImageViews = new TextureView[imageCount];
                for (int i = 0; i < _swapchainImageViews.Length; i++)
                {
                    _swapchainImageViews[i] = CreateSwapchainImageView(_swapchainImages[i], surfaceFormat.Format, textureCreateInfo);
                    if (_swapchainImageViews[i] == null)
                    {
                        errorMessage = "Failed to create swapchain image view";
                        Logger.Error?.Print(LogClass.Gpu, errorMessage);
                        return false;
                    }
                }
                Logger.Debug?.Print(LogClass.Gpu, $"Created {_swapchainImageViews.Length} swapchain image views");

                var semaphoreCreateInfo = new SemaphoreCreateInfo
                {
                    SType = StructureType.SemaphoreCreateInfo,
                };

                _imageAvailableSemaphores = new VkSemaphore[imageCount];
                for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                {
                    _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _imageAvailableSemaphores[i]).ThrowOnError();
                }
                Logger.Debug?.Print(LogClass.Gpu, $"Created {_imageAvailableSemaphores.Length} image available semaphores");

                _renderFinishedSemaphores = new VkSemaphore[imageCount];
                for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                {
                    _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _renderFinishedSemaphores[i]).ThrowOnError();
                }
                Logger.Debug?.Print(LogClass.Gpu, $"Created {_renderFinishedSemaphores.Length} render finished semaphores");

#if ANDROID
                _swapchainRecoveryAttempts = 0; // 重置恢复计数器
                Logger.Debug?.Print(LogClass.Gpu, "Reset swapchain recovery attempts counter");
#endif
                
                Logger.Debug?.Print(LogClass.Gpu, "TryCreateSwapchain completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Logger.Error?.Print(LogClass.Gpu, $"TryCreateSwapchain failed with exception: {ex}");
                return false;
            }
        }

        private unsafe void CreateSwapchain()
        {
            Logger.Debug?.Print(LogClass.Gpu, "CreateSwapchain started");
            
            if (!TryCreateSwapchain(out string error))
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to create swapchain: {error}");
                
#if ANDROID
                // Android特定：尝试延迟恢复
                Logger.Info?.Print(LogClass.Gpu, "Will retry swapchain creation after delay");
                Thread.Sleep(100); // 短暂延迟后重试
                if (!TryCreateSwapchain(out error))
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Swapchain creation failed after retry: {error}");
                    throw new InvalidOperationException($"Unable to create swapchain: {error}");
                }
#else
                throw new InvalidOperationException($"Unable to create swapchain: {error}");
#endif
            }
            
            Logger.Debug?.Print(LogClass.Gpu, "CreateSwapchain completed");
        }

        private unsafe TextureView CreateSwapchainImageView(Image swapchainImage, VkFormat format, TextureCreateInfo info)
        {
            Logger.Debug?.Print(LogClass.Gpu, "Creating swapchain image view");
            
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

            Logger.Debug?.Print(LogClass.Gpu, "Swapchain image view created successfully");
            return new TextureView(_gd, _device, new DisposableImageView(_gd.Api, _device, imageView), info, format);
        }

        private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats, bool colorSpacePassthroughEnabled)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"Choosing swap surface format, colorSpacePassthrough: {colorSpacePassthroughEnabled}, available formats: {availableFormats.Length}");
            
            // 定义标准颜色空间常量
            const ColorSpaceKHR StandardColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
            const VkFormat PreferredFormat = VkFormat.B8G8R8A8Unorm;

            // 空数组检查 - 返回安全默认值
            if (availableFormats.Length == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, "No available surface formats, using default");
                return new SurfaceFormatKHR(PreferredFormat, StandardColorSpace);
            }

            // 特殊格式处理
            if (availableFormats.Length == 1 && availableFormats[0].Format == VkFormat.Undefined)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Undefined format detected, using preferred format");
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
                    Logger.Debug?.Print(LogClass.Gpu, $"Selected compatible Android format: {format.Format}");
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
                        Logger.Debug?.Print(LogClass.Gpu, "Selected PassThrough color space format");
                        return format;
                    }
                }
                
                // 其次选择标准SRGB格式
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && 
                        format.ColorSpace == StandardColorSpace)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, "Selected standard SRGB color space format");
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
                        Logger.Debug?.Print(LogClass.Gpu, "Selected standard SRGB color space format");
                        return format;
                    }
                }
            }

            // 没有匹配时返回第一个可用格式
            Logger.Debug?.Print(LogClass.Gpu, $"No preferred format found, using first available: {availableFormats[0].Format}, {availableFormats[0].ColorSpace}");
            return availableFormats[0];
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supportedFlags)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"Choosing composite alpha from supported flags: {supportedFlags}");
            
            if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.OpaqueBitKhr))
            {
                Logger.Debug?.Print(LogClass.Gpu, "Selected Opaque composite alpha");
                return CompositeAlphaFlagsKHR.OpaqueBitKhr;
            }
            else if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.PreMultipliedBitKhr))
            {
                Logger.Debug?.Print(LogClass.Gpu, "Selected PreMultiplied composite alpha");
                return CompositeAlphaFlagsKHR.PreMultipliedBitKhr;
            }
            else
            {
                Logger.Debug?.Print(LogClass.Gpu, "Selected Inherit composite alpha");
                return CompositeAlphaFlagsKHR.InheritBitKhr;
            }
        }

        private static PresentModeKHR ChooseSwapPresentMode(PresentModeKHR[] availablePresentModes, bool vsyncEnabled)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"Choosing present mode, vsync: {vsyncEnabled}, available modes: {availablePresentModes.Length}");
            
            if (availablePresentModes.Length == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, "No available present modes, using FIFO as fallback");
                return PresentModeKHR.FifoKhr; // 安全默认值
            }
            
            // Android特定：使用更稳定的呈现模式
#if ANDROID
            // 在Android上优先使用FIFO模式，更稳定
            if (availablePresentModes.Contains(PresentModeKHR.FifoKhr))
            {
                Logger.Debug?.Print(LogClass.Gpu, "Selected FIFO present mode for Android compatibility");
                return PresentModeKHR.FifoKhr;
            }
#endif
            
            if (!vsyncEnabled && availablePresentModes.Contains(PresentModeKHR.ImmediateKhr))
            {
                Logger.Debug?.Print(LogClass.Gpu, "Selected Immediate present mode (VSync off)");
                return PresentModeKHR.ImmediateKhr;
            }
            else if (availablePresentModes.Contains(PresentModeKHR.MailboxKhr))
            {
                Logger.Debug?.Print(LogClass.Gpu, "Selected Mailbox present mode");
                return PresentModeKHR.MailboxKhr;
            }
            else
            {
                Logger.Debug?.Print(LogClass.Gpu, "Selected FIFO present mode (fallback)");
                return PresentModeKHR.FifoKhr;
            }
        }

        public static Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                Logger.Debug?.Print(LogClass.Gpu, $"Using current surface extent: {capabilities.CurrentExtent.Width}x{capabilities.CurrentExtent.Height}");
                return capabilities.CurrentExtent;
            }

            uint width = Math.Max(capabilities.MinImageExtent.Width, Math.Min(capabilities.MaxImageExtent.Width, SurfaceWidth));
            uint height = Math.Max(capabilities.MinImageExtent.Height, Math.Min(capabilities.MaxImageExtent.Height, SurfaceHeight));
            
            Logger.Debug?.Print(LogClass.Gpu, $"Calculated swap extent: {width}x{height} (min: {capabilities.MinImageExtent.Width}x{capabilities.MinImageExtent.Height}, max: {capabilities.MaxImageExtent.Width}x{capabilities.MaxImageExtent.Height})");

            return new Extent2D(width, height);
        }

        public unsafe override void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            Logger.Debug?.Print(LogClass.Gpu, "Present started");
            
            try
            {
                _gd.PipelineInternal.AutoFlush.Present();

                uint nextImage = 0;
                int semaphoreIndex = _frameIndex++ % _imageAvailableSemaphores.Length;
                
                Logger.Debug?.Print(LogClass.Gpu, $"Frame index: {_frameIndex}, semaphore index: {semaphoreIndex}");

                int acquireAttempts = 0;
                const int maxAcquireAttempts = 3;
                
                while (true)
                {
                    acquireAttempts++;
                    if (acquireAttempts > maxAcquireAttempts)
                    {
                        Logger.Error?.Print(LogClass.Gpu, $"AcquireNextImage failed after {maxAcquireAttempts} attempts");
                        throw new TimeoutException("Failed to acquire next image after multiple attempts");
                    }
                    
                    Logger.Debug?.Print(LogClass.Gpu, $"Acquiring next image (attempt {acquireAttempts})");
                    
                    var acquireResult = _gd.SwapchainApi.AcquireNextImage(
                        _device,
                        _swapchain,
                        1000000000, // 1秒超时，避免无限等待
                        _imageAvailableSemaphores[semaphoreIndex],
                        new Fence(),
                        ref nextImage);

                    Logger.Debug?.Print(LogClass.Gpu, $"AcquireNextImage result: {acquireResult}, nextImage: {nextImage}");

                    if (acquireResult == Result.ErrorOutOfDateKhr ||
                        acquireResult == Result.SuboptimalKhr ||
                        _swapchainIsDirty)
                    {
                        Logger.Info?.Print(LogClass.Gpu, "Swapchain out of date, recreating...");
                        RecreateSwapchain();
                        semaphoreIndex = (_frameIndex - 1) % _imageAvailableSemaphores.Length;
                        continue;
                    }
                    else if(acquireResult == Result.ErrorSurfaceLostKhr)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, "Surface lost, recreating...");
                        _gd.RecreateSurface();
                        continue;
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

                Logger.Debug?.Print(LogClass.Gpu, $"Using swapchain image: {nextImage}");
                var swapchainImage = _swapchainImages[nextImage];

                Logger.Debug?.Print(LogClass.Gpu, "Flushing all commands");
                _gd.FlushAllCommands();

                Logger.Debug?.Print(LogClass.Gpu, "Renting command buffer");
                var cbs = _gd.CommandBufferPool.Rent();

                Logger.Debug?.Print(LogClass.Gpu, "Transitioning swapchain image layout");
                Transition(
                    cbs.CommandBuffer,
                    swapchainImage,
                    0,
                    AccessFlags.TransferWriteBit,
                    ImageLayout.Undefined,
                    ImageLayout.General);

                var view = (TextureView)texture;
                Logger.Debug?.Print(LogClass.Gpu, $"Source texture view: {view.Width}x{view.Height}, format: {view.Info.Format}");

                UpdateEffect();

                if (_effect != null)
                {
                    Logger.Debug?.Print(LogClass.Gpu, "Running anti-aliasing effect");
                    view = _effect.Run(view, cbs, _width, _height);
                    Logger.Debug?.Print(LogClass.Gpu, $"Post-effect texture view: {view.Width}x{view.Height}");
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

                Logger.Debug?.Print(LogClass.Gpu, $"Source crop: ({srcX0}, {srcY0}) to ({srcX1}, {srcY1})");

                if (ScreenCaptureRequested)
                {
                    Logger.Debug?.Print(LogClass.Gpu, "Screen capture requested");
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

                Logger.Debug?.Print(LogClass.Gpu, $"Destination: ({dstX0}, {dstY0}) to ({dstX1}, {dstY1}), scaled: {dstWidth}x{dstHeight}");

                // 只有在Mali GPU且明确不支持Storage时才回退
                bool useScalingFilter = _scalingFilter != null;
                if (_isMaliGpu && useScalingFilter && !_maliSupportsAdvancedScaling)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Mali GPU: Advanced scaling filter requested but StorageBit not supported, falling back to bilinear");
                    useScalingFilter = false;
                    _isLinear = true;
                }

                Logger.Debug?.Print(LogClass.Gpu, $"Using scaling filter: {useScalingFilter}, filter type: {_currentScalingFilter}, isLinear: {_isLinear}");

                if (useScalingFilter)
                {
                    try
                    {
                        Logger.Debug?.Print(LogClass.Gpu, $"Running scaling filter: {_scalingFilter.GetType().Name}");
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
                        Logger.Debug?.Print(LogClass.Gpu, "Scaling filter completed successfully");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Gpu, $"Scaling filter failed: {ex.Message}");
                        Logger.Error?.Print(LogClass.Gpu, $"Scaling filter stack trace: {ex.StackTrace}");
                        
                        // 回退到双线性过滤
                        Logger.Info?.Print(LogClass.Gpu, "Falling back to bilinear filtering");
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
                    Logger.Debug?.Print(LogClass.Gpu, "Using helper shader blit color");
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

                Logger.Debug?.Print(LogClass.Gpu, "Transitioning to present layout");
                Transition(
                    cbs.CommandBuffer,
                    swapchainImage,
                    0,
                    0,
                    ImageLayout.General,
                    ImageLayout.PresentSrcKhr);

                Logger.Debug?.Print(LogClass.Gpu, "Returning command buffer");
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

                Logger.Debug?.Print(LogClass.Gpu, "Queue present");
                lock (_gd.QueueLock)
                {
                    _gd.SwapchainApi.QueuePresent(_gd.Queue, in presentInfo);
                }

                Logger.Debug?.Print(LogClass.Gpu, $"QueuePresent result: {result}");
                if (result != Result.Success && result != Result.SuboptimalKhr)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"QueuePresent returned: {result}");
                }

                //While this does nothing in most cases, it's useful to notify the end of the frame.
                swapBuffersCallback?.Invoke();
                
                Logger.Debug?.Print(LogClass.Gpu, "Present completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Present failed: {ex.Message}");
                Logger.Error?.Print(LogClass.Gpu, $"Present stack trace: {ex.StackTrace}");
                
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
            Logger.Debug?.Print(LogClass.Gpu, $"SetAntiAliasing: {effect}, current: {_currentAntiAliasing}");
            
            if (_currentAntiAliasing == effect && _effect != null)
            {
                return;
            }

            _currentAntiAliasing = effect;

            _updateEffect = true;
        }

        public override void SetScalingFilter(ScalingFilter type)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"SetScalingFilter: {type}, current: {_currentScalingFilter}, isMali: {_isMaliGpu}, maliSupports: {_maliSupportsAdvancedScaling}");
            
            if (_currentScalingFilter == type && _effect != null)
            {
                return;
            }

            // 对于Mali GPU，我们允许尝试高级缩放器，不再预先阻止
            _currentScalingFilter = type;

            _updateScalingFilter = true;
            _swapchainIsDirty = true; // 缩放器改变可能需要重新创建交换链
            
            Logger.Debug?.Print(LogClass.Gpu, "Scaling filter updated, swapchain marked as dirty");
        }

        public override void SetColorSpacePassthrough(bool colorSpacePassthroughEnabled)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"SetColorSpacePassthrough: {colorSpacePassthroughEnabled}");
            _colorSpacePassthroughEnabled = colorSpacePassthroughEnabled;
            _swapchainIsDirty = true;
        }

        private void UpdateEffect()
        {
            Logger.Debug?.Print(LogClass.Gpu, $"UpdateEffect: _updateEffect={_updateEffect}, _updateScalingFilter={_updateScalingFilter}");

            if (_updateEffect)
            {
                _updateEffect = false;
                Logger.Debug?.Print(LogClass.Gpu, $"Updating anti-aliasing effect to: {_currentAntiAliasing}");

                try
                {
                    switch (_currentAntiAliasing)
                    {
                        case AntiAliasing.Fxaa:
                            Logger.Debug?.Print(LogClass.Gpu, "Creating FXAA effect");
                            _effect?.Dispose();
                            _effect = new FxaaPostProcessingEffect(_gd, _device);
                            break;
                        case AntiAliasing.None:
                            Logger.Debug?.Print(LogClass.Gpu, "Disabling anti-aliasing effect");
                            _effect?.Dispose();
                            _effect = null;
                            break;
                        case AntiAliasing.SmaaLow:
                        case AntiAliasing.SmaaMedium:
                        case AntiAliasing.SmaaHigh:
                        case AntiAliasing.SmaaUltra:
                            var quality = _currentAntiAliasing - AntiAliasing.SmaaLow;
                            Logger.Debug?.Print(LogClass.Gpu, $"Updating SMAA effect, quality: {quality}");
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
                    Logger.Debug?.Print(LogClass.Gpu, "Anti-aliasing effect updated successfully");
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Failed to update anti-aliasing effect: {ex.Message}");
                    Logger.Error?.Print(LogClass.Gpu, $"Anti-aliasing effect stack trace: {ex.StackTrace}");
                    
                    // 回退到无抗锯齿
                    _effect?.Dispose();
                    _effect = null;
                    _currentAntiAliasing = AntiAliasing.None;
                    Logger.Info?.Print(LogClass.Gpu, "Fallen back to no anti-aliasing");
                }
            }

            if (_updateScalingFilter)
            {
                _updateScalingFilter = false;
                Logger.Debug?.Print(LogClass.Gpu, $"Updating scaling filter to: {_currentScalingFilter}");

                try
                {
                    switch (_currentScalingFilter)
                    {
                        case ScalingFilter.Bilinear:
                        case ScalingFilter.Nearest:
                            Logger.Debug?.Print(LogClass.Gpu, $"Setting basic scaling filter: {_currentScalingFilter}");
                            _scalingFilter?.Dispose();
                            _scalingFilter = null;
                            _isLinear = _currentScalingFilter == ScalingFilter.Bilinear;
                            Logger.Debug?.Print(LogClass.Gpu, $"Basic scaling filter set, isLinear: {_isLinear}");
                            break;
                        case ScalingFilter.Fsr:
                            Logger.Debug?.Print(LogClass.Gpu, "Setting FSR scaling filter");
                            if (_scalingFilter is not FsrScalingFilter)
                            {
                                _scalingFilter?.Dispose();
                                _scalingFilter = new FsrScalingFilter(_gd, _device);
                            }

                            _scalingFilter.Level = _scalingFilterLevel;
                            Logger.Debug?.Print(LogClass.Gpu, $"FSR scaling filter set, level: {_scalingFilterLevel}");
                            break;
                        case ScalingFilter.Area:
                            Logger.Debug?.Print(LogClass.Gpu, "Setting Area scaling filter");
                            if (_scalingFilter is not AreaScalingFilter)
                            {
                                _scalingFilter?.Dispose();
                                _scalingFilter = new AreaScalingFilter(_gd, _device);
                            }
                            Logger.Debug?.Print(LogClass.Gpu, "Area scaling filter set");
                            break;
                    }
                    Logger.Debug?.Print(LogClass.Gpu, "Scaling filter updated successfully");
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Failed to update scaling filter: {ex.Message}");
                    Logger.Error?.Print(LogClass.Gpu, $"Scaling filter stack trace: {ex.StackTrace}");
                    
                    // 回退到双线性过滤
                    _scalingFilter?.Dispose();
                    _scalingFilter = null;
                    _isLinear = true;
                    _currentScalingFilter = ScalingFilter.Bilinear;
                    Logger.Info?.Print(LogClass.Gpu, "Fallen back to bilinear scaling filter");
                }
            }
        }

        public override void SetScalingFilterLevel(float level)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"SetScalingFilterLevel: {level}");
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
            Logger.Debug?.Print(LogClass.Gpu, $"Image transition: {srcLayout} -> {dstLayout}, access: {srcAccess} -> {dstAccess}");

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
            
            Logger.Debug?.Print(LogClass.Gpu, "Image transition completed");
        }

        private void CaptureFrame(TextureView texture, int x, int y, int width, int height, bool isBgra, bool flipX, bool flipY)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"CaptureFrame: ({x}, {y}) {width}x{height}, isBgra: {isBgra}, flip: ({flipX}, {flipY})");
            byte[] bitmap = texture.GetData(x, y, width, height);

            _gd.OnScreenCaptured(new ScreenCaptureImageInfo(width, height, isBgra, bitmap, flipX, flipY));
            Logger.Debug?.Print(LogClass.Gpu, "Frame captured");
        }

        public override void SetSize(int width, int height)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"SetSize: {width}x{height}");
            // We don't need to use width and height as we can get the size from the surface.
            _swapchainIsDirty = true;
        }

        public override void ChangeVSyncMode(bool vsyncEnabled)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"ChangeVSyncMode: {vsyncEnabled}");
            _vsyncEnabled = vsyncEnabled;
            _swapchainIsDirty = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            Logger.Debug?.Print(LogClass.Gpu, $"Dispose: {disposing}");
            
            if (disposing)
            {
                unsafe
                {
                    Logger.Debug?.Print(LogClass.Gpu, "Disposing swapchain resources");
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
                
                Logger.Debug?.Print(LogClass.Gpu, "All resources disposed");
            }
        }

        public override void Dispose()
        {
            Logger.Debug?.Print(LogClass.Gpu, "Window dispose called");
            Dispose(true);
        }
    }
}
