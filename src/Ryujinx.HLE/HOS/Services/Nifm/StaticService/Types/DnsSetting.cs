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

        public DnsSetting(IPInterfaceProperties interfaceProperties)
        {
            // 在 Android 平台上直接使用备用 DNS，不调用任何可能抛出异常的方法
            if (OperatingSystem.IsAndroid())
            {
                IsDynamicDnsEnabled = false;
                PrimaryDns = new IpV4Address(IPAddress.Parse("8.8.8.8"));
                SecondaryDns = new IpV4Address(IPAddress.Parse("8.8.4.4"));
                return;
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
    }
}
