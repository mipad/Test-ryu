using Ryujinx.Audio.Common; // 添加必要的命名空间引用
using System.Collections.Generic;

namespace Ryujinx.Audio.Integration
{
    /// <summary>
    /// 音频设备方向
    /// </summary>
    public enum Direction
    {
        /// <summary>
        /// 输出设备 (播放)
        /// </summary>
        Output,
        
        /// <summary>
        /// 输入设备 (录音)
        /// </summary>
        Input
    }

    /// <summary>
    /// Represent a hardware device session on the device driver.
    /// </summary>
    public interface IHardwareDeviceSession
    {
        /// <summary>
        /// The direction of the session.
        /// </summary>
        Direction Direction { get; }

        /// <summary>
        /// Register a new buffer.
        /// </summary>
        /// <param name="buffer">The buffer to register</param>
        /// <returns>True if the registration was successful</returns>
        bool RegisterBuffer(AudioBuffer buffer);

        /// <summary>
        /// Unregister a buffer.
        /// </summary>
        /// <param name="buffer">The buffer to unregister</param>
        void UnregisterBuffer(AudioBuffer buffer);

        /// <summary>
        /// Queue a buffer for playing.
        /// </summary>
        /// <param name="buffer">The buffer to queue</param>
        void QueueBuffer(AudioBuffer buffer);

        /// <summary>
        /// Batch queue buffers for playing.
        /// </summary>
        /// <param name="buffers">The list of buffers to queue</param>
        void QueueBuffers(IList<AudioBuffer> buffers);

        /// <summary>
        /// Get released buffers from hardware.
        /// </summary>
        /// <param name="maxCount">Maximum number of buffers to retrieve</param>
        /// <returns>List of released buffers</returns>
        IList<AudioBuffer> GetReleasedBuffers(int maxCount);

        /// <summary>
        /// Check if a buffer has been fully consumed.
        /// </summary>
        /// <param name="buffer">The buffer to check</param>
        /// <returns>True if the buffer has been fully consumed</returns>
        bool WasBufferFullyConsumed(AudioBuffer buffer);

        /// <summary>
        /// Start the session.
        /// </summary>
        void Start();

        /// <summary>
        /// Stop the session.
        /// </summary>
        void Stop();

        /// <summary>
        /// Prepare the session for closing.
        /// </summary>
        void PrepareToClose();

        /// <summary>
        /// Get the volume of the session.
        /// </summary>
        /// <returns>The volume of the session</returns>
        float GetVolume();

        /// <summary>
        /// Set the volume of the session.
        /// </summary>
        /// <param name="volume">The new volume to set</param>
        void SetVolume(float volume);

        /// <summary>
        /// Get the played sample count.
        /// </summary>
        /// <returns>The played sample count</returns>
        ulong GetPlayedSampleCount();

        /// <summary>
        /// Create a pre-encoded silence buffer.
        /// </summary>
        /// <returns>A silence buffer in the correct format</returns>
        AudioBuffer CreateSilenceBuffer();

        /// <summary>
        /// Dispose the session.
        /// </summary>
        void Dispose();
    }
}
