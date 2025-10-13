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
            // 在 Android 平台上抛出异常，让上层代码处理
            if (OperatingSystem.IsAndroid())
            {
                throw new PlatformNotSupportedException("IPInterfaceProperties is not fully supported on Android");
            }

            // 原有逻辑保持不变
            IsDhcpEnabled = (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) || interfaceProperties.DhcpServerAddresses.Count != 0;
            Address = new IpV4Address(unicastIPAddressInformation.Address);
            IPv4Mask = new IpV4Address(unicastIPAddressInformation.IPv4Mask);
            GatewayAddress = (interfaceProperties.GatewayAddresses.Count == 0) ? new IpV4Address() : new IpV4Address(interfaceProperties.GatewayAddresses[0].Address);
        }

        // 为 Android 添加静态创建方法
        public static IpAddressSetting CreateForAndroid()
        {
            return new IpAddressSetting
            {
                IsDhcpEnabled = true, // Android 通常使用 DHCP
                Address = new IpV4Address(IPAddress.Parse("192.168.1.100")),
                IPv4Mask = new IpV4Address(IPAddress.Parse("255.255.255.0")),
                GatewayAddress = new IpV4Address(IPAddress.Parse("192.168.1.1"))
            };
        }

        // 通用的回退创建方法
        public static IpAddressSetting CreateFallback()
        {
            return new IpAddressSetting
            {
                IsDhcpEnabled = true,
                Address = new IpV4Address(IPAddress.Parse("192.168.1.100")),
                IPv4Mask = new IpV4Address(IPAddress.Parse("255.255.255.0")),
                GatewayAddress = new IpV4Address(IPAddress.Parse("192.168.1.1"))
            };
        }
    }
}
