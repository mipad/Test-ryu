using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Services.Nifm.StaticService.Types
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 0xd)]
    struct IpAddressSetting
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool IsDhcpEnabled;
        public IpV4Address Address;
        public IpV4Address IPv4Mask;
        public IpV4Address GatewayAddress;

        public IpAddressSetting(IPInterfaceProperties interfaceProperties, UnicastIPAddressInformation unicastIPAddressInformation)
        {
            // 尝试获取真实网络信息
            var realNetworkInfo = GetRealNetworkInfo();
            
            if (realNetworkInfo != null)
            {
                // 使用真实网络信息
                IsDhcpEnabled = realNetworkInfo.IsDhcpEnabled;
                Address = new IpV4Address(realNetworkInfo.Address);
                IPv4Mask = new IpV4Address(realNetworkInfo.SubnetMask);
                GatewayAddress = new IpV4Address(realNetworkInfo.Gateway);
                
                Logger.Info?.Print(LogClass.ServiceNifm, 
                    $"Using REAL network IP: {realNetworkInfo.Address}, " +
                    $"Subnet: {realNetworkInfo.SubnetMask}, " +
                    $"Gateway: {realNetworkInfo.Gateway}");
            }
            else
            {
                // 回退到模拟网络
                IsDhcpEnabled = true;
                Address = new IpV4Address(IPAddress.Parse("192.168.1.100"));
                IPv4Mask = new IpV4Address(IPAddress.Parse("255.255.255.0"));
                GatewayAddress = new IpV4Address(IPAddress.Parse("192.168.1.1"));
                
                Logger.Warning?.Print(LogClass.ServiceNifm, 
                    "Using FALLBACK network IP: 192.168.1.100 (real network info not available)");
            }
        }

        /// <summary>
        /// 获取真实网络信息
        /// </summary>
        private RealNetworkInfo GetRealNetworkInfo()
        {
            try
            {
                // 获取所有网络接口
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                
                Logger.Info?.Print(LogClass.ServiceNifm, 
                    $"Found {interfaces.Length} network interfaces");

                foreach (NetworkInterface ni in interfaces)
                {
                    // 只检查已启用且非回环的接口
                    if (ni.OperationalStatus == OperationalStatus.Up && 
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        IPInterfaceProperties properties = ni.GetIPProperties();
                        
                        // 获取 IPv4 地址
                        foreach (UnicastIPAddressInformation ip in properties.UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                var networkInfo = new RealNetworkInfo
                                {
                                    IsDhcpEnabled = properties.DhcpServerAddresses.Count > 0,
                                    Address = ip.Address,
                                    SubnetMask = ip.IPv4Mask ?? IPAddress.Parse("255.255.255.0"),
                                    Gateway = properties.GatewayAddresses.Count > 0 ? 
                                             properties.GatewayAddresses[0].Address : 
                                             GetDefaultGateway(ip.Address)
                                };

                                Logger.Info?.Print(LogClass.ServiceNifm, 
                                    $"Selected network interface: {ni.Name} ({ni.Description}), " +
                                    $"Type: {ni.NetworkInterfaceType}");

                                return networkInfo;
                            }
                        }
                    }
                    else
                    {
                        Logger.Debug?.Print(LogClass.ServiceNifm, 
                            $"Skipping interface: {ni.Name}, Status: {ni.OperationalStatus}, Type: {ni.NetworkInterfaceType}");
                    }
                }
                
                Logger.Warning?.Print(LogClass.ServiceNifm, "No suitable network interface found");
                return null;
            }
            catch (PlatformNotSupportedException ex)
            {
                Logger.Warning?.Print(LogClass.ServiceNifm, 
                    $"Platform not supported for network info: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.ServiceNifm, 
                    $"Error getting real network info: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 根据IP地址计算默认网关
        /// </summary>
        private IPAddress GetDefaultGateway(IPAddress ipAddress)
        {
            try
            {
                // 简单逻辑：如果IP是 192.168.x.x，网关通常是 192.168.x.1
                byte[] ipBytes = ipAddress.GetAddressBytes();
                if (ipBytes[0] == 192 && ipBytes[1] == 168)
                {
                    return IPAddress.Parse($"192.168.{ipBytes[2]}.1");
                }
                // 其他常见私有网络段
                else if (ipBytes[0] == 10)
                {
                    return IPAddress.Parse("10.0.0.1");
                }
                else if (ipBytes[0] == 172 && ipBytes[1] >= 16 && ipBytes[1] <= 31)
                {
                    return IPAddress.Parse($"172.{ipBytes[1]}.0.1");
                }
                
                return IPAddress.Parse("192.168.1.1");
            }
            catch
            {
                return IPAddress.Parse("192.168.1.1");
            }
        }
    }

    /// <summary>
    /// 真实网络信息类
    /// </summary>
    class RealNetworkInfo
    {
        public bool IsDhcpEnabled { get; set; }
        public IPAddress Address { get; set; }
        public IPAddress SubnetMask { get; set; }
        public IPAddress Gateway { get; set; }
    }
}
