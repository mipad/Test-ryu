using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Ldn.Types;
using System.Net;

namespace Ryujinx.HLE.HOS.Services.Ldn
{
    internal class NetworkInterface
    {
        public ResultCode NifmState { get; set; }
        public KEvent StateChangeEvent { get; private set; }

        private NetworkState _state;

        public NetworkInterface(Horizon system)
        {
            // 初始化为 None 状态（表示网络不可用）
            _state = NetworkState.None;
            StateChangeEvent = new KEvent(system.KernelContext);
            StateChangeEvent.WritableEvent.Signal(); // 立即通知状态变化
        }

        public ResultCode Initialize(int unknown, int version, IPAddress ipv4Address, IPAddress subnetMaskAddress)
        {
            // 返回成功但保持 None 状态
            Logger.Info?.Print(LogClass.ServiceLdn, "网络服务初始化成功（模拟禁用状态）");
            return ResultCode.Success;
        }

        public ResultCode GetState(out NetworkState state)
        {
            // 始终返回 None 状态（网络不可用）
            state = NetworkState.None;
            return ResultCode.Success;
        }

        public ResultCode Finalize()
        {
            // 保持 None 状态
            _state = NetworkState.None;
            StateChangeEvent.WritableEvent.Signal();
            return ResultCode.Success;
        }
    }
}
