public class MemoryManagerAndroid : MemoryManagerBase
{
    // 添加计数器用于统计验证情况
    private int _totalValidations = 0;
    private int _invalidAddressCount = 0;

    protected override bool ValidateAddress(ulong address)
    {
        _totalValidations++;
        
        // ARM64地址空间验证 (0x0000_0000_0000_0000 - 0x0000_FFFF_FFFF_FFFF)
        if (address >> 48 != 0)
        {
            _invalidAddressCount++;
            
            Logger.Warning?.Print(LogClass.Memory, 
                $"🔥 检测到无效地址访问: 0x{address:X16}");
            Logger.Debug?.Print(LogClass.Memory,
                $"验证统计: 总验证次数={_totalValidations}, 无效地址={_invalidAddressCount}");
            
            // 记录调用堆栈（仅调试模式）
            #if DEBUG
            Logger.Debug?.Print(LogClass.Memory, 
                $"调用堆栈:\n{Environment.StackTrace}");
            #endif
            
            return false;
        }
        
        // 定期输出验证统计
        if (_totalValidations % 1000 == 0)
        {
            Logger.Info?.Print(LogClass.Memory, 
                $"✅ 地址验证统计: 总数={_totalValidations}, 无效={_invalidAddressCount} " +
                $"(无效率: {_invalidAddressCount * 100.0 / _totalValidations:F2}%)");
        }
        
        return base.ValidateAddress(address);
    }
    
    // 添加方法获取验证统计
    public (int Total, int Invalid) GetValidationStats()
    {
        return (_totalValidations, _invalidAddressCount);
    }
}
