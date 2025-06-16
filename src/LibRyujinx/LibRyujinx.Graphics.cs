using LibRyujinx.Android;
using LibRyujinx.Shared;
using OpenTK.Graphics.OpenGL;
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
        
        // 添加线程同步和状态管理
        private static readonly object _syncLock = new object();
        private static bool _disposed = false;

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

            GraphicsConfiguration = graphicsConfiguration;

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
                Renderer = new VulkanRenderer(Vk.GetApi(), (instance, vk) => new SurfaceKHR(createSurfaceFunc == null ? null : (ulong?)createSurfaceFunc(instance.Handle)),
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

        public static void RunLoop()
        {
            if (Renderer == null)
            {
                Logger.Warning?.Print(LogClass.Application, "RunLoop: Renderer is null, exiting");
                return;
            }
            
            var device = SwitchDevice!.EmulationContext!;
            _gpuDoneEvent = new ManualResetEvent(true);

            try
            {
                lock (_syncLock)
                {
                    if (_disposed)
                    {
                        Logger.Warning?.Print(LogClass.Application, "RunLoop called after disposal");
                        return;
                    }

                    Logger.Info?.Print(LogClass.Application, "Starting GPU run loop");
                    device.Gpu.Renderer.Initialize(_enableGraphicsLogging ? GraphicsDebugLevel.All : GraphicsDebugLevel.None);

                    _gpuCancellationTokenSource = new CancellationTokenSource();
                    _isActive = true;
                    _isStopped = false;

                    device.Gpu.ShaderCacheStateChanged += LoadProgressStateChangedHandler;
                    device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged += LoadProgressStateChangedHandler;
                }

                device.Gpu.Renderer.RunLoop(() =>
                {
                    _gpuDoneEvent.Reset();
                    device.Gpu.SetGpuThread();
                    
                    try
                    {
                        device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Info?.Print(LogClass.Application, "Shader cache initialization canceled");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Application, $"Shader cache init failed: {ex}");
                    }

                    _isActive = true;

                    if (Ryujinx.Common.PlatformInfo.IsBionic)
                    {
                        setRenderingThread();
                    }

                    while (_isActive)
                    {
                        if (_isStopped || _disposed)
                        {
                            Logger.Info?.Print(LogClass.Application, "RunLoop exit requested");
                            break;
                        }

                        try
                        {
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
                            
                            // 添加短暂休眠防止CPU过载
                            Thread.Sleep(1);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Frame processing error: {ex}");
                            if (Debugger.IsAttached) Debugger.Break();
                            throw;
                        }
                    }

                    try
                    {
                        if (device.Gpu.Renderer is ThreadedRenderer threadedRenderer)
                        {
                            threadedRenderer.FlushThreadedCommands();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Application, $"Flush threaded commands failed: {ex}");
                    }
                    finally
                    {
                        _gpuDoneEvent.Set();
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"RunLoop crashed: {ex}");
                throw;
            }
            finally
            {
                lock (_syncLock)
                {
                    device.Gpu.ShaderCacheStateChanged -= LoadProgressStateChangedHandler;
                    device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged -= LoadProgressStateChangedHandler;
                    
                    Logger.Info?.Print(LogClass.Application, "RunLoop cleanup complete");
                }
            }
        }

        // 添加显式的资源释放方法
        public static void DisposeGraphics()
        {
            lock (_syncLock)
            {
                if (_disposed) return;
                
                Logger.Info?.Print(LogClass.Application, "Disposing graphics resources");
                
                _isActive = false;
                _isStopped = true;
                _disposed = true;
                
                try
                {
                    _gpuCancellationTokenSource?.Cancel();
                    _gpuDoneEvent?.WaitOne(3000);
                    
                    if (Renderer is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    
                    Renderer = null;
                    _gpuCancellationTokenSource?.Dispose();
                    _gpuDoneEvent?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Application, $"Graphics disposal error: {ex}");
                }
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
        public AspectRatio AspectRatio = AspectRatio.Stretched;

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
