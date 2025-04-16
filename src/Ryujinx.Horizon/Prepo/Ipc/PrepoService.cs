using MessagePack;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Prepo.Types;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.Horizon.Sdk.Arp;
using Ryujinx.Horizon.Sdk.Prepo;
using Ryujinx.Horizon.Sdk.Sf;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using System;
using System.Text;
using ApplicationId = Ryujinx.Horizon.Sdk.Ncm.ApplicationId;

namespace Ryujinx.Horizon.Prepo.Ipc
{
    partial class PrepoService : IPrepoService
    {
        enum PlayReportKind
        {
            Normal,
            System,
        }

        private readonly ArpApi _arp;
        private readonly PrepoServicePermissionLevel _permissionLevel;
        private ulong _systemSessionId;

        private bool _immediateTransmissionEnabled;
        private bool _userAgreementCheckEnabled = true;

        public PrepoService(ArpApi arp, PrepoServicePermissionLevel permissionLevel)
        {
            _arp = arp;
            _permissionLevel = permissionLevel;
        }

        [CmifCommand(10100)] // 1.0.0-5.1.0
        [CmifCommand(10102)] // 6.0.0-9.2.0
        [CmifCommand(10104)] // 10.0.0+
        public Result SaveReport([Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<byte> gameRoomBuffer, [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> reportBuffer, [ClientProcessId] ulong pid)
        {
            if ((_permissionLevel & PrepoServicePermissionLevel.User) == 0)
            {
                return PrepoResult.PermissionDenied;
            }

            ProcessPlayReport(PlayReportKind.Normal, gameRoomBuffer, reportBuffer, pid, Uid.Null);

            return Result.Success;
        }

        [CmifCommand(10101)] // 1.0.0-5.1.0
        [CmifCommand(10103)] // 6.0.0-9.2.0
        [CmifCommand(10105)] // 10.0.0+
        public Result SaveReportWithUser(Uid userId, [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<byte> gameRoomBuffer, [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> reportBuffer, [ClientProcessId] ulong pid)
        {
            if ((_permissionLevel & PrepoServicePermissionLevel.User) == 0)
            {
                return PrepoResult.PermissionDenied;
            }

            ProcessPlayReport(PlayReportKind.Normal, gameRoomBuffer, reportBuffer, pid, userId, true);

            return Result.Success;
        }

        [CmifCommand(10200)]
        public Result RequestImmediateTransmission()
        {
            _immediateTransmissionEnabled = true;

            return Result.Success;
        }

        [CmifCommand(10300)]
        public Result GetTransmissionStatus(out int status)
        {
            status = 0;

            if (_immediateTransmissionEnabled && _userAgreementCheckEnabled)
            {
                status = 1;
            }

            return Result.Success;
        }

        [CmifCommand(10400)] // 9.0.0+
        public Result GetSystemSessionId(out ulong systemSessionId)
        {
            systemSessionId = default;

            if ((_permissionLevel & PrepoServicePermissionLevel.User) == 0)
            {
                return PrepoResult.PermissionDenied;
            }

            if (_systemSessionId == 0)
            {
                _systemSessionId = (ulong)Random.Shared.NextInt64();
            }

            systemSessionId = _systemSessionId;

            return Result.Success;
        }

        [CmifCommand(20100)]
        public Result SaveSystemReport([Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<byte> gameRoomBuffer, ApplicationId applicationId, [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> reportBuffer)
        {
            if ((_permissionLevel & PrepoServicePermissionLevel.System) != 0)
            {
                return PrepoResult.PermissionDenied;
            }

            return ProcessPlayReport(PlayReportKind.System, gameRoomBuffer, reportBuffer, 0, Uid.Null, false, applicationId);
        }

        [CmifCommand(20101)]
        public Result SaveSystemReportWithUser(Uid userId, [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<byte> gameRoomBuffer, ApplicationId applicationId, [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> reportBuffer)
        {
            if ((_permissionLevel & PrepoServicePermissionLevel.System) != 0)
            {
                return PrepoResult.PermissionDenied;
            }

            return ProcessPlayReport(PlayReportKind.System, gameRoomBuffer, reportBuffer, 0, userId, true, applicationId);
        }

        [CmifCommand(40100)] // 2.0.0+
        public Result IsUserAgreementCheckEnabled(out bool enabled)
        {
            enabled = false;

            if (_permissionLevel == PrepoServicePermissionLevel.User || _permissionLevel == PrepoServicePermissionLevel.System)
            {
                enabled = _userAgreementCheckEnabled;
                return Result.Success;
            }

            return PrepoResult.PermissionDenied;
        }

        [CmifCommand(40101)] // 2.0.0+
        public Result SetUserAgreementCheckEnabled(bool enabled)
        {
            if (_permissionLevel == PrepoServicePermissionLevel.User || _permissionLevel == PrepoServicePermissionLevel.System)
            {
                _userAgreementCheckEnabled = enabled;
                return Result.Success;
            }

            return PrepoResult.PermissionDenied;
        }

        private Result ProcessPlayReport(PlayReportKind playReportKind, ReadOnlySpan<byte> gameRoomBuffer, ReadOnlySpan<byte> reportBuffer, ulong pid, Uid userId, bool withUserId = false, ApplicationId applicationId = default)
        {
            if (withUserId && userId.IsNull)
            {
                return PrepoResult.InvalidArgument;
            }

            if (gameRoomBuffer.Length > 31)
            {
                return PrepoResult.InvalidArgument;
            }

            string gameRoom = Encoding.UTF8.GetString(gameRoomBuffer).TrimEnd();
            if (string.IsNullOrEmpty(gameRoom))
            {
                return PrepoResult.InvalidState;
            }

            if (reportBuffer.Length == 0)
            {
                return PrepoResult.InvalidBufferSize;
            }

            var builder = new StringBuilder();
            builder.AppendLine("\nPlayReport log:");
            builder.AppendLine($" Kind: {playReportKind}");

            if (pid != 0)
            {
                builder.AppendLine($" Pid: {pid}");
            }
            else
            {
                builder.AppendLine($" ApplicationId: {applicationId}");
            }

            Result result = _arp.GetApplicationInstanceId(out ulong applicationInstanceId, pid);
            if (result.IsFailure)
            {
                return PrepoResult.InvalidPid;
            }

            _arp.GetApplicationLaunchProperty(out ApplicationLaunchProperty applicationLaunchProperty, applicationInstanceId).AbortOnFailure();
            builder.AppendLine($" ApplicationVersion: {applicationLaunchProperty.Version}");

            if (!userId.IsNull)
            {
                builder.AppendLine($" UserId: {userId}");
            }

            builder.AppendLine($" Room: {gameRoom}");
            
            // 调用新的MessagePack格式化工具
            builder.AppendLine($" Report: {MessagePackFormatter.Format(reportBuffer.ToArray())}");

            Logger.Info?.Print(LogClass.ServicePrepo, builder.ToString());

            return Result.Success;
        }
    }
}
