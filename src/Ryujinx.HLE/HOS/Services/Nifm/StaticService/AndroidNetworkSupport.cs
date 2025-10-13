using System;
using System.Net;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Nifm.StaticService.Types;

namespace Ryujinx.HLE.HOS.Services.Nifm.StaticService
{
    /// <summary>
    /// Android 网络支持工具类
    /// </summary>
    internal static class AndroidNetworkSupport
    {
        // Android 网络设置缓存
        private static IpAddressSetting? _cachedIpAddressSetting;
        private static DnsSetting? _cachedDnsSetting;

        /// <summary>
        /// 获取 Android 专用的 IP 地址设置
        /// </summary>
        public static IpAddressSetting GetIpAddressSetting()
        {
            if (_cachedIpAddressSetting == null)
            {
                _cachedIpAddressSetting = IpAddressSetting.CreateForAndroid();
                Logger.Info?.Print(LogClass.ServiceNifm, "Android: Created fallback IP address setting");
            }
            
            return _cachedIpAddressSetting.Value;
        }

        /// <summary>
        /// 获取 Android 专用的 DNS 设置
        /// </summary>
        public static DnsSetting GetDnsSetting()
        {
            if (_cachedDnsSetting == null)
            {
                _cachedDnsSetting = DnsSetting.CreateForAndroid();
                Logger.Info?.Print(LogClass.ServiceNifm, "Android: Created fallback DNS setting");
            }
            
            return _cachedDnsSetting.Value;
        }

        /// <summary>
        /// 检查是否是 Android 平台
        /// </summary>
        public static bool IsAndroid => OperatingSystem.IsAndroid();

        /// <summary>
        /// 清除缓存（用于网络变化时）
        /// </summary>
        public static void ClearCache()
        {
            _cachedIpAddressSetting = null;
            _cachedDnsSetting = null;
            Logger.Info?.Print(LogClass.ServiceNifm, "Android: Cleared network settings cache");
        }

        /// <summary>
        /// 刷新缓存（使用新的设置）
        /// </summary>
        public static void RefreshCache()
        {
            _cachedIpAddressSetting = IpAddressSetting.CreateForAndroid();
            _cachedDnsSetting = DnsSetting.CreateForAndroid();
            Logger.Info?.Print(LogClass.ServiceNifm, "Android: Refreshed network settings cache");
        }

        /// <summary>
        /// 获取当前 IP 地址（Android 版本）
        /// </summary>
        public static IpV4Address GetCurrentIpAddress()
        {
            return new IpV4Address(IPAddress.Parse("192.168.1.100"));
        }

        /// <summary>
        /// 检查网络连接状态（Android 版本）
        /// </summary>
        public static bool IsNetworkAvailable()
        {
            // 在 Android 上，我们假设网络总是可用的
            // 实际的实现可能需要通过 Android API 检查
            return true;
        }
    }
}
