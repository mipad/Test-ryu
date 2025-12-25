namespace Ryujinx.Graphics.Nvdec.MediaCodec.Common
{
    public class MediaCodecContext
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string MimeType { get; private set; }
        public int ColorFormat { get; private set; }
        public long FrameCount { get; private set; }
        public bool IsHardwareAccelerated { get; private set; }
        public bool IsRunning { get; private set; }
        
        private readonly Dictionary<string, object> _config;
        private readonly List<IMediaCodecSurface> _surfaces;
        private readonly object _lock = new object();
        
        public MediaCodecContext()
        {
            _config = new Dictionary<string, object>();
            _surfaces = new List<IMediaCodecSurface>();
        }
        
        public void UpdateConfiguration(Dictionary<string, object> config)
        {
            lock (_lock)
            {
                foreach (var kvp in config)
                {
                    _config[kvp.Key] = kvp.Value;
                }
                
                if (config.TryGetValue("width", out var width))
                    Width = (int)width;
                if (config.TryGetValue("height", out var height))
                    Height = (int)height;
                if (config.TryGetValue("mime", out var mime))
                    MimeType = (string)mime;
                if (config.TryGetValue("color-format", out var colorFormat))
                    ColorFormat = (int)colorFormat;
            }
        }
        
        public void AddSurface(IMediaCodecSurface surface)
        {
            lock (_lock)
            {
                _surfaces.Add(surface);
            }
        }
        
        public void RemoveSurface(IMediaCodecSurface surface)
        {
            lock (_lock)
            {
                _surfaces.Remove(surface);
                surface.Dispose();
            }
        }
        
        public void IncrementFrameCount()
        {
            lock (_lock)
            {
                FrameCount++;
            }
        }
        
        public void SetRunning(bool running)
        {
            lock (_lock)
            {
                IsRunning = running;
            }
        }
        
        public void SetHardwareAccelerated(bool hardware)
        {
            lock (_lock)
            {
                IsHardwareAccelerated = hardware;
            }
        }
        
        public Dictionary<string, object> GetConfigCopy()
        {
            lock (_lock)
            {
                return new Dictionary<string, object>(_config);
            }
        }
        
        public void Reset()
        {
            lock (_lock)
            {
                foreach (var surface in _surfaces)
                {
                    surface.Dispose();
                }
                _surfaces.Clear();
                _config.Clear();
                Width = 0;
                Height = 0;
                MimeType = null;
                ColorFormat = 0;
                FrameCount = 0;
                IsRunning = false;
            }
        }
    }
}
