using Ryujinx.HLE.HOS.Services.Ldn.UserServiceCreator;
using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Services.Ldn
{
    [Service("ldn:u")]
    class IUserServiceCreator : IpcService
    {
        public IUserServiceCreator(ServiceCtx context) : base(context.Device.System.LdnServer) { }

        [CommandCmif(0)]
        public ResultCode CreateUserLocalCommunicationService(ServiceCtx context)
        {
            // 允许创建服务但内部模拟无网络状态
            MakeObject(context, new IUserLocalCommunicationService(context));
            Logger.Info?.Print(LogClass.ServiceLdn, "网络服务创建成功（模拟禁用状态）");
            return ResultCode.Success;
        }
    }
}
