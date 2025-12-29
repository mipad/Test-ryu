namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    unsafe struct AVCodecHWConfig
    {
#pragma warning disable CS0649
        public int PixFmt;
        public int Methods;
        public int DeviceType;
#pragma warning restore CS0649
    }
}