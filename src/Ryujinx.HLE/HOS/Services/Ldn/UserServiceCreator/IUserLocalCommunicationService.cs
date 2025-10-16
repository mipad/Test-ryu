using LibHac.Ns;
using Ryujinx.Common;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Common.Utilities;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Ldn.Types;
using Ryujinx.HLE.HOS.Services.Ldn.UserServiceCreator.LdnMitm;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Ldn.UserServiceCreator
{
    class IUserLocalCommunicationService : IpcService, IDisposable
    {
        public INetworkClient NetworkClient { get; private set; }

        private const int NifmRequestID = 90;
        private const string DefaultIPAddress = "127.0.0.1";
        private const string DefaultSubnetMask = "255.255.255.0";
        private const bool IsDevelopment = false;

        private readonly KEvent _stateChangeEvent;
        private int _stateChangeEventHandle;

        private NetworkState _state;
        private DisconnectReason _disconnectReason;
        private ResultCode _nifmResultCode;

        private AccessPoint _accessPoint;
        private Station _station;

        public IUserLocalCommunicationService(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Creating IUserLocalCommunicationService ===");
            _stateChangeEvent = new KEvent(context.Device.System.KernelContext);
            _state = NetworkState.None;
            _disconnectReason = DisconnectReason.None;
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: IUserLocalCommunicationService created successfully ===");
        }

        private ushort CheckDevelopmentChannel(ushort channel)
        {
            return (ushort)(!IsDevelopment ? 0 : channel);
        }

        private SecurityMode CheckDevelopmentSecurityMode(SecurityMode securityMode)
        {
            return !IsDevelopment ? SecurityMode.Retail : securityMode;
        }

        private bool CheckLocalCommunicationIdPermission(ServiceCtx context, ulong localCommunicationIdChecked)
        {
            // TODO: Call nn::arp::GetApplicationControlProperty here when implemented.
            ApplicationControlProperty controlProperty = context.Device.Processes.ActiveApplication.ApplicationControlProperties;

            foreach (var localCommunicationId in controlProperty.LocalCommunicationId.ItemsRo)
            {
                if (localCommunicationId == localCommunicationIdChecked)
                {
                    return true;
                }
            }

            return false;
        }

        [CommandCmif(0)]
        // GetState() -> s32 state
        public ResultCode GetState(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetState called, current state: {_state} ===");
            
            if (_nifmResultCode != ResultCode.Success)
            {
                context.ResponseData.Write((int)NetworkState.Error);
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetState returning Error due to NIFM failure: {_nifmResultCode} ===");
                return ResultCode.Success;
            }

            // NOTE: Returns ResultCode.InvalidArgument if _state is null, doesn't occur in our case.
            context.ResponseData.Write((int)_state);
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetState returning state: {_state} ===");

            return ResultCode.Success;
        }

        public void SetState()
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: Signaling state change event, current state: {_state} ===");
            _stateChangeEvent.WritableEvent.Signal();
        }

        public void SetState(NetworkState state)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: Changing state from {_state} to {state} ===");
            _state = state;

            SetState();
        }

        [CommandCmif(1)]
        // GetNetworkInfo() -> buffer<network_info<0x480>, 0x1a>
        public ResultCode GetNetworkInfo(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetNetworkInfo called ===");
            
            ulong bufferPosition = context.Request.RecvListBuff[0].Position;

            MemoryHelper.FillWithZeros(context.Memory, bufferPosition, 0x480);

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNetworkInfo failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            ResultCode resultCode = GetNetworkInfoImpl(out NetworkInfo networkInfo);
            if (resultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNetworkInfoImpl failed: {resultCode} ===");
                return resultCode;
            }

            ulong infoSize = MemoryHelper.Write(context.Memory, bufferPosition, networkInfo);

            context.Response.PtrBuff[0] = context.Response.PtrBuff[0].WithSize(infoSize);

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetNetworkInfo completed successfully ===");
            return ResultCode.Success;
        }

        private ResultCode GetNetworkInfoImpl(out NetworkInfo networkInfo)
        {
            if (_state == NetworkState.StationConnected)
            {
                networkInfo = _station.NetworkInfo;
                Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetNetworkInfoImpl returning Station network info ===");
            }
            else if (_state == NetworkState.AccessPointCreated)
            {
                networkInfo = _accessPoint.NetworkInfo;
                Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetNetworkInfoImpl returning AccessPoint network info ===");
            }
            else
            {
                networkInfo = new NetworkInfo();
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNetworkInfoImpl failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }

            return ResultCode.Success;
        }

        private NodeLatestUpdate[] GetNodeLatestUpdateImpl(int count)
        {
            if (_state == NetworkState.StationConnected)
            {
                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNodeLatestUpdateImpl from Station, count: {count} ===");
                return _station.LatestUpdates.ConsumeLatestUpdate(count);
            }
            else if (_state == NetworkState.AccessPointCreated)
            {
                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNodeLatestUpdateImpl from AccessPoint, count: {count} ===");
                return _accessPoint.LatestUpdates.ConsumeLatestUpdate(count);
            }
            else
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNodeLatestUpdateImpl failed - invalid state: {_state} ===");
                return Array.Empty<NodeLatestUpdate>();
            }
        }

        [CommandCmif(2)]
        // GetIpv4Address() -> (u32 ip_address, u32 subnet_mask)
        public ResultCode GetIpv4Address(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetIpv4Address called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetIpv4Address failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            // NOTE: Return ResultCode.InvalidArgument if ip_address and subnet_mask are null, doesn't occur in our case.

            if (_state == NetworkState.AccessPointCreated || _state == NetworkState.StationConnected)
            {
                (_, UnicastIPAddressInformation unicastAddress) = NetworkHelpers.GetLocalInterface(context.Device.Configuration.MultiplayerLanInterfaceId);

                if (unicastAddress == null)
                {
                    Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Using default IP address ===");
                    context.ResponseData.Write(NetworkHelpers.ConvertIpv4Address(DefaultIPAddress));
                    context.ResponseData.Write(NetworkHelpers.ConvertIpv4Address(DefaultSubnetMask));
                }
                else
                {
                    Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: Console's LDN IP is \"{unicastAddress.Address}\" ===");

                    context.ResponseData.Write(NetworkHelpers.ConvertIpv4Address(unicastAddress.Address));
                    context.ResponseData.Write(NetworkHelpers.ConvertIpv4Address(unicastAddress.IPv4Mask));
                }
            }
            else
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetIpv4Address failed - invalid state: {_state} ===");
                return ResultCode.InvalidArgument;
            }

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetIpv4Address completed successfully ===");
            return ResultCode.Success;
        }

        [CommandCmif(3)]
        // GetDisconnectReason() -> u16 disconnect_reason
        public ResultCode GetDisconnectReason(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetDisconnectReason called, reason: {_disconnectReason} ===");
            
            // NOTE: Returns ResultCode.InvalidArgument if _disconnectReason is null, doesn't occur in our case.

            context.ResponseData.Write((short)_disconnectReason);

            return ResultCode.Success;
        }

        public void SetDisconnectReason(DisconnectReason reason)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: Setting disconnect reason to {reason}, current state: {_state} ===");
            
            if (_state != NetworkState.Initialized)
            {
                _disconnectReason = reason;

                SetState(NetworkState.Initialized);
            }
        }

        [CommandCmif(4)]
        // GetSecurityParameter() -> bytes<0x20, 1> security_parameter
        public ResultCode GetSecurityParameter(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetSecurityParameter called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetSecurityParameter failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            ResultCode resultCode = GetNetworkInfoImpl(out NetworkInfo networkInfo);
            if (resultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNetworkInfoImpl failed in GetSecurityParameter: {resultCode} ===");
                return resultCode;
            }

            SecurityParameter securityParameter = new()
            {
                Data = new Array16<byte>(),
                SessionId = networkInfo.NetworkId.SessionId,
            };

            context.ResponseData.WriteStruct(securityParameter);

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetSecurityParameter completed successfully ===");
            return ResultCode.Success;
        }

        [CommandCmif(5)]
        // GetNetworkConfig() -> bytes<0x20, 8> network_config
        public ResultCode GetNetworkConfig(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetNetworkConfig called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNetworkConfig failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            ResultCode resultCode = GetNetworkInfoImpl(out NetworkInfo networkInfo);
            if (resultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNetworkInfoImpl failed in GetNetworkConfig: {resultCode} ===");
                return resultCode;
            }

            NetworkConfig networkConfig = new()
            {
                IntentId = networkInfo.NetworkId.IntentId,
                Channel = networkInfo.Common.Channel,
                NodeCountMax = networkInfo.Ldn.NodeCountMax,
                LocalCommunicationVersion = networkInfo.Ldn.Nodes[0].LocalCommunicationVersion,
                Reserved2 = new Array10<byte>(),
            };

            context.ResponseData.WriteStruct(networkConfig);

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetNetworkConfig completed successfully ===");
            return ResultCode.Success;
        }

        [CommandCmif(100)]
        // AttachStateChangeEvent() -> handle<copy>
        public ResultCode AttachStateChangeEvent(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: AttachStateChangeEvent called ===");

            if (_stateChangeEventHandle == 0 && context.Process.HandleTable.GenerateHandle(_stateChangeEvent.ReadableEvent, out _stateChangeEventHandle) != Result.Success)
            {
                Logger.Error?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Failed to generate handle for state change event ===");
                throw new InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(_stateChangeEventHandle);

            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: AttachStateChangeEvent completed, handle: {_stateChangeEventHandle} ===");

            // Returns ResultCode.InvalidArgument if handle is null, doesn't occur in our case since we already throw an Exception.

            return ResultCode.Success;
        }

        [CommandCmif(101)]
        // GetNetworkInfoLatestUpdate() -> (buffer<network_info<0x480>, 0x1a>, buffer<node_latest_update, 0xa>)
        public ResultCode GetNetworkInfoLatestUpdate(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetNetworkInfoLatestUpdate called ===");

            ulong bufferPosition = context.Request.RecvListBuff[0].Position;

            MemoryHelper.FillWithZeros(context.Memory, bufferPosition, 0x480);

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNetworkInfoLatestUpdate failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            ResultCode resultCode = GetNetworkInfoImpl(out NetworkInfo networkInfo);
            if (resultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: GetNetworkInfoImpl failed in GetNetworkInfoLatestUpdate: {resultCode} ===");
                return resultCode;
            }

            ulong outputPosition = context.Request.RecvListBuff[0].Position;
            ulong outputSize = context.Request.RecvListBuff[0].Size;

            ulong latestUpdateSize = (ulong)Marshal.SizeOf<NodeLatestUpdate>();
            int count = (int)(outputSize / latestUpdateSize);

            NodeLatestUpdate[] latestUpdate = GetNodeLatestUpdateImpl(count);

            MemoryHelper.FillWithZeros(context.Memory, outputPosition, (int)outputSize);

            foreach (NodeLatestUpdate node in latestUpdate)
            {
                MemoryHelper.Write(context.Memory, outputPosition, node);

                outputPosition += latestUpdateSize;
            }

            ulong infoSize = MemoryHelper.Write(context.Memory, bufferPosition, networkInfo);

            context.Response.PtrBuff[0] = context.Response.PtrBuff[0].WithSize(infoSize);

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: GetNetworkInfoLatestUpdate completed successfully ===");
            return ResultCode.Success;
        }

        [CommandCmif(102)]
        // Scan(u16 channel, bytes<0x60, 8> scan_filter) -> (u16 count, buffer<network_info, 0x22>)
        public ResultCode Scan(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Scan called ===");
            return ScanImpl(context);
        }

        [CommandCmif(103)]
        // ScanPrivate(u16 channel, bytes<0x60, 8> scan_filter) -> (u16 count, buffer<network_info, 0x22>)
        public ResultCode ScanPrivate(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ScanPrivate called ===");
            return ScanImpl(context, true);
        }

        private ResultCode ScanImpl(ServiceCtx context, bool isPrivate = false)
        {
            ushort channel = (ushort)context.RequestData.ReadUInt64();
            ScanFilter scanFilter = context.RequestData.ReadStruct<ScanFilter>();

            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ScanImpl - channel: {channel}, isPrivate: {isPrivate} ===");

            (ulong bufferPosition, ulong bufferSize) = context.Request.GetBufferType0x22(0);

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ScanImpl failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (!isPrivate)
            {
                channel = CheckDevelopmentChannel(channel);
                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ScanImpl - development channel adjusted to: {channel} ===");
            }

            ResultCode resultCode = ResultCode.InvalidArgument;

            if (bufferSize != 0)
            {
                if (bufferPosition != 0)
                {
                    ScanFilterFlag scanFilterFlag = scanFilter.Flag;

                    if (!scanFilterFlag.HasFlag(ScanFilterFlag.NetworkType) || scanFilter.NetworkType <= NetworkType.All)
                    {
                        if (scanFilterFlag.HasFlag(ScanFilterFlag.Ssid))
                        {
                            if (scanFilter.Ssid.Length <= 31)
                            {
                                Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ScanImpl - SSID length check failed ===");
                                return resultCode;
                            }
                        }

                        if (!scanFilterFlag.HasFlag(ScanFilterFlag.MacAddress))
                        {
                            if (scanFilterFlag > ScanFilterFlag.All)
                            {
                                Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ScanImpl - scan filter flag check failed ===");
                                return resultCode;
                            }

                            if (_state - 3 >= NetworkState.AccessPoint)
                            {
                                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ScanImpl - invalid state: {_state} ===");
                                resultCode = ResultCode.InvalidState;
                            }
                            else
                            {
                                if (scanFilter.NetworkId.IntentId.LocalCommunicationId == -1 && NetworkClient.NeedsRealId)
                                {
                                    // TODO: Call nn::arp::GetApplicationControlProperty here when implemented.
                                    ApplicationControlProperty controlProperty = context.Device.Processes.ActiveApplication.ApplicationControlProperties;

                                    scanFilter.NetworkId.IntentId.LocalCommunicationId = (long)controlProperty.LocalCommunicationId[0];
                                    Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ScanImpl - updated LocalCommunicationId: {scanFilter.NetworkId.IntentId.LocalCommunicationId} ===");
                                }

                                resultCode = ScanInternal(context.Memory, channel, scanFilter, bufferPosition, bufferSize, out ulong counter);

                                context.ResponseData.Write(counter);
                                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ScanImpl completed, found {counter} networks ===");
                            }
                        }
                        else
                        {
                            Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ScanImpl - MAC address filtering not supported ===");
                            throw new NotSupportedException();
                        }
                    }
                }
            }

            return resultCode;
        }

        private ResultCode ScanInternal(IVirtualMemoryManager memory, ushort channel, ScanFilter scanFilter, ulong bufferPosition, ulong bufferSize, out ulong counter)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ScanInternal - channel: {channel}, bufferSize: {bufferSize} ===");
            
            ulong networkInfoSize = (ulong)Marshal.SizeOf(typeof(NetworkInfo));
            ulong maxGames = bufferSize / networkInfoSize;

            MemoryHelper.FillWithZeros(memory, bufferPosition, (int)bufferSize);

            NetworkInfo[] availableGames = NetworkClient.Scan(channel, scanFilter);

            counter = 0;

            foreach (NetworkInfo networkInfo in availableGames)
            {
                MemoryHelper.Write(memory, bufferPosition + (networkInfoSize * counter), networkInfo);

                if (++counter >= maxGames)
                {
                    break;
                }
            }

            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ScanInternal found {counter} networks ===");
            return ResultCode.Success;
        }

        [CommandCmif(104)] // 5.0.0+
        // SetWirelessControllerRestriction(u32 wireless_controller_restriction)
        public ResultCode SetWirelessControllerRestriction(ServiceCtx context)
        {
            uint wirelessControllerRestriction = context.RequestData.ReadUInt32();
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetWirelessControllerRestriction called: {wirelessControllerRestriction} ===");

            // NOTE: Return ResultCode.InvalidArgument if an internal IPAddress is null, doesn't occur in our case.

            if (wirelessControllerRestriction > 1)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: SetWirelessControllerRestriction - invalid restriction value ===");
                return ResultCode.InvalidArgument;
            }

            if (_state != NetworkState.Initialized)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetWirelessControllerRestriction - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }

            // NOTE: WirelessControllerRestriction value is used for the btm service in SetWlanMode call.
            //       Since we use our own implementation we can do nothing here.

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: SetWirelessControllerRestriction completed ===");
            return ResultCode.Success;
        }

        [CommandCmif(200)]
        // OpenAccessPoint()
        public ResultCode OpenAccessPoint(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: OpenAccessPoint called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: OpenAccessPoint failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (_state != NetworkState.Initialized)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: OpenAccessPoint failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }

            CloseStation();

            SetState(NetworkState.AccessPoint);

            _accessPoint = new AccessPoint(this);

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: OpenAccessPoint completed successfully ===");

            // NOTE: Calls nifm service and return related result codes.
            //       Since we use our own implementation we can return ResultCode.Success.

            return ResultCode.Success;
        }

        [CommandCmif(201)]
        // CloseAccessPoint()
        public ResultCode CloseAccessPoint(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CloseAccessPoint called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CloseAccessPoint failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (_state == NetworkState.AccessPoint || _state == NetworkState.AccessPointCreated)
            {
                DestroyNetworkImpl(DisconnectReason.DestroyedByUser);
            }
            else
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CloseAccessPoint failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }

            SetState(NetworkState.Initialized);

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CloseAccessPoint completed successfully ===");
            return ResultCode.Success;
        }

        private void CloseAccessPoint()
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CloseAccessPoint internal called ===");
            _accessPoint?.Dispose();
            _accessPoint = null;
        }

        [CommandCmif(202)]
        // CreateNetwork(bytes<0x44, 2> security_config, bytes<0x30, 1> user_config, bytes<0x20, 8> network_config)
        public ResultCode CreateNetwork(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CreateNetwork called ===");
            return CreateNetworkImpl(context);
        }

        [CommandCmif(203)]
        // CreateNetworkPrivate(bytes<0x44, 2> security_config, bytes<0x20, 1> security_parameter, bytes<0x30, 1>, bytes<0x20, 8> network_config, buffer<unknown, 9> address_entry, int count)
        public ResultCode CreateNetworkPrivate(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CreateNetworkPrivate called ===");
            return CreateNetworkImpl(context, true);
        }

        public ResultCode CreateNetworkImpl(ServiceCtx context, bool isPrivate = false)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CreateNetworkImpl called, isPrivate: {isPrivate} ===");

            SecurityConfig securityConfig = context.RequestData.ReadStruct<SecurityConfig>();
            SecurityParameter securityParameter = isPrivate ? context.RequestData.ReadStruct<SecurityParameter>() : new SecurityParameter();

            UserConfig userConfig = context.RequestData.ReadStruct<UserConfig>();

            context.RequestData.BaseStream.Seek(4, SeekOrigin.Current); // Alignment?
            NetworkConfig networkConfig = context.RequestData.ReadStruct<NetworkConfig>();

            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CreateNetworkImpl - NetworkConfig: Channel={networkConfig.Channel}, NodeCountMax={networkConfig.NodeCountMax} ===");

            if (networkConfig.IntentId.LocalCommunicationId == -1 && NetworkClient.NeedsRealId)
            {
                // TODO: Call nn::arp::GetApplicationControlProperty here when implemented.
                ApplicationControlProperty controlProperty = context.Device.Processes.ActiveApplication.ApplicationControlProperties;

                networkConfig.IntentId.LocalCommunicationId = (long)controlProperty.LocalCommunicationId[0];
                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CreateNetworkImpl - updated LocalCommunicationId: {networkConfig.IntentId.LocalCommunicationId} ===");
            }

            bool isLocalCommunicationIdValid = CheckLocalCommunicationIdPermission(context, (ulong)networkConfig.IntentId.LocalCommunicationId);
            if (!isLocalCommunicationIdValid && NetworkClient.NeedsRealId)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CreateNetworkImpl - invalid LocalCommunicationId permission ===");
                return ResultCode.InvalidObject;
            }

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CreateNetworkImpl failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            networkConfig.Channel = CheckDevelopmentChannel(networkConfig.Channel);
            securityConfig.SecurityMode = CheckDevelopmentSecurityMode(securityConfig.SecurityMode);

            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CreateNetworkImpl - adjusted Channel: {networkConfig.Channel}, SecurityMode: {securityConfig.SecurityMode} ===");

            if (networkConfig.NodeCountMax <= LdnConst.NodeCountMax)
            {
                if ((((ulong)networkConfig.LocalCommunicationVersion) & 0x80000000) == 0)
                {
                    if (securityConfig.SecurityMode <= SecurityMode.Retail)
                    {
                        if (securityConfig.Passphrase.Length <= LdnConst.PassphraseLengthMax)
                        {
                            if (_state == NetworkState.AccessPoint)
                            {
                                if (isPrivate)
                                {
                                    ulong bufferPosition = context.Request.PtrBuff[0].Position;
                                    ulong bufferSize = context.Request.PtrBuff[0].Size;

                                    byte[] addressListBytes = new byte[bufferSize];

                                    context.Memory.Read(bufferPosition, addressListBytes);

                                    AddressList addressList = MemoryMarshal.Cast<byte, AddressList>(addressListBytes)[0];

                                    Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CreateNetworkImpl - creating private network ===");
                                    _accessPoint.CreateNetworkPrivate(securityConfig, securityParameter, userConfig, networkConfig, addressList);
                                }
                                else
                                {
                                    Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CreateNetworkImpl - creating public network ===");
                                    _accessPoint.CreateNetwork(securityConfig, userConfig, networkConfig);
                                }

                                Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CreateNetworkImpl completed successfully ===");
                                return ResultCode.Success;
                            }
                            else
                            {
                                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CreateNetworkImpl failed - invalid state: {_state} ===");
                                return ResultCode.InvalidState;
                            }
                        }
                    }
                }
            }

            Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CreateNetworkImpl failed - invalid argument ===");
            return ResultCode.InvalidArgument;
        }

        [CommandCmif(204)]
        // DestroyNetwork()
        public ResultCode DestroyNetwork(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: DestroyNetwork called ===");
            return DestroyNetworkImpl(DisconnectReason.DestroyedByUser);
        }

        private ResultCode DestroyNetworkImpl(DisconnectReason disconnectReason)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: DestroyNetworkImpl called, reason: {disconnectReason} ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: DestroyNetworkImpl failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (disconnectReason - 3 <= DisconnectReason.DisconnectedByUser)
            {
                if (_state == NetworkState.AccessPointCreated)
                {
                    CloseAccessPoint();

                    SetState(NetworkState.AccessPoint);

                    Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: DestroyNetworkImpl completed successfully ===");
                    return ResultCode.Success;
                }

                CloseAccessPoint();

                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: DestroyNetworkImpl failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }

            Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: DestroyNetworkImpl failed - invalid disconnect reason ===");
            return ResultCode.InvalidArgument;
        }

        [CommandCmif(205)]
        // Reject(u32 node_id)
        public ResultCode Reject(ServiceCtx context)
        {
            uint nodeId = context.RequestData.ReadUInt32();
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: Reject called, nodeId: {nodeId} ===");
            return RejectImpl(DisconnectReason.Rejected, nodeId);
        }

        private ResultCode RejectImpl(DisconnectReason disconnectReason, uint nodeId)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: RejectImpl called, reason: {disconnectReason}, nodeId: {nodeId} ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: RejectImpl failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (_state != NetworkState.AccessPointCreated)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: RejectImpl failed - must be network host, current state: {_state} ===");
                return ResultCode.InvalidState; // Must be network host to reject nodes.
            }

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: RejectImpl completed ===");
            return NetworkClient.Reject(disconnectReason, nodeId);
        }

        [CommandCmif(206)]
        // SetAdvertiseData(buffer<advertise_data, 0x21>)
        public ResultCode SetAdvertiseData(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: SetAdvertiseData called ===");

            (ulong bufferPosition, ulong bufferSize) = context.Request.GetBufferType0x21(0);

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetAdvertiseData failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (bufferSize == 0 || bufferSize > LdnConst.AdvertiseDataSizeMax)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetAdvertiseData failed - invalid buffer size: {bufferSize} ===");
                return ResultCode.InvalidArgument;
            }

            if (_state == NetworkState.AccessPoint || _state == NetworkState.AccessPointCreated)
            {
                byte[] advertiseData = new byte[bufferSize];

                context.Memory.Read(bufferPosition, advertiseData);

                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetAdvertiseData setting data, size: {bufferSize} ===");
                return _accessPoint.SetAdvertiseData(advertiseData);
            }
            else
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetAdvertiseData failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }
        }

        [CommandCmif(207)]
        // SetStationAcceptPolicy(u8 accept_policy)
        public ResultCode SetStationAcceptPolicy(ServiceCtx context)
        {
            AcceptPolicy acceptPolicy = (AcceptPolicy)context.RequestData.ReadByte();
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetStationAcceptPolicy called, policy: {acceptPolicy} ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetStationAcceptPolicy failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (acceptPolicy > AcceptPolicy.WhiteList)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: SetStationAcceptPolicy failed - invalid accept policy ===");
                return ResultCode.InvalidArgument;
            }

            if (_state == NetworkState.AccessPoint || _state == NetworkState.AccessPointCreated)
            {
                Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: SetStationAcceptPolicy completed ===");
                return _accessPoint.SetStationAcceptPolicy(acceptPolicy);
            }
            else
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: SetStationAcceptPolicy failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }
        }

        [CommandCmif(208)]
        // AddAcceptFilterEntry(bytes<6, 1> mac_address)
        public ResultCode AddAcceptFilterEntry(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: AddAcceptFilterEntry called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: AddAcceptFilterEntry failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            // TODO
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: AddAcceptFilterEntry - TODO not implemented ===");

            return ResultCode.Success;
        }

        [CommandCmif(209)]
        // ClearAcceptFilter()
        public ResultCode ClearAcceptFilter(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ClearAcceptFilter called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ClearAcceptFilter failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            // TODO
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ClearAcceptFilter - TODO not implemented ===");

            return ResultCode.Success;
        }

        [CommandCmif(300)]
        // OpenStation()
        public ResultCode OpenStation(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: OpenStation called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: OpenStation failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (_state != NetworkState.Initialized)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: OpenStation failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }

            CloseAccessPoint();

            SetState(NetworkState.Station);

            _station?.Dispose();
            _station = new Station(this);

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: OpenStation completed successfully ===");

            // NOTE: Calls nifm service and returns related result codes.
            //       Since we use our own implementation we can return ResultCode.Success.

            return ResultCode.Success;
        }

        [CommandCmif(301)]
        // CloseStation()
        public ResultCode CloseStation(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CloseStation called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CloseStation failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (_state == NetworkState.Station || _state == NetworkState.StationConnected)
            {
                DisconnectImpl(DisconnectReason.DisconnectedByUser);
            }
            else
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: CloseStation failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }

            SetState(NetworkState.Initialized);

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CloseStation completed successfully ===");
            return ResultCode.Success;
        }

        private void CloseStation()
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: CloseStation internal called ===");
            _station?.Dispose();
            _station = null;
        }

        [CommandCmif(302)]
        // Connect(bytes<0x44, 2> security_config, bytes<0x30, 1> user_config, u32 local_communication_version, u32 option_unknown, buffer<network_info<0x480>, 0x19>)
        public ResultCode Connect(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Connect called ===");
            return ConnectImpl(context);
        }

        [CommandCmif(303)]
        // ConnectPrivate(bytes<0x44, 2> security_config, bytes<0x20, 1> security_parameter, bytes<0x30, 1> user_config, u32 local_communication_version, u32 option_unknown, bytes<0x20, 8> network_config)
        public ResultCode ConnectPrivate(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ConnectPrivate called ===");
            return ConnectImpl(context, true);
        }

        private ResultCode ConnectImpl(ServiceCtx context, bool isPrivate = false)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ConnectImpl called, isPrivate: {isPrivate} ===");

            SecurityConfig securityConfig = context.RequestData.ReadStruct<SecurityConfig>();
            SecurityParameter securityParameter = isPrivate ? context.RequestData.ReadStruct<SecurityParameter>() : new SecurityParameter();

            UserConfig userConfig = context.RequestData.ReadStruct<UserConfig>();
            uint localCommunicationVersion = context.RequestData.ReadUInt32();
            uint optionUnknown = context.RequestData.ReadUInt32();

            NetworkConfig networkConfig = new();
            NetworkInfo networkInfo = new();

            if (isPrivate)
            {
                context.RequestData.ReadUInt32(); // Padding.

                networkConfig = context.RequestData.ReadStruct<NetworkConfig>();
                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ConnectImpl - Private network config: Channel={networkConfig.Channel} ===");
            }
            else
            {
                ulong bufferPosition = context.Request.PtrBuff[0].Position;
                ulong bufferSize = context.Request.PtrBuff[0].Size;

                byte[] networkInfoBytes = new byte[bufferSize];

                context.Memory.Read(bufferPosition, networkInfoBytes);

                networkInfo = MemoryMarshal.Read<NetworkInfo>(networkInfoBytes);
                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ConnectImpl - Public network info loaded, bufferSize: {bufferSize} ===");
            }

            if (networkInfo.NetworkId.IntentId.LocalCommunicationId == -1 && NetworkClient.NeedsRealId)
            {
                // TODO: Call nn::arp::GetApplicationControlProperty here when implemented.
                ApplicationControlProperty controlProperty = context.Device.Processes.ActiveApplication.ApplicationControlProperties;

                networkInfo.NetworkId.IntentId.LocalCommunicationId = (long)controlProperty.LocalCommunicationId[0];
                Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ConnectImpl - updated LocalCommunicationId: {networkInfo.NetworkId.IntentId.LocalCommunicationId} ===");
            }

            bool isLocalCommunicationIdValid = CheckLocalCommunicationIdPermission(context, (ulong)networkInfo.NetworkId.IntentId.LocalCommunicationId);
            if (!isLocalCommunicationIdValid && NetworkClient.NeedsRealId)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ConnectImpl - invalid LocalCommunicationId permission ===");
                return ResultCode.InvalidObject;
            }

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ConnectImpl failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            securityConfig.SecurityMode = CheckDevelopmentSecurityMode(securityConfig.SecurityMode);

            ResultCode resultCode = ResultCode.InvalidArgument;

            if (securityConfig.SecurityMode - 1 <= SecurityMode.Debug)
            {
                if (optionUnknown <= 1 && (localCommunicationVersion >> 15) == 0 && securityConfig.PassphraseSize <= 64)
                {
                    resultCode = ResultCode.VersionTooLow;
                    if (localCommunicationVersion >= 0)
                    {
                        resultCode = ResultCode.VersionTooHigh;
                        if (localCommunicationVersion <= short.MaxValue)
                        {
                            if (_state != NetworkState.Station)
                            {
                                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ConnectImpl failed - invalid state: {_state} ===");
                                resultCode = ResultCode.InvalidState;
                            }
                            else
                            {
                                if (isPrivate)
                                {
                                    Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ConnectImpl - connecting to private network ===");
                                    resultCode = _station.ConnectPrivate(securityConfig, securityParameter, userConfig, localCommunicationVersion, optionUnknown, networkConfig);
                                }
                                else
                                {
                                    Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: ConnectImpl - connecting to public network ===");
                                    resultCode = _station.Connect(securityConfig, userConfig, localCommunicationVersion, optionUnknown, networkInfo);
                                }
                            }
                        }
                    }
                }
            }

            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: ConnectImpl completed with result: {resultCode} ===");
            return resultCode;
        }

        [CommandCmif(304)]
        // Disconnect()
        public ResultCode Disconnect(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Disconnect called ===");
            return DisconnectImpl(DisconnectReason.DisconnectedByUser);
        }

        private ResultCode DisconnectImpl(DisconnectReason disconnectReason)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: DisconnectImpl called, reason: {disconnectReason} ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: DisconnectImpl failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            if (disconnectReason <= DisconnectReason.DisconnectedBySystem)
            {
                if (_state == NetworkState.StationConnected)
                {
                    SetState(NetworkState.Station);

                    CloseStation();

                    _disconnectReason = disconnectReason;

                    Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: DisconnectImpl completed successfully ===");
                    return ResultCode.Success;
                }

                CloseStation();

                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: DisconnectImpl failed - invalid state: {_state} ===");
                return ResultCode.InvalidState;
            }

            Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: DisconnectImpl failed - invalid disconnect reason ===");
            return ResultCode.InvalidArgument;
        }

        [CommandCmif(400)]
        // InitializeOld(pid)
        public ResultCode InitializeOld(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: InitializeOld called ===");
            return InitializeImpl(context, context.Process.Pid, NifmRequestID);
        }

        [CommandCmif(401)]
        // Finalize()
        public ResultCode Finalize(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Finalize called ===");

            if (_nifmResultCode != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: Finalize failed due to NIFM: {_nifmResultCode} ===");
                return _nifmResultCode;
            }

            // NOTE: Use true when its called in nn::ldn::detail::ISystemLocalCommunicationService
            ResultCode resultCode = FinalizeImpl(false);
            if (resultCode == ResultCode.Success)
            {
                SetDisconnectReason(DisconnectReason.None);
            }

            if (_stateChangeEventHandle != 0)
            {
                context.Process.HandleTable.CloseHandle(_stateChangeEventHandle);
                _stateChangeEventHandle = 0;
            }

            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: Finalize completed with result: {resultCode} ===");
            return resultCode;
        }

        private ResultCode FinalizeImpl(bool isCausedBySystem)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: FinalizeImpl called, isCausedBySystem: {isCausedBySystem}, current state: {_state} ===");

            DisconnectReason disconnectReason;

            switch (_state)
            {
                case NetworkState.None:
                    Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: FinalizeImpl - state is None, nothing to do ===");
                    return ResultCode.Success;
                case NetworkState.AccessPoint:
                    {
                        Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: FinalizeImpl - closing AccessPoint ===");
                        CloseAccessPoint();

                        break;
                    }
                case NetworkState.AccessPointCreated:
                    {
                        if (isCausedBySystem)
                        {
                            disconnectReason = DisconnectReason.DestroyedBySystem;
                        }
                        else
                        {
                            disconnectReason = DisconnectReason.DestroyedByUser;
                        }

                        Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: FinalizeImpl - destroying network, reason: {disconnectReason} ===");
                        DestroyNetworkImpl(disconnectReason);

                        break;
                    }
                case NetworkState.Station:
                    {
                        Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: FinalizeImpl - closing Station ===");
                        CloseStation();

                        break;
                    }
                case NetworkState.StationConnected:
                    {
                        if (isCausedBySystem)
                        {
                            disconnectReason = DisconnectReason.DisconnectedBySystem;
                        }
                        else
                        {
                            disconnectReason = DisconnectReason.DisconnectedByUser;
                        }

                        Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: FinalizeImpl - disconnecting, reason: {disconnectReason} ===");
                        DisconnectImpl(disconnectReason);

                        break;
                    }
            }

            SetState(NetworkState.None);

            NetworkClient?.Dispose();
            NetworkClient = null;

            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: FinalizeImpl completed successfully ===");
            return ResultCode.Success;
        }

        [CommandCmif(402)] // 7.0.0+
        // Initialize(u64 ip_addresses, pid)
        public ResultCode Initialize(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Initialize called ===");

            _ = new IPAddress(context.RequestData.ReadUInt32());
            _ = new IPAddress(context.RequestData.ReadUInt32());

            // NOTE: It seems the guest can get ip_address and subnet_mask from nifm service and pass it through the initialize.
            //       This calls InitializeImpl() twice: The first time with NIFM_REQUEST_ID, and if it fails, a second time with nifm_request_id = 1.

            return InitializeImpl(context, context.Process.Pid, NifmRequestID);
        }

        public ResultCode InitializeImpl(ServiceCtx context, ulong pid, int nifmRequestId)
        {
            Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: InitializeImpl called, pid: {pid}, nifmRequestId: {nifmRequestId} ===");

            ResultCode resultCode = ResultCode.InvalidArgument;

            if (nifmRequestId <= 255)
            {
                if (_state != NetworkState.Initialized)
                {
                    // NOTE: Service calls nn::ldn::detail::NetworkInterfaceManager::NetworkInterfaceMonitor::Initialize() with nifmRequestId as argument,
                    //       then it stores the result code of it in a global variable. Since we use our own implementation, we can just check the connection
                    //       and return related error codes.
                    if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                    {
                        MultiplayerMode mode = context.Device.Configuration.MultiplayerMode;

                        Logger.Info?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: Initializing with multiplayer mode: {mode} ===");

                        switch (mode)
                        {
                            case MultiplayerMode.LdnMitm:
                                NetworkClient = new LdnMitmClient(context.Device.Configuration);
                                Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Using LdnMitmClient ===");
                                break;
                            case MultiplayerMode.Disabled:
                                NetworkClient = new LdnDisabledClient();
                                Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: Using LdnDisabledClient ===");
                                break;
                        }

                        // TODO: Call nn::arp::GetApplicationLaunchProperty here when implemented.
                        NetworkClient.SetGameVersion(context.Device.Processes.ActiveApplication.ApplicationControlProperties.DisplayVersion.Items.ToArray());

                        resultCode = ResultCode.Success;

                        _nifmResultCode = resultCode;

                        SetState(NetworkState.Initialized);
                        
                        Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: InitializeImpl completed successfully ===");
                    }
                    else
                    {
                        // NOTE: Service returns different ResultCode here related to the nifm ResultCode.
                        resultCode = ResultCode.DeviceDisabled;
                        _nifmResultCode = resultCode;
                        Logger.Warning?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: InitializeImpl failed - network not available ===");
                    }
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: InitializeImpl failed - already initialized, state: {_state} ===");
                }
            }
            else
            {
                Logger.Warning?.Print(LogClass.ServiceLdn, $"=== LDN DEBUG: InitializeImpl failed - invalid nifmRequestId: {nifmRequestId} ===");
            }

            return resultCode;
        }

        public void Dispose()
        {
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: IUserLocalCommunicationService Dispose called ===");
            
            _station?.Dispose();
            _station = null;

            _accessPoint?.Dispose();
            _accessPoint = null;

            NetworkClient?.Dispose();
            NetworkClient = null;
            
            Logger.Info?.Print(LogClass.ServiceLdn, "=== LDN DEBUG: IUserLocalCommunicationService Dispose completed ===");
        }
    }
}
