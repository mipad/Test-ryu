using System;
using System.Collections.Generic;
using System.IO;
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

        // 原有的构造函数 - 在 Android 上会抛出异常
        public DnsSetting(IPInterfaceProperties interfaceProperties)
        {
            // 在 Android 平台上立即抛出异常，让上层代码处理
            if (OperatingSystem.IsAndroid())
            {
                throw new PlatformNotSupportedException("IPInterfaceProperties.DnsAddresses is not supported on Android");
            }

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

        // 为 Android 添加新的静态创建方法
        public static DnsSetting CreateForAndroid()
        {
            return new DnsSetting
            {
                IsDynamicDnsEnabled = false,
                PrimaryDns = new IpV4Address(IPAddress.Parse("8.8.8.8")),
                SecondaryDns = new IpV4Address(IPAddress.Parse("8.8.4.4"))
            };
        }

        // 通用的回退创建方法
        public static DnsSetting CreateFallback()
        {
            return new DnsSetting
            {
                IsDynamicDnsEnabled = false,
                PrimaryDns = new IpV4Address(IPAddress.Parse("8.8.8.8")),
                SecondaryDns = new IpV4Address(IPAddress.Parse("8.8.4.4"))
            };
        }
    }
}
