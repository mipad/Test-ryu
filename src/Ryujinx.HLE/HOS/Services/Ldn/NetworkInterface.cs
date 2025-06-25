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
            // +++ 初始化为不可用状态 +++
            _state = NetworkState.Disabled;
            StateChangeEvent = new KEvent(system.KernelContext);
            StateChangeEvent.WritableEvent.Signal(); // 立即通知状态变化
        }

        public ResultCode Initialize(int unknown, int version, IPAddress ipv4Address, IPAddress subnetMaskAddress)
        {
            // +++ 返回成功但保持禁用状态 +++
            Logger.Info?.Print(LogClass.ServiceLdn, "Network service initialized (disabled)");
            return ResultCode.Success;
        }

        public ResultCode GetState(out NetworkState state)
        {
            // +++ 始终返回禁用状态 +++
            state = NetworkState.Disabled;
            return ResultCode.Success;
        }

        public ResultCode Finalize()
        {
            // +++ 简化清理过程 +++
            _state = NetworkState.Disabled;
            StateChangeEvent.WritableEvent.Signal();
            return ResultCode.Success;
        }
    }
}
