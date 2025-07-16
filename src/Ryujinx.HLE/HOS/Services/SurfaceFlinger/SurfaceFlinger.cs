using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.PreciseSleep;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    class SurfaceFlinger : IConsumerListener, IDisposable
    {
        // ==== 新增配置 ====
        private const int MaxAcquiredBuffers = 1; // Android 允许的最大获取缓冲区数
        private static readonly long StaleBufferThreshold = Stopwatch.Frequency; // 1秒
        private const int MaxRetryCount = 3; // 最大重试次数
        private const int RetryDelayMs = 50; // 重试延迟
        
        private readonly Switch _device;

        private readonly Dictionary<long, Layer> _layers;

        private bool _isRunning;

        private readonly Thread _composerThread;

        private readonly AutoResetEvent _event = new(false);
        private readonly AutoResetEvent _nextFrameEvent = new(true);
        private long _ticks;
        private long _ticksPerFrame;
        private readonly long _spinTicks;
        private readonly long _1msTicks;

        private int _swapInterval;
        private int _swapIntervalDelay;

        private readonly object _lock = new();

        // ==== 新增缓冲区状态跟踪 ====
        private class AcquiredBuffer
        {
            public long LayerId { get; }
            public BufferItem Item { get; }
            public long AcquireTime { get; }
            
            public AcquiredBuffer(long layerId, BufferItem item)
            {
                LayerId = layerId;
                Item = item;
                AcquireTime = Stopwatch.GetTimestamp();
            }
        }
        
        private readonly List<AcquiredBuffer> _acquiredBuffers = new List<AcquiredBuffer>();

        public long RenderLayerId { get; private set; }

        private class Layer
        {
            public int ProducerBinderId;
            public IGraphicBufferProducer Producer;
            public BufferItemConsumer Consumer;
            public BufferQueueCore Core;
            public ulong Owner;
            public LayerState State;
        }

        private class TextureCallbackInformation
        {
            public Layer Layer;
            public BufferItem Item;
        }

        public SurfaceFlinger(Switch device)
        {
            _device = device;
            _layers = new Dictionary<long, Layer>();
            RenderLayerId = 0;

            _composerThread = new Thread(HandleComposition)
            {
                Name = "SurfaceFlinger.Composer",
                Priority = ThreadPriority.AboveNormal
            };

            _ticks = 0;
            _spinTicks = Stopwatch.Frequency / 500;
            _1msTicks = Stopwatch.Frequency / 1000;

            UpdateSwapInterval(1);

            _composerThread.Start();
        }

        private void UpdateSwapInterval(int swapInterval)
        {
            _swapInterval = swapInterval;

            // If the swap interval is 0, Game VSync is disabled.
            if (_swapInterval == 0)
            {
                _nextFrameEvent.Set();
                _ticksPerFrame = 1;
            }
            else
            {
                _ticksPerFrame = Stopwatch.Frequency / TargetFps;
            }
        }

        public IGraphicBufferProducer CreateLayer(out long layerId, ulong pid, LayerState initialState = LayerState.ManagedClosed)
        {
            layerId = 1;

            lock (_lock)
            {
                foreach (KeyValuePair<long, Layer> pair in _layers)
                {
                    if (pair.Key >= layerId)
                    {
                        layerId = pair.Key + 1;
                    }
                }
            }

            CreateLayerFromId(pid, layerId, initialState);

            return GetProducerByLayerId(layerId);
        }

        private void CreateLayerFromId(ulong pid, long layerId, LayerState initialState)
        {
            lock (_lock)
            {
                Logger.Info?.Print(LogClass.SurfaceFlinger, $"Creating layer {layerId}");

                BufferQueueCore core = BufferQueue.CreateBufferQueue(_device, pid, out BufferQueueProducer producer, out BufferQueueConsumer consumer);

                core.BufferQueued += () =>
                {
                    _nextFrameEvent.Set();
                };

                _layers.Add(layerId, new Layer
                {
                    ProducerBinderId = HOSBinderDriverServer.RegisterBinderObject(producer),
                    Producer = producer,
                    Consumer = new BufferItemConsumer(_device, consumer, 0, -1, false, this),
                    Core = core,
                    Owner = pid,
                    State = initialState,
                });
            }
        }

        public Vi.ResultCode OpenLayer(ulong pid, long layerId, out IBinder producer)
        {
            Layer layer = GetLayerByIdLocked(layerId);

            if (layer == null || layer.State != LayerState.ManagedClosed)
            {
                producer = null;

                return Vi.ResultCode.InvalidArguments;
            }

            layer.State = LayerState.ManagedOpened;
            producer = layer.Producer;

            return Vi.ResultCode.Success;
        }

        public Vi.ResultCode CloseLayer(long layerId)
        {
            lock (_lock)
            {
                Layer layer = GetLayerByIdLocked(layerId);

                if (layer == null)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to close layer {layerId}");

                    return Vi.ResultCode.InvalidValue;
                }

                CloseLayer(layerId, layer);

                return Vi.ResultCode.Success;
            }
        }

        public Vi.ResultCode DestroyManagedLayer(long layerId)
        {
            lock (_lock)
            {
                Layer layer = GetLayerByIdLocked(layerId);

                if (layer == null)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy managed layer {layerId} (not found)");

                    return Vi.ResultCode.InvalidValue;
                }

                if (layer.State != LayerState.ManagedClosed && layer.State != LayerState.ManagedOpened)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy managed layer {layerId} (permission denied)");

                    return Vi.ResultCode.PermissionDenied;
                }

                HOSBinderDriverServer.UnregisterBinderObject(layer.ProducerBinderId);

                if (_layers.Remove(layerId) && layer.State == LayerState.ManagedOpened)
                {
                    CloseLayer(layerId, layer);
                }

                return Vi.ResultCode.Success;
            }
        }

        public Vi.ResultCode DestroyStrayLayer(long layerId)
        {
            lock (_lock)
            {
                Layer layer = GetLayerByIdLocked(layerId);

                if (layer == null)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy stray layer {layerId} (not found)");

                    return Vi.ResultCode.InvalidValue;
                }

                if (layer.State != LayerState.Stray)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy stray layer {layerId} (permission denied)");

                    return Vi.ResultCode.PermissionDenied;
                }

                HOSBinderDriverServer.UnregisterBinderObject(layer.ProducerBinderId);

                if (_layers.Remove(layerId))
                {
                    CloseLayer(layerId, layer);
                }

                return Vi.ResultCode.Success;
            }
        }

        private void CloseLayer(long layerId, Layer layer)
        {
            // ==== 新增：释放层关联的缓冲区 ====
            ReleaseLayerBuffers(layerId);
            
            // If the layer was removed and the current in use, we need to change the current layer in use.
            if (RenderLayerId == layerId)
            {
                // If no layer is availaible, reset to default value.
                if (_layers.Count == 0)
                {
                    SetRenderLayer(0);
                }
                else
                {
                    SetRenderLayer(_layers.Last().Key);
                }
            }

            if (layer.State == LayerState.ManagedOpened)
            {
                layer.State = LayerState.ManagedClosed;
            }
        }

        // ==== 新增方法：释放层关联的缓冲区 ====
        private void ReleaseLayerBuffers(long layerId)
        {
            for (int i = _acquiredBuffers.Count - 1; i >= 0; i--)
            {
                if (_acquiredBuffers[i].LayerId == layerId)
                {
                    Logger.Info?.Print(LogClass.SurfaceFlinger, $"Releasing buffer for closed layer {layerId}");
                    ReleaseBufferInternal(_acquiredBuffers[i].LayerId, _acquiredBuffers[i].Item);
                    _acquiredBuffers.RemoveAt(i);
                }
            }
        }

        public void SetRenderLayer(long layerId)
        {
            lock (_lock)
            {
                RenderLayerId = layerId;
            }
        }

        private Layer GetLayerByIdLocked(long layerId)
        {
            foreach (KeyValuePair<long, Layer> pair in _layers)
            {
                if (pair.Key == layerId)
                {
                    return pair.Value;
                }
            }

            return null;
        }

        public IGraphicBufferProducer GetProducerByLayerId(long layerId)
        {
            lock (_lock)
            {
                Layer layer = GetLayerByIdLocked(layerId);

                if (layer != null)
                {
                    return layer.Producer;
                }
            }

            return null;
        }

        private void HandleComposition()
        {
            _isRunning = true;

            long lastTicks = PerformanceCounter.ElapsedTicks;

            while (_isRunning)
            {
                long ticks = PerformanceCounter.ElapsedTicks;

                if (_swapInterval == 0)
                {
                    Compose();

                    _device.System?.SignalVsync();

                    _nextFrameEvent.WaitOne(17);
                    lastTicks = ticks;
                }
                else
                {
                    _ticks += ticks - lastTicks;
                    lastTicks = ticks;

                    if (_ticks >= _ticksPerFrame)
                    {
                        if (_swapIntervalDelay-- == 0)
                        {
                            Compose();

                            // When a frame is presented, delay the next one by its swap interval value.
                            _swapIntervalDelay = Math.Max(0, _swapInterval - 1);
                        }

                        _device.System?.SignalVsync();

                        // Apply a maximum bound of 3 frames to the tick remainder, in case some event causes Ryujinx to pause for a long time or messes with the timer.
                        _ticks = Math.Min(_ticks - _ticksPerFrame, _ticksPerFrame * 3);
                    }

                    // Sleep if possible. If the time til the next frame is too low, spin wait instead.
                    long diff = _ticksPerFrame - (_ticks + PerformanceCounter.ElapsedTicks - ticks);
                    if (diff > 0)
                    {
                        PreciseSleepHelper.SleepUntilTimePoint(_event, PerformanceCounter.ElapsedTicks + diff);

                        diff = _ticksPerFrame - (_ticks + PerformanceCounter.ElapsedTicks - ticks);

                        if (diff < _spinTicks)
                        {
                            PreciseSleepHelper.SpinWaitUntilTimePoint(PerformanceCounter.ElapsedTicks + diff);
                        }
                        else
                        {
                            _event.WaitOne((int)(diff / _1msTicks));
                        }
                    }
                }
            }
        }

        public void Compose()
        {
            lock (_lock)
            {
                // ==== 新增：释放过期缓冲区 ====
                ReleaseStaleBuffers();
                
                // TODO: support multilayers (& multidisplay ?)
                if (RenderLayerId == 0)
                {
                    return;
                }

                Layer layer = GetLayerByIdLocked(RenderLayerId);
                if (layer == null) return;

                // ==== 新增：检查是否达到缓冲区获取限制 ====
                if (_acquiredBuffers.Count >= MaxAcquiredBuffers)
                {
                    Logger.Warning?.Print(LogClass.SurfaceFlinger, 
                        $"Max acquired buffers reached ({_acquiredBuffers.Count}). Releasing oldest.");
                    ReleaseOldestBuffer();
                }

                Status acquireStatus = Status.NoBufferAvailaible;
                BufferItem item = null;
                int retryCount = 0;
                
                // ==== 新增：带重试的缓冲区获取 ====
                while (retryCount++ < MaxRetryCount)
                {
                    acquireStatus = layer.Consumer.AcquireBuffer(out item, 0);

                    if (acquireStatus == Status.Success)
                    {
                        break;
                    }
                    else if (acquireStatus == Status.NoBufferAvailaible)
                    {
                        // 短暂等待后重试
                        Thread.Sleep(RetryDelayMs);
                    }
                    else
                    {
                        break;
                    }
                }

                if (acquireStatus == Status.Success)
                {
                    // 记录获取的缓冲区
                    _acquiredBuffers.Add(new AcquiredBuffer(RenderLayerId, item));
                    
                    // If device vsync is disabled, reflect the change.
                    if (!_device.EnableDeviceVsync)
                    {
                        if (_swapInterval != 0)
                        {
                            UpdateSwapInterval(0);
                        }
                    }
                    else if (item.SwapInterval != _swapInterval)
                    {
                        UpdateSwapInterval(item.SwapInterval);
                    }

                    PostFrameBuffer(layer, item);
                }
                else if (acquireStatus != Status.NoBufferAvailaible && acquireStatus != Status.InvalidOperation)
                {
                    throw new InvalidOperationException();
                }
            }
        }

        // ==== 新增方法：释放过期缓冲区 ====
        private void ReleaseStaleBuffers()
        {
            long currentTime = Stopwatch.GetTimestamp();
            
            for (int i = _acquiredBuffers.Count - 1; i >= 0; i--)
            {
                var buffer = _acquiredBuffers[i];
                double ageSeconds = (double)(currentTime - buffer.AcquireTime) / Stopwatch.Frequency;
                
                if (ageSeconds > StaleBufferThreshold)
                {
                    Logger.Warning?.Print(LogClass.SurfaceFlinger, 
                        $"Releasing stale buffer held for {ageSeconds:F2} seconds");
                    
                    ReleaseBufferInternal(buffer.LayerId, buffer.Item);
                    _acquiredBuffers.RemoveAt(i);
                }
            }
        }
        
        // ==== 新增方法：释放最旧缓冲区 ====
        private void ReleaseOldestBuffer()
        {
            if (_acquiredBuffers.Count > 0)
            {
                var oldest = _acquiredBuffers[0];
                ReleaseBufferInternal(oldest.LayerId, oldest.Item);
                _acquiredBuffers.RemoveAt(0);
            }
        }
        
        // ==== 新增方法：内部缓冲区释放 ====
        private void ReleaseBufferInternal(long layerId, BufferItem item)
        {
            var layer = GetLayerByIdLocked(layerId);
            if (layer != null)
            {
                AndroidFence fence = AndroidFence.NoFence;
                layer.Consumer.ReleaseBuffer(item, ref fence);
            }
        }

        private void PostFrameBuffer(Layer layer, BufferItem item)
        {
            int frameBufferWidth = item.GraphicBuffer.Object.Width;
            int frameBufferHeight = item.GraphicBuffer.Object.Height;

            int nvMapHandle = item.GraphicBuffer.Object.Buffer.Surfaces[0].NvMapHandle;

            if (nvMapHandle == 0)
            {
                nvMapHandle = item.GraphicBuffer.Object.Buffer.NvMapId;
            }

            ulong bufferOffset = (ulong)item.GraphicBuffer.Object.Buffer.Surfaces[0].Offset;

            NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(layer.Owner, nvMapHandle);

            ulong frameBufferAddress = map.Address + bufferOffset;

            Format format = ConvertColorFormat(item.GraphicBuffer.Object.Buffer.Surfaces[0].ColorFormat);

            byte bytesPerPixel =
                format == Format.B5G6R5Unorm ||
                format == Format.R4G4B4A4Unorm ? (byte)2 : (byte)4;

            int gobBlocksInY = 1 << item.GraphicBuffer.Object.Buffer.Surfaces[0].BlockHeightLog2;

            // Note: Rotation is being ignored.
            Rect cropRect = item.Crop;

            bool flipX = item.Transform.HasFlag(NativeWindowTransform.FlipX);
            bool flipY = item.Transform.HasFlag(NativeWindowTransform.FlipY);

            AspectRatio aspectRatio = _device.Configuration.AspectRatio;
            bool isStretched = aspectRatio == AspectRatio.Stretched;

            ImageCrop crop = new(
                cropRect.Left,
                cropRect.Right,
                cropRect.Top,
                cropRect.Bottom,
                flipX,
                flipY,
                isStretched,
                aspectRatio.ToFloatX(),
                aspectRatio.ToFloatY());

            TextureCallbackInformation textureCallbackInformation = new()
            {
                Layer = layer,
                Item = item,
            };

            if (_device.Gpu.Window.EnqueueFrameThreadSafe(
                layer.Owner,
                frameBufferAddress,
                frameBufferWidth,
                frameBufferHeight,
                0,
                false,
                gobBlocksInY,
                format,
                bytesPerPixel,
                crop,
                AcquireBuffer,
                ReleaseBuffer,
                textureCallbackInformation))
            {
                if (item.Fence.FenceCount == 0)
                {
                    _device.Gpu.Window.SignalFrameReady();
                    _device.Gpu.GPFifo.Interrupt();
                }
                else
                {
                    item.Fence.RegisterCallback(_device.Gpu, (x) =>
                    {
                        _device.Gpu.Window.SignalFrameReady();
                        _device.Gpu.GPFifo.Interrupt();
                    });
                }
            }
            else
            {
                ReleaseBuffer(textureCallbackInformation);
                // ==== 新增：从获取列表移除 ====
                RemoveAcquiredBuffer(layer, item);
            }
        }

        // ==== 新增方法：从获取列表移除缓冲区 ====
        private void RemoveAcquiredBuffer(Layer layer, BufferItem item)
        {
            for (int i = 0; i < _acquiredBuffers.Count; i++)
            {
                if (_acquiredBuffers[i].Layer == layer && _acquiredBuffers[i].Item == item)
                {
                    _acquiredBuffers.RemoveAt(i);
                    break;
                }
            }
        }

        private void ReleaseBuffer(object obj)
        {
            ReleaseBuffer((TextureCallbackInformation)obj);
        }

        private void ReleaseBuffer(TextureCallbackInformation information)
        {
            AndroidFence fence = AndroidFence.NoFence;
            information.Layer.Consumer.ReleaseBuffer(information.Item, ref fence);
            
            // ==== 新增：从获取列表移除 ====
            RemoveAcquiredBuffer(information.Layer, information.Item);
        }

        private void AcquireBuffer(GpuContext ignored, object obj)
        {
            AcquireBuffer((TextureCallbackInformation)obj);
        }

        private void AcquireBuffer(TextureCallbackInformation information)
        {
            information.Item.Fence.WaitForever(_device.Gpu);
        }

        public static Format ConvertColorFormat(ColorFormat colorFormat)
        {
            return colorFormat switch
            {
                ColorFormat.A8B8G8R8 => Format.R8G8B8A8Unorm,
                ColorFormat.X8B8G8R8 => Format.R8G8B8A8Unorm,
                ColorFormat.R5G6B5 => Format.B5G6R5Unorm,
                ColorFormat.A8R8G8B8 => Format.B8G8R8A8Unorm,
                ColorFormat.A4B4G4R4 => Format.R4G4B4A4Unorm,
                _ => throw new NotImplementedException($"Color Format \"{colorFormat}\" not implemented!"),
            };
        }

        public void Dispose()
        {
            _isRunning = false;

            // ==== 新增：释放所有获取的缓冲区 ====
            foreach (var buffer in _acquiredBuffers)
            {
                ReleaseBufferInternal(buffer.LayerId, buffer.Item);
            }
            _acquiredBuffers.Clear();
            
            foreach (Layer layer in _layers.Values)
            {
                layer.Core.PrepareForExit();
            }
        }

        public void OnFrameAvailable(ref BufferItem item)
        {
            _device.Statistics.RecordGameFrameTime();
        }

        public void OnFrameReplaced(ref BufferItem item)
        {
            _device.Statistics.RecordGameFrameTime();
        }

        public void OnBuffersReleased() { }
    }
}
