using Ryujinx.HLE.HOS.Services.Ldn.UserServiceCreator;

namespace Ryujinx.HLE.HOS.Services.Ldn
{
    [Service("ldn:u")]
    class IUserServiceCreator : IpcService
    {
        public IUserServiceCreator(ServiceCtx context) : base(context.Device.System.LdnServer) { }

        [CommandCmif(0)]
        // CreateUserLocalCommunicationService() -> object<nn::ldn::detail::IUserLocalCommunicationService>
        public ResultCode CreateUserLocalCommunicationService(ServiceCtx context)
        {
            // +++ 强制禁用网络服务 +++
            // 直接返回服务不可用错误码，阻止任何网络服务创建
            return ResultCode.ServiceNotAvailable; // 错误码 0x415
            
            // 以下是原始代码（已禁用）
            /*
            MakeObject(context, new IUserLocalCommunicationService(context));
            return ResultCode.Success;
            */
        }
    }
}
