using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.HOS.Services.Nifm.StaticService.GeneralService;
using Ryujinx.HLE.HOS.Services.Nifm.StaticService.Types;
using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;

namespace Ryujinx.HLE.HOS.Services.Nifm.StaticService
{
    class IGeneralService : DisposableIpcService
    {
        private readonly GeneralServiceDetail _generalServiceDetail;

        private IPInterfaceProperties _targetPropertiesCache = null;
        private UnicastIPAddressInformation _targetAddressInfoCache = null;
        private string _cacheChosenInterface = null;

        public IGeneralService()
        {
            _generalServiceDetail = new GeneralServiceDetail
            {
                ClientId = GeneralServiceManager.Count,
                IsAnyInternetRequestAccepted = true, // NOTE: Why not accept any internet request?
            };
            
            if (!Ryujinx.Common.PlatformInfo.IsBionic)
            {
                NetworkChange.NetworkAddressChanged += LocalInterfaceCacheHandler;
            }

            GeneralServiceManager.Add(_generalServiceDetail);
        }

        [CommandCmif(1)]
        // GetClientId() -> buffer<nn::nifm::ClientId, 0x1a, 4>
        public ResultCode GetClientId(ServiceCtx context)
        {
            ulong position = context.Request.RecvListBuff[0].Position;

            context.Response.PtrBuff[0] = context.Response.PtrBuff[0].WithSize(sizeof(int));

            context.Memory.Write(position, _generalServiceDetail.ClientId);

            return ResultCode.Success;
        }

        [CommandCmif(4)]
        // CreateRequest(u32 version) -> object<nn::nifm::detail::IRequest>
        public ResultCode CreateRequest(ServiceCtx context)
        {
            uint version = context.RequestData.ReadUInt32();

            MakeObject(context, new IRequest(context.Device.System, version));

            // Doesn't occur in our case.
            // return ResultCode.ObjectIsNull;

            Logger.Stub?.PrintStub(LogClass.ServiceNifm, new { version });

            return ResultCode.Success;
        }

        [CommandCmif(5)]
        // GetCurrentNetworkProfile() -> buffer<nn::nifm::detail::sf::NetworkProfileData, 0x1a, 0x17c>
        public ResultCode GetCurrentNetworkProfile(ServiceCtx context)
        {
            ulong networkProfileDataPosition = context.Request.RecvListBuff[0].Position;

            // Android 平台特殊处理
            if (AndroidNetworkSupport.IsAndroid)
            {
                return GetCurrentNetworkProfileForAndroid(context, networkProfileDataPosition);
            }

            // 原有非Android逻辑
            (IPInterfaceProperties interfaceProperties, UnicastIPAddressInformation unicastAddress) = GetLocalInterface(context);

            if (interfaceProperties == null || unicastAddress == null)
            {
                return ResultCode.NoInternetConnection;
            }

            Logger.Info?.Print(LogClass.ServiceNifm, $"Console's local IP is \"{unicastAddress.Address}\".");

            context.Response.PtrBuff[0] = context.Response.PtrBuff[0].WithSize((uint)Unsafe.SizeOf<NetworkProfileData>());

            NetworkProfileData networkProfile = new()
            {
                Uuid = UInt128Utils.CreateRandom(),
            };

            networkProfile.IpSettingData.IpAddressSetting = new IpAddressSetting(interfaceProperties, unicastAddress);
            networkProfile.IpSettingData.DnsSetting = new DnsSetting(interfaceProperties);

            "RyujinxNetwork"u8.CopyTo(networkProfile.Name.AsSpan());

            context.Memory.Write(networkProfileDataPosition, networkProfile);

            return ResultCode.Success;
        }

        [CommandCmif(12)]
        // GetCurrentIpAddress() -> nn::nifm::IpV4Address
        public ResultCode GetCurrentIpAddress(ServiceCtx context)
        {
            // Android 平台特殊处理
            if (AndroidNetworkSupport.IsAndroid)
            {
                // 返回一个默认的 IP 地址
                context.ResponseData.WriteStruct(AndroidNetworkSupport.GetCurrentIpAddress());
                Logger.Info?.Print(LogClass.ServiceNifm, "Android: Using fallback IP address");
                return ResultCode.Success;
            }

            (_, UnicastIPAddressInformation unicastAddress) = GetLocalInterface(context);

            if (unicastAddress == null)
            {
                return ResultCode.NoInternetConnection;
            }

            context.ResponseData.WriteStruct(new IpV4Address(unicastAddress.Address));

            Logger.Info?.Print(LogClass.ServiceNifm, $"Console's local IP is \"{unicastAddress.Address}\".");

            return ResultCode.Success;
        }

        [CommandCmif(15)]
        // GetCurrentIpConfigInfo() -> (nn::nifm::IpAddressSetting, nn::nifm::DnsSetting)
        public ResultCode GetCurrentIpConfigInfo(ServiceCtx context)
        {
            // Android 平台特殊处理
            if (AndroidNetworkSupport.IsAndroid)
            {
                return GetCurrentIpConfigInfoForAndroid(context);
            }

            (IPInterfaceProperties interfaceProperties, UnicastIPAddressInformation unicastAddress) = GetLocalInterface(context);

            if (interfaceProperties == null || unicastAddress == null)
            {
                return ResultCode.NoInternetConnection;
            }

            Logger.Info?.Print(LogClass.ServiceNifm, $"Console's local IP is \"{unicastAddress.Address}\".");

            context.ResponseData.WriteStruct(new IpAddressSetting(interfaceProperties, unicastAddress));
            context.ResponseData.WriteStruct(new DnsSetting(interfaceProperties));

            return ResultCode.Success;
        }

        [CommandCmif(18)]
        // GetInternetConnectionStatus() -> nn::nifm::detail::sf::InternetConnectionStatus
        public ResultCode GetInternetConnectionStatus(ServiceCtx context)
        {
            // Android 平台特殊处理
            if (AndroidNetworkSupport.IsAndroid)
            {
                if (!AndroidNetworkSupport.IsNetworkAvailable())
                {
                    return ResultCode.NoInternetConnection;
                }
            }
            else
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    return ResultCode.NoInternetConnection;
                }
            }

            InternetConnectionStatus internetConnectionStatus = new()
            {
                Type = InternetConnectionType.WiFi,
                WifiStrength = 3,
                State = InternetConnectionState.Connected,
            };

            context.ResponseData.WriteStruct(internetConnectionStatus);

            return ResultCode.Success;
        }

        [CommandCmif(21)]
        // IsAnyInternetRequestAccepted(buffer<nn::nifm::ClientId, 0x19, 4>) -> bool
        public ResultCode IsAnyInternetRequestAccepted(ServiceCtx context)
        {
            ulong position = context.Request.PtrBuff[0].Position;
#pragma warning disable IDE0059 // Remove unnecessary value assignment
            ulong size = context.Request.PtrBuff[0].Size;
#pragma warning restore IDE0059

            int clientId = context.Memory.Read<int>(position);

            context.ResponseData.Write(GeneralServiceManager.Get(clientId).IsAnyInternetRequestAccepted);

            return ResultCode.Success;
        }

        /// <summary>
        /// Android 专用的网络配置获取方法
        /// </summary>
        private ResultCode GetCurrentNetworkProfileForAndroid(ServiceCtx context, ulong networkProfileDataPosition)
        {
            try
            {
                Logger.Info?.Print(LogClass.ServiceNifm, "Android: Using fallback network profile");

                context.Response.PtrBuff[0] = context.Response.PtrBuff[0].WithSize((uint)Unsafe.SizeOf<NetworkProfileData>());

                NetworkProfileData networkProfile = new()
                {
                    Uuid = UInt128Utils.CreateRandom(),
                };

                // 使用 Android 支持类获取设置
                networkProfile.IpSettingData.IpAddressSetting = AndroidNetworkSupport.GetIpAddressSetting();
                networkProfile.IpSettingData.DnsSetting = AndroidNetworkSupport.GetDnsSetting();

                "RyujinxNetwork"u8.CopyTo(networkProfile.Name.AsSpan());

                context.Memory.Write(networkProfileDataPosition, networkProfile);

                return ResultCode.Success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceNifm, $"Android network profile failed: {ex.Message}");
                // 使用 NoInternetConnection 作为通用错误码
                return ResultCode.NoInternetConnection;
            }
        }

        /// <summary>
        /// Android 专用的 IP 配置获取方法
        /// </summary>
        private ResultCode GetCurrentIpConfigInfoForAndroid(ServiceCtx context)
        {
            try
            {
                Logger.Info?.Print(LogClass.ServiceNifm, "Android: Using fallback IP config");

                // 使用 Android 支持类获取设置
                context.ResponseData.WriteStruct(AndroidNetworkSupport.GetIpAddressSetting());
                context.ResponseData.WriteStruct(AndroidNetworkSupport.GetDnsSetting());

                return ResultCode.Success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceNifm, $"Android IP config failed: {ex.Message}");
                // 使用 NoInternetConnection 作为通用错误码
                return ResultCode.NoInternetConnection;
            }
        }

        private (IPInterfaceProperties, UnicastIPAddressInformation) GetLocalInterface(ServiceCtx context)
        {
            // Android 平台直接返回 null，因为我们使用专门的 Android 实现
            if (AndroidNetworkSupport.IsAndroid)
            {
                return (null, null);
            }

            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return (null, null);
            }

            string chosenInterface = context.Device.Configuration.MultiplayerLanInterfaceId;

            if (_targetPropertiesCache == null || _targetAddressInfoCache == null || _cacheChosenInterface != chosenInterface)
            {
                _cacheChosenInterface = chosenInterface;

                (_targetPropertiesCache, _targetAddressInfoCache) = NetworkHelpers.GetLocalInterface(chosenInterface);
            }

            return (_targetPropertiesCache, _targetAddressInfoCache);
        }

        private void LocalInterfaceCacheHandler(object sender, EventArgs e)
        {
            Logger.Info?.Print(LogClass.ServiceNifm, "NetworkAddress changed, invalidating cached data.");

            _targetPropertiesCache = null;
            _targetAddressInfoCache = null;
            
            // 如果是 Android，也清除 Android 特定的缓存
            if (AndroidNetworkSupport.IsAndroid)
            {
                AndroidNetworkSupport.ClearCache();
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                if (!Ryujinx.Common.PlatformInfo.IsBionic)
                {
                    NetworkChange.NetworkAddressChanged -= LocalInterfaceCacheHandler;
                }

                GeneralServiceManager.Remove(_generalServiceDetail.ClientId);
            }
        }
    }
}
