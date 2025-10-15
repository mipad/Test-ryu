using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS;
using Ryujinx.Input.HLE;
using Ryujinx.HLE;
using Ryujinx.Common.Utilities;
using System.Linq;
using System;
using System.Runtime.InteropServices;
using Ryujinx.Common.Configuration;
using LibHac.Tools.FsSystem;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.UI.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Audio.Integration;
using Ryujinx.Audio.Backends.SDL2;
using System.IO;
using LibHac.Common.Keys;
using LibHac.Common;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Fs;
using Path = System.IO.Path;
using LibHac;
using OpenTK.Audio.OpenAL;
using Ryujinx.HLE.Loaders.Npdm;
using System.Globalization;
using Ryujinx.UI.Common.Configuration.System;
using System.Collections.Generic;
using System.Text;
using Ryujinx.HLE.UI;
using LibRyujinx.Android;
using System.IO.Compression;
using LibHac.FsSrv;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ryujinx.Common.Configuration.Multiplayer;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using System.Net.NetworkInformation;
using System.Collections.Concurrent;
using Ryujinx.Common.Logging.Targets;
using SystemTimeSpan = System.TimeSpan;

namespace LibRyujinx
{
    public static partial class LibRyujinx
    {
        internal static IHardwareDeviceDriver AudioDriver { get; set; } = new DummyHardwareDeviceDriver();

        private static readonly TitleUpdateMetadataJsonSerializerContext _titleSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());
        
        // 添加 ModMetadata 序列化上下文
        [JsonSerializable(typeof(ModMetadata))]
        [JsonSerializable(typeof(ModInfo))]
        [JsonSerializable(typeof(List<ModInfo>))]
        public partial class ModMetadataJsonSerializerContext : JsonSerializerContext
        {
        }
        
        private static readonly ModMetadataJsonSerializerContext _modSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());
        
        // 添加 LobbyInfo 序列化上下文
        [JsonSerializable(typeof(LobbyInfo))]
        [JsonSerializable(typeof(List<LobbyInfo>))]
        public partial class LobbyInfoJsonSerializerContext : JsonSerializerContext
        {
        }
        
        private static readonly LobbyInfoJsonSerializerContext _lobbyInfoJsonSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());
        
        public static SwitchDevice? SwitchDevice { get; set; }

        // 添加静态字段来存储画面比例
        private static AspectRatio _currentAspectRatio = AspectRatio.Stretched;

        // 添加静态字段来存储内存配置
        private static MemoryConfiguration _currentMemoryConfiguration = MemoryConfiguration.MemoryConfiguration8GiB;

        // 添加静态字段来存储系统时间偏移
        private static long _systemTimeOffset = 0;

        // 添加网络相关静态字段
        private static MultiplayerMode _currentMultiplayerMode = MultiplayerMode.Disabled;
        private static string _currentLanInterfaceId = "0";

        // 大厅管理相关静态字段
        private static List<LobbyInfo> _lobbyList = new List<LobbyInfo>();
        private static LobbyInfo _currentLobby = null;
        private static bool _isHosting = false;
        private static CancellationTokenSource _lobbyScanCancellation = null;
        private static bool _isScanning = false;

        // 网络通信相关静态字段
        private static UdpClient _udpBroadcastClient;
        private static TcpListener _tcpListener;
        private static CancellationTokenSource _networkCancellation;
        private static readonly ConcurrentDictionary<string, LobbyInfo> _networkLobbies = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lobbyLastSeen = new();
        private static bool _isNetworkInitialized = false;
        private static int _broadcastPort = 11451; // 广播端口
        private static int _tcpPort = 11452; // TCP通信端口

        // 添加子网广播地址相关字段
        private static IPAddress _currentLocalIp = null;
        private static IPAddress _currentSubnetMask = null;
        private static IPAddress _currentBroadcastAddress = null;
        
        // 添加IP地址缓存时间戳
        private static DateTime _lastIpUpdateTime = DateTime.MinValue;
        private static readonly System.TimeSpan _ipCacheTimeout = System.TimeSpan.FromSeconds(5); // 5秒缓存

        // Mod 相关类型定义
        public class ModInfo
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
            
            [JsonPropertyName("path")]
            public string Path { get; set; } = string.Empty;
            
            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; }
            
            [JsonPropertyName("inExternalStorage")]
            public bool InExternalStorage { get; set; }
            
            [JsonPropertyName("type")]
            public string Type { get; set; } = string.Empty; // 改为字符串类型避免枚举序列化问题
        }

        public enum ModType
        {
            RomFs,
            ExeFs
        }

        public class ModMetadata
        {
            [JsonPropertyName("mods")]
            public List<ModEntry> Mods { get; set; } = new List<ModEntry>();
        }

        public class ModEntry
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
            
            [JsonPropertyName("path")]
            public string Path { get; set; } = string.Empty;
            
            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; }
        }

        // 大厅信息类
        public class LobbyInfo
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;
            
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
            
            [JsonPropertyName("gameTitle")]
            public string GameTitle { get; set; } = string.Empty;
            
            [JsonPropertyName("hostName")]
            public string HostName { get; set; } = string.Empty;
            
            [JsonPropertyName("playerCount")]
            public int PlayerCount { get; set; }
            
            [JsonPropertyName("maxPlayers")]
            public int MaxPlayers { get; set; }
            
            [JsonPropertyName("ping")]
            public int Ping { get; set; }
            
            [JsonPropertyName("isPasswordProtected")]
            public bool IsPasswordProtected { get; set; }
            
            [JsonPropertyName("hostIp")]
            public string HostIp { get; set; } = string.Empty;
            
            [JsonPropertyName("port")]
            public int Port { get; set; } = 11452; // LDN 默认端口
            
            [JsonPropertyName("gameId")]
            public string GameId { get; set; } = string.Empty;
            
            [JsonPropertyName("createdTime")]
            public DateTime CreatedTime { get; set; } = DateTime.Now;
        }

        public static bool Initialize(string? basePath)
        {
            if (SwitchDevice != null)
            {
                return false;
            }

            try
            {
                AppDataManager.Initialize(basePath);

                ConfigurationState.Initialize();
                LoggerModule.Initialize();

                string logDir = Path.Combine(AppDataManager.BaseDirPath, "Logs");
                FileStream logFile = FileLogTarget.PrepareLogFile(logDir);
                Logger.AddTarget(new AsyncLogTargetWrapper(
                    new FileLogTarget("file", logFile),
                    1000,
                    AsyncLogTargetOverflowAction.Block
                ));

                Logger.Notice.Print(LogClass.Application, "Initializing...");
                Logger.Notice.Print(LogClass.Application, $"Using base path: {AppDataManager.BaseDirPath}");

                SwitchDevice = new SwitchDevice();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
            
            OpenALLibraryNameContainer.OverridePath = "libopenal.so";

            return true;
        }

        // 添加设置画面比例的方法
        public static void SetAspectRatio(AspectRatio aspectRatio)
        {
            _currentAspectRatio = aspectRatio;
            
            // 如果设备已初始化，立即应用新的画面比例
            if (SwitchDevice?.EmulationContext != null)
            {
                // 这里需要重新配置模拟器上下文以应用新的画面比例
                // 可能需要重启模拟器才能完全生效
            }
        }

        // 添加获取画面比例的方法
        public static AspectRatio GetAspectRatio()
        {
            return _currentAspectRatio;
        }

        // 添加设置内存配置的方法
        public static void SetMemoryConfiguration(MemoryConfiguration memoryConfig)
        {
            _currentMemoryConfiguration = memoryConfig;
            
            // 如果设备已初始化，记录需要重启才能生效
            if (SwitchDevice?.EmulationContext != null)
            {
            }
        }

        // 添加获取内存配置的方法
        public static MemoryConfiguration GetMemoryConfiguration()
        {
            return _currentMemoryConfiguration;
        }

        // 添加设置系统时间偏移的方法
        public static void SetSystemTimeOffset(long offset)
        {
            _systemTimeOffset = offset;
            
            // 如果设备已初始化，记录需要重启才能生效
            if (SwitchDevice?.EmulationContext != null)
            {
            }
        }

        // 添加获取系统时间偏移的方法
        public static long GetSystemTimeOffset()
        {
            return _systemTimeOffset;
        }

        // 添加设置多人游戏模式的方法
        public static void SetMultiplayerMode(MultiplayerMode mode)
        {
            _currentMultiplayerMode = mode;
            
            // 如果设备已初始化，记录需要重启才能生效
            if (SwitchDevice?.EmulationContext != null)
            {
                Logger.Info?.Print(LogClass.Application, $"Multiplayer mode changed to: {mode}. Restart required to take effect.");
            }
        }

        // 添加获取多人游戏模式的方法
        public static MultiplayerMode GetMultiplayerMode()
        {
            return _currentMultiplayerMode;
        }

        // 添加设置网络接口的方法
        public static void SetLanInterface(string interfaceId)
        {
            _currentLanInterfaceId = interfaceId ?? "0";
            
            // 清除IP缓存，强制下次重新获取
            _currentLocalIp = null;
            _currentSubnetMask = null;
            _currentBroadcastAddress = null;
            _lastIpUpdateTime = DateTime.MinValue;
            
            // 如果设备已初始化，记录需要重启才能生效
            if (SwitchDevice?.EmulationContext != null)
            {
                Logger.Info?.Print(LogClass.Application, $"LAN interface changed to: {interfaceId}. Restart required to take effect.");
            }
        }

        // 添加获取网络接口的方法
        public static string GetLanInterface()
        {
            return _currentLanInterfaceId;
        }

        // ==================== 网络通信功能 ====================

        /// <summary>
        /// 计算子网广播地址
        /// </summary>
        private static IPAddress CalculateBroadcastAddress(IPAddress ipAddress, IPAddress subnetMask)
        {
            byte[] ipBytes = ipAddress.GetAddressBytes();
            byte[] maskBytes = subnetMask.GetAddressBytes();
            byte[] broadcastBytes = new byte[4];

            for (int i = 0; i < 4; i++)
            {
                broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
            }

            return new IPAddress(broadcastBytes);
        }

        /// <summary>
        /// 获取子网广播地址
        /// </summary>
        private static IPAddress GetBroadcastAddress()
        {
            // 检查是否需要更新IP地址
            if (DateTime.Now - _lastIpUpdateTime > _ipCacheTimeout || _currentBroadcastAddress == null)
            {
                // 强制更新IP地址
                _currentLocalIp = null;
                _currentSubnetMask = null;
                string currentIp = GetLocalIpAddress();
            }

            if (_currentBroadcastAddress != null)
            {
                return _currentBroadcastAddress;
            }

            // 如果没有计算过广播地址，尝试计算
            if (_currentLocalIp != null && _currentSubnetMask != null)
            {
                _currentBroadcastAddress = CalculateBroadcastAddress(_currentLocalIp, _currentSubnetMask);
                Logger.Info?.Print(LogClass.ServiceLdn, $"Calculated broadcast address: {_currentBroadcastAddress} from IP: {_currentLocalIp}, Mask: {_currentSubnetMask}");
                return _currentBroadcastAddress;
            }

            // 如果无法计算，回退到子网定向广播（例如192.168.21.255）
            if (_currentLocalIp != null)
            {
                byte[] ipBytes = _currentLocalIp.GetAddressBytes();
                if (ipBytes.Length == 4)
                {
                    // 假设是 /24 子网
                    byte[] broadcastBytes = new byte[] { ipBytes[0], ipBytes[1], ipBytes[2], 255 };
                    _currentBroadcastAddress = new IPAddress(broadcastBytes);
                    Logger.Info?.Print(LogClass.ServiceLdn, $"Using fallback broadcast address: {_currentBroadcastAddress}");
                    return _currentBroadcastAddress;
                }
            }

            // 最后回退到全局广播
            Logger.Warning?.Print(LogClass.ServiceLdn, "Using global broadcast address as fallback");
            return IPAddress.Broadcast;
        }

        /// <summary>
        /// 初始化网络通信
        /// </summary>
        public static void InitializeNetwork()
        {
            if (_isNetworkInitialized) return;

            try
            {
                _networkCancellation = new CancellationTokenSource();
                _udpBroadcastClient = new UdpClient();
                _udpBroadcastClient.EnableBroadcast = true;
                _udpBroadcastClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                // 清除IP缓存
                _currentLocalIp = null;
                _currentSubnetMask = null;
                _currentBroadcastAddress = null;
                _lastIpUpdateTime = DateTime.MinValue;

                // 启动广播监听
                Task.Run(StartBroadcastListener, _networkCancellation.Token);
                
                // 启动TCP服务器（如果是主机）
                if (_isHosting)
                {
                    Task.Run(StartTcpServer, _networkCancellation.Token);
                }

                // 启动大厅清理任务
                Task.Run(CleanupStaleLobbies, _networkCancellation.Token);

                _isNetworkInitialized = true;
                Logger.Info?.Print(LogClass.ServiceLdn, "Network initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Failed to initialize network: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动UDP广播监听
        /// </summary>
        private static async Task StartBroadcastListener()
        {
            using var listener = new UdpClient(_broadcastPort);
            listener.EnableBroadcast = true;
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            Logger.Info?.Print(LogClass.ServiceLdn, $"Started UDP broadcast listener on port {_broadcastPort}");

            while (!_networkCancellation.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await listener.ReceiveAsync(_networkCancellation.Token);
                    var message = Encoding.UTF8.GetString(result.Buffer);
                    ProcessBroadcastMessage(message, result.RemoteEndPoint.Address.ToString());
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.ServiceLdn, $"Error in broadcast listener: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 处理广播消息
        /// </summary>
        private static void ProcessBroadcastMessage(string message, string remoteIp)
        {
            try
            {
                if (message.StartsWith("LOBBY_BROADCAST:"))
                {
                    var json = message.Substring("LOBBY_BROADCAST:".Length);
                    var lobby = JsonSerializer.Deserialize<LobbyInfo>(json, _lobbyInfoJsonSerializerContext.LobbyInfo);
                    
                    // 更新最后可见时间
                    _lobbyLastSeen[lobby.Id] = DateTime.Now;
                    
                    // 更新或添加大厅信息
                    if (_networkLobbies.TryGetValue(lobby.Id, out var existingLobby))
                    {
                        // 更新玩家数量等信息
                        existingLobby.PlayerCount = lobby.PlayerCount;
                        existingLobby.Ping = CalculatePing(remoteIp);
                        existingLobby.HostIp = remoteIp; // 确保使用最新的IP地址
                    }
                    else
                    {
                        lobby.HostIp = remoteIp;
                        lobby.Ping = CalculatePing(remoteIp);
                        _networkLobbies[lobby.Id] = lobby;
                        Logger.Info?.Print(LogClass.ServiceLdn, $"Discovered new lobby: {lobby.Name} from {remoteIp}");
                    }
                }
                else if (message.StartsWith("LOBBY_QUERY:"))
                {
                    // 收到查询请求，如果自己是主机就回复
                    if (_isHosting && _currentLobby != null)
                    {
                        SendBroadcastResponse(remoteIp);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error processing broadcast message: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送广播响应
        /// </summary>
        private static async void SendBroadcastResponse(string targetIp)
        {
            try
            {
                var message = $"LOBBY_RESPONSE:{JsonSerializer.Serialize(_currentLobby, _lobbyInfoJsonSerializerContext.LobbyInfo)}";
                var data = Encoding.UTF8.GetBytes(message);
                var target = new IPEndPoint(IPAddress.Parse(targetIp), _broadcastPort);
                
                await _udpBroadcastClient.SendAsync(data, data.Length, target);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error sending broadcast response: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动TCP服务器（用于主机）
        /// </summary>
        private static async Task StartTcpServer()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, _tcpPort);
                _tcpListener.Start();
                Logger.Info?.Print(LogClass.ServiceLdn, $"Started TCP server on port {_tcpPort}");

                while (!_networkCancellation.Token.IsCancellationRequested)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleTcpClient(client));
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error in TCP server: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理TCP客户端连接
        /// </summary>
        private static async Task HandleTcpClient(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (message.StartsWith("JOIN_LOBBY:"))
                {
                    // 处理玩家加入请求
                    var playerName = message.Substring("JOIN_LOBBY:".Length);
                    if (_currentLobby != null && _currentLobby.PlayerCount < _currentLobby.MaxPlayers)
                    {
                        _currentLobby.PlayerCount++;
                        
                        // 发送确认消息
                        var response = $"JOIN_SUCCESS:{JsonSerializer.Serialize(_currentLobby, _lobbyInfoJsonSerializerContext.LobbyInfo)}";
                        var responseData = Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(responseData, 0, responseData.Length);
                        
                        Logger.Info?.Print(LogClass.ServiceLdn, $"Player {playerName} joined lobby {_currentLobby.Name}");
                    }
                    else
                    {
                        var response = "JOIN_FAILED:Lobby is full";
                        var responseData = Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(responseData, 0, responseData.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error handling TCP client: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }

        /// <summary>
        /// 广播大厅存在 - 使用子网广播地址
        /// </summary>
        private static async Task BroadcastLobbyExistence()
        {
            if (!_isHosting || _currentLobby == null) return;

            try
            {
                // 每次广播前强制更新IP地址
                string currentIp = GetLocalIpAddress();
                if (_currentLobby.HostIp != currentIp)
                {
                    _currentLobby.HostIp = currentIp;
                    Logger.Info?.Print(LogClass.ServiceLdn, $"Updated lobby host IP to: {currentIp}");
                }

                var message = $"LOBBY_BROADCAST:{JsonSerializer.Serialize(_currentLobby, _lobbyInfoJsonSerializerContext.LobbyInfo)}";
                var data = Encoding.UTF8.GetBytes(message);
                
                // 使用子网广播地址而不是全局广播
                var broadcastAddress = new IPEndPoint(GetBroadcastAddress(), _broadcastPort);
                
                await _udpBroadcastClient.SendAsync(data, data.Length, broadcastAddress);
                
                // 同时发送到全局广播作为备用
                var globalBroadcastAddress = new IPEndPoint(IPAddress.Broadcast, _broadcastPort);
                await _udpBroadcastClient.SendAsync(data, data.Length, globalBroadcastAddress);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error broadcasting lobby: {ex.Message}");
            }
        }

        /// <summary>
        /// 查询网络中的大厅 - 使用子网广播地址
        /// </summary>
        private static async Task QueryNetworkLobbies()
        {
            try
            {
                var message = "LOBBY_QUERY:REQUEST";
                var data = Encoding.UTF8.GetBytes(message);
                
                // 使用子网广播地址而不是全局广播
                var broadcastAddress = new IPEndPoint(GetBroadcastAddress(), _broadcastPort);
                await _udpBroadcastClient.SendAsync(data, data.Length, broadcastAddress);
                
                // 同时发送到全局广播作为备用
                var globalBroadcastAddress = new IPEndPoint(IPAddress.Broadcast, _broadcastPort);
                await _udpBroadcastClient.SendAsync(data, data.Length, globalBroadcastAddress);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error querying lobbies: {ex.Message}");
            }
        }

        /// <summary>
        /// 计算Ping值（简化版）
        /// </summary>
        private static int CalculatePing(string ipAddress)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(ipAddress, 1000); // 1秒超时
                return reply?.Status == IPStatus.Success ? (int)reply.RoundtripTime : 999;
            }
            catch
            {
                return 999;
            }
        }

        /// <summary>
        /// 清理过期的大厅
        /// </summary>
        private static async Task CleanupStaleLobbies()
        {
            while (!_networkCancellation.Token.IsCancellationRequested)
            {
                try
                {
                    var cutoffTime = DateTime.Now.AddSeconds(-10); // 10秒未更新视为过期
                    var staleLobbies = _lobbyLastSeen.Where(x => x.Value < cutoffTime).ToList();

                    foreach (var stale in staleLobbies)
                    {
                        _lobbyLastSeen.TryRemove(stale.Key, out _);
                        _networkLobbies.TryRemove(stale.Key, out _);
                    }

                    await Task.Delay(5000, _networkCancellation.Token); // 每5秒清理一次
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.ServiceLdn, $"Error cleaning up stale lobbies: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 停止网络通信
        /// </summary>
        public static void StopNetwork()
        {
            _networkCancellation?.Cancel();
            _udpBroadcastClient?.Close();
            _tcpListener?.Stop();
            _isNetworkInitialized = false;
            
            _networkLobbies.Clear();
            _lobbyLastSeen.Clear();
            _currentBroadcastAddress = null;
            _currentLocalIp = null;
            _currentSubnetMask = null;
            _lastIpUpdateTime = DateTime.MinValue;
            
            Logger.Info?.Print(LogClass.ServiceLdn, "Network stopped");
        }

        // ==================== 大厅管理功能 ====================

        /// <summary>
        /// 创建大厅（集成网络功能）
        /// </summary>
        public static bool CreateLobby(string lobbyName, string gameTitle, int maxPlayers, string gameId = "")
        {
            try
            {
                if (_isHosting)
                {
                    Logger.Warning?.Print(LogClass.ServiceLdn, "Already hosting a lobby");
                    return false;
                }

                // 初始化网络
                InitializeNetwork();

                // 强制清除IP缓存
                _currentLocalIp = null;
                _currentSubnetMask = null;
                _currentBroadcastAddress = null;
                _lastIpUpdateTime = DateTime.MinValue;

                string localIp = GetLocalIpAddress();
                if (string.IsNullOrEmpty(localIp) || localIp == "127.0.0.1")
                {
                    Logger.Error?.Print(LogClass.ServiceLdn, "Cannot get valid local IP address for networking");
                    return false;
                }

                _currentLobby = new LobbyInfo
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = lobbyName,
                    GameTitle = gameTitle,
                    HostName = Environment.MachineName, // 使用设备名
                    PlayerCount = 1,
                    MaxPlayers = maxPlayers,
                    HostIp = localIp,
                    Port = _tcpPort,
                    GameId = gameId,
                    CreatedTime = DateTime.Now
                };

                _isHosting = true;

                // 启动广播任务
                _ = Task.Run(async () =>
                {
                    while (_isHosting && !_networkCancellation.Token.IsCancellationRequested)
                    {
                        await BroadcastLobbyExistence();
                        await Task.Delay(2000, _networkCancellation.Token); // 每2秒广播一次
                    }
                }, _networkCancellation.Token);

                Logger.Info?.Print(LogClass.ServiceLdn, $"Lobby created and broadcasting: {lobbyName} at {localIp}:{_tcpPort}");
                Logger.Info?.Print(LogClass.ServiceLdn, $"Using broadcast address: {GetBroadcastAddress()}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error creating lobby: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 加入大厅（集成网络功能）
        /// </summary>
        public static bool JoinLobby(string hostIp, int port = 11452)
        {
            try
            {
                if (_currentLobby != null)
                {
                    Logger.Warning?.Print(LogClass.ServiceLdn, "Already in a lobby");
                    return false;
                }

                // 初始化网络
                InitializeNetwork();

                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(hostIp, port);
                
                if (connectTask.Wait(3000)) // 3秒连接超时
                {
                    if (client.Connected)
                    {
                        var stream = client.GetStream();
                        var message = $"JOIN_LOBBY:{Environment.MachineName}";
                        var data = Encoding.UTF8.GetBytes(message);
                        
                        stream.Write(data, 0, data.Length);
                        
                        // 读取响应
                        var buffer = new byte[4096];
                        var bytesRead = stream.Read(buffer, 0, buffer.Length);
                        var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        
                        if (response.StartsWith("JOIN_SUCCESS:"))
                        {
                            var lobbyJson = response.Substring("JOIN_SUCCESS:".Length);
                            _currentLobby = JsonSerializer.Deserialize<LobbyInfo>(lobbyJson, _lobbyInfoJsonSerializerContext.LobbyInfo);
                            
                            Logger.Info?.Print(LogClass.ServiceLdn, $"Joined lobby: {_currentLobby.Name} at {hostIp}:{port}");
                            return true;
                        }
                        else
                        {
                            Logger.Error?.Print(LogClass.ServiceLdn, $"Failed to join lobby: {response}");
                            return false;
                        }
                    }
                }
                
                Logger.Error?.Print(LogClass.ServiceLdn, $"Connection timeout to {hostIp}:{port}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error joining lobby: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 离开大厅（集成网络功能）
        /// </summary>
        public static bool LeaveLobby()
        {
            try
            {
                if (_currentLobby != null)
                {
                    if (_isHosting)
                    {
                        // 停止广播
                        _isHosting = false;
                        Logger.Info?.Print(LogClass.ServiceLdn, $"Closed lobby: {_currentLobby.Name}");
                    }
                    else
                    {
                        Logger.Info?.Print(LogClass.ServiceLdn, $"Left lobby: {_currentLobby.Name}");
                    }

                    _currentLobby = null;
                    
                    // 停止网络（如果不是主机或者没有其他网络活动）
                    if (!_isHosting)
                    {
                        StopNetwork();
                    }
                    
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error leaving lobby: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取大厅列表（集成网络发现）- 移除模拟大厅
        /// </summary>
        public static List<LobbyInfo> GetLobbyList()
        {
            // 只返回网络发现的大厅，不再添加模拟数据
            var allLobbies = new List<LobbyInfo>();
            
            // 添加网络发现的大厅
            foreach (var lobby in _networkLobbies.Values)
            {
                allLobbies.Add(lobby);
            }
            
            // 移除模拟大厅数据，只返回真实网络中发现的大厅
            return allLobbies;
        }

        /// <summary>
        /// 设置大厅数据
        /// </summary>
        public static bool SetLobbyData(string key, string value)
        {
            try
            {
                if (_currentLobby == null || !_isHosting)
                {
                    Logger.Warning?.Print(LogClass.ServiceLdn, "Not hosting a lobby");
                    return false;
                }

                // 这里可以设置大厅的元数据
                // 实际实现需要调用 LDN 的 SetAdvertiseData 方法
                Logger.Info?.Print(LogClass.ServiceLdn, $"Set lobby data: {key} = {value}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error setting lobby data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取大厅数据
        /// </summary>
        public static string GetLobbyData(string key)
        {
            try
            {
                if (_currentLobby == null)
                {
                    return string.Empty;
                }

                // 这里可以根据key返回相应的大厅数据
                return string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error getting lobby data: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 刷新大厅列表（主动网络扫描）
        /// </summary>
        public static void RefreshLobbyList()
        {
            try
            {
                if (_isScanning)
                {
                    Logger.Info?.Print(LogClass.ServiceLdn, "Lobby scan already in progress");
                    return;
                }

                _isScanning = true;
                _lobbyScanCancellation = new CancellationTokenSource();

                Task.Run(async () =>
                {
                    try
                    {
                        Logger.Info?.Print(LogClass.ServiceLdn, "Starting network lobby scan...");

                        // 清除旧的网络大厅（保留当前大厅）
                        var currentLobbyId = _currentLobby?.Id;
                        var lobbiesToRemove = _networkLobbies.Where(x => x.Key != currentLobbyId).Select(x => x.Key).ToList();
                        foreach (var id in lobbiesToRemove)
                        {
                            _networkLobbies.TryRemove(id, out _);
                            _lobbyLastSeen.TryRemove(id, out _);
                        }

                        // 清除IP缓存，确保使用最新的网络配置
                        _currentLocalIp = null;
                        _currentSubnetMask = null;
                        _currentBroadcastAddress = null;
                        _lastIpUpdateTime = DateTime.MinValue;

                        // 发送网络查询
                        await QueryNetworkLobbies();

                        // 等待网络响应
                        await Task.Delay(3000, _lobbyScanCancellation.Token);

                        if (!_lobbyScanCancellation.Token.IsCancellationRequested)
                        {
                            Logger.Info?.Print(LogClass.ServiceLdn, $"Lobby scan completed. Found {_networkLobbies.Count} network lobbies.");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Info?.Print(LogClass.ServiceLdn, "Lobby scan cancelled");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.ServiceLdn, $"Error during lobby scan: {ex.Message}");
                    }
                    finally
                    {
                        _isScanning = false;
                        _lobbyScanCancellation = null;
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error refreshing lobby list: {ex.Message}");
                _isScanning = false;
            }
        }

        /// <summary>
        /// 停止大厅扫描
        /// </summary>
        public static void StopLobbyScan()
        {
            try
            {
                if (_isScanning && _lobbyScanCancellation != null)
                {
                    _lobbyScanCancellation.Cancel();
                    Logger.Info?.Print(LogClass.ServiceLdn, "Stopping lobby scan...");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error stopping lobby scan: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前大厅信息
        /// </summary>
        public static LobbyInfo GetCurrentLobby()
        {
            return _currentLobby;
        }

        /// <summary>
        /// 检查是否正在托管大厅
        /// </summary>
        public static bool IsHostingLobby()
        {
            return _isHosting;
        }

        /// <summary>
        /// 检查是否正在扫描大厅
        /// </summary>
        public static bool IsScanningLobbies()
        {
            return _isScanning;
        }

        // 辅助方法：获取本地 IP 地址（改进版本）
        private static string GetLocalIpAddress()
        {
            // 检查缓存是否有效
            if (_currentLocalIp != null && DateTime.Now - _lastIpUpdateTime < _ipCacheTimeout)
            {
                return _currentLocalIp.ToString();
            }

            try
            {
                // 首先尝试使用 NetworkHelpers 获取网络接口信息
                var (properties, addressInfo) = NetworkHelpers.GetLocalInterface(_currentLanInterfaceId);
                if (addressInfo != null)
                {
                    string ip = addressInfo.Address.ToString();
                    if (!string.IsNullOrEmpty(ip) && ip != "127.0.0.1")
                    {
                        Logger.Info?.Print(LogClass.ServiceLdn, $"Got local IP from network interface: {ip}");
                        _currentLocalIp = addressInfo.Address;
                        _currentSubnetMask = addressInfo.IPv4Mask;
                        _lastIpUpdateTime = DateTime.Now;
                        return ip;
                    }
                }

                // 备用方法：使用 NetworkInterface 直接查询
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // 只选择已启用且不是回环地址的网络接口
                    if (ni.OperationalStatus == OperationalStatus.Up && 
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                string localIp = ip.Address.ToString();
                                if (!localIp.StartsWith("169.254.")) // 排除 APIPA 地址
                                {
                                    Logger.Info?.Print(LogClass.ServiceLdn, $"Got local IP from {ni.Description}: {localIp}");
                                    _currentLocalIp = ip.Address;
                                    _currentSubnetMask = ip.IPv4Mask;
                                    _lastIpUpdateTime = DateTime.Now;
                                    return localIp;
                                }
                            }
                        }
                    }
                }

                // 最后回退到原来的方法
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string localIp = ip.ToString();
                        if (localIp != "127.0.0.1")
                        {
                            Logger.Info?.Print(LogClass.ServiceLdn, $"Got local IP from host entry: {localIp}");
                            _currentLocalIp = ip;
                            _currentSubnetMask = IPAddress.Parse("255.255.255.0"); // 假设默认子网掩码
                            _lastIpUpdateTime = DateTime.Now;
                            return localIp;
                        }
                    }
                }

                Logger.Warning?.Print(LogClass.ServiceLdn, "No valid local IP address found, using 127.0.0.1");
                _currentLocalIp = IPAddress.Parse("127.0.0.1");
                _currentSubnetMask = IPAddress.Parse("255.255.255.0");
                _lastIpUpdateTime = DateTime.Now;
                return "127.0.0.1";
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, $"Error getting local IP: {ex.Message}");
                _currentLocalIp = IPAddress.Parse("127.0.0.1");
                _currentSubnetMask = IPAddress.Parse("255.255.255.0");
                _lastIpUpdateTime = DateTime.Now;
                return "127.0.0.1";
            }
        }

        // 删除重复的 InitializeDevice 方法，已移至 LibRyujinx.Device.cs

        public static void InitializeAudio()
        {
            AudioDriver = new SDL2HardwareDeviceDriver();
        }

        public static GameStats GetGameStats()
        {
            if (SwitchDevice?.EmulationContext == null)
                return new GameStats();

            var context = SwitchDevice.EmulationContext;

            return new GameStats
            {
                Fifo = context.Statistics.GetFifoPercent(),
                GameFps = context.Statistics.GetGameFrameRate(),
                GameTime = context.Statistics.GetGameFrameTime()
            };
        }

        // ==================== Mod 管理功能 ====================

        /// <summary>
        /// 获取指定标题ID的Mod列表
        /// </summary>
        public static List<ModInfo> GetMods(string titleId)
        {
            var mods = new List<ModInfo>();
            
            if (SwitchDevice?.VirtualFileSystem == null)
            {
                Logger.Warning?.Print(LogClass.ModLoader, "SwitchDevice.VirtualFileSystem is null, cannot get mods");
                return mods;
            }

            try
            {
                string[] modsBasePaths = { 
                    Path.Combine(AppDataManager.BaseDirPath, "mods"),
                    "/storage/emulated/0/Android/data/org.ryujinx.android/files/mods"
                };

                Logger.Info?.Print(LogClass.ModLoader, $"Starting mod scan for titleId: {titleId}");
                Logger.Info?.Print(LogClass.ModLoader, $"Base paths to scan: {string.Join(", ", modsBasePaths)}");

                foreach (var basePath in modsBasePaths)
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"Scanning base path: {basePath}");
                    
                    if (!Directory.Exists(basePath))
                    {
                        Logger.Warning?.Print(LogClass.ModLoader, $"Base path does not exist: {basePath}");
                        continue;
                    }

                    var inExternal = basePath.StartsWith("/storage/emulated/0");
                    var modCache = new ModLoader.ModCache();
                    var contentsDir = new DirectoryInfo(Path.Combine(basePath, "contents"));
                    
                    Logger.Info?.Print(LogClass.ModLoader, $"Contents directory: {contentsDir.FullName}, Exists: {contentsDir.Exists}");
                    
                    if (contentsDir.Exists)
                    {
                        // 使用 ulong 类型的 titleId
                        if (ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdNum))
                        {
                            Logger.Info?.Print(LogClass.ModLoader, $"Querying contents directory for titleId: {titleIdNum:X16}");
                            
                            // 修复：传递空的DLC列表作为第四个参数
                            ModLoader.QueryContentsDir(modCache, contentsDir, titleIdNum, new ulong[0]);

                            Logger.Info?.Print(LogClass.ModLoader, $"Found {modCache.RomfsDirs.Count} RomFs dirs, {modCache.ExefsDirs.Count} ExeFs dirs, {modCache.RomfsContainers.Count} RomFs containers, {modCache.ExefsContainers.Count} ExeFs containers");

                            // 处理 romfs 目录
                            foreach (var mod in modCache.RomfsDirs)
                            {
                                var modPath = mod.Path.Parent?.FullName ?? mod.Path.FullName;
                                var modName = mod.Name;
                                
                                Logger.Info?.Print(LogClass.ModLoader, $"Found RomFs directory mod: {modName} at {modPath}, Enabled: {mod.Enabled}");
                                
                                if (mods.All(x => x.Path != modPath))
                                {
                                    mods.Add(new ModInfo
                                    {
                                        Name = modName,
                                        Path = modPath,
                                        Enabled = mod.Enabled,
                                        InExternalStorage = inExternal,
                                        Type = "RomFs"
                                    });
                                }
                            }

                            // 处理 romfs 容器
                            foreach (var mod in modCache.RomfsContainers)
                            {
                                Logger.Info?.Print(LogClass.ModLoader, $"Found RomFs container mod: {mod.Name} at {mod.Path.FullName}, Enabled: {mod.Enabled}");
                                
                                mods.Add(new ModInfo
                                {
                                    Name = mod.Name,
                                    Path = mod.Path.FullName,
                                    Enabled = mod.Enabled,
                                    InExternalStorage = inExternal,
                                    Type = "RomFs"
                                });
                            }

                            // 处理 exefs 目录
                            foreach (var mod in modCache.ExefsDirs)
                            {
                                var modPath = mod.Path.Parent?.FullName ?? mod.Path.FullName;
                                var modName = mod.Name;
                                
                                Logger.Info?.Print(LogClass.ModLoader, $"Found ExeFs directory mod: {modName} at {modPath}, Enabled: {mod.Enabled}");
                                
                                if (mods.All(x => x.Path != modPath))
                                {
                                    mods.Add(new ModInfo
                                    {
                                        Name = modName,
                                        Path = modPath,
                                        Enabled = mod.Enabled,
                                        InExternalStorage = inExternal,
                                        Type = "ExeFs"
                                    });
                                }
                            }

                            // 处理 exefs 容器
                            foreach (var mod in modCache.ExefsContainers)
                            {
                                Logger.Info?.Print(LogClass.ModLoader, $"Found ExeFs container mod: {mod.Name} at {mod.Path.FullName}, Enabled: {mod.Enabled}");
                                
                                mods.Add(new ModInfo
                                {
                                    Name = mod.Name,
                                    Path = mod.Path.FullName,
                                    Enabled = mod.Enabled,
                                    InExternalStorage = inExternal,
                                    Type = "ExeFs"
                                });
                            }
                        }
                        else
                        {
                            Logger.Error?.Print(LogClass.ModLoader, $"Failed to parse titleId: {titleId}");
                        }
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.ModLoader, $"Contents directory does not exist: {contentsDir.FullName}");
                    }
                }

                Logger.Info?.Print(LogClass.ModLoader, $"Total mods found for {titleId}: {mods.Count}");
                foreach (var mod in mods)
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"  - {mod.Name} ({mod.Type}) at {mod.Path}, Enabled: {mod.Enabled}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error getting mods: {ex.Message}");
                Logger.Error?.Print(LogClass.Application, $"Stack trace: {ex.StackTrace}");
            }

            return mods;
        }

        /// <summary>
        /// 设置Mod启用状态
        /// </summary>
        public static bool SetModEnabled(string titleId, string modPath, bool enabled)
        {
            try
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Setting mod enabled: titleId={titleId}, modPath={modPath}, enabled={enabled}");
                
                string modJsonPath = Path.Combine(AppDataManager.GamesDirPath, titleId, "mods.json");
                Logger.Info?.Print(LogClass.ModLoader, $"Mod JSON path: {modJsonPath}");
                
                ModMetadata modData = new ModMetadata();
                
                // 如果文件存在，读取现有数据
                if (File.Exists(modJsonPath))
                {
                    try
                    {
                        modData = JsonHelper.DeserializeFromFile(modJsonPath, _modSerializerContext.ModMetadata);
                        Logger.Info?.Print(LogClass.ModLoader, $"Loaded existing mods.json with {modData.Mods.Count} mods");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.ModLoader, $"Failed to deserialize mods.json: {ex.Message}");
                        modData = new ModMetadata();
                    }
                }
                else
                {
                    Logger.Info?.Print(LogClass.ModLoader, "mods.json does not exist, creating new one");
                }

                // 查找并更新Mod状态
                var mod = modData.Mods.FirstOrDefault(m => m.Path == modPath);
                if (mod != null)
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"Updating existing mod: {mod.Name}, Path: {mod.Path}");
                    mod.Enabled = enabled;
                }
                else
                {
                    // 如果Mod不存在，添加新条目
                    Logger.Info?.Print(LogClass.ModLoader, $"Adding new mod entry: {Path.GetFileName(modPath)}");
                    modData.Mods.Add(new ModEntry
                    {
                        Name = Path.GetFileName(modPath),
                        Path = modPath,
                        Enabled = enabled
                    });
                }

                // 保存到文件
                try
                {
                    JsonHelper.SerializeToFile(modJsonPath, modData, _modSerializerContext.ModMetadata);
                    Logger.Info?.Print(LogClass.ModLoader, $"Successfully saved mods.json with {modData.Mods.Count} mods");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.ModLoader, $"Failed to save mods.json: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error setting mod enabled: {ex.Message}");
                Logger.Error?.Print(LogClass.Application, $"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 删除Mod
        /// </summary>
        public static bool DeleteMod(string titleId, string modPath)
        {
            try
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Deleting mod: titleId={titleId}, modPath={modPath}");
                
                if (Directory.Exists(modPath))
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"Deleting directory: {modPath}");
                    Directory.Delete(modPath, true);
                    
                    // 从mods.json中移除
                    RemoveModFromJson(titleId, modPath);
                    
                    Logger.Info?.Print(LogClass.ModLoader, "Mod directory deleted successfully");
                    return true;
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ModLoader, $"Mod directory does not exist: {modPath}");
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error deleting mod: {ex.Message}");
                Logger.Error?.Print(LogClass.Application, $"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 删除所有Mod
        /// </summary>
        public static bool DeleteAllMods(string titleId)
        {
            try
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Deleting all mods for titleId: {titleId}");
                
                var mods = GetMods(titleId);
                bool success = true;
                
                Logger.Info?.Print(LogClass.ModLoader, $"Found {mods.Count} mods to delete");
                
                foreach (var mod in mods)
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"Deleting mod: {mod.Name}");
                    if (!DeleteMod(titleId, mod.Path))
                    {
                        Logger.Warning?.Print(LogClass.ModLoader, $"Failed to delete mod: {mod.Name}");
                        success = false;
                    }
                }
                
                Logger.Info?.Print(LogClass.ModLoader, $"Delete all mods completed. Success: {success}");
                return success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error deleting all mods: {ex.Message}");
                Logger.Error?.Print(LogClass.Application, $"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 从mods.json中移除Mod条目
        /// </summary>
        private static void RemoveModFromJson(string titleId, string modPath)
        {
            try
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Removing mod from JSON: titleId={titleId}, modPath={modPath}");
                
                string modJsonPath = Path.Combine(AppDataManager.GamesDirPath, titleId, "mods.json");
                
                if (File.Exists(modJsonPath))
                {
                    var modData = JsonHelper.DeserializeFromFile(modJsonPath, _modSerializerContext.ModMetadata);
                    int initialCount = modData.Mods.Count;
                    modData.Mods.RemoveAll(m => m.Path == modPath);
                    int removedCount = initialCount - modData.Mods.Count;
                    
                    JsonHelper.SerializeToFile(modJsonPath, modData, _modSerializerContext.ModMetadata);
                    Logger.Info?.Print(LogClass.ModLoader, $"Removed {removedCount} mod entries from mods.json");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ModLoader, "mods.json does not exist, nothing to remove");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error removing mod from JSON: {ex.Message}");
                Logger.Error?.Print(LogClass.Application, $"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 启用所有Mod
        /// </summary>
        public static bool EnableAllMods(string titleId)
        {
            try
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Enabling all mods for titleId: {titleId}");
                
                var mods = GetMods(titleId);
                bool success = true;
                
                Logger.Info?.Print(LogClass.ModLoader, $"Found {mods.Count} mods to enable");
                
                foreach (var mod in mods)
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"Enabling mod: {mod.Name}");
                    if (!SetModEnabled(titleId, mod.Path, true))
                    {
                        Logger.Warning?.Print(LogClass.ModLoader, $"Failed to enable mod: {mod.Name}");
                        success = false;
                    }
                }
                
                Logger.Info?.Print(LogClass.ModLoader, $"Enable all mods completed. Success: {success}");
                return success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error enabling all mods: {ex.Message}");
                Logger.Error?.Print(LogClass.Application, $"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 禁用所有Mod
        /// </summary>
        public static bool DisableAllMods(string titleId)
        {
            try
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Disabling all mods for titleId: {titleId}");
                
                var mods = GetMods(titleId);
                bool success = true;
                
                Logger.Info?.Print(LogClass.ModLoader, $"Found {mods.Count} mods to disable");
                
                foreach (var mod in mods)
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"Disabling mod: {mod.Name}");
                    if (!SetModEnabled(titleId, mod.Path, false))
                    {
                        Logger.Warning?.Print(LogClass.ModLoader, $"Failed to disable mod: {mod.Name}");
                        success = false;
                    }
                }
                
                Logger.Info?.Print(LogClass.ModLoader, $"Disable all mods completed. Success: {success}");
                return success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error disabling all mods: {ex.Message}");
                Logger.Error?.Print(LogClass.Application, $"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 添加Mod（从源目录复制到Mod目录）
        /// </summary>
        public static bool AddMod(string titleId, string sourcePath, string modName)
        {
            try
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Adding mod: titleId={titleId}, sourcePath={sourcePath}, modName={modName}");
                
                if (!Directory.Exists(sourcePath))
                {
                    Logger.Error?.Print(LogClass.ModLoader, $"Source directory does not exist: {sourcePath}");
                    return false;
                }

                // 确定目标路径
                string targetBasePath = Path.Combine(AppDataManager.BaseDirPath, "mods", "contents", titleId);
                string targetPath = Path.Combine(targetBasePath, modName);
                
                Logger.Info?.Print(LogClass.ModLoader, $"Target base path: {targetBasePath}");
                Logger.Info?.Print(LogClass.ModLoader, $"Target path: {targetPath}");
                
                // 如果目标已存在，添加数字后缀
                if (Directory.Exists(targetPath))
                {
                    Logger.Info?.Print(LogClass.ModLoader, "Target path already exists, adding suffix");
                    int counter = 1;
                    string newTargetPath;
                    do
                    {
                        newTargetPath = $"{targetPath}_{counter}";
                        counter++;
                    } while (Directory.Exists(newTargetPath));
                    targetPath = newTargetPath;
                    Logger.Info?.Print(LogClass.ModLoader, $"Using new target path: {targetPath}");
                }

                // 复制目录
                Logger.Info?.Print(LogClass.ModLoader, $"Copying directory from {sourcePath} to {targetPath}");
                CopyDirectory(sourcePath, targetPath);
                
                Logger.Info?.Print(LogClass.ModLoader, "Mod added successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error adding mod: {ex.Message}");
                Logger.Error?.Print(LogClass.Application, $"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 复制目录及其所有内容
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            
            if (!dir.Exists)
            {
                Logger.Warning?.Print(LogClass.ModLoader, $"Source directory does not exist: {sourceDir}");
                return;
            }

            Logger.Info?.Print(LogClass.ModLoader, $"Creating destination directory: {destinationDir}");
            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                Logger.Info?.Print(LogClass.ModLoader, $"Copying file: {file.Name} to {targetFilePath}");
                file.CopyTo(targetFilePath, true);
            }

            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                Logger.Info?.Print(LogClass.ModLoader, $"Copying subdirectory: {subDir.Name} to {newDestinationDir}");
                CopyDirectory(subDir.FullName, newDestinationDir);
            }
        }

        public static GameInfo? GetGameInfo(string? file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return new GameInfo();
            }

            using var stream = File.Open(file, FileMode.Open);

            return GetGameInfo(stream, new FileInfo(file).Extension.Remove('.'));
        }

        public static GameInfo? GetGameInfo(Stream gameStream, string extension)
        {
            if (SwitchDevice == null)
            {
                return null;
            }
            GameInfo gameInfo = GetDefaultInfo(gameStream);

            const Language TitleLanguage = Language.SimplifiedChinese;

            BlitStruct<ApplicationControlProperty> controlHolder = new(1);

            try
            {
                try
                {
                    if (extension == "nsp" || extension == "pfs0" || extension == "xci")
                    {
                        IFileSystem pfs;

                        bool isExeFs = false;

                        if (extension == "xci")
                        {
                            Xci xci = new(SwitchDevice.VirtualFileSystem.KeySet, gameStream.AsStorage());

                            pfs = xci.OpenPartition(XciPartitionType.Secure);
                        }
                        else
                        {
                            var pfsTemp = new PartitionFileSystem();
                            pfsTemp.Initialize(gameStream.AsStorage()).ThrowIfFailure();
                            pfs = pfsTemp;

                            // If the NSP doesn't have a main NCA, decrement the number of applications found and then continue to the next application.
                            bool hasMainNca = false;

                            foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*"))
                            {
                                if (Path.GetExtension(fileEntry.FullPath).ToLower() == ".nca")
                                {
                                    using UniqueRef<IFile> ncaFile = new();

                                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    Nca nca = new(SwitchDevice.VirtualFileSystem.KeySet, ncaFile.Get.AsStorage());
                                    int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                                    // Some main NCAs don't have a data partition, so check if the partition exists before opening it
                                    if (nca.Header.ContentType == NcaContentType.Program && !(nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection()))
                                    {
                                        hasMainNca = true;

                                        break;
                                    }
                                }
                                else if (Path.GetFileNameWithoutExtension(fileEntry.FullPath) == "main")
                                {
                                    isExeFs = true;
                                }
                            }

                            if (!hasMainNca && !isExeFs)
                            {
                                return null;
                            }
                        }

                        if (isExeFs)
                        {
                            using UniqueRef<IFile> npdmFile = new();

                            Result result = pfs.OpenFile(ref npdmFile.Ref, "/main.npdm".ToU8Span(), OpenMode.Read);

                            if (ResultFs.PathNotFound.Includes(result))
                            {
                                Npdm npdm = new(npdmFile.Get.AsStream());

                                gameInfo.TitleName = npdm.TitleName;
                                gameInfo.TitleId = npdm.Aci0.TitleId.ToString("x16");
                            }
                        }
                        else
                        {
                            GetControlFsAndTitleId(pfs, out IFileSystem? controlFs, out string? id);

                            gameInfo.TitleId = id;

                            if (controlFs == null)
                            {
                                return null;
                            }

                            // Check if there is an update available.
                            if (IsUpdateApplied(gameInfo.TitleId, out IFileSystem? updatedControlFs))
                            {
                                // Replace the original ControlFs by the updated one.
                                controlFs = updatedControlFs;
                            }

                            ReadControlData(controlFs, controlHolder.ByteSpan);

                            GetGameInformation(ref controlHolder.Value, out gameInfo.TitleName, out _, out gameInfo.Developer, out gameInfo.Version);

                            // Read the icon from the ControlFS and store it as a byte array
                            try
                            {
                                using UniqueRef<IFile> icon = new();

                                controlFs?.OpenFile(ref icon.Ref, $"/icon_{TitleLanguage}.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                using MemoryStream stream = new();

                                icon.Get.AsStream().CopyTo(stream);
                                gameInfo.Icon = stream.ToArray();
                            }
                            catch (HorizonResultException)
                            {
                                foreach (DirectoryEntryEx entry in controlFs.EnumerateEntries("/", "*"))
                                {
                                    if (entry.Name == "control.nacp")
                                    {
                                        continue;
                                    }

                                    using var icon = new UniqueRef<IFile>();

                                    controlFs?.OpenFile(ref icon.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    using MemoryStream stream = new();

                                    icon.Get.AsStream().CopyTo(stream);
                                    gameInfo.Icon = stream.ToArray();

                                    if (gameInfo.Icon != null)
                                    {
                                        break;
                                    }
                                }

                            }
                        }
                    }
                    else if (extension == "nro")
                    {
                        BinaryReader reader = new(gameStream);

                        byte[] Read(long position, int size)
                        {
                            gameStream.Seek(position, SeekOrigin.Begin);

                            return reader.ReadBytes(size);
                        }

                        gameStream.Seek(24, SeekOrigin.Begin);

                        int assetOffset = reader.ReadInt32();

                        if (Encoding.ASCII.GetString(Read(assetOffset, 4)) == "ASET")
                        {
                            byte[] iconSectionInfo = Read(assetOffset + 8, 0x10);

                            long iconOffset = BitConverter.ToInt64(iconSectionInfo, 0);
                            long iconSize = BitConverter.ToInt64(iconSectionInfo, 8);

                            ulong nacpOffset = reader.ReadUInt64();
                            ulong nacpSize = reader.ReadUInt64();

                            // Reads and stores game icon as byte array
                            if (iconSize > 0)
                            {
                                gameInfo.Icon = Read(assetOffset + iconOffset, (int)iconSize);
                            }

                            // Read the NACP data
                            Read(assetOffset + (int)nacpOffset, (int)nacpSize).AsSpan().CopyTo(controlHolder.ByteSpan);

                            GetGameInformation(ref controlHolder.Value, out gameInfo.TitleName, out _, out gameInfo.Developer, out gameInfo.Version);
                        }
                    }
                }
                catch (MissingKeyException exception)
                {
                }
                catch (InvalidDataException exception)
                {
                }
                catch (Exception exception)
                {
                    return null;
                }
            }
            catch (IOException exception)
            {
            }

            void ReadControlData(IFileSystem? controlFs, Span<byte> outProperty)
            {
                using UniqueRef<IFile> controlFile = new();

                controlFs?.OpenFile(ref controlFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                controlFile.Get.Read(out _, 0, outProperty, ReadOption.None).ThrowIfFailure();
            }

            void GetGameInformation(ref ApplicationControlProperty controlData, out string? titleName, out string titleId, out string? publisher, out string? version)
            {
                _ = Enum.TryParse(TitleLanguage.ToString(), out TitleLanguage desiredTitleLanguage);

                if (controlData.Title.Length > (int)desiredTitleLanguage)
                {
                    titleName = controlData.Title[(int)desiredTitleLanguage].NameString.ToString();
                    publisher = controlData.Title[(int)desiredTitleLanguage].PublisherString.ToString();
                }
                else
                {   
                    titleName = null;
                    publisher = null;
                }

                if (string.IsNullOrWhiteSpace(titleName))
                {
                    foreach (ref readonly var controlTitle in controlData.Title)
                    {
                        if (!controlTitle.NameString.IsEmpty())
                        {
                            titleName = controlTitle.NameString.ToString();

                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(publisher))
                {
                    foreach (ref readonly var controlTitle in controlData.Title)
                    {
                        if (!controlTitle.PublisherString.IsEmpty())
                        {
                            publisher = controlTitle.PublisherString.ToString();

                            break;
                        }
                    }
                }

                if (controlData.PresenceGroupId != 0)
                {
                    titleId = controlData.PresenceGroupId.ToString("x16");
                }
                else if (controlData.SaveDataOwnerId != 0)
                {
                    titleId = controlData.SaveDataOwnerId.ToString();
                }
                else if (controlData.AddOnContentBaseId != 0)
                {
                    titleId = (controlData.AddOnContentBaseId - 0x1000).ToString("x16");
                }
                else
                {
                    titleId = "0000000000000000";
                }

                version = controlData.DisplayVersionString.ToString();
            }

            void GetControlFsAndTitleId(IFileSystem pfs, out IFileSystem? controlFs, out string? titleId)
            {
                if (SwitchDevice == null)
                {
                    controlFs = null;
                    titleId = null;
                    return;
                }
                (_, _, Nca? controlNca) = GetGameData(SwitchDevice.VirtualFileSystem, pfs, 0);

                if (controlNca == null)
                {
                }

                // Return the ControlFS
                controlFs = controlNca?.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                titleId = controlNca?.Header.TitleId.ToString("x16");
            }

            (Nca? mainNca, Nca? patchNca, Nca? controlNca) GetGameData(VirtualFileSystem fileSystem, IFileSystem pfs, int programIndex)
            {
                Nca? mainNca = null;
                Nca? patchNca = null;
                Nca? controlNca = null;

                fileSystem.ImportTickets(pfs);

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(fileSystem.KeySet, ncaFile.Release().AsStorage());

                    int ncaProgramIndex = (int)(nca.Header.TitleId & 0xF);

                    if (ncaProgramIndex != programIndex)
                    {
                        continue;
                    }

                    if (nca.Header.ContentType == NcaContentType.Program)
                    {
                        int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                        if (nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                        {
                            patchNca = nca;
                        }
                        else
                        {
                            mainNca = nca;
                        }
                    }
                    else if (nca.Header.ContentType == NcaContentType.Control)
                    {
                        controlNca = nca;
                    }
                }

                return (mainNca, patchNca, controlNca);
            }

            bool IsUpdateApplied(string? titleId, out IFileSystem? updatedControlFs)
            {
                updatedControlFs = null;

                string? updatePath = "(unknown)";

                if (SwitchDevice?.VirtualFileSystem == null)
                {
                    return false;
                }

                try
                {
                    (Nca? patchNca, Nca? controlNca) = GetGameUpdateData(SwitchDevice.VirtualFileSystem, titleId, 0, out updatePath);

                    if (patchNca != null && controlNca != null)
                    {
                        updatedControlFs = controlNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);

                        return true;
                    }
                }
                catch (InvalidDataException)
                {
                }
                catch (MissingKeyException exception)
                {
                }

                return false;
            }

            (Nca? patch, Nca? control) GetGameUpdateData(VirtualFileSystem fileSystem, string? titleId, int programIndex, out string? updatePath)
            {
                updatePath = null;

                if (ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdBase))
                {
                    // Clear the program index part.
                    titleIdBase &= ~0xFUL;

                    // Load update information if exists.
                    string titleUpdateMetadataPath = Path.Combine(AppDataManager.GamesDirPath, titleIdBase.ToString("x16"), "updates.json");

                    if (File.Exists(titleUpdateMetadataPath))
                    {
                        updatePath = JsonHelper.DeserializeFromFile(titleUpdateMetadataPath, _titleSerializerContext.TitleUpdateMetadata).Selected;

                        if (File.Exists(updatePath))
                        {
                            FileStream file = new(updatePath, FileMode.Open, FileAccess.Read);
                            PartitionFileSystem nsp = new();
                            nsp.Initialize(file.AsStorage()).ThrowIfFailure();

                            return GetGameUpdateDataFromPartition(fileSystem, nsp, titleIdBase.ToString("x16"), programIndex);
                        }
                    }
                }

                return (null, null);
            }

            (Nca? patchNca, Nca? controlNca) GetGameUpdateDataFromPartition(VirtualFileSystem fileSystem, PartitionFileSystem pfs, string titleId, int programIndex)
            {
                Nca? patchNca = null;
                Nca? controlNca = null;

                fileSystem.ImportTickets(pfs);

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(fileSystem.KeySet, ncaFile.Release().AsStorage());

                    int ncaProgramIndex = (int)(nca.Header.TitleId & 0xF);

                    if (ncaProgramIndex != programIndex)
                    {
                        continue;
                    }

                    if ($"{nca.Header.TitleId.ToString("x16")[..^3]}000" != titleId)
                    {
                        break;
                    }

                    if (nca.Header.ContentType == NcaContentType.Program)
                    {
                        patchNca = nca;
                    }
                    else if (nca.Header.ContentType == NcaContentType.Control)
                    {
                        controlNca = nca;
                    }
                }

                return (patchNca, controlNca);
            }

            return gameInfo;
        }

        private static GameInfo GetDefaultInfo(Stream gameStream)
        {
            return new GameInfo
            {
                FileSize = gameStream.Length * 0.000000000931,
                TitleName = "Unknown",
                TitleId = "0000000000000000",
                Developer = "Unknown",
                Version = "0",
                Icon = null
            };
        }

        public static string GetDlcTitleId(string path, string ncaPath)
        {
            if (File.Exists(path))
            {
                using FileStream containerFile = File.OpenRead(path);

                PartitionFileSystem partitionFileSystem = new();
                partitionFileSystem.Initialize(containerFile.AsStorage()).ThrowIfFailure();

                SwitchDevice.VirtualFileSystem.ImportTickets(partitionFileSystem);

                using UniqueRef<IFile> ncaFile = new();

                partitionFileSystem.OpenFile(ref ncaFile.Ref, ncaPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                // 修改：使用条件性完整性检查
                IntegrityCheckLevel checkLevel = SwitchDevice.EnableFsIntegrityChecks ? 
                    IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None;
                
                Nca nca = TryOpenNca(ncaFile.Get.AsStorage(), ncaPath, checkLevel);
                if (nca != null)
                {
                    return nca.Header.TitleId.ToString("X16");
                }
            }
            return string.Empty;
        }

        // 修改 TryOpenNca 方法以接受完整性检查参数
        private static Nca TryOpenNca(IStorage ncaStorage, string containerPath, IntegrityCheckLevel checkLevel = IntegrityCheckLevel.None)
        {
            try
            {
                var nca = new Nca(SwitchDevice.VirtualFileSystem.KeySet, ncaStorage);
                return nca;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public static List<string> GetDlcContentList(string path, ulong titleId)
        {
            if (!File.Exists(path))
                return new List<string>();

            using FileStream containerFile = File.OpenRead(path);

            PartitionFileSystem partitionFileSystem = new();
            partitionFileSystem.Initialize(containerFile.AsStorage()).ThrowIfFailure();

            SwitchDevice.VirtualFileSystem.ImportTickets(partitionFileSystem);
            List<string> paths = new List<string>();

            foreach (DirectoryEntryEx fileEntry in partitionFileSystem.EnumerateEntries("/", "*.nca"))
            {
                using var ncaFile = new UniqueRef<IFile>();

                partitionFileSystem.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                Nca nca = TryOpenNca(ncaFile.Get.AsStorage(), path);
                if (nca == null)
                {
                    continue;
                }

                if (nca.Header.ContentType == NcaContentType.PublicData)
                {
                    if ((nca.Header.TitleId & 0xFFFFFFFFFFFFE000) != titleId)
                    {
                        break;
                    }

                    paths.Add(fileEntry.FullPath);
            }
            }

            return paths;
        }

        public static void SetupUiHandler()
        {
            if (SwitchDevice is { } switchDevice)
            {
                switchDevice.HostUiHandler = new AndroidUIHandler();
            }
        }

        public static void SetUiHandlerResponse(bool isOkPressed, string input)
        {
            if (SwitchDevice?.HostUiHandler is AndroidUIHandler uiHandler)
            {
                uiHandler.SetResponse(isOkPressed, input);
            }
        }

        public static List<string> GetCheats(string titleId, string gamePath)
        {
            var cheats = new List<string>();
            
            if (SwitchDevice?.VirtualFileSystem == null)
            {
                return cheats;
            }
            
            // 获取金手指目录路径
            string modsBasePath = ModLoader.GetModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(modsBasePath, titleId);
            string cheatsPath = Path.Combine(titleModsPath, "cheats");
            
            if (!Directory.Exists(cheatsPath))
            {
                return cheats;
            }
            
            // 读取金手指文件
            foreach (var file in Directory.GetFiles(cheatsPath, "*.txt"))
            {
                if (Path.GetFileName(file) == "enabled.txt") continue;
                
                string buildId = Path.GetFileNameWithoutExtension(file);
                var cheatInstructions = ModLoader.GetCheatsInFile(new FileInfo(file));
                
                foreach (var cheat in cheatInstructions)
                {
                    string cheatIdentifier = $"{buildId}-{cheat.Name}"; // 直接使用名称，不加 < >
                    cheats.Add(cheatIdentifier);
                }
            }
            
            return cheats;
        }

        public static List<string> GetEnabledCheats(string titleId)
        {
            var enabledCheats = new List<string>();
            
            // 获取已启用的金手指列表
            string modsBasePath = ModLoader.GetModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(modsBasePath, titleId);
            string enabledCheatsPath = Path.Combine(titleModsPath, "cheats", "enabled.txt");
            
            if (File.Exists(enabledCheatsPath))
            {
                enabledCheats.AddRange(File.ReadAllLines(enabledCheatsPath));
            }
            
            return enabledCheats;
        }

        public static void SetCheatEnabled(string titleId, string cheatId, bool enabled)
        {
            // 这里需要修改enabled.txt文件，添加或移除金手指ID
            string modsBasePath = ModLoader.GetModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(modsBasePath, titleId);
            string enabledCheatsPath = Path.Combine(titleModsPath, "cheats", "enabled.txt");
            
            var enabledCheats = new HashSet<string>();
            if (File.Exists(enabledCheatsPath))
            {
                // 确保读取时去除空行和空白字符
                var lines = File.ReadAllLines(enabledCheatsPath)
                              .Where(line => !string.IsNullOrWhiteSpace(line))
                              .Select(line => line.Trim());
                enabledCheats.UnionWith(lines);
            }
            
            if (enabled)
            {
                enabledCheats.Add(cheatId);
            }
            else
            {
                enabledCheats.Remove(cheatId);
            }
            
            Directory.CreateDirectory(Path.GetDirectoryName(enabledCheatsPath));
            File.WriteAllLines(enabledCheatsPath, enabledCheats);
            
            // 如果游戏正在运行，可能需要重新加载金手指
            if (SwitchDevice?.EmulationContext != null)
            {
            }
        }

        public static void SaveCheats(string titleId)
        {
            // 如果需要立即生效，可以在这里调用TamperMachine.EnableCheats
            // 但通常我们会在游戏启动时自动加载，所以这里可能不需要做任何事情
        }

        // ==================== 改进的存档管理功能 ====================

        /// <summary>
        /// 检查指定标题ID的存档是否存在（改进版本）
        /// </summary>
        public static bool SaveDataExists(string titleId)
        {
            return !string.IsNullOrEmpty(GetSaveIdByTitleId(titleId));
        }

        /// <summary>
        /// 获取所有存档文件夹的信息（改进版本，支持十六进制格式）
        /// </summary>
        public static List<SaveDataInfo> GetSaveDataList()
        {
            var saveDataList = new List<SaveDataInfo>();
            
            if (SwitchDevice?.VirtualFileSystem == null)
                return saveDataList;

            try
            {
                string saveBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save");
                string saveMetaBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "saveMeta");
                
                if (!Directory.Exists(saveBasePath))
                    return saveDataList;

                // 修改：支持十六进制格式的文件夹名
                var saveDirs = Directory.GetDirectories(saveBasePath)
                    .Where(dir => {
                        string dirName = Path.GetFileName(dir);
                        // 检查是否为16位十六进制字符串（0-9, a-f, A-F）
                        return dirName.Length == 16 && 
                               dirName.All(c => (c >= '0' && c <= '9') || 
                                              (c >= 'a' && c <= 'f') || 
                                              (c >= 'A' && c <= 'F'));
                    })
                    .ToList();

                foreach (var saveDir in saveDirs)
                {
                    string saveId = Path.GetFileName(saveDir);
                    var saveInfo = GetSaveDataInfo(saveId);
                    if (saveInfo != null)
                    {
                        saveDataList.Add(saveInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                // 记录错误日志
                Logger.Error?.Print(LogClass.Application, $"Error in GetSaveDataList: {ex.Message}");
                return saveDataList;
            }

            return saveDataList;
        }

        /// <summary>
        /// 获取特定存档文件夹的详细信息（改进版本）
        /// </summary>
        private static SaveDataInfo GetSaveDataInfo(string saveId)
        {
            try
            {
                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (!Directory.Exists(savePath))
                    return null;

                // 优先从 saveMeta 目录读取标题ID
                string titleId = GetTitleIdFromSaveMeta(saveId);
                
                // 如果 saveMeta 中没有找到，回退到 ExtraData 方法
                if (string.IsNullOrEmpty(titleId) || titleId == "0000000000000000")
                {
                    titleId = ExtractTitleIdFromExtraData(savePath);
                }

                string titleName = "Unknown Game";
                
                // 如果有标题ID，尝试获取游戏名称
                if (!string.IsNullOrEmpty(titleId) && titleId != "0000000000000000")
                {
                    titleName = $"Game [{titleId}]"; // 可以进一步改进为获取实际游戏名称
                }

                var directoryInfo = new DirectoryInfo(savePath);
                long totalSize = CalculateDirectorySize(savePath);

                return new SaveDataInfo
                {
                    SaveId = saveId,
                    TitleId = titleId ?? "0000000000000000",
                    TitleName = titleName,
                    LastModified = directoryInfo.LastWriteTime,
                    Size = totalSize
                };
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// 从 saveMeta 目录读取标题ID（新增方法）
        /// </summary>
        private static string GetTitleIdFromSaveMeta(string saveId)
        {
            try
            {
                string saveMetaPath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "saveMeta", saveId);
                if (!Directory.Exists(saveMetaPath))
                {
                    return null;
                }

                string metaFilePath = Path.Combine(saveMetaPath, "00000001.meta");
                if (!File.Exists(metaFilePath))
                {
                    return null;
                }

                // 读取 meta 文件并解析标题ID
                using var fileStream = File.OpenRead(metaFilePath);
                if (fileStream.Length >= 8)
                {
                    byte[] buffer = new byte[8];
                    fileStream.Read(buffer, 0, 8);
                    
                    // meta 文件中的标题ID通常是小端序
                    ulong titleIdValue = BitConverter.ToUInt64(buffer, 0);
                    string titleId = titleIdValue.ToString("x16");
                    
                    if (IsValidTitleId(titleId))
                    {
                        return titleId;
                    }
                }
            }
            catch (Exception ex)
            {
            }
            
            return null;
        }

        /// <summary>
        /// 改进的 ExtraData 标题ID提取方法
        /// </summary>
        private static string ExtractTitleIdFromExtraData(string savePath)
        {
            try
            {
                // 尝试读取 ExtraData0 或 ExtraData1 文件来获取标题ID
                string[] extraDataFiles = { "ExtraData0", "ExtraData1" };
                
                foreach (var fileName in extraDataFiles)
                {
                    string filePath = Path.Combine(savePath, fileName);
                    if (File.Exists(filePath))
                    {
                        using var fileStream = File.OpenRead(filePath);
                        if (fileStream.Length >= 8)
                        {
                            byte[] buffer = new byte[8];
                            fileStream.Read(buffer, 0, 8);
                            
                            // 尝试两种字节序
                            ulong titleIdValue1 = BitConverter.ToUInt64(buffer, 0);
                            string titleId1 = titleIdValue1.ToString("x16");
                            
                            if (IsValidTitleId(titleId1))
                            {
                                return titleId1;
                            }
                            
                            // 如果是大端序，需要反转字节
                            Array.Reverse(buffer);
                            ulong titleIdValue2 = BitConverter.ToUInt64(buffer, 0);
                            string titleId2 = titleIdValue2.ToString("x16");
                            
                            if (IsValidTitleId(titleId2))
                            {
                                return titleId2;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }
            
            return null;
        }

        /// <summary>
        /// 改进的验证标题ID格式方法
        /// </summary>
        private static bool IsValidTitleId(string titleId)
        {
            if (string.IsNullOrEmpty(titleId) || titleId.Length != 16)
                return false;
            
            // 检查是否全是0（无效）
            if (titleId.All(c => c == '0'))
                return false;
                
            // 检查格式：16位十六进制
            return titleId.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        /// <summary>
        /// 计算目录大小
        /// </summary>
        private static long CalculateDirectorySize(string path)
        {
            long size = 0;
            try
            {
                var directory = new DirectoryInfo(path);
                foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                {
                    size += file.Length;
                }
            }
            catch (Exception ex)
            {
            }
            return size;
        }

        /// <summary>
        /// 根据游戏标题ID获取对应的存档文件夹ID（改进版本）
        /// </summary>
        public static string GetSaveIdByTitleId(string titleId)
        {
            if (string.IsNullOrEmpty(titleId) || titleId == "0000000000000000")
                return null;

            var saveDataList = GetSaveDataList();
            var saveInfo = saveDataList.FirstOrDefault(s => s.TitleId == titleId);
            
            if (saveInfo != null)
            {
                return saveInfo.SaveId;
            }
            
            return null;
        }

        /// <summary>
        /// 导出存档为ZIP文件（只导出0和1文件夹）
        /// </summary>
        public static bool ExportSaveData(string titleId, string outputZipPath)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    return false;
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (!Directory.Exists(savePath))
                {
                    return false;
                }

                // 确保输出目录存在
                Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath));

                // 创建临时目录用于存放0和1文件夹
                string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempPath);

                try
                {
                    // 只复制0和1文件夹
                    string[] foldersToExport = { "0", "1" };
                    foreach (string folder in foldersToExport)
                    {
                        string sourceFolder = Path.Combine(savePath, folder);
                        string destFolder = Path.Combine(tempPath, folder);
                        
                        if (Directory.Exists(sourceFolder))
                        {
                            CopyDirectory(sourceFolder, destFolder);
                        }
                    }

                    // 使用 System.IO.Compression 创建ZIP文件
                    ZipFile.CreateFromDirectory(tempPath, outputZipPath, CompressionLevel.Optimal, false);
                    return true;
                }
                finally
                {
                    // 清理临时目录
                    if (Directory.Exists(tempPath))
                    {
                        Directory.Delete(tempPath, true);
                    }
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 从ZIP文件导入存档（只导入0和1文件夹）
        /// </summary>
        public static bool ImportSaveData(string titleId, string zipFilePath)
        {
            try
            {
                if (!File.Exists(zipFilePath))
                {
                    return false;
                }

                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    // 如果没有现有的存档文件夹，创建一个新的
                    saveId = FindNextAvailableSaveId();
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                
                // 创建目标目录
                Directory.CreateDirectory(savePath);

                // 创建临时目录用于解压
                string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempPath);

                try
                {
                    // 解压ZIP文件到临时目录
                    ZipFile.ExtractToDirectory(zipFilePath, tempPath);
                    
                    // 只复制0和1文件夹到目标目录
                    string[] foldersToImport = { "0", "1" };
                    foreach (string folder in foldersToImport)
                    {
                        string sourceFolder = Path.Combine(tempPath, folder);
                        string destFolder = Path.Combine(savePath, folder);
                        
                        if (Directory.Exists(sourceFolder))
                        {
                            // 如果目标文件夹已存在，先删除
                            if (Directory.Exists(destFolder))
                            {
                                Directory.Delete(destFolder, true);
                            }
                            
                            CopyDirectory(sourceFolder, destFolder);
                        }
                    }
                    
                    return true;
                }
                finally
                {
                    // 清理临时目录
                    if (Directory.Exists(tempPath))
                    {
                        Directory.Delete(tempPath, true);
                    }
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 创建saveMeta文件（新增方法）
        /// </summary>
        private static void CreateSaveMetaFile(string saveMetaPath, string titleId)
        {
            try
            {
                string metaFilePath = Path.Combine(saveMetaPath, "00000001.meta");
                
                if (ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdValue))
                {
                    byte[] titleIdBytes = BitConverter.GetBytes(titleIdValue);
                    
                    // 确保是小端序
                    if (!BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(titleIdBytes);
                    }
                    
                    File.WriteAllBytes(metaFilePath, titleIdBytes);
                }
            }
            catch (Exception ex)
            {
            }
        }

        /// <summary>
        /// 查找下一个可用的存档文件夹ID（改进版本，支持十六进制）
        /// </summary>
        private static string FindNextAvailableSaveId()
        {
            string saveBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save");
            
            if (!Directory.Exists(saveBasePath))
                return "0000000000000001";

            var existingIds = Directory.GetDirectories(saveBasePath)
                .Where(dir => {
                    string dirName = Path.GetFileName(dir);
                    return dirName.Length == 16 && 
                           dirName.All(c => (c >= '0' && c <= '9') || 
                                          (c >= 'a' && c <= 'f') || 
                                          (c >= 'A' && c <= 'F'));
                })
                .Select(dir => {
                    // 将十六进制字符串转换为长整型
                    if (long.TryParse(Path.GetFileName(dir), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long id))
                        return id;
                    return 0L;
                })
                .Where(id => id > 0)
                .OrderBy(id => id)
                .ToList();

            long nextId = 1;
            if (existingIds.Any())
            {
                nextId = existingIds.Last() + 1;
            }

            return nextId.ToString("X16").ToLower(); // 格式化为16位十六进制小写
        }

        /// <summary>
        /// 删除存档（改进版本，同时删除saveMeta）
        /// </summary>
        public static bool DeleteSaveData(string titleId)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    return false;
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                string saveMetaPath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "saveMeta", saveId);
                
                bool success = true;

                // 删除存档目录
                if (Directory.Exists(savePath))
                {
                    Directory.Delete(savePath, true);
                }
                else
                {
                    success = false;
                }

                // 删除存档元数据目录
                if (Directory.Exists(saveMetaPath))
                {
                    Directory.Delete(saveMetaPath, true);
                }
                
                return success;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 删除存档文件（只删除0和1文件夹中的文件，保留存档文件夹结构）
        /// </summary>
        public static bool DeleteSaveFiles(string titleId)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    return false;
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (!Directory.Exists(savePath))
                {
                    return false;
                }

                bool success = true;

                // 只删除0和1文件夹中的内容，保留文件夹结构
                string[] foldersToDelete = { "0", "1" };
                foreach (string folder in foldersToDelete)
                {
                    string folderPath = Path.Combine(savePath, folder);
                    if (Directory.Exists(folderPath))
                    {
                        try
                        {
                            // 删除文件夹中的所有内容，但保留文件夹本身
                            var directory = new DirectoryInfo(folderPath);
                            foreach (FileInfo file in directory.GetFiles())
                            {
                                file.Delete();
                            }
                            foreach (DirectoryInfo subDirectory in directory.GetDirectories())
                            {
                                subDirectory.Delete(true);
                            }
                        }
                        catch (Exception ex)
                        {
                            success = false;
                        }
                    }
                }
                
                return success;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 调试方法：显示所有存档文件夹的详细信息（改进版本）
        /// </summary>
        public static void DebugSaveData()
        {
            string saveBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save");
            string saveMetaBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "saveMeta");
            
            if (!Directory.Exists(saveBasePath))
            {
                Logger.Info?.Print(LogClass.Application, "Save base path does not exist");
                return;
            }
            
            // 修改：使用新的十六进制筛选条件
            var saveDirs = Directory.GetDirectories(saveBasePath)
                .Where(dir => {
                    string dirName = Path.GetFileName(dir);
                    return dirName.Length == 16 && 
                           dirName.All(c => (c >= '0' && c <= '9') || 
                                          (c >= 'a' && c <= 'f') || 
                                          (c >= 'A' && c <= 'F'));
                })
                .ToList();
            
            Logger.Info?.Print(LogClass.Application, $"Found {saveDirs.Count} save directories:");
            
            foreach (var saveDir in saveDirs)
            {
                string saveId = Path.GetFileName(saveDir);
                
                // 检查 saveMeta
                string saveMetaPath = Path.Combine(saveMetaBasePath, saveId);
                bool hasSaveMeta = Directory.Exists(saveMetaPath);
                
                // 检查 ExtraData 文件
                string[] extraDataFiles = { "ExtraData0", "ExtraData1" };
                bool hasExtraData = extraDataFiles.Any(file => File.Exists(Path.Combine(saveDir, file)));
                
                // 尝试获取标题ID
                string titleIdFromMeta = GetTitleIdFromSaveMeta(saveId);
                string titleIdFromExtra = ExtractTitleIdFromExtraData(saveDir);
                
                Logger.Info?.Print(LogClass.Application, 
                    $"SaveID: {saveId}, " +
                    $"HasSaveMeta: {hasSaveMeta}, " +
                    $"HasExtraData: {hasExtraData}, " +
                    $"TitleID from Meta: {titleIdFromMeta ?? "N/A"}, " +
                    $"TitleID from Extra: {titleIdFromExtra ?? "N/A"}");
            }
        }

        /// <summary>
        /// 强制刷新存档列表（新增方法）
        /// </summary>
        public static void RefreshSaveData()
        {
            // 强制重新扫描文件系统
            var freshList = GetSaveDataList();
        }
    }

    public class SwitchDevice : IDisposable
    {
        private readonly SystemVersion _firmwareVersion;
        public VirtualFileSystem VirtualFileSystem { get; set; }
        public ContentManager ContentManager { get; set; }
        public AccountManager AccountManager { get; set; }
        public LibHacHorizonManager LibHacHorizonManager { get; set; }
        public UserChannelPersistence UserChannelPersistence { get; set; }
        public InputManager? InputManager { get; set; }
        public Switch? EmulationContext { get; set; }
        public IHostUIHandler? HostUiHandler { get; set; }
        public bool EnableJitCacheEviction { get; set; }
        public bool EnableFsIntegrityChecks { get; set; }
        
        public void Dispose()
        {
            GC.SuppressFinalize(this);

            VirtualFileSystem.Dispose();
            InputManager?.Dispose();
            EmulationContext?.Dispose();
        }

        public SwitchDevice()
        {
            VirtualFileSystem = VirtualFileSystem.CreateInstance();
            LibHacHorizonManager = new LibHacHorizonManager();

            LibHacHorizonManager.InitializeFsServer(VirtualFileSystem);
            LibHacHorizonManager.InitializeArpServer();
            LibHacHorizonManager.InitializeBcatServer();
            LibHacHorizonManager.InitializeSystemClients();

            ContentManager = new ContentManager(VirtualFileSystem);
            AccountManager = new AccountManager(LibHacHorizonManager.RyujinxClient);
            UserChannelPersistence = new UserChannelPersistence();
            
            EnableFsIntegrityChecks = false;
            
            _firmwareVersion = ContentManager.GetCurrentFirmwareVersion();

            if (_firmwareVersion != null)
            {
            }
        }

        public bool InitializeContext(bool isHostMapped,
                                      bool useHypervisor,
                                      SystemLanguage systemLanguage,
                                      RegionCode regionCode,
                                      bool enableVsync,
                                      bool enableDockedMode,
                                      bool enablePtc,
                                      bool enableJitCacheEviction,
                                      bool enableInternetAccess,
                                      string? timeZone,
                                      bool ignoreMissingServices,
                                      MemoryConfiguration memoryConfiguration,
                                      long systemTimeOffset,
                                      // 新增网络参数
                                      MultiplayerMode multiplayerMode,
                                      string lanInterfaceId)
        {
            if (LibRyujinx.Renderer == null)
            {
                return false;
            }

            var renderer = LibRyujinx.Renderer;
            BackendThreading threadingMode = LibRyujinx.GraphicsConfiguration.BackendThreading;

            bool threadedGAL = threadingMode == BackendThreading.On || (threadingMode == BackendThreading.Auto && renderer.PreferThreading);

            if (threadedGAL)
            {
                renderer = new ThreadedRenderer(renderer);
            }

            HLEConfiguration configuration = new HLEConfiguration(VirtualFileSystem,
                                                                  LibHacHorizonManager,
                                                                  ContentManager,
                                                                  AccountManager,
                                                                  UserChannelPersistence,
                                                                  renderer,
                                                                  LibRyujinx.AudioDriver,
                                                                  memoryConfiguration,
                                                                  HostUiHandler,
                                                                  systemLanguage,
                                                                  regionCode,
                                                                  enableVsync,
                                                                  enableDockedMode,
                                                                  enablePtc,
                                                                  enableJitCacheEviction,
                                                                  enableInternetAccess,
                                                                  IntegrityCheckLevel.None,
                                                                  0,
                                                                  systemTimeOffset,
                                                                  timeZone,
                                                                  MemoryManagerMode.HostMappedUnsafe,
                                                                  ignoreMissingServices,
                                                                  LibRyujinx.GetAspectRatio(),
                                                                  100,
                                                                  useHypervisor,
                                                                  lanInterfaceId, // 修正：移除多余的""参数，直接使用lanInterfaceId
                                                                  multiplayerMode);

            try
            {
                EmulationContext = new Switch(configuration);
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        internal void ReloadFileSystem()
        {
            VirtualFileSystem.ReloadKeySet();
            ContentManager = new ContentManager(VirtualFileSystem);
            AccountManager = new AccountManager(LibHacHorizonManager.RyujinxClient);
        }

        internal void DisposeContext()
        {
            EmulationContext?.Dispose();
            EmulationContext?.DisposeGpu();
            EmulationContext = null;
            LibRyujinx.Renderer = null;
        }
    }

    public class GameInfo
    {
        public double FileSize;
        public string? TitleName;
        public string? TitleId;
        public string? Developer;
        public string? Version;
        public byte[]? Icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct GameInfoNative
    {
        public double FileSize;
        public char* TitleName;
        public char* TitleId;
        public char* Developer;
        public char* Version;
        public char* Icon;

        public GameInfoNative()
        {

        }

        public GameInfoNative(double fileSize, string? titleName, string? titleId, string? developer, string? version, byte[]? icon)
        {
            FileSize = fileSize;
            TitleId = (char*)Marshal.StringToHGlobalAnsi(titleId);
            Version = (char*)Marshal.StringToHGlobalAnsi(version);
            Developer = (char*)Marshal.StringToHGlobalAnsi(developer);
            TitleName = (char*)Marshal.StringToHGlobalAnsi(titleName);

            if (icon != null)
            {
                Icon = (char*)Marshal.StringToHGlobalAnsi(Convert.ToBase64String(icon));
            }
            else
            {
                Icon = (char*)0;
            }
        }

        public GameInfoNative(GameInfo info) : this(info.FileSize, info.TitleName, info.TitleId, info.Developer, info.Version, info.Icon){}
    }

    public class GameStats
    {
        public double Fifo;
        public double GameFps;
        public double GameTime;
    }

    // 存档信息类
    public class SaveDataInfo
    {
        public string SaveId { get; set; } = string.Empty; // 数字文件夹名，如 "0000000000000001"
        public string TitleId { get; set; } = string.Empty; // 游戏标题ID
        public string TitleName { get; set; } = string.Empty; // 游戏名称
        public DateTime LastModified { get; set; } // 最后修改时间
        public long Size { get; set; } // 存档大小
    }
}
