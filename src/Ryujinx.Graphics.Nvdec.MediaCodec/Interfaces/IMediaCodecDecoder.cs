using System;
using Ryujinx.Graphics.Nvdec.MediaCodec.Common;

namespace Ryujinx.Graphics.Nvdec.MediaCodec.Interfaces
{
    public interface IMediaCodecDecoder : IDecoder
    {
        bool Initialize(string mimeType, int width, int height);
        bool Configure(MediaFormatConfig config);
        bool Start();
        bool Stop();
        bool Release();
        
        int DequeueInputBuffer(long timeoutUs);
        ByteBuffer GetInputBuffer(int index);
        void QueueInputBuffer(int index, int offset, int size, long presentationTimeUs, int flags);
        
        int DequeueOutputBuffer(ref BufferInfo info, long timeoutUs);
        ByteBuffer GetOutputBuffer(int index);
        void ReleaseOutputBuffer(int index, bool render);
        
        event Action<MediaCodecEvent> OnEvent;
    }
    
    public enum MediaCodecEvent
    {
        OutputFormatChanged,
        OutputBuffersChanged,
        Error,
        Eos
    }
}
