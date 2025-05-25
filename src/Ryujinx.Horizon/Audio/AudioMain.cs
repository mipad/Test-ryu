using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Horizon.Audio
{
    class AudioMain : IService
    {
        private static readonly object _initializationLock = new();
        private static AudioUserIpcServer _ipcServer;
        private static volatile bool _isInitialized = false;
        private static volatile bool _isShutdownRequested = false;
        private static bool _initializationFailed = false;

        public static void Main(ServiceTable serviceTable)
        {
            Logger.Info?.Print(LogClass.ServiceAudio, "Audio service starting...");

            // Thread-safe initialization
            lock (_initializationLock)
            {
                if (_isInitialized)
                {
                    Logger.Warning?.Print(LogClass.ServiceAudio, "Audio service already initialized!");
                    return;
                }

                try
                {
                    _ipcServer = new AudioUserIpcServer();
                    
                    _ipcServer.Initialize(); // 不再检查返回值
                    _initializationFailed = false;

                    _isInitialized = true;
                    serviceTable.SignalServiceReady();
                    Logger.Info?.Print(LogClass.ServiceAudio, "Audio service initialized successfully");
                }
                catch (Exception ex)
                {
                    _initializationFailed = true;
                    Logger.Error?.Print(LogClass.ServiceAudio, $"Audio service initialization crashed: {ex}");
                    // 移除 SignalFailure 调用
                    return;
                }
            }

            // Main service loop
            while (!_isShutdownRequested && !_initializationFailed)
            {
                try
                {
                    _ipcServer.ServiceRequests();
                }
                catch (ThreadAbortException)
                {
                    Logger.Info?.Print(LogClass.ServiceAudio, "Audio service thread aborted");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.ServiceAudio, $"Audio service error: {ex}");

                    if (!TryRecoverService())
                    {
                        Logger.Error?.Print(LogClass.ServiceAudio, "Audio service recovery failed");
                        break;
                    }
                }
            }

            SafeShutdown();
        }

        private static bool TryRecoverService()
        {
            try
            {
                Logger.Info?.Print(LogClass.ServiceAudio, "Attempting audio service recovery...");

                lock (_initializationLock)
                {
                    _ipcServer?.Shutdown();

                    _ipcServer = new AudioUserIpcServer();
                    try
                    {
                        _ipcServer.Initialize();
                        _initializationFailed = false;
                        Logger.Info?.Print(LogClass.ServiceAudio, "Audio service recovered");
                        return true;
                    }
                    catch
                    {
                        _initializationFailed = true;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceAudio, $"Recovery crashed: {ex}");
                _initializationFailed = true;
                return false;
            }
        }

        private static void SafeShutdown()
        {
            lock (_initializationLock)
            {
                if (!_isInitialized || _isShutdownRequested)
                    return;

                _isShutdownRequested = true;

                try
                {
                    Logger.Info?.Print(LogClass.ServiceAudio, "Shutting down audio service...");
                    _ipcServer?.Shutdown();
                    Logger.Info?.Print(LogClass.ServiceAudio, "Audio service shutdown complete");
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.ServiceAudio, $"Shutdown error: {ex}");
                }
                finally
                {
                    _ipcServer = null;
                    _isInitialized = false;
                }
            }
        }

        public static void RequestShutdown()
        {
            _isShutdownRequested = true;
        }
    }
}
