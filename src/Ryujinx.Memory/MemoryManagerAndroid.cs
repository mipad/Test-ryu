public class MemoryManagerAndroid : MemoryManagerBase
{
    protected override bool ValidateAddress(ulong address)
    {
        // ARM64地址空间验证 (0x0000_0000_0000_0000 - 0x0000_FFFF_FFFF_FFFF)
        if (address >> 48 != 0)
        {
            Logger.Warning?.Print(LogClass.Memory, 
                $"Invalid address access: 0x{address:X16}");
            return false;
        }
        return base.ValidateAddress(address);
    }
}
