// 文件：ISyncActionHandler.cs
namespace Ryujinx.Graphics.Gpu.Synchronization
{
    /// <summary>
    /// 需要实现资源验证的同步操作接口
    /// </summary>
    public interface ISyncActionHandler
    {
        /// <summary>
        /// 新增：验证资源是否有效
        /// </summary>
        /// <returns>验证通过返回true，否则false</returns>
        bool ValidateResource();

        // 原有方法保持不变
        bool SyncAction(bool syncpoint);
        void SyncPreAction(bool syncpoint);
    }
}
