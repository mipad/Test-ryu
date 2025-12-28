using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    // 硬件解码相关的新结构体
    [StructLayout(LayoutKind.Sequential)]
    internal struct AVBufferRef
    {
        public unsafe AVBuffer* Buffer;
        public unsafe byte* Data;
        public int Size;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct AVBuffer
    {
        // AVBuffer的内部结构，通常不需要直接访问
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct AVCodecHWConfig
    {
        public FFmpegApi.AVPixelFormat PixFmt;
        public FFmpegApi.AVHWDeviceType DeviceType;
        public AVCodecHWConfigMethod Methods;
        public int DeviceCaps;
        public unsafe byte* ConstraintSets;
        public int NumConstraintSets;
    }
    
    [Flags]
    internal enum AVCodecHWConfigMethod
    {
        AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01,
        AV_CODEC_HW_CONFIG_METHOD_HW_FRAMES_CTX = 0x02,
        AV_CODEC_HW_CONFIG_METHOD_INTERNAL = 0x04,
        AV_CODEC_HW_CONFIG_METHOD_AD_HOC = 0x08,
    }
    
    // FFCodec结构体用于访问编解码器私有API
    [StructLayout(LayoutKind.Sequential)]
    internal struct FFCodec<T> where T : unmanaged
    {
        public T Base;
        public unsafe void* CodecCallback;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct FFCodecLegacy<T> where T : unmanaged
    {
        public T Base;
        public unsafe void* Decode;
    }
    
    // AVCodec的旧版本结构体
    [StructLayout(LayoutKind.Sequential)]
    internal struct AVCodec501
    {
        public unsafe byte* Name;
        public unsafe byte* LongName;
        public int Type;
        public AVCodecID Id;
        public int Capabilities;
        public unsafe byte** SupportedFramerates;
        public unsafe int* PixFmts;
        public unsafe int* SupportedSamplerates;
        public unsafe ushort* SampleFmts;
        public unsafe ulong* ChannelLayouts;
        public byte MaxLowres;
        public unsafe void* PrivClass;
        public unsafe byte** Profiles;
        public unsafe void* Next;
        public unsafe void* InitThreadCopy;
        public unsafe void* UpdateThreadContext;
        public unsafe void* Defaults;
        public unsafe void* InitStaticData;
        public unsafe void* Init;
        public unsafe void* EncodeSub;
        public unsafe void* Encode2;
        public unsafe void* Decode;
        public unsafe void* Close;
        public unsafe void* ReceiveFrame;
        public unsafe void* ReceivePacket;
        public int Flush;
        public unsafe void* CapsInternal;
        public unsafe byte* WrapperName;
        public unsafe void* PrivDataSize;
        public unsafe void* UpdateThreadContextForUser;
    }
}