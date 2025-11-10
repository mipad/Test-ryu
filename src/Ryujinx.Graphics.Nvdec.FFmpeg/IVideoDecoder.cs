using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    /// <summary>
    /// 视频解码器统一接口
    /// </summary>
    public interface IVideoDecoder : IDisposable
    {
        /// <summary>
        /// 解码器是否已初始化
        /// </summary>
        bool IsInitialized { get; }
        
        /// <summary>
        /// 是否为硬件解码器
        /// </summary>
        bool IsHardwareDecoder { get; }
        
        /// <summary>
        /// 解码器名称
        /// </summary>
        string CodecName { get; }
        
        /// <summary>
        /// 解码帧
        /// </summary>
        /// <param name="output">输出Surface</param>
        /// <param name="bitstream">输入比特流</param>
        /// <returns>解码结果</returns>
        int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream);
        
        /// <summary>
        /// 刷新解码器
        /// </summary>
        void Flush();
        
        /// <summary>
        /// 硬件解码器特定信息
        /// </summary>
        string HardwareDecoderName { get; }
    }
}