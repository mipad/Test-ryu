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
    // 配置类（手动初始化，无需依赖注入）
    public class AudioIpcSettings
    {
        public int PointerBufferSize { get; set; } = 0x2000;    // 8KB
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

        // 构造函数直接接收配置对象
        public HwopusIpcServer(AudioIpcSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Initialize();
        }

        private void Initialize()
        {
            // 使用 ArrayPool 分配器
            var allocator = new ArrayPoolAllocator();

            // 初始化系统管理器
            _sm = new SmApi();
            _sm.Initialize().AbortOnFailure();

            // 配置 IPC 选项
            _options = new ManagerOptions(
                _settings.PointerBufferSize,
                MaxDomains,
                MaxDomainObjects,
                enableAliasAllocation: false);

            // 创建 ServerManager
            _serverManager = new ServerManager(
                allocator,
                _sm,
                MaxPortsCount,
                _options,
                _settings.MaxSessions);

            // 配置解码器参数
            var decoderConfig = new HardwareOpusDecoderConfig 
            { 
                BufferSize = _settings.DecodeBufferSize,
                EnableHardwareAcceleration = _settings.EnableHardwareAcceleration
            };

            _decoderManager = new HardwareOpusDecoderManager(decoderConfig);

            // 注册服务
            _serverManager.RegisterObjectForServer(
                _decoderManager,
                ServiceName.Encode("hwopus"),
                _settings.MaxSessions);

            Console.WriteLine($"[INFO] Hwopus IPC 服务器已初始化，解码缓冲区: {_settings.DecodeBufferSize} 字节");
        }

        public async Task StartAsync()
        {
            _serviceCts = new CancellationTokenSource();
            
            await Task.Run(() => 
            {
                var stopwatch = new Stopwatch();
                while (!_serviceCts.Token.IsCancellationRequested)
                {
                    stopwatch.Restart();
                    
                    try 
                    {
                        // 处理 IPC 请求
                        _serverManager.ServiceRequests();
                        
                        // 记录处理耗时
                        var elapsedMs = stopwatch.ElapsedMilliseconds;
                        Console.WriteLine($"[DEBUG] IPC 请求处理耗时: {elapsedMs}ms");

                        // 检查缓冲区剩余容量
                        int remaining = _decoderManager.GetRemainingBufferCapacity();
                        if (remaining < _settings.DecodeBufferSize / 4)
                        {
                            Console.WriteLine($"[WARN] 解码缓冲区容量不足！剩余: {remaining} 字节");
                        }

                        // 让出 CPU 避免 100% 占用
                        Thread.Sleep(1);
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常退出循环
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] 处理请求时发生异常: {ex.Message}");
                        Console.WriteLine(ex.StackTrace);
                    }
                }
            }, _serviceCts.Token);
        }

        public void Dispose()
        {
            try
            {
                _serviceCts?.Cancel();
                _serviceCts?.Dispose();
                _serverManager?.Dispose();
                _sm?.Dispose();
                _decoderManager?.Dispose();
                Console.WriteLine("[INFO] 服务器资源已释放");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 释放资源时发生错误: {ex.Message}");
            }
        }
    }

    // ArrayPool 内存分配器
    internal class ArrayPoolAllocator : IDisposable
    {
        public byte[] Allocate(int size)
        {
            return ArrayPool<byte>.Shared.Rent(size);
        }

        public void Free(byte[] buffer)
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Dispose()
        {
            // 无需要释放的资源
        }
    }

    // 硬件解码器管理器（模拟实现）
    internal class HardwareOpusDecoderManager : IDisposable
    {
        private readonly HardwareOpusDecoderConfig _config;
        private readonly byte[] _decodeBuffer;
        private int _bufferPosition;

        public HardwareOpusDecoderManager(HardwareOpusDecoderConfig config)
        {
            _config = config;
            _decodeBuffer = new byte[_config.BufferSize];
            _bufferPosition = 0;

            if (_config.EnableHardwareAcceleration)
            {
                Console.WriteLine("[INFO] 已启用硬件加速解码");
            }
        }

        public int GetRemainingBufferCapacity()
        {
            return _decodeBuffer.Length - _bufferPosition;
        }

        public void Dispose()
        {
            // 模拟资源清理操作
            Console.WriteLine("[INFO] 解码器资源已释放");
        }
    }

    // 解码器配置类
    internal class HardwareOpusDecoderConfig
    {
        public int BufferSize { get; set; }
        public bool EnableHardwareAcceleration { get; set; }
    }
}
