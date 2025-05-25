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
                    
                    if (!_ipcServer.Initialize())
                    {
                        Logger.Error?.Print(LogClass.ServiceAudio, "Audio IPC server initialization failed!");
                        serviceTable.SignalFailure();
                        return;
                    }

                    _isInitialized = true;
                    serviceTable.SignalServiceReady();
                    Logger.Info?.Print(LogClass.ServiceAudio, "Audio service initialized successfully");
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.ServiceAudio, $"Audio service initialization crashed: {ex}");
                    serviceTable.SignalFailure();
                    return;
                }
            }

            // Main service loop
            while (!_isShutdownRequested)
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

                    if (!RecoverService())
                    {
                        Logger.Error?.Print(LogClass.ServiceAudio, "Audio service recovery failed");
                        break;
                    }
                }
            }

            SafeShutdown();
        }

        private static bool RecoverService()
        {
            try
            {
                Logger.Info?.Print(LogClass.ServiceAudio, "Attempting audio service recovery...");

                lock (_initializationLock)
                {
                    _ipcServer?.Shutdown();

                    _ipcServer = new AudioUserIpcServer();
                    if (!_ipcServer.Initialize())
                    {
                        Logger.Error?.Print(LogClass.ServiceAudio, "Recovery initialization failed");
                        return false;
                    }

                    Logger.Info?.Print(LogClass.ServiceAudio, "Audio service recovered");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceAudio, $"Recovery crashed: {ex}");
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
