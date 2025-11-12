using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Effects;
using Silk.NET.Vulkan;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using VkFormat = Silk.NET.Vulkan.Format;
using Ryujinx.Common.Logging;
using Extents2D = Ryujinx.Graphics.GAL.Extents2D;

namespace Ryujinx.Graphics.Vulkan
{
    public class Window : WindowBase, IDisposable
    {
        private const int SurfaceWidth = 1280;
        private const int SurfaceHeight = 720;

        private readonly VulkanRenderer _gd;
        private readonly PhysicalDevice _physicalDevice;
        private readonly Device _device;
        private SurfaceKHR _surface;
        private SwapchainKHR _swapchain;

        private Image[] _swapchainImages;
        private TextureView[] _swapchainImageViews;

        private Semaphore[] _imageAvailableSemaphores;
        private Semaphore[] _renderFinishedSemaphores;

        private int _frameIndex;

        private int _width;
        private int _height;
        private bool _vsyncEnabled;
        private bool _swapchainIsDirty;
        private VkFormat _format;
        private AntiAliasing _currentAntiAliasing;
        private bool _updateEffect;
        private IPostProcessingEffect _effect;
        private bool _isLinear;
        private float _scalingFilterLevel;
        private bool _updateScalingFilter;
        private ScalingFilter _currentScalingFilter;
        private bool _colorSpacePassthroughEnabled;

        // 缩放过滤器
        private IScalingFilter _scalingFilter;

        // 自定义表面格式相关字段
        private static bool _useCustomSurfaceFormat = false;
        private static SurfaceFormatKHR _customSurfaceFormat;
        private static bool _customFormatValid = false;
        private static List<SurfaceFormatKHR> _availableSurfaceFormats = new List<SurfaceFormatKHR>();

        // 表面查询保护机制
        private volatile bool _allowSurfaceQueries = true;

        public SurfaceTransformFlagsKHR CurrentTransform { get; private set; }

        public bool ScreenCaptureRequested { get; set; }

        public unsafe Window(VulkanRenderer gd, SurfaceKHR surface, PhysicalDevice physicalDevice, Device device)
        {
            _gd = gd;
            _physicalDevice = physicalDevice;
            _device = device;
            _surface = surface;

            if (_gd.PresentAllowed && _surface.Handle != 0)
            {
                CreateSwapchain();
            }
            else
            {
                _swapchainIsDirty = true;
            }
        }

        public void SetSurfaceQueryAllowed(bool allowed) => _allowSurfaceQueries = allowed;
        private bool CanQuerySurface() => _allowSurfaceQueries && _gd.PresentAllowed && _surface.Handle != 0;

        private unsafe bool TryGetSurfaceCapabilities(out SurfaceCapabilitiesKHR caps)
        {
            caps = default;
            if (!CanQuerySurface()) return false;
            var res = _gd.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out caps);
            return res == Result.Success;
        }

        private unsafe bool TryGetSurfaceFormats(out SurfaceFormatKHR[] formats)
        {
            formats = Array.Empty<SurfaceFormatKHR>();
            if (!CanQuerySurface()) return false;

            uint count = 0;
            var res = _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &count, null);
            if (res != Result.Success || count == 0) return false;

            formats = new SurfaceFormatKHR[count];
            fixed (SurfaceFormatKHR* p = formats)
            {
                if (_gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &count, p) != Result.Success)
                    return false;
            }
            return true;
        }

        private unsafe bool TryGetPresentModes(out PresentModeKHR[] modes)
        {
            modes = Array.Empty<PresentModeKHR>();
            if (!CanQuerySurface()) return false;

            uint count = 0;
            var res = _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &count, null);
            if (res != Result.Success || count == 0) return false;

            modes = new PresentModeKHR[count];
            fixed (PresentModeKHR* p = modes)
            {
                if (_gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &count, p) != Result.Success)
                    return false;
            }
            return true;
        }

        private void RecreateSwapchain()
        {
            if (!_gd.PresentAllowed || _surface.Handle == 0 || !CanQuerySurface())
            {
                _swapchainIsDirty = true;
                return;
            }

            lock (_gd.SurfaceLock)
            {
                var oldSwapchain = _swapchain;
                _swapchainIsDirty = false;

                if (_swapchainImageViews != null)
                {
                    for (int i = 0; i < _swapchainImageViews.Length; i++)
                    {
                        _swapchainImageViews[i]?.Dispose();
                    }
                }

                _gd.Api.DeviceWaitIdle(_device);

                unsafe
                {
                    if (_imageAvailableSemaphores != null)
                    {
                        for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                        {
                            if (_imageAvailableSemaphores[i].Handle != 0)
                            {
                                _gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                            }
                        }
                    }

                    if (_renderFinishedSemaphores != null)
                    {
                        for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                        {
                            if (_renderFinishedSemaphores[i].Handle != 0)
                            {
                                _gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                            }
                        }
                    }
                }

                if (oldSwapchain.Handle != 0)
                {
                    _gd.SwapchainApi.DestroySwapchain(_device, oldSwapchain, Span<AllocationCallbacks>.Empty);
                }

                CreateSwapchain();
            }
        }

        internal void SetSurface(SurfaceKHR surface)
        {
            lock (_gd.SurfaceLock)
            {
                _surface = surface;

                if (!_gd.PresentAllowed || _surface.Handle == 0)
                {
                    _swapchainIsDirty = true;
                    return;
                }

                SetSurfaceQueryAllowed(true);
                RecreateSwapchain();
            }
        }

        private unsafe void CreateSwapchain()
        {
            if (!_gd.PresentAllowed || _surface.Handle == 0 || !CanQuerySurface())
            {
                _swapchainIsDirty = true;
                return;
            }

            lock (_gd.SurfaceLock)
            {
                if (!TryGetSurfaceCapabilities(out var capabilities))
                {
                    _swapchainIsDirty = true;
                    return;
                }

                if (!TryGetSurfaceFormats(out var surfaceFormats))
                {
                    _swapchainIsDirty = true;
                    return;
                }

                _availableSurfaceFormats.Clear();
                _availableSurfaceFormats.AddRange(surfaceFormats);

                LogAvailableSurfaceFormats(surfaceFormats);
                LogDetailedFormatInfo();

                if (!TryGetPresentModes(out var presentModes))
                {
                    _swapchainIsDirty = true;
                    return;
                }

                uint imageCount = capabilities.MinImageCount + 1;
                if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
                {
                    imageCount = capabilities.MaxImageCount;
                }

                var surfaceFormat = ChooseSwapSurfaceFormat(surfaceFormats, _colorSpacePassthroughEnabled);

                Logger.Info?.Print(LogClass.Gpu, $"Selected surface format: {GetFormatDisplayName(surfaceFormat.Format, surfaceFormat.ColorSpace)}");

                var extent = ChooseSwapExtent(capabilities);

                if (extent.Width == 0 || extent.Height == 0)
                {
                    _swapchainIsDirty = true;
                    return;
                }

                _width = (int)extent.Width;
                _height = (int)extent.Height;
                _format = surfaceFormat.Format;

                var oldSwapchain = _swapchain;

                CurrentTransform = capabilities.CurrentTransform;

                var usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit;
                if (!Ryujinx.Common.PlatformInfo.IsBionic)
                {
                    usage |= ImageUsageFlags.StorageBit;
                }

                var preTransform = Ryujinx.Common.PlatformInfo.IsBionic
                    ? SurfaceTransformFlagsKHR.IdentityBitKhr
                    : capabilities.CurrentTransform;

                var swapchainCreateInfo = new SwapchainCreateInfoKHR
                {
                    SType = StructureType.SwapchainCreateInfoKhr,
                    Surface = _surface,
                    MinImageCount = imageCount,
                    ImageFormat = surfaceFormat.Format,
                    ImageColorSpace = surfaceFormat.ColorSpace,
                    ImageExtent = extent,
                    ImageUsage = usage,
                    ImageSharingMode = SharingMode.Exclusive,
                    ImageArrayLayers = 1,
                    PreTransform = preTransform,
                    CompositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
                    PresentMode = ChooseSwapPresentMode(presentModes, _vsyncEnabled),
                    Clipped = true,
                };

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
                    result.ThrowOnError();
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

                _imageAvailableSemaphores = new Semaphore[imageCount];

                for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                {
                    _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _imageAvailableSemaphores[i]).ThrowOnError();
                }

                _renderFinishedSemaphores = new Semaphore[imageCount];

                for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                {
                    _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _renderFinishedSemaphores[i]).ThrowOnError();
                }
            }
        }

        private void LogAvailableSurfaceFormats(SurfaceFormatKHR[] availableFormats)
        {
            if (availableFormats.Length == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, "No available surface formats found!");
                return;
            }

            Logger.Info?.Print(LogClass.Gpu, $"Available surface formats ({availableFormats.Length}):");
            
            for (int i = 0; i < availableFormats.Length; i++)
            {
                var format = availableFormats[i];
                string formatInfo = $"  [{i}] {GetFormatDisplayName(format.Format, format.ColorSpace)}";
                
                if (format.Format == VkFormat.B8G8R8A8Unorm || format.Format == VkFormat.B8G8R8A8Srgb)
                {
                    formatInfo += " (Common)";
                }
                else if (format.Format == VkFormat.R8G8B8A8Unorm || format.Format == VkFormat.R8G8B8A8Srgb)
                {
                    formatInfo += " (RGBA)";
                }
                
                Logger.Info?.Print(LogClass.Gpu, formatInfo);
            }
        }
        
        private void LogDetailedFormatInfo()
        {
            Logger.Info?.Print(LogClass.Gpu, "=== Detailed Surface Format Information ===");
            
            if (_availableSurfaceFormats.Count == 0)
            {
                Logger.Info?.Print(LogClass.Gpu, "No surface formats available");
                return;
            }
            
            foreach (var format in _availableSurfaceFormats)
            {
                string formatInfo = $"Format: {format.Format}, ColorSpace: {format.ColorSpace}";
                
                switch (format.Format)
                {
                    case VkFormat.B8G8R8A8Unorm:
                        formatInfo += " (BGRA8 Unorm - Most Common Windows Format)";
                        break;
                    case VkFormat.R8G8B8A8Unorm:
                        formatInfo += " (RGBA8 Unorm - Common Linux/Android Format)";
                        break;
                    case VkFormat.R5G6B5UnormPack16:
                        formatInfo += " (RGB565 - 16-bit)";
                        break;
                    case VkFormat.R16G16B16A16Sfloat:
                        formatInfo += " (RGBA16 Float - HDR)";
                        break;
                    case VkFormat.A2B10G10R10UnormPack32:
                        formatInfo += " (A2B10G10R10 - 10-bit per channel)";
                        break;
                }
                
                Logger.Info?.Print(LogClass.Gpu, formatInfo);
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

            _gd.Api.CreateImageView(_device, in imageCreateInfo, null, out var imageView).ThrowOnError();

            return new TextureView(_gd, _device, new DisposableImageView(_gd.Api, _device, imageView), info, format);
        }

        private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats, bool colorSpacePassthroughEnabled)
        {
            if (_useCustomSurfaceFormat && _customFormatValid)
            {
                foreach (var format in availableFormats)
                {
                    if (format.Format == _customSurfaceFormat.Format && 
                        format.ColorSpace == _customSurfaceFormat.ColorSpace)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"Using custom surface format: {GetFormatDisplayName(format.Format, format.ColorSpace)}");
                        return format;
                    }
                }
                
                Logger.Warning?.Print(LogClass.Gpu, $"Custom surface format not available: {GetFormatDisplayName(_customSurfaceFormat.Format, _customSurfaceFormat.ColorSpace)}, falling back to automatic selection");
                _customFormatValid = false;
            }

            const ColorSpaceKHR StandardColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
            
            var preferredFormats = new[]
            {
                VkFormat.B8G8R8A8Unorm,
                VkFormat.R8G8B8A8Unorm,
                VkFormat.B8G8R8A8Srgb,
                VkFormat.R8G8B8A8Srgb,
                VkFormat.A2B10G10R10UnormPack32,
                VkFormat.A2R10G10B10UnormPack32,
            };

            if (availableFormats.Length == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, "No available surface formats, using fallback format");
                return new SurfaceFormatKHR(VkFormat.B8G8R8A8Unorm, StandardColorSpace);
            }

            if (availableFormats.Length == 1 && availableFormats[0].Format == VkFormat.Undefined)
            {
                Logger.Info?.Print(LogClass.Gpu, "Surface format undefined, using default format");
                return new SurfaceFormatKHR(VkFormat.B8G8R8A8Unorm, StandardColorSpace);
            }

            if (colorSpacePassthroughEnabled)
            {
                Logger.Info?.Print(LogClass.Gpu, "Color space passthrough enabled, looking for compatible formats...");
                
                foreach (var preferredFormat in preferredFormats)
                {
                    foreach (var format in availableFormats)
                    {
                        if (format.Format == preferredFormat && 
                            format.ColorSpace == ColorSpaceKHR.SpacePassThroughExt)
                        {
                            Logger.Info?.Print(LogClass.Gpu, $"Found preferred format with PassThrough color space: {GetFormatDisplayName(format.Format, format.ColorSpace)}");
                            return format;
                        }
                    }
                }
                
                foreach (var preferredFormat in preferredFormats)
                {
                    foreach (var format in availableFormats)
                    {
                        if (format.Format == preferredFormat && 
                            format.ColorSpace == StandardColorSpace)
                        {
                            Logger.Info?.Print(LogClass.Gpu, $"Found preferred format with SRGB color space: {GetFormatDisplayName(format.Format, format.ColorSpace)}");
                            return format;
                        }
                    }
                }
            }
            else
            {
                Logger.Info?.Print(LogClass.Gpu, "Standard color space mode, looking for SRGB formats...");
                
                foreach (var preferredFormat in preferredFormats)
                {
                    foreach (var format in availableFormats)
                    {
                        if (format.Format == preferredFormat && 
                            format.ColorSpace == StandardColorSpace)
                        {
                            Logger.Info?.Print(LogClass.Gpu, $"Found preferred format: {GetFormatDisplayName(format.Format, format.ColorSpace)}");
                            return format;
                        }
                    }
                }
            }

            var fallbackFormat = availableFormats[0];
            Logger.Info?.Print(LogClass.Gpu, $"No preferred format found, using fallback: {GetFormatDisplayName(fallbackFormat.Format, fallbackFormat.ColorSpace)}");
            return fallbackFormat;
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
                Logger.Warning?.Print(LogClass.Gpu, "No available present modes, using FIFO as fallback");
                return PresentModeKHR.FifoKhr;
            }
            
            if (!vsyncEnabled && availablePresentModes.Contains(PresentModeKHR.ImmediateKhr))
            {
                Logger.Info?.Print(LogClass.Gpu, "VSync disabled, using Immediate present mode");
                return PresentModeKHR.ImmediateKhr;
            }
            else if (availablePresentModes.Contains(PresentModeKHR.MailboxKhr))
            {
                Logger.Info?.Print(LogClass.Gpu, "Using Mailbox present mode");
                return PresentModeKHR.MailboxKhr;
            }
            else
            {
                Logger.Info?.Print(LogClass.Gpu, "Using FIFO present mode");
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

        public static void SetCustomSurfaceFormat(VkFormat format, ColorSpaceKHR colorSpace)
        {
            _customSurfaceFormat = new SurfaceFormatKHR(format, colorSpace);
            _useCustomSurfaceFormat = true;
            _customFormatValid = true;
            
            Logger.Info?.Print(LogClass.Gpu, $"Custom surface format set: {GetFormatDisplayName(format, colorSpace)}");
        }

        public static void ClearCustomSurfaceFormat()
        {
            _useCustomSurfaceFormat = false;
            _customFormatValid = false;
            Logger.Info?.Print(LogClass.Gpu, "Custom surface format cleared");
        }

        public static bool IsCustomSurfaceFormatValid()
        {
            return _customFormatValid;
        }

        public static string GetCurrentSurfaceFormatInfo()
        {
            if (_useCustomSurfaceFormat && _customFormatValid)
            {
                return $"Custom: Format={_customSurfaceFormat.Format}, ColorSpace={_customSurfaceFormat.ColorSpace}";
            }
            else
            {
                return "Auto-selected surface format";
            }
        }

        public static List<SurfaceFormatKHR> GetAvailableSurfaceFormats()
        {
            try
            {
                if (_availableSurfaceFormats != null && _availableSurfaceFormats.Count > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"Returning {_availableSurfaceFormats.Count} cached surface formats");
                    return new List<SurfaceFormatKHR>(_availableSurfaceFormats);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, "No cached surface formats available");
                    return new List<SurfaceFormatKHR>();
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Error in GetAvailableSurfaceFormats: {ex.Message}");
                return new List<SurfaceFormatKHR>();
            }
        }

        public static string GetFormatDisplayName(VkFormat format, ColorSpaceKHR colorSpace)
        {
            string formatName = format.ToString();
            string colorSpaceName = colorSpace.ToString();
            
            if (formatName.StartsWith("B8G8R8A8"))
                formatName = "BGRA8";
            else if (formatName.StartsWith("R8G8B8A8"))
                formatName = "RGBA8";
            else if (formatName.StartsWith("A2B10G10R10"))
                formatName = "A2B10G10R10";
            else if (formatName.StartsWith("A2R10G10B10"))
                formatName = "A2R10G10B10";
            else if (formatName.StartsWith("R5G6B5"))
                formatName = "RGB565";
            else if (formatName.StartsWith("R16G16B16A16"))
                formatName = "RGBA16F";
                
            if (colorSpaceName.Contains("SrgbNonlinear"))
                colorSpaceName = "SRGB";
            else if (colorSpaceName.Contains("PassThrough"))
                colorSpaceName = "PassThrough";
            else if (colorSpaceName.Contains("DisplayP3"))
                colorSpaceName = "DisplayP3";
            else if (colorSpaceName.Contains("ExtendedSrgb"))
                colorSpaceName = "ExtendedSRGB";
                
            return $"{formatName} ({colorSpaceName})";
        }

        public unsafe override void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            if (!_allowSurfaceQueries && _surface.Handle != 0)
            {
                _allowSurfaceQueries = true;
            }

            if (!_gd.PresentAllowed || _surface.Handle == 0)
            {
                swapBuffersCallback?.Invoke();
                return;
            }

            if (_width <= 0 || _height <= 0)
            {
                RecreateSwapchain();
                swapBuffersCallback?.Invoke();
                return;
            }

            if (_swapchain.Handle == 0 || _imageAvailableSemaphores == null || _renderFinishedSemaphores == null)
            {
                try { CreateSwapchain(); } catch { /* 下一帧重试 */ }
                if (_swapchain.Handle == 0 || _imageAvailableSemaphores == null || _renderFinishedSemaphores == null)
                {
                    swapBuffersCallback?.Invoke();
                    return;
                }
            }

            _gd.PipelineInternal.AutoFlush.Present();

            uint nextImage = 0;
            int semaphoreIndex = _frameIndex++ % _imageAvailableSemaphores.Length;

            while (true)
            {
                var acquireResult = _gd.SwapchainApi.AcquireNextImage(
                    _device,
                    _swapchain,
                    ulong.MaxValue,
                    _imageAvailableSemaphores[semaphoreIndex],
                    new Fence(),
                    ref nextImage);

                if (acquireResult == Result.ErrorOutOfDateKhr ||
                    acquireResult == Result.SuboptimalKhr ||
                    _swapchainIsDirty)
                {
                    RecreateSwapchain();

                    if (_swapchain.Handle == 0 || _imageAvailableSemaphores == null)
                    {
                        swapBuffersCallback?.Invoke();
                        return;
                    }

                    semaphoreIndex = (_frameIndex - 1) % _imageAvailableSemaphores.Length;
                }
                else if (acquireResult == Result.ErrorSurfaceLostKhr)
                {
                    _gd.ReleaseSurface();
                    swapBuffersCallback?.Invoke();
                    return;
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
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                0,
                AccessFlags.ColorAttachmentWriteBit,
                ImageLayout.Undefined,
                ImageLayout.ColorAttachmentOptimal);

            var view = (TextureView)texture;

            UpdateEffect();
            UpdateScalingFilter();

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
                    var emptySems = Array.Empty<Silk.NET.Vulkan.Semaphore>();
                    var waitStagesCO = new PipelineStageFlags[] { PipelineStageFlags.ColorAttachmentOutputBit };
                    _gd.CommandBufferPool.Return(
                        cbs,
                        emptySems,
                        waitStagesCO,
                        emptySems);
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

            Logger.Info?.Print(LogClass.Gpu, $"Scaling Debug: ScalingFilter={_currentScalingFilter}, Source=({srcX0},{srcY0})-({srcX1},{srcY1}), Destination=({dstX0},{dstY0})-({dstX1},{dstY1})");
            Logger.Info?.Print(LogClass.Gpu, $"Scaling Debug: View size={view.Width}x{view.Height}, Swapchain size={_width}x{_height}");

            // 使用缩放过滤器
            if (_scalingFilter != null)
            {
                try
                {
                    _scalingFilter.Run(
                        view, cbs, 
                        _swapchainImageViews[nextImage].GetImageView(),
                        FormatTable.GetFormat(_format), _width, _height,
                        new Extents2D(srcX0, srcY0, srcX1, srcY1),
                        new Extents2D(dstX0, dstY0, dstX1, dstY1));
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Scaling filter error: {ex.Message}, falling back to bilinear");
                    // 回退到双线性
                    _gd.HelperShader.BlitColor(
                        _gd, cbs, view, _swapchainImageViews[nextImage],
                        new Extents2D(srcX0, srcY0, srcX1, srcY1),
                        new Extents2D(dstX0, dstY1, dstX1, dstY0),
                        true, true);
                }
            }
            else
            {
                // 回退到原有的 HelperShader 路径
                _gd.HelperShader.BlitColor(
                    _gd, cbs, view, _swapchainImageViews[nextImage],
                    new Extents2D(srcX0, srcY0, srcX1, srcY1),
                    new Extents2D(dstX0, dstY1, dstX1, dstY0),
                    _isLinear, true);
            }

            // 转换到Present布局
            Transition(
                cbs.CommandBuffer,
                swapchainImage,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.BottomOfPipeBit,
                AccessFlags.ColorAttachmentWriteBit,
                0,
                ImageLayout.ColorAttachmentOptimal,
                ImageLayout.PresentSrcKhr);

            var waitSems = new Silk.NET.Vulkan.Semaphore[] { _imageAvailableSemaphores[semaphoreIndex] };
            var waitStages = new PipelineStageFlags[] { PipelineStageFlags.ColorAttachmentOutputBit };
            var signalSems = new Silk.NET.Vulkan.Semaphore[] { _renderFinishedSemaphores[semaphoreIndex] };
            _gd.CommandBufferPool.Return(cbs, waitSems, waitStages, signalSems);

            PresentOne(_gd, _renderFinishedSemaphores[semaphoreIndex], _swapchain, nextImage);

            swapBuffersCallback?.Invoke();
        }

        private static unsafe void PresentOne(
            VulkanRenderer gd,
            Silk.NET.Vulkan.Semaphore signal,
            SwapchainKHR swapchain,
            uint imageIndex)
        {
            Silk.NET.Vulkan.Semaphore* pWait = stackalloc Silk.NET.Vulkan.Semaphore[1];
            SwapchainKHR* pSwap = stackalloc SwapchainKHR[1];
            uint* pImageIndex = stackalloc uint[1];

            pWait[0] = signal;
            pSwap[0] = swapchain;
            pImageIndex[0] = imageIndex;

            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = pWait,
                SwapchainCount = 1,
                PSwapchains = pSwap,
                PImageIndices = pImageIndex,
                PResults = null
            };

            lock (gd.QueueLock)
            {
                gd.SwapchainApi.QueuePresent(gd.Queue, in presentInfo);
            }
        }

        private unsafe void Transition(
            CommandBuffer commandBuffer,
            Image image,
            PipelineStageFlags srcStage,
            PipelineStageFlags dstStage,
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
                srcStage,
                dstStage,
                0,
                0,
                null,
                0,
                null,
                1,
                in barrier);
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
            Logger.Info?.Print(LogClass.Gpu, $"Setting scaling filter to: {type}");
            if (_currentScalingFilter == type && _scalingFilter != null)
            {
                return;
            }

            _currentScalingFilter = type;
            _updateScalingFilter = true;
        }

        public override void SetScalingFilterLevel(float level)
        {
            Logger.Info?.Print(LogClass.Gpu, $"Setting scaling filter level to: {level}");
            _scalingFilterLevel = level;
            if (_scalingFilter != null)
            {
                _scalingFilter.Level = level;
            }
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
        }

        private void UpdateScalingFilter()
        {
            if (_updateScalingFilter)
            {
                _updateScalingFilter = false;
                
                _scalingFilter?.Dispose();
                _scalingFilter = null;

                switch (_currentScalingFilter)
                {
                    case ScalingFilter.Bilinear:
                        _isLinear = true;
                        break;
                    case ScalingFilter.Nearest:
                        _isLinear = false;
                        break;
                    case ScalingFilter.Fsr:
                        try
                        {
                            _scalingFilter = new FsrScalingFilter(_gd, _device);
                            _scalingFilter.Level = _scalingFilterLevel;
                            Logger.Info?.Print(LogClass.Gpu, "FSR scaling filter created successfully");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.Gpu, $"Failed to create FSR scaling filter: {ex.Message}");
                            _scalingFilter = null;
                            _isLinear = true; // 回退到双线性
                        }
                        break;
                    case ScalingFilter.Area:
                        // 可以创建 AreaScalingFilter
                        _isLinear = true;
                        break;
                }
                
                Logger.Info?.Print(LogClass.Gpu, $"Scaling filter updated: {_currentScalingFilter}, Level: {_scalingFilterLevel}");
            }
        }

        private void CaptureFrame(TextureView texture, int x, int y, int width, int height, bool isBgra, bool flipX, bool flipY)
        {
            byte[] bitmap = texture.GetData(x, y, width, height);

            _gd.OnScreenCaptured(new ScreenCaptureImageInfo(width, height, isBgra, bitmap, flipX, flipY));
        }

        public override void SetSize(int width, int height)
        {
            _swapchainIsDirty = true;

            if (_surface.Handle != 0)
            {
                SetSurfaceQueryAllowed(true);
            }
        }

        public override void ChangeVSyncMode(bool vsyncEnabled)
        {
            _vsyncEnabled = vsyncEnabled;
            _swapchainIsDirty = true;
        }

        public void OnSurfaceLost()
        {
            lock (_gd.SurfaceLock)
            {
                _swapchainIsDirty = true;
                SetSurfaceQueryAllowed(false);

                _gd.Api.DeviceWaitIdle(_device);

                unsafe
                {
                    if (_imageAvailableSemaphores != null)
                    {
                        for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                        {
                            if (_imageAvailableSemaphores[i].Handle != 0)
                            {
                                _gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                            }
                        }
                        _imageAvailableSemaphores = null;
                    }

                    if (_renderFinishedSemaphores != null)
                    {
                        for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                        {
                            if (_renderFinishedSemaphores[i].Handle != 0)
                            {
                                _gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                            }
                        }
                        _renderFinishedSemaphores = null;
                    }
                }

                if (_swapchainImageViews != null)
                {
                    for (int i = 0; i < _swapchainImageViews.Length; i++)
                    {
                        _swapchainImageViews[i]?.Dispose();
                    }
                    _swapchainImageViews = null;
                }

                if (_swapchain.Handle != 0)
                {
                    _gd.SwapchainApi.DestroySwapchain(_device, _swapchain, Span<AllocationCallbacks>.Empty);
                    _swapchain = default;
                }

                _surface = new SurfaceKHR(0);
                _width = _height = 0;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gd.SurfaceLock)
                {
                    unsafe
                    {
                        if (_swapchainImageViews != null)
                        {
                            for (int i = 0; i < _swapchainImageViews.Length; i++)
                            {
                                _swapchainImageViews[i]?.Dispose();
                            }
                        }

                        if (_imageAvailableSemaphores != null)
                        {
                            for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                            {
                                if (_imageAvailableSemaphores[i].Handle != 0)
                                {
                                    _gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                                }
                            }
                        }

                        if (_renderFinishedSemaphores != null)
                        {
                            for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                            {
                                if (_renderFinishedSemaphores[i].Handle != 0)
                                {
                                    _gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                                }
                            }
                        }

                        if (_swapchain.Handle != 0)
                        {
                            _gd.SwapchainApi.DestroySwapchain(_device, _swapchain, null);
                        }
                    }
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