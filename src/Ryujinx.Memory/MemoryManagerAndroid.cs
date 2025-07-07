using Ryujinx.Memory; 
using Ryujinx.Common.Logging;

namespace Ryujinx.Memory 
{
    /// <summary>
    /// Android 平台专用的内存管理器，提供地址验证功能
    /// </summary>
    public class MemoryManagerAndroid : MemoryManagerBase
    {
        // 添加计数器用于调试
        private int _validationCount = 0;
        private int _invalidCount = 0;
        
        /// <summary>
        /// 验证内存地址是否在合法范围内
        /// </summary>
        /// <param name="address">要验证的内存地址</param>
        /// <returns>如果地址有效则返回 true，否则返回 false</returns>
        protected override bool ValidateAddress(ulong address)
        {
            _validationCount++;
            
            // ARM64地址空间验证 (0x0000_0000_0000_0000 - 0x0000_FFFF_FFFF_FFFF)
            if ((address >> 48) != 0)
            {
                _invalidCount++;
                
                // 记录警告日志（仅在启用日志时记录）
                Logger.Warning?.Print(LogClass.Memory, 
                    $"🚫 拦截无效内存地址访问: 0x{address:X16}" +
                    $"\n验证统计: 总验证={_validationCount}, 无效={_invalidCount}");
                
                return false;
            }
            
            // 每1000次验证记录一次统计信息
            if (_validationCount % 1000 == 0)
            {
                Logger.Info?.Print(LogClass.Memory, 
                    $"✅ 地址验证统计: 总数={_validationCount}, 无效={_invalidCount}");
            }
            
            return base.ValidateAddress(address);
        }
        
        /// <summary>
        /// 获取验证统计信息（用于调试）
        /// </summary>
        public (int Total, int Invalid) GetValidationStats()
        {
            return (_validationCount, _invalidCount);
        }
    }
}
