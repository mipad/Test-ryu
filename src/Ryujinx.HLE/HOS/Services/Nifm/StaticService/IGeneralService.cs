using Ryujinx.Common;
using System.Net;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.HOS.Services.Nifm.StaticService.GeneralService;
using Ryujinx.HLE.HOS.Services.Nifm.StaticService.Types;
using System;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;

namespace Ryujinx.HLE.HOS.Services.Nifm.StaticService
{
    class IGeneralService : DisposableIpcService
    {
        private readonly GeneralServiceDetail _generalServiceDetail;

        public IGeneralService()
        {
            _generalServiceDetail = new GeneralServiceDetail
            {
                ClientId = GeneralServiceManager.Count,
                IsAnyInternetRequestAccepted = true,
            };

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

            Logger.Stub?.PrintStub(LogClass.ServiceNifm, new { version });

            return ResultCode.Success;
        }

        [CommandCmif(5)]
        // GetCurrentNetworkProfile() -> buffer<nn::nifm::detail::sf::NetworkProfileData, 0x1a, 0x17c>
        public ResultCode GetCurrentNetworkProfile(ServiceCtx context)
        {
            ulong networkProfileDataPosition = context.Request.RecvListBuff[0].Position;

            context.Response.PtrBuff[0] = context.Response.PtrBuff[0].WithSize((uint)Unsafe.SizeOf<NetworkProfileData>());

            NetworkProfileData networkProfile = new()
            {
                Uuid = UInt128Utils.CreateRandom(),
            };

            // 直接创建设置，不依赖网络接口
            networkProfile.IpSettingData.IpAddressSetting = CreateIpAddressSetting();
            networkProfile.IpSettingData.DnsSetting = CreateDnsSetting();

            "RyujinxNetwork"u8.CopyTo(networkProfile.Name.AsSpan());

            context.Memory.Write(networkProfileDataPosition, networkProfile);

            return ResultCode.Success;
        }

        [CommandCmif(12)]
        // GetCurrentIpAddress() -> nn::nifm::IpV4Address
        public ResultCode GetCurrentIpAddress(ServiceCtx context)
        {
            // 返回备用 IP 地址
            context.ResponseData.WriteStruct(new IpV4Address(IPAddress.Parse("192.168.1.100")));
            return ResultCode.Success;
        }

        [CommandCmif(15)]
        // GetCurrentIpConfigInfo() -> (nn::nifm::IpAddressSetting, nn::nifm::DnsSetting)
        public ResultCode GetCurrentIpConfigInfo(ServiceCtx context)
        {
            // 直接创建设置
            context.ResponseData.WriteStruct(CreateIpAddressSetting());
            context.ResponseData.WriteStruct(CreateDnsSetting());

            return ResultCode.Success;
        }

        [CommandCmif(18)]
        // GetInternetConnectionStatus() -> nn::nifm::detail::sf::InternetConnectionStatus
        public ResultCode GetInternetConnectionStatus(ServiceCtx context)
        {
            // 假设网络总是可用的
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
            ulong size = context.Request.PtrBuff[0].Size;

            int clientId = context.Memory.Read<int>(position);

            context.ResponseData.Write(GeneralServiceManager.Get(clientId).IsAnyInternetRequestAccepted);

            return ResultCode.Success;
        }

        /// <summary>
        /// 创建 IP 地址设置
        /// </summary>
        private IpAddressSetting CreateIpAddressSetting()
        {
            return new IpAddressSetting(null, null);
        }

        /// <summary>
        /// 创建 DNS 设置
        /// </summary>
        private DnsSetting CreateDnsSetting()
        {
            return new DnsSetting(null);
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                GeneralServiceManager.Remove(_generalServiceDetail.ClientId);
            }
        }
    }
}
