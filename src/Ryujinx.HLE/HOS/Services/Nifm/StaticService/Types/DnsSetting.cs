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
            // 直接使用备用 DNS，不调用任何可能失败的方法
            IsDynamicDnsEnabled = false;
            PrimaryDns = new IpV4Address(IPAddress.Parse("8.8.8.8"));
            SecondaryDns = new IpV4Address(IPAddress.Parse("8.8.4.4"));
        }
    }
}
