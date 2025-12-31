// Ryujinx.Graphics.Nvdec.FFmpeg/FFmpegContextFactory.cs
using System;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System.Collections.Concurrent;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    public class FFmpegContextFactory : IDisposable
    {
        private readonly ConcurrentDictionary<AVCodecID, FFmpegContext> _contexts = new();
        private readonly ConcurrentDictionary<AVCodecID, long> _nativeWindows = new();
        private readonly object _lock = new object();
        
        public FFmpegContext GetOrCreateContext(AVCodecID codecId, long nativeWindowPtr = -1)
        {
            lock (_lock)
            {
                if (!_contexts.TryGetValue(codecId, out var context))
                {
                    // 存储 NativeWindow 指针
                    if (nativeWindowPtr != -1)
                    {
                        _nativeWindows[codecId] = nativeWindowPtr;
                    }
                    
                    // 创建新的上下文
                    context = new FFmpegContext(codecId, nativeWindowPtr);
                    _contexts[codecId] = context;
                }
                else if (nativeWindowPtr != -1 && _nativeWindows.TryGetValue(codecId, out var storedWindow) && storedWindow != nativeWindowPtr)
                {
                    // NativeWindow 已更改，需要重新创建上下文
                    context.Dispose();
                    
                    _nativeWindows[codecId] = nativeWindowPtr;
                    context = new FFmpegContext(codecId, nativeWindowPtr);
                    _contexts[codecId] = context;
                }
                
                return context;
            }
        }
        
        public void UpdateNativeWindow(AVCodecID codecId, long nativeWindowPtr)
        {
            lock (_lock)
            {
                _nativeWindows[codecId] = nativeWindowPtr;
                
                if (_contexts.TryGetValue(codecId, out var context))
                {
                    // 如果上下文存在且正在使用硬件加速，需要重新创建
                    if (context.IsHardwareAccelerated)
                    {
                        context.Dispose();
                        _contexts[codecId] = new FFmpegContext(codecId, nativeWindowPtr);
                    }
                }
            }
        }
        
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var context in _contexts.Values)
                {
                    context.Dispose();
                }
                
                _contexts.Clear();
                _nativeWindows.Clear();
            }
        }
    }
}
