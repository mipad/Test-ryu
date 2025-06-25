using Ryujinx.HLE.HOS.Services.Ldn.UserServiceCreator;

namespace Ryujinx.HLE.HOS.Services.Ldn
{
    // 定义本地错误码
    public enum LocalResultCode
    {
        ServiceNotAvailable = 0x415
    }

    [Service("ldn:u")]
    class IUserServiceCreator : IpcService
    {
        public IUserServiceCreator(ServiceCtx context) : base(context.Device.System.LdnServer) { }

        [CommandCmif(0)]
        public ResultCode CreateUserLocalCommunicationService(ServiceCtx context)
        {
            // 使用本地定义的错误码
            return (ResultCode)LocalResultCode.ServiceNotAvailable;
            
            // 原始代码（已禁用）
            /*
            MakeObject(context, new IUserLocalCommunicationService(context));
            return ResultCode.Success;
            */
        }
    }
}
