using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Nvdec.Image;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Nvdec
{
    public class NvdecDevice : IDeviceStateWithContext
    {
        private readonly ResourceManager _rm;
        private readonly DeviceState<NvdecRegisters> _state;

        private long _currentId;
        private readonly ConcurrentDictionary<long, NvdecDecoderContext> _contexts;
        private NvdecDecoderContext _currentContext;

        public NvdecDevice(DeviceMemoryManager mm)
        {
            Logger.Info?.Print(LogClass.Nvdec, $"[NvdecDevice] Constructor called");
            _rm = new ResourceManager(mm, new SurfaceCache(mm));
            _state = new DeviceState<NvdecRegisters>(new Dictionary<string, RwCallback>
            {
                { nameof(NvdecRegisters.Execute), new RwCallback(Execute, null) },
            });
            _contexts = new ConcurrentDictionary<long, NvdecDecoderContext>();
        }

        public long CreateContext()
        {
            long id = Interlocked.Increment(ref _currentId);
            _contexts.TryAdd(id, new NvdecDecoderContext());
            
            Logger.Info?.Print(LogClass.Nvdec, $"[NvdecDevice] CreateContext: created context {id}");
            
            return id;
        }

        public void DestroyContext(long id)
        {
            Logger.Info?.Print(LogClass.Nvdec, $"[NvdecDevice] DestroyContext: destroying context {id}");
            
            if (_contexts.TryRemove(id, out var context))
            {
                context.Dispose();
            }

            _rm.Cache.Trim();
        }

        public void BindContext(long id)
        {
            Logger.Info?.Print(LogClass.Nvdec, $"[NvdecDevice] BindContext: binding context {id}");
            
            if (_contexts.TryGetValue(id, out var context))
            {
                _currentContext = context;
                Logger.Info?.Print(LogClass.Nvdec, $"[NvdecDevice] BindContext: context {id} bound successfully");
            }
            else
            {
                Logger.Error?.Print(LogClass.Nvdec, $"[NvdecDevice] BindContext: context {id} not found!");
                _currentContext = null;
            }
        }

        public int Read(int offset) => _state.Read(offset);
        
        public void Write(int offset, int data)
        {
            Logger.Debug?.Print(LogClass.Nvdec, $"[NvdecDevice] Write: offset=0x{offset:X}, data=0x{data:X}");
            _state.Write(offset, data);
        }

        private void Execute(int data)
        {
            Logger.Info?.Print(LogClass.Nvdec, $"[NvdecDevice] Execute called with data=0x{data:X}");
            Decode((ApplicationId)_state.State.SetApplicationId);
        }

        private void Decode(ApplicationId applicationId)
        {
            Logger.Info?.Print(LogClass.Nvdec, 
                $"[NvdecDevice] Decode called: applicationId={applicationId}, " +
                $"CurrentContext={(_currentContext != null ? "Set" : "NULL!")}");
            
            switch (applicationId)
            {
                case ApplicationId.H264:
                    Logger.Info?.Print(LogClass.Nvdec, "[NvdecDevice] Starting H264 decode");
                    H264Decoder.Decode(_currentContext, _rm, ref _state.State);
                    break;
                case ApplicationId.Vp8:
                    Logger.Info?.Print(LogClass.Nvdec, "[NvdecDevice] Starting VP8 decode");
                    Vp8Decoder.Decode(_currentContext, _rm, ref _state.State);
                    break;
                case ApplicationId.Vp9:
                    Logger.Info?.Print(LogClass.Nvdec, "[NvdecDevice] Starting VP9 decode");
                    Vp9Decoder.Decode(_rm, ref _state.State);
                    break;
                default:
                    Logger.Error?.Print(LogClass.Nvdec, $"[NvdecDevice] Unsupported codec \"{applicationId}\".");
                    break;
            }
        }
    }
}
