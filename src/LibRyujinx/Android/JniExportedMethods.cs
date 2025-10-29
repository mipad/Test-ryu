using LibRyujinx.Android;
using LibRyujinx.Jni.Pointers;
using Ryujinx.Audio.Backends.OpenAL;
using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.Audio.Backends.SDL2;
#if ANDROID
using Ryujinx.Audio.Backends.Oboe;
#endif
using Ryujinx.Common;
using Ryujinx.UI.Common.Configuration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.Input;
using Silk.NET.Core.Loader;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Ryujinx.HLE; 

namespace LibRyujinx
{
    public static partial class LibRyujinx
    {
        private static long _surfacePtr;
        private static long _window = 0;

        public static VulkanLoader? VulkanLoader { get; private set; }

        [DllImport("libryujinxjni")]
        internal extern static void setRenderingThread();

        [DllImport("libryujinxjni")]
        internal extern static void debug_break(int code);

        [DllImport("libryujinxjni")]
        internal extern static void setCurrentTransform(long native_window, int transform);

        public delegate IntPtr JniCreateSurface(IntPtr native_surface, IntPtr instance);

        [UnmanagedCallersOnly(EntryPoint = "javaInitialize")]
        public unsafe static bool JniInitialize(IntPtr jpathId, IntPtr jniEnv)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            PlatformInfo.IsBionic = true;

            Logger.AddTarget(
                new AsyncLogTargetWrapper(
                    new AndroidLogTarget("RyujinxLog"),
                    1000,
                    AsyncLogTargetOverflowAction.Block
                ));

            var path = Marshal.PtrToStringAnsi(jpathId);

            var init = Initialize(path);

            Interop.Initialize(new JEnvRef(jniEnv));

            Interop.Test();

            return init;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceReloadFilesystem")]
        public static void JnaReloadFileSystem()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SwitchDevice?.ReloadFileSystem();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceInitialize")]
        public static bool JnaDeviceInitialize(int memoryManagerMode,
                                                    bool useNce,
                                                    int systemLanguage,
                                                    int regionCode,
                                                    bool enableVsync,
                                                    bool enableDockedMode,
                                                    bool enablePtc,
                                                    bool enableJitCacheEviction,
                                                    bool enableInternetAccess,
                                                    IntPtr timeZonePtr,
                                                    bool ignoreMissingServices,
                                                    int audioEngineType,
                                                    int memoryConfiguration,
                                                    long systemTimeOffset)
        {
            debug_break(4);
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            
            // 根据音频引擎类型设置音频驱动
            switch (audioEngineType)
            {
                case 0: // 禁用音频
                    AudioDriver = new DummyHardwareDeviceDriver();
                    break;
                case 1: // OpenAL
                    AudioDriver = new OpenALHardwareDeviceDriver();
                    break;
                case 2: // SDL2
                    AudioDriver = new SDL2HardwareDeviceDriver();
                    break;
                case 3: // Oboe
                    AudioDriver = new OboeHardwareDeviceDriver();
                    break;
                default:
                    AudioDriver = new OpenALHardwareDeviceDriver();
                    break;
            }

            var timezone = Marshal.PtrToStringAnsi(timeZonePtr);
            return InitializeDevice((MemoryManagerMode)memoryManagerMode,
                                    useNce,
                                    (SystemLanguage)systemLanguage,
                                    (RegionCode)regionCode,
                                    enableVsync,
                                    enableDockedMode,
                                    enablePtc,
                                    enableJitCacheEviction,
                                    enableInternetAccess,
                                    timezone,
                                    ignoreMissingServices,
                                    (MemoryConfiguration)memoryConfiguration,
                                    systemTimeOffset);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetGameFifo")]
        public static double JnaGetGameFifo()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var stats = SwitchDevice?.EmulationContext?.Statistics.GetFifoPercent() ?? 0;

            return stats;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetGameFrameTime")]
        public static double JnaGetGameFrameTime()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var stats = SwitchDevice?.EmulationContext?.Statistics.GetGameFrameTime() ?? 0;

            return stats;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetGameFrameRate")]
        public static double JnaGetGameFrameRate()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var stats = SwitchDevice?.EmulationContext?.Statistics.GetGameFrameRate() ?? 0;

            return stats;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceLaunchMiiEditor")]
        public static bool JNALaunchMiiEditApplet()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            if (SwitchDevice?.EmulationContext == null)
            {
                return false;
            }

            return LaunchMiiEditApplet();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetDlcContentList")]
        public static IntPtr JniGetDlcContentListNative(IntPtr pathPtr, long titleId)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var list = GetDlcContentList(Marshal.PtrToStringAnsi(pathPtr) ?? "", (ulong)titleId);

            return CreateStringArray(list);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetDlcTitleId")]
        public static long JniGetDlcTitleIdNative(IntPtr pathPtr, IntPtr ncaPath)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            return Marshal.StringToHGlobalAnsi(GetDlcTitleId(Marshal.PtrToStringAnsi(pathPtr) ?? "", Marshal.PtrToStringAnsi(ncaPath) ?? ""));
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSignalEmulationClose")]
        public static void JniSignalEmulationCloseNative()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SignalEmulationClose();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceCloseEmulation")]
        public static void JniCloseEmulationNative()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            CloseEmulation();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceLoadDescriptor")]
        public static bool JnaLoadApplicationNative(int descriptor, int type, int updateDescriptor)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            if (SwitchDevice?.EmulationContext == null)
            {
                return false;
            }

            var stream = OpenFile(descriptor);
            var update = updateDescriptor == -1 ? null : OpenFile(updateDescriptor);

            return LoadApplication(stream, (FileType)type, update);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceVerifyFirmware")]
        public static IntPtr JniVerifyFirmware(int descriptor, bool isXci)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");

            var stream = OpenFile(descriptor);

            IntPtr stringHandle = 0;
            string? version = "0.0";

            try
            {
                version = VerifyFirmware(stream, isXci)?.VersionString;
            }
            catch (Exception _)
            {
                Logger.Error?.Print(LogClass.Service, $"Unable to verify firmware. Exception: {_}");
            }

            if (version != null)
            {
                stringHandle = Marshal.StringToHGlobalAnsi(version);
            }

            return stringHandle;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceInstallFirmware")]
        public static void JniInstallFirmware(int descriptor, bool isXci)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");

            var stream = OpenFile(descriptor);

            InstallFirmware(stream, isXci);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetInstalledFirmwareVersion")]
        public static IntPtr JniGetInstalledFirmwareVersion()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");

            var version = GetInstalledFirmwareVersion() ?? "0.0";
            return Marshal.StringToHGlobalAnsi(version);
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsInitialize")]
        public static bool JnaGraphicsInitialize(float resScale,
                float maxAnisotropy,
                bool fastGpuTime,
                bool fast2DCopy,
                bool enableMacroJit,
                bool enableMacroHLE,
                bool enableShaderCache,
                bool enableTextureRecompression,
                int backendThreading,
                int aspectRatio)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SearchPathContainer.Platform = UnderlyingPlatform.Android;
            return InitializeGraphics(new GraphicsConfiguration()
            {
                ResScale = resScale,
                MaxAnisotropy = maxAnisotropy,
                FastGpuTime = fastGpuTime,
                Fast2DCopy = fast2DCopy,
                EnableMacroJit = enableMacroJit,
                EnableMacroHLE = enableMacroHLE,
                EnableShaderCache = enableShaderCache,
                EnableTextureRecompression = enableTextureRecompression,
                BackendThreading = (BackendThreading)backendThreading,
                AspectRatio = (AspectRatio)aspectRatio
            });
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsInitializeRenderer")]
        public unsafe static bool JnaGraphicsInitializeRenderer(char** extensionsArray,
                                                                          int extensionsLength,
                                                                          long driverHandle)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            if (Renderer != null)
            {
                return false;
            }

            List<string?> extensions = new();

            for (int i = 0; i < extensionsLength; i++)
            {
                extensions.Add(Marshal.PtrToStringAnsi((IntPtr)extensionsArray[i]));
            }

            if (driverHandle != 0)
            {
                VulkanLoader = new VulkanLoader((IntPtr)driverHandle);
            }

            CreateSurface createSurfaceFunc = instance =>
            {
                _surfacePtr = Interop.GetSurfacePtr();
                _window = Interop.GetWindowsHandle();

                var api = VulkanLoader?.GetApi() ?? Vk.GetApi();
                if (api.TryGetInstanceExtension(new Instance(instance), out KhrAndroidSurface surfaceExtension))
                {
                    var createInfo = new AndroidSurfaceCreateInfoKHR
                    {
                        SType = StructureType.AndroidSurfaceCreateInfoKhr,
                        Window = (nint*)_surfacePtr,
                    };

                    var result = surfaceExtension.CreateAndroidSurface(new Instance(instance), createInfo, null, out var surface);

                    return (nint)surface.Handle;
                }

                return IntPtr.Zero;
            };

            return InitializeGraphicsRenderer(GraphicsBackend.Vulkan, createSurfaceFunc, extensions.ToArray());
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsRendererSetSize")]
        public static void JnaSetRendererSizeNative(int width, int height)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            Renderer?.Window?.SetSize(width, height);
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsRendererRunLoop")]
        public static void JniRunLoopNative()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetSwapBuffersCallback(() =>
            {
                var time = SwitchDevice.EmulationContext.Statistics.GetGameFrameTime();
                Interop.FrameEnded(time);
            });
            RunLoop();
        }

        [UnmanagedCallersOnly(EntryPoint = "loggingSetEnabled")]
        public static void JniSetLoggingEnabledNative(int logLevel, bool enabled)
        {
            Logger.SetEnable((LogLevel)logLevel, enabled);
        }

        [UnmanagedCallersOnly(EntryPoint = "loggingEnabledGraphicsLog")]
        public static void JniSetLoggingEnabledGraphicsLog(bool enabled)
        {
            _enableGraphicsLogging = enabled;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetGameInfo")]
        public unsafe static void JniGetGameInfo(int fileDescriptor, IntPtr extension, IntPtr infoPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            using var stream = OpenFile(fileDescriptor);
            var ext = Marshal.PtrToStringAnsi(extension);
            var info = GetGameInfo(stream, ext.ToLower()) ?? GetDefaultInfo(stream);
            var i = (GameInfoNative*)infoPtr;
            var n = new GameInfoNative(info);
            i->TitleId = n.TitleId;
            i->TitleName = n.TitleName;
            i->Version = n.Version;
            i->FileSize = n.FileSize;
            i->Icon = n.Icon;
            i->Version = n.Version;
            i->Developer = n.Developer;
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsRendererSetVsync")]
        public static void JnaSetVsyncStateNative(bool enabled)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetVsyncState(enabled);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputInitialize")]
        public static void JnaInitializeInput(int width, int height)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            InitializeInput(width, height);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetClientSize")]
        public static void JnaSetClientSize(int width, int height)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetClientSize(width, height);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetTouchPoint")]
        public static void JnaSetTouchPoint(int x, int y)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetTouchPoint(x, y);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputReleaseTouchPoint")]
        public static void JnaReleaseTouchPoint()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            ReleaseTouchPoint();
        }

        [UnmanagedCallersOnly(EntryPoint = "inputUpdate")]
        public static void JniUpdateInput()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            UpdateInput();
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetButtonPressed")]
        public static void JnaSetButtonPressed(int button, int id)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetButtonPressed((GamepadButtonInputId)button, id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetButtonReleased")]
        public static void JnaSetButtonReleased(int button, int id)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetButtonReleased((GamepadButtonInputId)button, id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetAccelerometerData")]
        public static void JniSetAccelerometerData(float x, float y, float z, int id)
        {
            var accel = new Vector3(x, y, z);
            SetAccelerometerData(accel, id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetGyroData")]
        public static void JniSetGyroData(float x, float y, float z, int id)
        {
            var gyro = new Vector3(x, y, z);
            SetGyroData(gyro, id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetStickAxis")]
        public static void JnaSetStickAxis(int stick, float x, float y, int id)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetStickAxis((StickInputId)stick, new Vector2(float.IsNaN(x) ? 0 : x, float.IsNaN(y) ? 0 : y), id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputConnectGamepad")]
        public static int JnaConnectGamepad(int index)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            return ConnectGamepad(index);
        }

        [UnmanagedCallersOnly(EntryPoint = "userGetOpenedUser")]
        public static IntPtr JniGetOpenedUser()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userId = GetOpenedUser();
            var ptr = Marshal.StringToHGlobalAnsi(userId);

            return ptr;
        }

        [UnmanagedCallersOnly(EntryPoint = "userGetUserPicture")]
        public static IntPtr JniGetUserPicture(IntPtr userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            return Marshal.StringToHGlobalAnsi(GetUserPicture(userId));
        }

        [UnmanagedCallersOnly(EntryPoint = "userSetUserPicture")]
        public static void JniGetUserPicture(IntPtr userIdPtr, IntPtr picturePtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";
            var picture = Marshal.PtrToStringAnsi(picturePtr) ?? "";

            SetUserPicture(userId, picture);
        }

        [UnmanagedCallersOnly(EntryPoint = "userGetUserName")]
        public static IntPtr JniGetUserName(IntPtr userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            return Marshal.StringToHGlobalAnsi(GetUserName(userId));
        }

        [UnmanagedCallersOnly(EntryPoint = "userSetUserName")]
        public static void JniSetUserName(IntPtr userIdPtr, IntPtr userNamePtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";
            var userName = Marshal.PtrToStringAnsi(userNamePtr) ?? "";

            SetUserName(userId, userName);
        }

        [UnmanagedCallersOnly(EntryPoint = "userGetAllUsers")]
        public static IntPtr JniGetAllUsers()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var users = GetAllUsers();

            return CreateStringArray(users.ToList());
        }

        [UnmanagedCallersOnly(EntryPoint = "userAddUser")]
        public static void JniAddUser(IntPtr userNamePtr, IntPtr picturePtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userName = Marshal.PtrToStringAnsi(userNamePtr) ?? "";
            var picture = Marshal.PtrToStringAnsi(picturePtr) ?? "";

            AddUser(userName, picture);
        }

        [UnmanagedCallersOnly(EntryPoint = "userDeleteUser")]
        public static void JniDeleteUser(IntPtr userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            DeleteUser(userId);
        }

        [UnmanagedCallersOnly(EntryPoint = "uiHandlerSetup")]
        public static void JniSetupUiHandler()
        {
            SetupUiHandler();
        }

        [UnmanagedCallersOnly(EntryPoint = "uiHandlerSetResponse")]
        public static void JniSetUiHandlerResponse(bool isOkPressed, IntPtr input)
        {
            SetUiHandlerResponse(isOkPressed, Marshal.PtrToStringAnsi(input) ?? "");
        }

        [UnmanagedCallersOnly(EntryPoint = "userOpenUser")]
        public static void JniOpenUser(IntPtr userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            OpenUser(userId);
        }

        [UnmanagedCallersOnly(EntryPoint = "userCloseUser")]
        public static void JniCloseUser(IntPtr userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            var userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            CloseUser(userId);
        }

        [UnmanagedCallersOnly(EntryPoint = "setAspectRatio")]
        public static void JnaSetAspectRatio(int aspectRatio)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetAspectRatio((AspectRatio)aspectRatio);
        }

        // 新增：设置内存配置的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "setMemoryConfiguration")]
        public static void JnaSetMemoryConfiguration(int memoryConfig)
        {
            SetMemoryConfiguration((MemoryConfiguration)memoryConfig);
        }

        // 新增：设置系统时间偏移的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "setSystemTimeOffset")]
        public static void JnaSetSystemTimeOffset(long offset)
        {
            SetSystemTimeOffset(offset);
        }

        // 新增：设置缩放过滤器的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "setScalingFilter")]
        public static void JnaSetScalingFilter(int filter)
        {
            try
            {
                // 更新配置状态
                ConfigurationState.Instance.Graphics.ScalingFilter.Value = (ScalingFilter)filter;
                
                // 如果渲染器已初始化，直接应用设置
                if (Renderer != null && Renderer.Window != null)
                {
                    Renderer.Window.SetScalingFilter((Ryujinx.Graphics.GAL.ScalingFilter)filter);
                }
            }
            catch (Exception ex)
            {
            }
        }

        // 新增：设置缩放过滤器级别的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "setScalingFilterLevel")]
        public static void JnaSetScalingFilterLevel(int level)
        {
            try
            {
                // 更新配置状态
                ConfigurationState.Instance.Graphics.ScalingFilterLevel.Value = level;
                
                // 如果渲染器已初始化，直接应用设置
                if (Renderer != null && Renderer.Window != null)
                {
                    Renderer.Window.SetScalingFilterLevel(level);
                }
            }
            catch (Exception ex)
            {
            }
        }

        // 新增：设置抗锯齿的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "setAntiAliasing")]
        public static void JnaSetAntiAliasing(int mode)
        {
            try
            {
                // 更新配置状态
                ConfigurationState.Instance.Graphics.AntiAliasing.Value = (AntiAliasing)mode;
                
                // 如果渲染器已初始化，直接应用设置
                if (Renderer != null && Renderer.Window != null)
                {
                    Renderer.Window.SetAntiAliasing((Ryujinx.Graphics.GAL.AntiAliasing)mode);
                }
            }
            catch (Exception ex)
            {
            }
        }

        // ==================== 表面格式管理的JNI方法 ====================

        [UnmanagedCallersOnly(EntryPoint = "surfaceGetAvailableFormats")]
        public static IntPtr JnaGetAvailableSurfaceFormats()
        {
            try
            {
                var formats = GetAvailableSurfaceFormats();
                return CreateStringArray(formats.ToList());
            }
            catch (Exception ex)
            {
                return IntPtr.Zero;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "surfaceSetCustomFormat")]
        public static void JnaSetCustomSurfaceFormat(int format, int colorSpace)
        {
            try
            {
                SetCustomSurfaceFormat(format, colorSpace);
            }
            catch (Exception ex)
            {
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "surfaceClearCustomFormat")]
        public static void JnaClearCustomSurfaceFormat()
        {
            try
            {
                ClearCustomSurfaceFormat();
            }
            catch (Exception ex)
            {
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "surfaceIsCustomFormatValid")]
        public static bool JnaIsCustomSurfaceFormatValid()
        {
            try
            {
                return IsCustomSurfaceFormatValid();
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "surfaceGetCurrentFormatInfo")]
        public static IntPtr JnaGetCurrentSurfaceFormatInfo()
        {
            try
            {
                var info = GetCurrentSurfaceFormatInfo();
                return Marshal.StringToHGlobalAnsi(info ?? "Unknown");
            }
            catch (Exception ex)
            {
                return IntPtr.Zero;
            }
        }

        // 新增：游戏启动时触发表面格式保存的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "surfaceOnGameStarted")]
        public static void JnaOnGameStarted()
        {
            try
            {
                Logger.Info?.Print(LogClass.Application, "Game started, triggering surface formats save");
                OnGameStarted();
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error triggering surface formats save: {ex.Message}");
            }
        }

        // ==================== 存档管理的JNI方法 ====================

        [UnmanagedCallersOnly(EntryPoint = "saveDataExport")]
        public static bool JnaExportSaveData(IntPtr titleIdPtr, IntPtr outputPathPtr)
        {
            try
            {
                string titleId = Marshal.PtrToStringAnsi(titleIdPtr) ?? "";
                string outputPath = Marshal.PtrToStringAnsi(outputPathPtr) ?? "";
                
                return ExportSaveData(titleId, outputPath);
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "saveDataImport")]
        public static bool JnaImportSaveData(IntPtr titleIdPtr, IntPtr zipFilePathPtr)
        {
            try
            {
                string titleId = Marshal.PtrToStringAnsi(titleIdPtr) ?? "";
                string zipFilePath = Marshal.PtrToStringAnsi(zipFilePathPtr) ?? "";
                
                return ImportSaveData(titleId, zipFilePath);
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "saveDataDelete")]
        public static bool JnaDeleteSaveData(IntPtr titleIdPtr)
        {
            try
            {
                string titleId = Marshal.PtrToStringAnsi(titleIdPtr) ?? "";
                return DeleteSaveData(titleId);
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        // 新增：删除存档文件的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "saveDataDeleteFiles")]
        public static bool JnaDeleteSaveFiles(IntPtr titleIdPtr)
        {
            try
            {
                string titleId = Marshal.PtrToStringAnsi(titleIdPtr) ?? "";
                return DeleteSaveFiles(titleId);
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "saveDataGetSaveId")]
        public static IntPtr JnaGetSaveIdByTitleId(IntPtr titleIdPtr)
        {
            try
            {
                string titleId = Marshal.PtrToStringAnsi(titleIdPtr) ?? "";
                string saveId = GetSaveIdByTitleId(titleId);
                
                return Marshal.StringToHGlobalAnsi(saveId ?? "");
            }
            catch (Exception ex)
            {
                return IntPtr.Zero;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "saveDataExists")]
        public static bool JnaSaveDataExists(IntPtr titleIdPtr)
        {
            try
            {
                string titleId = Marshal.PtrToStringAnsi(titleIdPtr) ?? "";
                return SaveDataExists(titleId);
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        // 新增：刷新存档数据的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "saveDataRefresh")]
        public static void JnaRefreshSaveData()
        {
            try
            {
                RefreshSaveData();
            }
            catch (Exception ex)
            {
            }
        }

        // 新增：调试存档数据的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "saveDataDebug")]
        public static void JnaDebugSaveData()
        {
            try
            {
                DebugSaveData();
            }
            catch (Exception ex)
            {
            }
        }

        // 新增：获取所有存档列表的JNI方法
        [UnmanagedCallersOnly(EntryPoint = "saveDataGetAll")]
        public static IntPtr JnaGetAllSaveData()
        {
            try
            {
                var saveDataList = GetSaveDataList();
                var titleIds = saveDataList.Select(s => s.TitleId).Where(id => !string.IsNullOrEmpty(id) && id != "0000000000000000").ToList();
                
                return CreateStringArray(titleIds);
            }
            catch (Exception ex)
            {
                return IntPtr.Zero;
            }
        }        
    }

    internal static partial class Logcat
    {
        [LibraryImport("liblog", StringMarshalling = StringMarshalling.Utf8)]
        private static partial void __android_log_print(LogLevel level, string? tag, string format, string args, IntPtr ptr);

        internal static void AndroidLogPrint(LogLevel level, string? tag, string message) =>
            __android_log_print(level, tag, "%s", message, IntPtr.Zero);

        internal enum LogLevel
        {
            Unknown = 0x00,
            Default = 0x01,
            Verbose = 0x02,
            Debug = 0x03,
            Info = 0x04,
            Warn = 0x05,
            Error = 0x06,
            Fatal = 0x07,
            Silent = 0x08,
        }
    }
}
