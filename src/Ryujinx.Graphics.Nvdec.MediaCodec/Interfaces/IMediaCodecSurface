namespace Ryujinx.Graphics.Nvdec.MediaCodec.Interfaces
{
    public interface IMediaCodecSurface : IDisposable
    {
        IntPtr NativeSurface { get; }
        int TextureId { get; }
        
        void UpdateTexture();
        void GetTransformMatrix(float[] matrix);
        
        int Width { get; }
        int Height { get; }
        bool IsValid { get; }
    }
}
