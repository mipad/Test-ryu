using LibRyujinx.Android;
using LibRyujinx.Shared;
using OpenTK.Graphics.OpenGL;
using Ryujinx.UI.Common.Configuration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LibRyujinx
{
    public static partial class LibRyujinx
    {
        private static bool _isActive;
        private static bool _isStopped;
        private static CancellationTokenSource _gpuCancellationTokenSource;
        private static SwapBuffersCallback? _swapBuffersCallback;
        private static NativeGraphicsInterop _nativeGraphicsInterop;
        private static ManualResetEvent _gpuDoneEvent;
        private static bool _enableGraphicsLogging;

        public delegate void SwapBuffersCallback();
        public delegate IntPtr GetProcAddress(string name);
        public delegate IntPtr CreateSurface(IntPtr instance);

        public static IRenderer? Renderer { get; set; }
        public static GraphicsConfiguration GraphicsConfiguration { get; private set; }

        public static bool InitializeGraphics(GraphicsConfiguration graphicsConfiguration)
        {
            GraphicsConfig.ResScale = graphicsConfiguration.ResScale;
            GraphicsConfig.MaxAnisotropy = graphicsConfiguration.MaxAnisotropy;
            GraphicsConfig.FastGpuTime = graphicsConfiguration.FastGpuTime;
            GraphicsConfig.Fast2DCopy = graphicsConfiguration.Fast2DCopy;
            GraphicsConfig.EnableMacroJit = graphicsConfiguration.EnableMacroJit;
            GraphicsConfig.EnableMacroHLE = graphicsConfiguration.EnableMacroHLE;
            GraphicsConfig.EnableShaderCache = graphicsConfiguration.EnableShaderCache;
            GraphicsConfig.EnableTextureRecompression = graphicsConfiguration.EnableTextureRecompression;
            
            // 初始化色彩空间直通设置
            GraphicsConfig.EnableColorSpacePassthrough = graphicsConfiguration.EnableColorSpacePassthrough;
            
            // 初始化新的图形设置 - 修复类型转换错误
            ConfigurationState.Instance.Graphics.AntiAliasing.Value = (Ryujinx.Common.Configuration.AntiAliasing)graphicsConfiguration.AntiAliasing;
            ConfigurationState.Instance.Graphics.ScalingFilter.Value = (Ryujinx.Common.Configuration.ScalingFilter)graphicsConfiguration.ScalingFilter;
            ConfigurationState.Instance.Graphics.ScalingFilterLevel.Value = graphicsConfiguration.ScalingFilterLevel;
            ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough.Value = graphicsConfiguration.EnableColorSpacePassthrough;

            GraphicsConfiguration = graphicsConfiguration;

            Logger.Info?.Print(LogClass.Application, "Graphics configuration initialized:");
            Logger.Info?.Print(LogClass.Application, $"  - AntiAliasing: {graphicsConfiguration.AntiAliasing}");
            Logger.Info?.Print(LogClass.Application, $"  - ScalingFilter: {graphicsConfiguration.ScalingFilter}");
            Logger.Info?.Print(LogClass.Application, $"  - ScalingFilterLevel: {graphicsConfiguration.ScalingFilterLevel}");
            Logger.Info?.Print(LogClass.Application, $"  - EnableColorSpacePassthrough: {graphicsConfiguration.EnableColorSpacePassthrough}");

            return true;
        }

        public static bool InitializeGraphicsRenderer(GraphicsBackend graphicsBackend, CreateSurface? createSurfaceFunc, string?[] requiredExtensions)
        {
            if (Renderer != null)
            {
                return false;
            }

            if (graphicsBackend == GraphicsBackend.OpenGl)
            {
                Renderer = new OpenGLRenderer();
            }
            else if (graphicsBackend == GraphicsBackend.Vulkan)
            {
                // Prefer the platform-provided Vulkan loader (if present), fall back to default.
                var api = VulkanLoader?.GetApi() ?? Vk.GetApi();

                Renderer = new VulkanRenderer(
                    api,
                    (instance, _) =>
                    {
                        // use provided CreateSurface delegate (Android path will create ANativeWindow surface)
                        return new SurfaceKHR(createSurfaceFunc == null ? null : (ulong?)createSurfaceFunc(instance.Handle));
                    },
                    () => requiredExtensions,
                    null);
            }
            else
            {
                return false;
            }

            return true;
        }

        public static void SetRendererSize(int width, int height)
        {
            Renderer?.Window?.SetSize(width, height);
        }

        public static void SetVsyncState(bool enabled)
        {
            var device = SwitchDevice!.EmulationContext!;
            device.EnableDeviceVsync = enabled;
            device.Gpu.Renderer.Window.ChangeVSyncMode(enabled);
        }

        public static void ApplyGraphicsSettings()
        {
            try
            {
                Logger.Info?.Print(LogClass.Application, "Attempting to apply graphics settings...");
                
                if (Renderer == null)
                {
                    Logger.Warning?.Print(LogClass.Application, "Renderer is null, cannot apply graphics settings");
                    return;
                }

                if (Renderer.Window == null)
                {
                    Logger.Warning?.Print(LogClass.Application, "Renderer.Window is null, cannot apply graphics settings");
                    return;
                }

                // 获取当前配置值 - 修复类型转换
                var antiAliasing = (Ryujinx.Graphics.GAL.AntiAliasing)ConfigurationState.Instance.Graphics.AntiAliasing.Value;
                var scalingFilter = (Ryujinx.Graphics.GAL.ScalingFilter)ConfigurationState.Instance.Graphics.ScalingFilter.Value;
                var scalingFilterLevel = ConfigurationState.Instance.Graphics.ScalingFilterLevel.Value;
                var enableColorSpacePassthrough = ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough.Value;
                
                Logger.Info?.Print(LogClass.Application, $"Current graphics settings:");
                Logger.Info?.Print(LogClass.Application, $"  - AntiAliasing: {antiAliasing}");
                Logger.Info?.Print(LogClass.Application, $"  - ScalingFilter: {scalingFilter}");
                Logger.Info?.Print(LogClass.Application, $"  - ScalingFilterLevel: {scalingFilterLevel}");
                Logger.Info?.Print(LogClass.Application, $"  - EnableColorSpacePassthrough: {enableColorSpacePassthrough}");
                Logger.Info?.Print(LogClass.Application, $"  - ResScale: {ConfigurationState.Instance.Graphics.ResScale.Value}");
                Logger.Info?.Print(LogClass.Application, $"  - MaxAnisotropy: {ConfigurationState.Instance.Graphics.MaxAnisotropy.Value}");

                // 应用抗锯齿设置
                Logger.Info?.Print(LogClass.Application, $"Applying anti-aliasing: {antiAliasing}");
                Renderer.Window.SetAntiAliasing(antiAliasing);
                Logger.Info?.Print(LogClass.Application, $"Anti-aliasing applied successfully: {antiAliasing}");
                
                // 应用Scaling Filter设置
                Logger.Info?.Print(LogClass.Application, $"Applying scaling filter: {scalingFilter}");
                Renderer.Window.SetScalingFilter(scalingFilter);
                Logger.Info?.Print(LogClass.Application, $"Scaling filter applied successfully: {scalingFilter}");
                
                // 应用Scaling Filter Level设置
                Logger.Info?.Print(LogClass.Application, $"Applying scaling filter level: {scalingFilterLevel}");
                Renderer.Window.SetScalingFilterLevel(scalingFilterLevel);
                Logger.Info?.Print(LogClass.Application, $"Scaling filter level applied successfully: {scalingFilterLevel}");

                // 应用色彩空间直通设置
                Logger.Info?.Print(LogClass.Application, $"Applying color space passthrough: {enableColorSpacePassthrough}");
                Renderer.Window.SetColorSpacePassthrough(enableColorSpacePassthrough);
                Logger.Info?.Print(LogClass.Application, $"Color space passthrough applied successfully: {enableColorSpacePassthrough}");
                
                Logger.Info?.Print(LogClass.Application, "All graphics settings applied successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Failed to apply graphics settings: {ex.Message}");
                Logger.Debug?.Print(LogClass.Application, $"Graphics settings failure stack trace: {ex.StackTrace}");
            }
        }

        public static void RunLoop()
        {
            if (Renderer == null)
            {
                Logger.Error?.Print(LogClass.Application, "Renderer is null, cannot start run loop");
                return;
            }
            
            // 添加渲染器状态检查
            Logger.Info?.Print(LogClass.Application, $"Renderer type: {Renderer.GetType().Name}");
            Logger.Info?.Print(LogClass.Application, $"Renderer.Window is null: {Renderer.Window == null}");
            
            // 移除这里的 ApplyGraphicsSettings() 调用
            // 图形设置将在渲染循环内部应用
            
            ARMeilleure.Optimizations.CacheEviction = SwitchDevice!.EnableJitCacheEviction;
            
            var device = SwitchDevice!.EmulationContext!;
            _gpuDoneEvent = new ManualResetEvent(true);

            device.Gpu.Renderer.Initialize(_enableGraphicsLogging ? GraphicsDebugLevel.All : GraphicsDebugLevel.None);

            _gpuCancellationTokenSource = new CancellationTokenSource();

            device.Gpu.ShaderCacheStateChanged += LoadProgressStateChangedHandler;
            device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged += LoadProgressStateChangedHandler;

            try
            {
                device.Gpu.Renderer.RunLoop(() =>
                {
                    _gpuDoneEvent.Reset();
                    device.Gpu.SetGpuThread();
                    
                    // 在这里应用图形设置，确保渲染器已完全初始化
                    ApplyGraphicsSettings();
                    
                    device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                    _isActive = true;

                    if (Ryujinx.Common.PlatformInfo.IsBionic)
                    {
                        setRenderingThread();
                    }

                    while (_isActive)
                    {
                        if (_isStopped)
                        {
                            break;
                        }

                        if (device.WaitFifo())
                        {
                            device.Statistics.RecordFifoStart();
                            device.ProcessFrame();
                            device.Statistics.RecordFifoEnd();
                        }

                        while (device.ConsumeFrameAvailable())
                        {
                            device.PresentFrame(() =>
                            {
                                if (device.Gpu.Renderer is ThreadedRenderer threaded && threaded.BaseRenderer is VulkanRenderer vulkanRenderer)
                                {
                                    setCurrentTransform(_window, (int)vulkanRenderer.CurrentTransform);
                                }
                                _swapBuffersCallback?.Invoke();
                            });
                        }
                    }

                    if (device.Gpu.Renderer is ThreadedRenderer threaded)
                    {
                        threaded.FlushThreadedCommands();
                    }

                    _gpuDoneEvent.Set();
                });
            }
            finally
            {
                device.Gpu.ShaderCacheStateChanged -= LoadProgressStateChangedHandler;
                device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged -= LoadProgressStateChangedHandler;
            }
        }

        private static void LoadProgressStateChangedHandler<T>(T state, int current, int total) where T : Enum
        {
            void SetInfo(string status, float value)
            {
                if(Ryujinx.Common.PlatformInfo.IsBionic)
                {
                    Interop.UpdateProgress(status, value);
                }
            }
            var status = $"{current} / {total}";
            var progress = current / (float)total;
            if (float.IsNaN(progress))
                progress = 0;

            switch (state)
            {
                case LoadState ptcState:
                    if (float.IsNaN((progress)))
                        progress = 0;

                    switch (ptcState)
                    {
                        case LoadState.Unloaded:
                        case LoadState.Loading:
                            SetInfo($"Loading PTC {status}", progress);
                            break;
                        case LoadState.Loaded:
                            SetInfo($"PTC Loaded", -1);
                            break;
                    }
                    break;
                case ShaderCacheState shaderCacheState:
                    switch (shaderCacheState)
                    {
                        case ShaderCacheState.Start:
                        case ShaderCacheState.Loading:
                            SetInfo($"Compiling Shaders {status}", progress);
                            break;
                        case ShaderCacheState.Packaging:
                            SetInfo($"Packaging Shaders {status}", progress);
                            break;
                        case ShaderCacheState.Loaded:
                            SetInfo($"Shaders Loaded", -1);
                            break;
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown Progress Handler type {typeof(T)}");
            }
        }

        public static void SetSwapBuffersCallback(SwapBuffersCallback swapBuffersCallback)
        {
            _swapBuffersCallback = swapBuffersCallback;
        }
        // ===== Convenience-Wrapper für Vulkan re-attach (von JNI nutzbar) =====
        public static bool TryReattachSurface()
        {
            if (Renderer is VulkanRenderer vr)
            {
                return vr.RecreateSurface();
            }
            return false;
        }

        public static void ReleaseRendererSurface()
        {
            (Renderer as VulkanRenderer)?.ReleaseSurface();
        }

        public static void SetPresentEnabled(bool enabled)
        {
            (Renderer as VulkanRenderer)?.SetPresentEnabled(enabled);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GraphicsConfiguration
    {
        public float ResScale = 1f;
        public float MaxAnisotropy = -1;
        public bool FastGpuTime = true;
        public bool Fast2DCopy = true;
        public bool EnableMacroJit = false;
        public bool EnableMacroHLE = true;
        public bool EnableShaderCache = true;
        public bool EnableTextureRecompression = false;
        public BackendThreading BackendThreading = BackendThreading.Auto;
        public AspectRatio AspectRatio = AspectRatio.Fixed16x9;
        // 新增：色彩空间直通设置
        public bool EnableColorSpacePassthrough = false;
        
        // 添加缺失的字段：
        public int AntiAliasing = 0;           // 0=None, 1=Fxaa, 2=SmaaLow, etc.
        public int ScalingFilter = 0;          // 0=Bilinear, 1=Nearest, 2=FSR, 3=Area
        public int ScalingFilterLevel = 25;    // 0-100

        public GraphicsConfiguration()
        {
        }
    }

    public struct NativeGraphicsInterop
    {
        public IntPtr GlGetProcAddress;
        public IntPtr VkNativeContextLoader;
        public IntPtr VkCreateSurface;
        public IntPtr VkRequiredExtensions;
        public int VkRequiredExtensionsCount;
    }
}
