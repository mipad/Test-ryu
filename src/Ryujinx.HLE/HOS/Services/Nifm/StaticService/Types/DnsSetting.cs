using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nifm.StaticService.Types
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 9)]
    struct DnsSetting
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool IsDynamicDnsEnabled;
        public IpV4Address PrimaryDns;
        public IpV4Address SecondaryDns;

        public DnsSetting(IPInterfaceProperties interfaceProperties)
        {
            // 在 Android 平台上，IPInterfaceProperties.DnsAddresses 不可用
            if (OperatingSystem.IsAndroid())
            {
                IsDynamicDnsEnabled = false;
                
                // 使用已知的公共 DNS 服务器作为备用
                var dnsAddresses = GetDnsAddressesForAndroid();
                
                if (dnsAddresses.Count == 0)
                {
                    PrimaryDns = new IpV4Address();
                    SecondaryDns = new IpV4Address();
                }
                else
                {
                    PrimaryDns = new IpV4Address(dnsAddresses[0]);
                    SecondaryDns = new IpV4Address(dnsAddresses.Count > 1 ? dnsAddresses[1] : dnsAddresses[0]);
                }
            }
            else
            {
                // 原有逻辑保持不变
                IsDynamicDnsEnabled = OperatingSystem.IsWindows() && interfaceProperties.IsDynamicDnsEnabled;

                if (interfaceProperties.DnsAddresses.Count == 0)
                {
                    PrimaryDns = new IpV4Address();
                    SecondaryDns = new IpV4Address();
                }
                else
                {
                    PrimaryDns = new IpV4Address(interfaceProperties.DnsAddresses[0]);
                    SecondaryDns = new IpV4Address(interfaceProperties.DnsAddresses[interfaceProperties.DnsAddresses.Count > 1 ? 1 : 0]);
                }
            }
        }

        /// <summary>
        /// 为 Android 平台获取 DNS 地址
        /// </summary>
        private static List<IPAddress> GetDnsAddressesForAndroid()
        {
            var dnsAddresses = new List<IPAddress>();
            
            try
            {
                // 方法1: 尝试通过 JNI 从 Android 系统获取 DNS
                var androidDns = GetDnsFromAndroidSystem();
                if (androidDns != null && androidDns.Count > 0)
                {
                    return androidDns;
                }
                
                // 方法2: 使用已知的公共 DNS 服务器
                dnsAddresses.Add(IPAddress.Parse("8.8.8.8"));     // Google DNS
                dnsAddresses.Add(IPAddress.Parse("8.8.4.4"));     // Google DNS
                dnsAddresses.Add(IPAddress.Parse("1.1.1.1"));     // Cloudflare DNS
                dnsAddresses.Add(IPAddress.Parse("208.67.222.222")); // OpenDNS
            }
            catch (Exception ex)
            {
                // 如果所有方法都失败，使用最基础的 DNS
                dnsAddresses.Clear();
                dnsAddresses.Add(IPAddress.Parse("8.8.8.8"));
                
                // 记录错误但不抛出异常
                Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(Ryujinx.Common.Logging.LogClass.Service, 
                    $"Failed to get DNS addresses for Android: {ex.Message}");
            }
            
            return dnsAddresses;
        }

        /// <summary>
        /// 通过 Android 系统 API 获取 DNS 服务器
        /// </summary>
        private static List<IPAddress> GetDnsFromAndroidSystem()
        {
            var dnsList = new List<IPAddress>();
            
            try
            {
                // 这里需要调用 Android 原生代码来获取 DNS
                // 由于这是 C# 代码，我们需要通过 JNI 或特定于 Ryujinx Android 的方法
                
                // 方法1: 通过环境变量（如果设置了的话）
                string dnsFromEnv = Environment.GetEnvironmentVariable("DNS_SERVER");
                if (!string.IsNullOrEmpty(dnsFromEnv))
                {
                    if (IPAddress.TryParse(dnsFromEnv, out IPAddress dnsAddr))
                    {
                        dnsList.Add(dnsAddr);
                    }
                }
                
                // 方法2: 使用 /system/etc/resolv.conf（如果可访问）
                if (File.Exists("/system/etc/resolv.conf"))
                {
                    try
                    {
                        var lines = File.ReadAllLines("/system/etc/resolv.conf");
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("nameserver"))
                            {
                                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 2 && IPAddress.TryParse(parts[1], out IPAddress dnsAddr))
                                {
                                    dnsList.Add(dnsAddr);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 忽略文件访问错误
                    }
                }
                
                // 如果通过系统方法没有获取到 DNS，返回 null 让上层使用备用方案
                return dnsList.Count > 0 ? dnsList : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
