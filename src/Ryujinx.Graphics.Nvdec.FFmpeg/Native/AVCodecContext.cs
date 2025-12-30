using Ryujinx.Common.Memory;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct AVCodecContext
    {
#pragma warning disable CS0649 // Field is never assigned to
        public unsafe IntPtr AvClass;
        public int LogLevelOffset;
        public int CodecType;
        public unsafe AVCodec* Codec;
        public AVCodecID CodecId;
        public uint CodecTag;
        public IntPtr PrivData;
        public IntPtr Internal;
        public IntPtr Opaque;
        public long BitRate;
        public int BitRateTolerance;
        public int GlobalQuality;
        public int CompressionLevel;
        public int Flags; // flags 字段
        public int Flags2; // flags2 字段
        public IntPtr ExtraData;
        public int ExtraDataSize;
        public AVRational TimeBase;
        public int TicksPerFrame;
        public int Delay;
        public int Width;
        public int Height;
        public int CodedWidth;
        public int CodedHeight;
        public int GopSize;
        public int PixFmt;
        public IntPtr DrawHorizBand;
        public IntPtr GetFormat;
        public int MaxBFrames;
        public float BQuantFactor;
        public float BQuantOffset;
        public int HasBFrames;
        public float IQuantFactor;
        public float IQuantOffset;
        public float LumiMasking;
        public float TemporalCplxMasking;
        public float SpatialCplxMasking;
        public float PMasking;
        public float DarkMasking;
        public int SliceCount;
        public IntPtr SliceOffset;
        public AVRational SampleAspectRatio;
        public int MeCmp;
        public int MeSubCmp;
        public int MbCmp;
        public int IldctCmp;
        public int DiaSize;
        public int LastPredictorCount;
        public int MePreCmp;
        public int PreDiaSize;
        public int MeSubpelQuality;
        public int MeRange;
        public int SliceFlags;
        public int MbDecision;
        public IntPtr IntraMatrix;
        public IntPtr InterMatrix;
        public int IntraDcPrecision;
        public int SkipTop;
        public int SkipBottom;
        public int MbLmin;
        public int MbLmax;
        public int BidirRefine;
        public int KeyintMin;
        public int Refs;
        public int Mv0Threshold;
        public int ColorPrimaries;
        public int ColorPrc;
        public int Colorspace;
        public int ColorRange;
        public int ChromaSampleLocation;
        public int Slices;
        public int FieldOrder;
        public int SampleRate;
        public int Channels;
        public int SampleFmt;
        public int FrameSize;
        public int FrameNumber;
        public int BlockAlign;
        public int CutOff;
        public ulong ChannelLayout;
        public ulong RequestChannelLayout;
        public int AudioServiceType;
        public int RequestSampleFmt;
        public IntPtr GetBuffer2;
        public float QCompress;
        public float QBlur;
        public int QMin;
        public int QMax;
        public int MaxQdiff;
        public int RcBufferSize;
        public int RcOverrideCount;
        public IntPtr RcOverride;
        public long RcMaxRate;
        public long RcMinRate;
        public float RcMax_available_vbv_use;
        public float RcMin_vbv_overflow_use;
        public int RcInitialBufferOccupancy;
        public int Trellis;
        public IntPtr StatsOut;
        public IntPtr StatsIn;
        public int WorkaroundBugs;
        public int StrictStdCompliance;
        public int ErrorConcealment;
        public int Debug;
        public int ErrRecognition;
        public long ReorderedOpaque;
        public IntPtr HwAccel;
        public IntPtr HwAccelContext;
        public Array8<ulong> Error;
        public int DctAlgo;
        public int IdctAlgo;
        public int BitsPerCodedSample;
        public int BitsPerRawSample;
        public int LowRes;
        public int ThreadCount; // 多线程相关
        public int ThreadType;
        public int ActiveThreadType;
        public int ThreadSafeCallbacks;
        public IntPtr Execute;
        public IntPtr Execute2;
        public int NsseWeight;
        public int Profile;
        public int Level;
        public int SkipLoopFilter; // skip_loop_filter 字段
        public int SkipIdct;       // skip_idct 字段
        public int SkipFrame;      // skip_frame 字段
        public IntPtr SubtitleHeader;
        public int SubtitleHeaderSize;
        public int InitialPadding;
        public AVRational Framerate;
        public int SwPixFmt;
        public AVRational PktTimebase;
        public IntPtr CodecDescriptor;
        public long PtsCorrectionNumFaultyPts;
        public long PtsCorrectionNumFaultyDts;
        public long PtsCorrectionLastPts;
        public long PtsCorrectionLastDts;
        public IntPtr SubCharenc;
        public int SubCharencMode;
        public int SkipAlpha;
        public int SeekPreroll;
        public int DebugMv;
        public IntPtr ChromaIntraMatrix;
        public IntPtr DumpSeparator;
        public IntPtr CodecWhitelist;
        public uint Properties;
        public IntPtr CodedSideData;
        public int NbCodedSideData;
        public IntPtr HwFramesCtx;
        public int SubTextFormat;
        public int TrailingPadding;
        public long MaxPixels;
        public IntPtr HwDeviceCtx;
        public int HwAccelFlags;
        public int ApplyCropping;
        public int ExtraHwFrames;
        public int DiscardDamagedPercentage;
        public long MaxSamples;
        public int ExportSideData;
        public IntPtr GetEncodeBuffer;
        
        // 新添加的字段（FFmpeg 6.x 可能需要的）
        public int Strict;
        public int CodecWhitelistLength;
        public int CodecBlacklistLength;
        public int ThreadCountMax;
        public int ThreadCountMin;
        public int ThreadCountAuto;
        public int ThreadFrameCount;
        public int ThreadSliceCount;
        public int RefCountedFrames;
        public int SideDataOnlyPackets;
        public int ApplyCroppingAutomatically;
        public int ExtraHwFramesPadding;
#pragma warning restore CS0649
    }
}
