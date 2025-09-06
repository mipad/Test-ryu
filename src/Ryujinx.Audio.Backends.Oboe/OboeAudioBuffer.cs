#if ANDROID
namespace Ryujinx.Audio.Backends.Oboe
{
    internal class OboeAudioBuffer
    {
        public ulong DriverIdentifier { get; set; }
        public ulong SampleCount { get; set; }
        public byte[] AudioData { get; set; }
        public int Offset { get; set; }
        public int Size { get; set; }
    }
}
#endif
