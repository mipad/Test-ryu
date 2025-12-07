using System.Collections.Generic;
using System.Linq;
using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    public class ProcessTamperInfo  // 将 class 改为 public
    {
        public KProcess Process { get; }
        public IEnumerable<string> BuildIds { get; }
        public IEnumerable<ulong> CodeAddresses { get; }
        public ulong HeapAddress { get; }
        public ulong AliasAddress { get; }
        public ulong AslrAddress { get; }
        
        // 新增：主NSO基址（包含NCE补丁偏移和ASLR调整）
        public ulong MainNsoBase { get; }
        
        // 新增：固定的codeStart地址（用于计算偏移）
        public ulong FixedCodeStart { get; }
        
        // 新增：第一个代码地址（用于兼容性）
        public ulong FirstCodeAddress => CodeAddresses?.FirstOrDefault() ?? 0;
        
        // 新增：计算主NSO相对于ASLR的偏移
        public ulong MainNsoOffsetFromAslr => MainNsoBase - AslrAddress;
        
        // 新增：计算主NSO相对于固定codeStart的偏移
        public ulong MainNsoOffsetFromFixed => MainNsoBase - FixedCodeStart;
        
        // 新增：NCE模式判断
        public bool IsLikelyNceMode => AslrAddress > 0x100000000UL; // ASLR地址大于4GB可能是NCE模式
        
        // 新增：金手指编译使用的exeAddress
        public ulong CheatCompileExeAddress
        {
            get
            {
                // 关键：对于金手指编译，我们需要使用主NSO基址
                // 因为金手指指令中的地址是基于固定偏移的
                // 在NCE模式下，主NSO基址已经包含了所有偏移
                return MainNsoBase;
            }
        }

        // 原有构造函数（为了兼容性保留）
        public ProcessTamperInfo(KProcess process, IEnumerable<string> buildIds, IEnumerable<ulong> codeAddresses, ulong heapAddress, ulong aliasAddress, ulong aslrAddress)
            : this(process, buildIds, codeAddresses, heapAddress, aliasAddress, aslrAddress, 
                  codeAddresses?.FirstOrDefault() ?? 0, 0x8500000UL) // 默认使用第一个代码地址和固定codeStart
        {
        }

        // 新增构造函数：明确指定主NSO基址和固定codeStart
        public ProcessTamperInfo(KProcess process, IEnumerable<string> buildIds, IEnumerable<ulong> codeAddresses, 
                                ulong heapAddress, ulong aliasAddress, ulong aslrAddress, 
                                ulong mainNsoBase, ulong fixedCodeStart)
        {
            Process = process;
            BuildIds = buildIds;
            CodeAddresses = codeAddresses;
            HeapAddress = heapAddress;
            AliasAddress = aliasAddress;
            AslrAddress = aslrAddress;
            MainNsoBase = mainNsoBase;
            FixedCodeStart = fixedCodeStart;
            
            // 记录详细的地址信息，用于调试
            Logger.Debug?.Print(LogClass.Tamper, 
                $"[ProcessTamperInfo] 创建完成: " +
                $"PID={Process.Pid}, " +
                $"ASLR=0x{AslrAddress:X}, " +
                $"主NSO基址=0x{MainNsoBase:X}, " +
                $"堆地址=0x{HeapAddress:X}, " +
                $"别名地址=0x{AliasAddress:X}, " +
                $"固定codeStart=0x{FixedCodeStart:X}");
            
            Logger.Debug?.Print(LogClass.Tamper,
                $"[ProcessTamperInfo] 偏移计算: " +
                $"主NSO偏移ASLR=0x{MainNsoOffsetFromAslr:X}, " +
                $"主NSO偏移固定=0x{MainNsoOffsetFromFixed:X}, " +
                $"金手指编译地址=0x{CheatCompileExeAddress:X}");
            
            if (IsLikelyNceMode)
            {
                Logger.Info?.Print(LogClass.Tamper,
                    $"[ProcessTamperInfo] 检测到NCE模式: ASLR地址显著大于4GB");
            }
            
            // 记录所有NSO地址
            if (CodeAddresses != null)
            {
                int index = 0;
                foreach (var addr in CodeAddresses)
                {
                    Logger.Debug?.Print(LogClass.Tamper,
                        $"[ProcessTamperInfo] NSO[{index}]: 0x{addr:X} " +
                        $"(偏移ASLR: 0x{addr - AslrAddress:X})");
                    index++;
                }
            }
        }
        
        // 新增：获取NSO基址
        public ulong GetNsoBase(int index)
        {
            if (CodeAddresses == null || index < 0)
                return 0;
            
            var addrList = CodeAddresses.ToArray();
            if (index >= addrList.Length)
                return 0;
            
            return addrList[index];
        }
        
        // 新增：获取NSO相对于ASLR的偏移
        public ulong GetNsoOffsetFromAslr(int index)
        {
            ulong addr = GetNsoBase(index);
            return addr > 0 ? addr - AslrAddress : 0;
        }
    }
}
