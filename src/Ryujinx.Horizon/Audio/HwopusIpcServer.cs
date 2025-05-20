using Ryujinx.Horizon.Sdk.Codec.Detail;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using Ryujinx.Horizon.Sdk.Sm;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Horizon.Audio
{
    public class AudioIpcSettings
    {
        public int PointerBufferSize { get; set; } = 0x2000;
        public int MaxSessions { get; set; } = 8;
        public int DecodeBufferSize { get; set; } = 8192;
        public bool EnableHardwareAcceleration { get; set; } = true;
    }

    class HwopusIpcServer : IDisposable
    {
        private const int MaxDomains = 8;
        private const int MaxDomainObjects = 256;
        private const int MaxPortsCount = 1;

        private readonly AudioIpcSettings _settings;
        private ManagerOptions _options;
        private SmApi _sm;
        private ServerManager _serverManager;
        private CancellationTokenSource _serviceCts;
        private HardwareOpusDecoderManager _decoderManager;

        public HwopusIpcServer(AudioIpcSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Initialize();
        }

        private void Initialize()
        {
            var allocator = new HeapAllocator(); // 恢复原始分配器

            _sm = new SmApi();
            _sm.Initialize().AbortOnFailure();

            _options = new ManagerOptions(
                _settings.PointerBufferSize,
                MaxDomains,
                MaxDomainObjects);

            _serverManager = new ServerManager(
                allocator,
                _sm,
                MaxPortsCount,
                _options,
                _settings.MaxSessions);

            var decoderConfig = new HardwareOpusDecoderConfig 
            { 
                BufferSize = _settings.DecodeBufferSize,
                EnableHardwareAcceleration = _settings.EnableHardwareAcceleration
            };

            _decoderManager = new HardwareOpusDecoderManager(decoderConfig);

            _serverManager.RegisterObjectForServer(
                (IServiceObject)_decoderManager, // 显式转换为接口
                ServiceName.Encode("hwopus"),
                _settings.MaxSessions);

            Console.WriteLine($"服务器初始化完成，缓冲区: {_settings.DecodeBufferSize} 字节");
        }

        public async Task StartAsync()
        {
            _serviceCts = new CancellationTokenSource();
            await Task.Run(() => 
            {
                while (!_serviceCts.Token.IsCancellationRequested)
                {
                    _serverManager.ServiceRequests();
                    Thread.Sleep(1);
                }
            }, _serviceCts.Token);
        }

        public void Dispose()
        {
            _serviceCts?.Cancel();
            _serverManager?.Dispose();
            _sm?.Dispose();
            _decoderManager?.Dispose();
        }
    }

    internal class HardwareOpusDecoderManager : IServiceObject, IDisposable
    {
        private readonly HardwareOpusDecoderConfig _config;
        private readonly byte[] _decodeBuffer;

        public HardwareOpusDecoderManager(HardwareOpusDecoderConfig config)
        {
            _config = config;
            _decodeBuffer = new byte[_config.BufferSize];
        }

        public void GetServiceObject(out IServiceObject serviceObject)
        {
            serviceObject = this;
        }

        public int GetRemainingBufferCapacity()
        {
            return _decodeBuffer.Length; // 模拟实现
        }

        public void Dispose()
        {
            // 资源清理
        }
    }

    internal class HardwareOpusDecoderConfig
    {
        public int BufferSize { get; set; }
        public bool EnableHardwareAcceleration { get; set; }
    }
}
