namespace Ryujinx.Graphics.Nvdec.MediaCodec.Common
{
    public class MediaCodecSurfacePool : IDisposable
    {
        private readonly List<IMediaCodecSurface> _availableSurfaces;
        private readonly List<IMediaCodecSurface> _usedSurfaces;
        private readonly object _lock = new object();
        private readonly int _maxSurfaces;
        private bool _disposed;
        
        public MediaCodecSurfacePool(int maxSurfaces = 3)
        {
            _maxSurfaces = maxSurfaces;
            _availableSurfaces = new List<IMediaCodecSurface>();
            _usedSurfaces = new List<IMediaCodecSurface>();
        }
        
        public IMediaCodecSurface GetSurface(int width, int height)
        {
            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(MediaCodecSurfacePool));
                
                // 尝试从可用池中获取
                for (int i = 0; i < _availableSurfaces.Count; i++)
                {
                    var surface = _availableSurfaces[i];
                    if (surface.Width == width && surface.Height == height)
                    {
                        _availableSurfaces.RemoveAt(i);
                        _usedSurfaces.Add(surface);
                        return surface;
                    }
                }
                
                // 创建新的 Surface
                if (_availableSurfaces.Count + _usedSurfaces.Count < _maxSurfaces)
                {
                    var newSurface = CreateNewSurface(width, height);
                    _usedSurfaces.Add(newSurface);
                    return newSurface;
                }
                
                // 重用最旧的 Surface
                if (_availableSurfaces.Count > 0)
                {
                    var surface = _availableSurfaces[0];
                    _availableSurfaces.RemoveAt(0);
                    _usedSurfaces.Add(surface);
                    
                    // 重新初始化 Surface
                    if (surface.Width != width || surface.Height != height)
                    {
                        surface.Dispose();
                        return CreateNewSurface(width, height);
                    }
                    
                    return surface;
                }
                
                throw new InvalidOperationException("Surface池已满");
            }
        }
        
        private IMediaCodecSurface CreateNewSurface(int width, int height)
        {
            // 创建 Android SurfaceTexture
            var textureId = GenerateTextureId();
            var surfaceTexture = AndroidJniWrapper.SurfaceTexture.Create(textureId);
            var surface = AndroidJniWrapper.Surface.Create(surfaceTexture);
            
            return new AndroidMediaCodecSurface(surface, textureId, width, height);
        }
        
        private int GenerateTextureId()
        {
            // 生成 OpenGL ES 纹理 ID
            int[] textures = new int[1];
            GL.GenTextures(1, textures);
            return textures[0];
        }
        
        public void ReturnSurface(IMediaCodecSurface surface)
        {
            if (surface == null)
                return;
            
            lock (_lock)
            {
                if (_disposed)
                {
                    surface.Dispose();
                    return;
                }
                
                if (_usedSurfaces.Remove(surface))
                {
                    _availableSurfaces.Add(surface);
                }
            }
        }
        
        public void Trim()
        {
            lock (_lock)
            {
                // 释放所有可用 Surface
                foreach (var surface in _availableSurfaces)
                {
                    surface.Dispose();
                }
                _availableSurfaces.Clear();
            }
        }
        
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                
                _disposed = true;
                
                foreach (var surface in _availableSurfaces.Concat(_usedSurfaces))
                {
                    surface.Dispose();
                }
                
                _availableSurfaces.Clear();
                _usedSurfaces.Clear();
            }
        }
    }
}
