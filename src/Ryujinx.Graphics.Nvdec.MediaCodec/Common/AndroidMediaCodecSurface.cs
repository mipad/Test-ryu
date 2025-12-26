using System;
using Ryujinx.Graphics.Nvdec.MediaCodec.Interfaces;

namespace Ryujinx.Graphics.Nvdec.MediaCodec.Common
{
    public class AndroidMediaCodecSurface : IMediaCodecSurface
    {
        private readonly IntPtr _nativeSurface;
        private readonly int _textureId;
        private readonly int _width;
        private readonly int _height;
        private bool _disposed;
        
        public IntPtr NativeSurface => _nativeSurface;
        public int TextureId => _textureId;
        public int Width => _width;
        public int Height => _height;
        public bool IsValid => !_disposed && _nativeSurface != IntPtr.Zero;
        
        public AndroidMediaCodecSurface(IntPtr nativeSurface, int textureId, int width, int height)
        {
            _nativeSurface = nativeSurface;
            _textureId = textureId;
            _width = width;
            _height = height;
        }
        
        public void UpdateTexture()
        {
            if (_disposed) return;
            
            try
            {
                // 实际应该调用 SurfaceTexture.updateTexImage()
                // 这里简化处理
            }
            catch (Exception ex)
            {
                ErrorHandler.LogError("UpdateTexture 失败", ex);
            }
        }
        
        public void GetTransformMatrix(float[] matrix)
        {
            if (_disposed || matrix == null || matrix.Length < 16) return;
            
            try
            {
                // 实际应该调用 SurfaceTexture.getTransformMatrix()
                // 这里返回单位矩阵
                for (int i = 0; i < 16; i++)
                {
                    matrix[i] = 0f;
                }
                matrix[0] = matrix[5] = matrix[10] = matrix[15] = 1f;
            }
            catch (Exception ex)
            {
                ErrorHandler.LogError("GetTransformMatrix 失败", ex);
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            
            try
            {
                // 释放资源
                if (_textureId != 0)
                {
                    GL.DeleteTexture(_textureId);
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.LogError("Dispose Surface 失败", ex);
            }
        }
    }
}
