using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Ryujinx.Common.Logging;

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
            // 直接使用可靠的公共 DNS
            IsDynamicDnsEnabled = false;
            PrimaryDns = new IpV4Address(IPAddress.Parse("8.8.8.8"));
            SecondaryDns = new IpV4Address(IPAddress.Parse("8.8.4.4"));
            
            Logger.Info?.Print(LogClass.ServiceNifm, 
                "Using PUBLIC DNS: Primary=8.8.8.8, Secondary=8.8.4.4");
        }
    }
}
