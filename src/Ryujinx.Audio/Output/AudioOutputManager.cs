using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ryujinx.Audio.Output
{
    /// <summary>
    /// The audio output manager.
    /// </summary>
    public class AudioOutputManager : IDisposable
    {
        /// <summary>
        /// Lock used for session allocation (仅用于ID分配，不用于会话操作).
        /// </summary>
        private readonly object _sessionIdLock = new();

        /// <summary>
        /// The session ids allocation table.
        /// </summary>
        private readonly ConcurrentQueue<int> _availableSessionIds;

        /// <summary>
        /// The device driver.
        /// </summary>
        private IHardwareDeviceDriver _deviceDriver;

        /// <summary>
        /// The events linked to each session.
        /// </summary>
        private IWritableEvent[] _sessionsBufferEvents;

        /// <summary>
        /// The <see cref="AudioOutputSystem"/> session instances.
        /// </summary>
        private readonly ConcurrentDictionary<int, AudioOutputSystem> _sessions;

        /// <summary>
        /// The count of active sessions.
        /// </summary>
        private int _activeSessionCount;

        /// <summary>
        /// The dispose state.
        /// </summary>
        private int _disposeState;

        /// <summary>
        /// Create a new <see cref="AudioOutputManager"/>.
        /// </summary>
        public AudioOutputManager()
        {
            _availableSessionIds = new ConcurrentQueue<int>();
            _sessions = new ConcurrentDictionary<int, AudioOutputSystem>();
            _activeSessionCount = 0;

            // 初始化可用的会话ID
            for (int i = 0; i < Constants.AudioOutSessionCountMax; i++)
            {
                _availableSessionIds.Enqueue(i);
            }
        }

        /// <summary>
        /// Initialize the <see cref="AudioOutputManager"/>.
        /// </summary>
        /// <param name="deviceDriver">The device driver.</param>
        /// <param name="sessionRegisterEvents">The events associated to each session.</param>
        public void Initialize(IHardwareDeviceDriver deviceDriver, IWritableEvent[] sessionRegisterEvents)
        {
            _deviceDriver = deviceDriver;
            _sessionsBufferEvents = sessionRegisterEvents;
        }

        /// <summary>
        /// Acquire a new session id.
        /// </summary>
        /// <returns>A new session id.</returns>
        private int AcquireSessionId()
        {
            // 使用无锁方式获取会话ID
            if (_availableSessionIds.TryDequeue(out int sessionId))
            {
                Interlocked.Increment(ref _activeSessionCount);
                Logger.Info?.Print(LogClass.AudioRenderer, $"Registered new output ({sessionId})");
                return sessionId;
            }

            throw new InvalidOperationException("No available session IDs");
        }

        /// <summary>
        /// Release a given <paramref name="sessionId"/>.
        /// </summary>
        /// <param name="sessionId">The session id to release.</param>
        private void ReleaseSessionId(int sessionId)
        {
            _availableSessionIds.Enqueue(sessionId);
            Interlocked.Decrement(ref _activeSessionCount);
            Logger.Info?.Print(LogClass.AudioRenderer, $"Unregistered output ({sessionId})");
        }

        /// <summary>
        /// Used to update audio output system.
        /// </summary>
        public void Update()
        {
            // 无锁遍历所有会话
            foreach (var sessionPair in _sessions)
            {
                sessionPair.Value?.Update();
            }
        }

        /// <summary>
        /// Register a new <see cref="AudioOutputSystem"/>.
        /// </summary>
        /// <param name="output">The <see cref="AudioOutputSystem"/> to register.</param>
        private void Register(AudioOutputSystem output)
        {
            int sessionId = output.GetSessionId();
            if (!_sessions.TryAdd(sessionId, output))
            {
                throw new InvalidOperationException($"Session {sessionId} already exists");
            }
        }

        /// <summary>
        /// Unregister a new <see cref="AudioOutputSystem"/>.
        /// </summary>
        /// <param name="output">The <see cref="AudioOutputSystem"/> to unregister.</param>
        internal void Unregister(AudioOutputSystem output)
        {
            int sessionId = output.GetSessionId();
            if (_sessions.TryRemove(sessionId, out _))
            {
                ReleaseSessionId(sessionId);
            }
        }

        /// <summary>
        /// Get the list of all audio outputs name.
        /// </summary>
        /// <returns>The list of all audio outputs name</returns>
        public string[] ListAudioOuts()
        {
            return new[] { Constants.DefaultDeviceOutputName };
        }

        /// <summary>
        /// Open a new <see cref="AudioOutputSystem"/>.
        /// </summary>
        /// <param name="outputDeviceName">The output device name selected by the <see cref="AudioOutputSystem"/></param>
        /// <param name="outputConfiguration">The output audio configuration selected by the <see cref="AudioOutputSystem"/></param>
        /// <param name="obj">The new <see cref="AudioOutputSystem"/></param>
        /// <param name="memoryManager">The memory manager that will be used for all guest memory operations</param>
        /// <param name="inputDeviceName">The input device name wanted by the user</param>
        /// <param name="sampleFormat">The sample format to use</param>
        /// <param name="parameter">The user configuration</param>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success</returns>
        public ResultCode OpenAudioOut(out string outputDeviceName,
                                       out AudioOutputConfiguration outputConfiguration,
                                       out AudioOutputSystem obj,
                                       IVirtualMemoryManager memoryManager,
                                       string inputDeviceName,
                                       SampleFormat sampleFormat,
                                       ref AudioInputConfiguration parameter)
        {
            int sessionId = AcquireSessionId();

            _sessionsBufferEvents[sessionId].Clear();

            IHardwareDeviceSession deviceSession = _deviceDriver.OpenDeviceSession(IHardwareDeviceDriver.Direction.Output, memoryManager, sampleFormat, parameter.SampleRate, parameter.ChannelCount);

            // 注意：移除了 parentLock 参数
            AudioOutputSystem audioOut = new AudioOutputSystem(this, deviceSession, _sessionsBufferEvents[sessionId]);

            ResultCode result = audioOut.Initialize(inputDeviceName, sampleFormat, ref parameter, sessionId);

            if (result == ResultCode.Success)
            {
                outputDeviceName = audioOut.DeviceName;
                outputConfiguration = new AudioOutputConfiguration
                {
                    ChannelCount = audioOut.ChannelCount,
                    SampleFormat = audioOut.SampleFormat,
                    SampleRate = audioOut.SampleRate,
                    AudioOutState = audioOut.GetState(),
                };

                obj = audioOut;

                Register(audioOut);
            }
            else
            {
                ReleaseSessionId(sessionId);

                obj = null;
                outputDeviceName = null;
                outputConfiguration = default;
            }

            return result;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) == 0)
            {
                Dispose(true);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 无锁方式处理所有会话的释放
                var sessions = _sessions.Values.ToArray();
                _sessions.Clear();

                foreach (AudioOutputSystem output in sessions)
                {
                    output?.Dispose();
                }
            }
        }
    }
}