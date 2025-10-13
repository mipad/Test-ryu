using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

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
            // 在 Android 平台上直接使用备用 IP 设置
            if (OperatingSystem.IsAndroid())
            {
                IsDhcpEnabled = true;
                Address = new IpV4Address(IPAddress.Parse("192.168.1.100"));
                IPv4Mask = new IpV4Address(IPAddress.Parse("255.255.255.0"));
                GatewayAddress = new IpV4Address(IPAddress.Parse("192.168.1.1"));
                return;
            }

            // 原有逻辑保持不变
            IsDhcpEnabled = (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) || interfaceProperties.DhcpServerAddresses.Count != 0;
            Address = new IpV4Address(unicastIPAddressInformation.Address);
            IPv4Mask = new IpV4Address(unicastIPAddressInformation.IPv4Mask);
            GatewayAddress = (interfaceProperties.GatewayAddresses.Count == 0) ? new IpV4Address() : new IpV4Address(interfaceProperties.GatewayAddresses[0].Address);
        }
    }
}
