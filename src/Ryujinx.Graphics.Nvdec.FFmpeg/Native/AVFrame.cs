using Ryujinx.Common.Memory;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    public struct AVFrame
    {
#pragma warning disable CS0649 // Field is never assigned to
        public Array8<IntPtr> Data;
        public Array8<int> LineSize;
        public IntPtr ExtendedData;
        public int Width;
        public int Height;
        public int NumSamples;
        public int Format;  // 像素格式或样本格式
        public int KeyFrame;
        public int PictureType;
        public AVRational SampleAspectRatio;
        public long Pts;
        public long PktDts;
        public AVRational TimeBase;
        public int CodedPictureNumber;
        public int DisplayPictureNumber;
        public int Quality;
        public IntPtr Opaque;
        public int RepeatPicture;
        public int InterlacedFrame;
        public int TopFieldFirst;
        public int PaletteHasChanged;
        public long ReorderedOpaque;
        public int SampleRate;
        public ulong ChannelLayout;
        public int Colorspace;
        public int ColorRange;
        public IntPtr HwFramesCtx;
        public IntPtr OpaqueRef;
        public long CropTop;
        public long CropBottom;
        public long CropLeft;
        public long CropRight;
        public IntPtr PrivateRef;
#pragma warning restore CS0649

        // NOTE: There is more after, but the layout kind of changed a bit and we don't need more than this. This is safe as we only manipulate this behind a reference.
    }
}