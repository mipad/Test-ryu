using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ryujinx.Horizon.Sdk.Codec.Detail;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using Ryujinx.Horizon.Sdk.Sm;
using System;
using System.Buffers;          // 引入 ArrayPool 支持
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Horizon.Audio
{
    /// <summary>
    /// IPC 服务器配置参数（从 JSON 文件加载）
    /// </summary>
    public class AudioIpcSettings
    {
        public int PointerBufferSize { get; set; } = 0x2000;    // 默认 8KB
        public int MaxSessions { get; set; } = 8;               // 最大并发会话数
        public int DecodeBufferSize { get; set; } = 8192;       // 解码缓冲区大小
        public bool EnableHardwareAcceleration { get; set; } = true;
        public int ServiceThreadPriority { get; set; } = 2;     // 服务线程优先级
    }

    /// <summary>
    /// 硬件 Opus 解码 IPC 服务器
    /// </summary>
    class HwopusIpcServer : IDisposable
    {
        private const int MaxDomains = 8;
        private const int MaxDomainObjects = 256;
        private const int MaxPortsCount = 1;

        // 依赖注入的配置和日志
        private readonly AudioIpcSettings _settings;
        private readonly ILogger _logger;

        // IPC 相关对象
        private ManagerOptions _options;
        private SmApi _sm;
        private ServerManager _serverManager;
        private CancellationTokenSource _serviceCts;
        private HardwareOpusDecoderManager _decoderManager;

        /// <summary>
        /// 构造函数（依赖注入）
        /// </summary>
        public HwopusIpcServer(
            IOptions<AudioIpcSettings> settings,
            ILogger<HwopusIpcServer> logger)
        {
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            Initialize();
        }

        /// <summary>
        /// 初始化 IPC 服务器
        /// </summary>
        private void Initialize()
        {
            // 使用 ArrayPool 分配器（替代 PooledAllocator）
            var allocator = new ArrayPoolAllocator();

            // 初始化系统管理器
            _sm = new SmApi();
            _sm.Initialize().AbortOnFailure();

            // 配置 IPC 管理器选项
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

            // 注册服务到指定名称
            _serverManager.RegisterObjectForServer(
                _decoderManager,
                ServiceName.Encode("hwopus"),
                _settings.MaxSessions);

            _logger.LogInformation(
                "Hwopus IPC 服务器已初始化，解码缓冲区大小: {BufferSize} 字节", 
                _settings.DecodeBufferSize
            );
        }

        /// <summary>
        /// 异步启动服务循环
        /// </summary>
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
                        _logger.LogDebug("IPC 请求处理耗时: {ElapsedMs}ms", elapsedMs);

                        // 监控缓冲区剩余容量
                        int remaining = _decoderManager.GetRemainingBufferCapacity();
                        if (remaining < _settings.DecodeBufferSize / 4)
                        {
                            _logger.LogWarning(
                                "解码缓冲区容量不足！剩余: {RemainingBytes} 字节", 
                                remaining
                            );
                        }

                        // 根据优先级调整 CPU 占用
                        if (_settings.ServiceThreadPriority > 1)
                        {
                            Thread.SpinWait(10);  // 高优先级不释放 CPU
                        }
                        else
                        {
                            Thread.Sleep(1);      // 低优先级适当释放
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常关闭，无需处理
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "处理 IPC 请求时发生异常");
                    }
                }
            }, _serviceCts.Token);
        }

        /// <summary>
        /// 关闭服务并释放资源
        /// </summary>
        public void Shutdown()
        {
            try
            {
                _serviceCts?.Cancel();
                _serviceCts?.Dispose();
                
                _serverManager?.Dispose();
                _sm?.Dispose();
                _decoderManager?.Dispose();

                _logger.LogInformation("Hwopus IPC 服务器已关闭");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "关闭资源时发生异常");
            }
        }

        /// <summary>
        /// 实现 IDisposable 接口
        /// </summary>
        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 基于 ArrayPool 的内存分配器
    /// </summary>
    internal class ArrayPoolAllocator : IDisposable
    {
        /// <summary>
        /// 分配指定大小的缓冲区
        /// </summary>
        public byte[] Allocate(int size)
        {
            // 从共享池租用数组（自动处理对齐）
            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
            return buffer;
        }

        /// <summary>
        /// 释放缓冲区
        /// </summary>
        public void Free(byte[] buffer)
        {
            if (buffer != null)
            {
                // 归还数组到池
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Dispose()
        {
            // 无需要清理的资源
        }
    }

    /// <summary>
    /// 硬件 Opus 解码器管理器（模拟实现）
    /// </summary>
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

            InitializeHardwareAcceleration();
        }

        private void InitializeHardwareAcceleration()
        {
            if (_config.EnableHardwareAcceleration)
            {
                // 模拟硬件加速初始化
            }
        }

        /// <summary>
        /// 获取剩余缓冲区容量
        /// </summary>
        public int GetRemainingBufferCapacity()
        {
            return _decodeBuffer.Length - _bufferPosition;
        }

        public void Dispose()
        {
            // 模拟资源清理
        }
    }

    /// <summary>
    /// 解码器配置参数
    /// </summary>
    internal class HardwareOpusDecoderConfig
    {
        public int BufferSize { get; set; }
        public bool EnableHardwareAcceleration { get; set; }
    }
}
