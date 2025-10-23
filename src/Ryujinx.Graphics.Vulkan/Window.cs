using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Effects;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class Window : WindowBase, IDisposable
    {
        private const int SurfaceWidth = 1280;
        private const int SurfaceHeight = 720;
        
        // 标准颜色空间常量
        private const ColorSpaceKHR StandardColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
        private const VkFormat PreferredFormat = VkFormat.B8G8R8A8Unorm;

        private readonly VulkanRenderer _gd;
        private readonly PhysicalDevice _physicalDevice;
        private readonly Device _device;
        private SurfaceKHR _surface;
        private SwapchainKHR _swapchain;

        private Image[] _swapchainImages;
        private TextureView[] _swapchainImageViews;

        // 修复：明确指定使用 Vulkan 的 Semaphore 类型
        private Silk.NET.Vulkan.Semaphore[] _imageAvailableSemaphores;
        private Silk.NET.Vulkan.Semaphore[] _renderFinishedSemaphores;

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

        // 添加同步锁和重试机制
        private readonly object _swapchainLock = new object();
        private const int MaxRetryCount = 3;
        private const int RetryDelayMs = 16;

        public unsafe Window(VulkanRenderer gd, SurfaceKHR surface, PhysicalDevice physicalDevice, Device device)
        {
            _gd = gd;
            _physicalDevice = physicalDevice;
            _device = device;
            _surface = surface;

            CreateSwapchain();
        }

        private bool RecreateSwapchain()
        {
            lock (_swapchainLock)
            {
                var oldSwapchain = _swapchain;
                _swapchainIsDirty = false;

                try
                {
                    // 清理现有资源
                    CleanupSwapchainResources();

                    // 等待设备空闲以确保安全
                    _gd.Api.DeviceWaitIdle(_device);

                    // 创建新交换链
                    CreateSwapchainInternal();

                    // 如果创建成功，销毁旧交换链
                    if (oldSwapchain.Handle != 0)
                    {
                        _gd.SwapchainApi.DestroySwapchain(_device, oldSwapchain, Span<AllocationCallbacks>.Empty);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    // 重建失败，标记为脏并返回false
                    _swapchainIsDirty = true;
                    return false;
                }
            }
        }

        private unsafe void CleanupSwapchainResources()
        {
            // 清理图像视图
            if (_swapchainImageViews != null)
            {
                for (int i = 0; i < _swapchainImageViews.Length; i++)
                {
                    _swapchainImageViews[i]?.Dispose();
                }
                _swapchainImageViews = null;
            }

            // 清理信号量
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

            _swapchainImages = null;
        }

        internal void SetSurface(SurfaceKHR surface)
        {
            lock (_swapchainLock)
            {
                _surface = surface;
                RecreateSwapchain();
            }
        }

        private unsafe void CreateSwapchain()
        {
            lock (_swapchainLock)
            {
                CreateSwapchainInternal();
            }
        }

        private unsafe void CreateSwapchainInternal()
        {
            int retryCount = 0;
            
            retry_create:
            try
            {
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities);

                uint surfaceFormatsCount;
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, null);

                var surfaceFormats = new SurfaceFormatKHR[surfaceFormatsCount];
                if (surfaceFormatsCount > 0)
                {
                    fixed (SurfaceFormatKHR* pSurfaceFormats = surfaceFormats)
                    {
                        _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, pSurfaceFormats);
                    }
                }

                uint presentModesCount;
                _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, null);

                var presentModes = new PresentModeKHR[presentModesCount];
                if (presentModesCount > 0)
                {
                    fixed (PresentModeKHR* pPresentModes = presentModes)
                    {
                        _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, pPresentModes);
                    }
                }

                uint imageCount = capabilities.MinImageCount + 1;
                if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
                {
                    imageCount = capabilities.MaxImageCount;
                }

                var surfaceFormat = ChooseSwapSurfaceFormat(surfaceFormats, _colorSpacePassthroughEnabled);
                var extent = ChooseSwapExtent(capabilities);

                _width = (int)extent.Width;
                _height = (int)extent.Height;
                _format = surfaceFormat.Format;

                CurrentTransform = capabilities.CurrentTransform;

                var swapchainCreateInfo = new SwapchainCreateInfoKHR
                {
                    SType = StructureType.SwapchainCreateInfoKhr,
                    Surface = _surface,
                    MinImageCount = imageCount,
                    ImageFormat = surfaceFormat.Format,
                    ImageColorSpace = surfaceFormat.ColorSpace,
                    ImageExtent = extent,
                    ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | (PlatformInfo.IsBionic ? 0 : ImageUsageFlags.StorageBit),
                    ImageSharingMode = SharingMode.Exclusive,
                    ImageArrayLayers = 1,
                    PreTransform = PlatformInfo.IsBionic ? SurfaceTransformFlagsKHR.IdentityBitKhr : capabilities.CurrentTransform,
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

                // 使用健壮的错误处理
                var result = _gd.SwapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain);
                
                if (result == Result.ErrorNativeWindowInUseKhr && retryCount < MaxRetryCount)
                {
                    retryCount++;
                    Thread.Sleep(RetryDelayMs);
                    goto retry_create;
                }
                else if (result != Result.Success)
                {
                    if (result == Result.ErrorOutOfDateKhr || result == Result.ErrorSurfaceLostKhr)
                    {
                        _swapchainIsDirty = true;
                        return;
                    }
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

                _imageAvailableSemaphores = new Silk.NET.Vulkan.Semaphore[imageCount];

                for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                {
                    _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _imageAvailableSemaphores[i]).ThrowOnError();
                }

                _renderFinishedSemaphores = new Silk.NET.Vulkan.Semaphore[imageCount];

                for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                {
                    _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _renderFinishedSemaphores[i]).ThrowOnError();
                }
            }
            catch (VulkanException ex) when (ex.Result == Result.ErrorNativeWindowInUseKhr && retryCount < MaxRetryCount)
            {
                retryCount++;
                Thread.Sleep(RetryDelayMs);
                goto retry_create;
            }
            catch (Exception ex)
            {
                _swapchainIsDirty = true;
                throw;
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
            // 修复1: 添加空数组检查
            if (availableFormats.Length == 0)
            {
                return new SurfaceFormatKHR(PreferredFormat, StandardColorSpace);
            }

            // 特殊格式处理
            if (availableFormats.Length == 1 && availableFormats[0].Format == VkFormat.Undefined)
            {
                return new SurfaceFormatKHR(PreferredFormat, StandardColorSpace);
            }

            var formatToReturn = availableFormats[0];
            
            // 修复2: 使用正确的颜色空间枚举
            if (colorSpacePassthroughEnabled)
            {
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && format.ColorSpace == ColorSpaceKHR.SpacePassThroughExt)
                    {
                        formatToReturn = format;
                        break;
                    }
                    else if (format.Format == PreferredFormat && format.ColorSpace == StandardColorSpace)
                    {
                        formatToReturn = format;
                    }
                }
            }
            else
            {
                foreach (var format in availableFormats)
                {
                    if (format.Format == PreferredFormat && format.ColorSpace == StandardColorSpace)
                    {
                        formatToReturn = format;
                        break;
                    }
                }
            }

            return formatToReturn;
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
            // 修复3: 添加空数组检查
            if (availablePresentModes.Length == 0)
            {
                return PresentModeKHR.FifoKhr;
            }

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
            int retryCount = 0;
            
            retry_present:
            try
            {
                // 如果交换链标记为脏，先重建
                if (_swapchainIsDirty)
                {
                    if (!RecreateSwapchain())
                    {
                        if (retryCount < MaxRetryCount)
                        {
                            retryCount++;
                            Thread.Sleep(RetryDelayMs);
                            goto retry_present;
                        }
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
                        if (!RecreateSwapchain() && retryCount < MaxRetryCount)
                        {
                            retryCount++;
                            Thread.Sleep(RetryDelayMs);
                            goto retry_present;
                        }
                        semaphoreIndex = (_frameIndex - 1) % _imageAvailableSemaphores.Length;
                        
                        // 如果重建后仍然有问题，退出
                        if (_swapchainIsDirty)
                        {
                            return;
                        }
                    }
                    else if (acquireResult == Result.ErrorSurfaceLostKhr)
                    {
                        _gd.RecreateSurface();
                    }
                    else if (acquireResult == Result.ErrorNativeWindowInUseKhr && retryCount < MaxRetryCount)
                    {
                        retryCount++;
                        Thread.Sleep(RetryDelayMs);
                        goto retry_present;
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

                // WICHTIG: Unser Renderpass erwartet die Attachments im Layout GENERAL.
                // Also von Undefined/Present -> General mit ColorAttachmentWrite als Zielzugriff.
#if DEBUG
                ValidateTransition(
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    0,
                    AccessFlags.ColorAttachmentWriteBit,
                    ImageLayout.Undefined,
                    ImageLayout.General);
#endif

                Transition(
                    cbs.CommandBuffer,
                    swapchainImage,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    0,
                    AccessFlags.ColorAttachmentWriteBit,
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

                if (_scalingFilter != null)
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

                // Nach den ColorAttachment-Writes: General -> PresentSrcKhr
#if DEBUG
                ValidateTransition(
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    PipelineStageFlags.BottomOfPipeBit,
                    AccessFlags.ColorAttachmentWriteBit,
                    0,
                    ImageLayout.General,
                    ImageLayout.PresentSrcKhr);
#endif

                Transition(
                    cbs.CommandBuffer,
                    swapchainImage,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    PipelineStageFlags.BottomOfPipeBit,
                    AccessFlags.ColorAttachmentWriteBit,
                    0,
                    ImageLayout.General,
                    ImageLayout.PresentSrcKhr);

                // Robuster: auf TOP_OF_PIPE warten (deckt Compute/Graphics gleichermaßen ab).
                _gd.CommandBufferPool.Return(
                    cbs,
                    [_imageAvailableSemaphores[semaphoreIndex]],
                    [PipelineStageFlags.TopOfPipeBit],
                    [_renderFinishedSemaphores[semaphoreIndex]]);

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

                // 检查呈现结果
                if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr)
                {
                    _swapchainIsDirty = true;
                }
                else if (result == Result.ErrorNativeWindowInUseKhr && retryCount < MaxRetryCount)
                {
                    retryCount++;
                    Thread.Sleep(RetryDelayMs);
                    goto retry_present;
                }
                else if (result != Result.Success)
                {
                    // 记录错误但不抛出异常
                }

                //While this does nothing in most cases, it's useful to notify the end of the frame, and is used to handle native window in Android.
                swapBuffersCallback?.Invoke();
            }
            catch (VulkanException ex) when (ex.Result == Result.ErrorNativeWindowInUseKhr && retryCount < MaxRetryCount)
            {
                retryCount++;
                Thread.Sleep(RetryDelayMs);
                goto retry_present;
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

            _currentScalingFilter = type;

            _updateScalingFilter = true;
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

            if (_updateScalingFilter)
            {
                _updateScalingFilter = false;

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
        }

        public override void SetScalingFilterLevel(float level)
        {
            _scalingFilterLevel = level;
            _updateScalingFilter = true;
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

#if DEBUG
        [Conditional("DEBUG")]
        private void ValidateTransition(PipelineStageFlags srcStage, PipelineStageFlags dstStage, 
                                       AccessFlags srcAccess, AccessFlags dstAccess,
                                       ImageLayout oldLayout, ImageLayout newLayout)
        {
            // 验证布局转换的合理性
            if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.General)
            {
                // 这是初始转换，应该使用TopOfPipe
                Debug.Assert(srcStage == PipelineStageFlags.TopOfPipeBit, 
                    "从Undefined到General的转换应该使用TopOfPipe作为源阶段");
            }
            
            if (oldLayout == ImageLayout.General && newLayout == ImageLayout.PresentSrcKhr)
            {
                // 这是显示前转换，应该等待颜色附件输出完成
                Debug.Assert(srcStage.HasFlag(PipelineStageFlags.ColorAttachmentOutputBit),
                    "从General到PresentSrcKhr的转换应该等待颜色附件输出完成");
                Debug.Assert(srcAccess.HasFlag(AccessFlags.ColorAttachmentWriteBit),
                    "从General到PresentSrcKhr的转换应该等待颜色附件写入完成");
            }
        }
#endif

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
            //present mode may change, so mark the swapchain for recreation
            _swapchainIsDirty = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _effect?.Dispose();
                _effect = null;
                
                _scalingFilter?.Dispose();
                _scalingFilter = null;
                
                CleanupSwapchainResources();

                // 确保交换链被销毁
                if (_swapchain.Handle != 0)
                {
                    _gd.SwapchainApi.DestroySwapchain(_device, _swapchain, null);
                    _swapchain = default;
                }
            }
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~Window()
        {
            Dispose(false);
        }
    }
}
