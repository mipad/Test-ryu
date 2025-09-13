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
        private const int TargetFps = 60;

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
                // TODO: support multilayers (& multidisplay ?)
                if (RenderLayerId == 0)
                {
                    return;
                }

                Layer layer = GetLayerByIdLocked(RenderLayerId);

                Status acquireStatus = layer.Consumer.AcquireBuffer(out BufferItem item, 0);

                if (acquireStatus == Status.Success)
                {
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
            // 提取颜色空间、分量顺序、分量大小和数据类型信息
            ColorSpace colorSpace = (ColorSpace)((ulong)colorFormat & 0xFF);
            ColorSwizzle swizzle = (ColorSwizzle)((ulong)colorFormat & 0xFF00);
            ColorComponent component = (ColorComponent)((ulong)colorFormat & 0xFF0000);
            ColorDataType dataType = (ColorDataType)((ulong)colorFormat & 0xFF000000);

            // 首先处理一些特定的颜色格式
            switch (colorFormat)
            {
                case ColorFormat.A8B8G8R8:
                case ColorFormat.X8B8G8R8:
                    return Format.R8G8B8A8Unorm;
                    
                case ColorFormat.R5G6B5:
                    return Format.B5G6R5Unorm;
                    
                case ColorFormat.A8R8G8B8:
                    return Format.B8G8R8A8Unorm;
                    
                case ColorFormat.A4B4G4R4:
                    return Format.R4G4B4A4Unorm;
                    
                case ColorFormat.R8G8B8A8:
                    return Format.R8G8B8A8Unorm;
                    
                case ColorFormat.B8G8R8A8:
                    return Format.B8G8R8A8Unorm;
                    
                case ColorFormat.R10G10B10A2:
                    return Format.R10G10B10A2Unorm;
                    
                case ColorFormat.B10G10R10A2:
                    return Format.B10G10R10A2Unorm;
                    
                case ColorFormat.B5G6R5:
                    return Format.B5G6R5Unorm;
                    
                case ColorFormat.B5G5R5A1:
                    return Format.B5G5R5A1Unorm;
            }

            // 然后基于组件信息进行通用转换
            switch (component)
            {
                case ColorComponent.X8Y8Z8W8:
                    // 根据分量顺序决定格式
                    if (swizzle == ColorSwizzle.WZYX) // ABGR顺序
                        return dataType == ColorDataType.Float ? Format.R32G32B32A32Float : Format.R8G8B8A8Unorm;
                    else if (swizzle == ColorSwizzle.XYZW) // RGBA顺序
                        return dataType == ColorDataType.Float ? Format.R32G32B32A32Float : Format.R8G8B8A8Unorm;
                    else if (swizzle == ColorSwizzle.ZYXW) // BGRA顺序
                        return Format.B8G8R8A8Unorm;
                    else
                        return Format.R8G8B8A8Unorm; // 默认
                    
                case ColorComponent.X5Y6Z5:
                    return Format.B5G6R5Unorm;
                    
                case ColorComponent.X4Y4Z4W4:
                    return Format.R4G4B4A4Unorm;
                    
                case ColorComponent.X1Y5Z5W5:
                    return Format.B5G5R5A1Unorm;
                    
                case ColorComponent.X16Y16Z16W16:
                    return dataType == ColorDataType.Float ? Format.R16G16B16A16Float : Format.R16G16B16A16Unorm;
                    
                case ColorComponent.X16Y16:
                    return dataType == ColorDataType.Float ? Format.R16G16Float : Format.R16G16Unorm;
                    
                case ColorComponent.X16:
                    return dataType == ColorDataType.Float ? Format.R16Float : Format.R16Unorm;
                    
                case ColorComponent.X8Y8:
                    return Format.R8G8Unorm;
                    
                case ColorComponent.X8:
                    return Format.R8Unorm;
                    
                case ColorComponent.X10Y10Z10W2:
                    return Format.R10G10B10A2Unorm;
                    
                case ColorComponent.X11Y11Z10:
                    return Format.R11G11B10Float;
                    
                case ColorComponent.X32:
                    return dataType == ColorDataType.Float ? Format.R32Float : Format.R32Uint;
                    
                // 添加更多组件类型的处理...
                
                default:
                    // 对于YUV格式，可能需要特殊处理
                    if (colorSpace == ColorSpace.YCbCr601 || 
                        colorSpace == ColorSpace.YCbCr601_RR || 
                        colorSpace == ColorSpace.YCbCr601_ER ||
                        colorSpace == ColorSpace.YCbCr709 ||
                        colorSpace == ColorSpace.YCbCr709_ER)
                    {
                        // YUV格式通常需要特殊处理，这里返回一个默认格式
                        // 实际应用中可能需要更复杂的转换逻辑
                        return Format.R8G8B8A8Unorm;
                    }
                    
                    // 默认抛出异常
                    throw new NotImplementedException($"Color Format \"{colorFormat}\" (Space: {colorSpace}, Swizzle: {swizzle}, Component: {component}, DataType: {dataType}) not implemented!");
            }
        }

        public void Dispose()
        {
            _isRunning = false;

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
