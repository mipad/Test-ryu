using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Effects;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Linq;
using System.Collections.Generic;
using VkFormat = Silk.NET.Vulkan.Format;
using Ryujinx.Common.Logging; // 添加日志命名空间

namespace Ryujinx.Graphics.Vulkan
{
    public class Window : WindowBase, IDisposable
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
        private IScalingFilter _scalingFilter;
        private bool _isLinear;
        private float _scalingFilterLevel;
        private bool _updateScalingFilter;
        private ScalingFilter _currentScalingFilter;
        private bool _colorSpacePassthroughEnabled;

        // 新增：自定义表面格式相关字段
        private static bool _useCustomSurfaceFormat = false;
        private static SurfaceFormatKHR _customSurfaceFormat;
        private static bool _customFormatValid = false;
        private static List<SurfaceFormatKHR> _availableSurfaceFormats = new List<SurfaceFormatKHR>();

        public unsafe Window(VulkanRenderer gd, SurfaceKHR surface, PhysicalDevice physicalDevice, Device device)
        {
            _gd = gd;
            _physicalDevice = physicalDevice;
            _device = device;
            _surface = surface;

            CreateSwapchain();
        }

        private void RecreateSwapchain()
        {
            var oldSwapchain = _swapchain;
            _swapchainIsDirty = false;

            for (int i = 0; i < _swapchainImageViews.Length; i++)
            {
                _swapchainImageViews[i].Dispose();
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

        private unsafe void CreateSwapchain()
        {
            _gd.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities);

            uint surfaceFormatsCount;

            _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, null);

            var surfaceFormats = new SurfaceFormatKHR[surfaceFormatsCount];

            fixed (SurfaceFormatKHR* pSurfaceFormats = surfaceFormats)
            {
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, pSurfaceFormats);
            }

            // 保存可用的表面格式列表
            _availableSurfaceFormats.Clear();
            _availableSurfaceFormats.AddRange(surfaceFormats);

            // 新增：记录可用的表面格式到日志
            LogAvailableSurfaceFormats(surfaceFormats);

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

            // 修改：使用新的表面格式选择方法
            var surfaceFormat = ChooseSwapSurfaceFormat(surfaceFormats, _colorSpacePassthroughEnabled);

            // 新增：记录最终选择的表面格式到日志
            Logger.Info?.Print(LogClass.Gpu, $"Selected surface format: {GetFormatDisplayName(surfaceFormat.Format, surfaceFormat.ColorSpace)}");

            var extent = ChooseSwapExtent(capabilities);

            _width = (int)extent.Width;
            _height = (int)extent.Height;
            _format = surfaceFormat.Format;

            var oldSwapchain = _swapchain;

            CurrentTransform = capabilities.CurrentTransform;

            var swapchainCreateInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | (Ryujinx.Common.PlatformInfo.IsBionic ? 0 : ImageUsageFlags.StorageBit),
                ImageSharingMode = SharingMode.Exclusive,
                ImageArrayLayers = 1,
                PreTransform = Ryujinx.Common.PlatformInfo.IsBionic ? SurfaceTransformFlagsKHR.IdentityBitKhr : capabilities.CurrentTransform,
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

        // 新增：记录可用表面格式到日志的方法
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
                
                // 标记常用格式
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

        // 修改：增强的表面格式选择方法，支持自定义格式
        private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats, bool colorSpacePassthroughEnabled)
        {
            // 如果启用了自定义表面格式且格式有效，优先使用自定义格式
            if (_useCustomSurfaceFormat && _customFormatValid)
            {
                // 检查自定义格式是否在可用格式列表中
                foreach (var format in availableFormats)
                {
                    if (format.Format == _customSurfaceFormat.Format && 
                        format.ColorSpace == _customSurfaceFormat.ColorSpace)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"Using custom surface format: {GetFormatDisplayName(format.Format, format.ColorSpace)}");
                        return format;
                    }
                }
                
                // 如果自定义格式不可用，回退到自动选择并记录警告
                Logger.Warning?.Print(LogClass.Gpu, $"Custom surface format not available: {GetFormatDisplayName(_customSurfaceFormat.Format, _customSurfaceFormat.ColorSpace)}, falling back to automatic selection");
                _customFormatValid = false;
            }

            // 定义标准颜色空间常量
            const ColorSpaceKHR StandardColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
            const VkFormat PreferredFormat = VkFormat.B8G8R8A8Unorm;

            // 空数组检查 - 返回安全默认值
            if (availableFormats.Length == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, "No available surface formats, using fallback format");
                return new SurfaceFormatKHR(PreferredFormat, StandardColorSpace);
            }

            // 特殊格式处理
            if (availableFormats.Length == 1 && availableFormats[0].Format == VkFormat.Undefined)
            {
                Logger.Info?.Print(LogClass.Gpu, "Surface format undefined, using default format");
                return new SurfaceFormatKHR(PreferredFormat, StandardColorSpace);
            }

            // 修复：使用正确的颜色空间枚举值
            if (colorSpacePassthroughEnabled)
            {
                Logger.Info?.Print(LogClass.Gpu, "Color space passthrough enabled, looking for compatible formats...");
                
                // 优先选择PassThrough格式
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && 
                        format.ColorSpace == ColorSpaceKHR.SpacePassThroughExt)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"Found preferred format with PassThrough color space: {GetFormatDisplayName(format.Format, format.ColorSpace)}");
                        return format;
                    }
                }
                
                // 其次选择标准SRGB格式
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && 
                        format.ColorSpace == StandardColorSpace)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"Found preferred format with SRGB color space: {GetFormatDisplayName(format.Format, format.ColorSpace)}");
                        return format;
                    }
                }
            }
            else
            {
                Logger.Info?.Print(LogClass.Gpu, "Standard color space mode, looking for SRGB formats...");
                
                // 标准模式下优先选择SRGB格式
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && 
                        format.ColorSpace == StandardColorSpace)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"Found preferred format: {GetFormatDisplayName(format.Format, format.ColorSpace)}");
                        return format;
                    }
                }
            }

            // 没有匹配时返回第一个可用格式
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
                return PresentModeKHR.FifoKhr; // 安全默认值
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

        // 新增：设置自定义表面格式的方法
        public static void SetCustomSurfaceFormat(VkFormat format, ColorSpaceKHR colorSpace)
        {
            _customSurfaceFormat = new SurfaceFormatKHR(format, colorSpace);
            _useCustomSurfaceFormat = true;
            _customFormatValid = true;
            
            Logger.Info?.Print(LogClass.Gpu, $"Custom surface format set: {GetFormatDisplayName(format, colorSpace)}");
        }

        // 新增：清除自定义表面格式设置
        public static void ClearCustomSurfaceFormat()
        {
            _useCustomSurfaceFormat = false;
            _customFormatValid = false;
            Logger.Info?.Print(LogClass.Gpu, "Custom surface format cleared");
        }

        // 新增：检查自定义表面格式是否可用
        public static bool IsCustomSurfaceFormatValid()
        {
            return _customFormatValid;
        }

        // 新增：获取当前使用的表面格式信息
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

        // 新增：获取设备支持的表面格式列表
        public static List<SurfaceFormatKHR> GetAvailableSurfaceFormats()
        {
            return new List<SurfaceFormatKHR>(_availableSurfaceFormats);
        }

        // 新增：获取格式名称的友好显示
        public static string GetFormatDisplayName(VkFormat format, ColorSpaceKHR colorSpace)
        {
            string formatName = format.ToString();
            string colorSpaceName = colorSpace.ToString();
            
            // 简化格式名称显示
            if (formatName.StartsWith("B8G8R8A8"))
                formatName = "BGRA8";
            else if (formatName.StartsWith("R8G8B8A8"))
                formatName = "RGBA8";
            else if (formatName.StartsWith("A2B10G10R10"))
                formatName = "A2B10G10R10";
            else if (formatName.StartsWith("A2R10G10B10"))
                formatName = "A2R10G10B10";
                
            // 简化色彩空间名称显示
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

        // 其余方法保持不变...
        public unsafe override void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            // ... 现有代码保持不变 ...
        }

        // ... 其他现有方法保持不变 ...

        protected virtual void Dispose(bool disposing)
        {
            // ... 现有代码保持不变 ...
        }

        public override void Dispose()
        {
            Dispose(true);
        }
    }
}
