using System.Collections.Generic;
using System.Linq;
using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    internal class ProcessTamperInfo
    {
        public KProcess Process { get; }
        public IEnumerable<string> BuildIds { get; }
        public IEnumerable<ulong> CodeAddresses { get; }
        public ulong HeapAddress { get; }
        public ulong AliasAddress { get; }
        public ulong AslrAddress { get; }
        
        // 主NSO基址（实际加载地址）
        public ulong MainNsoBase { get; }
        
        // 固定的codeStart地址（用于计算偏移）
        public ulong FixedCodeStart { get; }
        
        // 第二个NSO基址（通常是金手指的目标）
        public ulong SecondNsoBase { get; }
        
        // 计算主NSO相对于ASLR的偏移
        public ulong MainNsoOffsetFromAslr => MainNsoBase - AslrAddress;
        
        // 计算第二个NSO相对于ASLR的偏移
        public ulong SecondNsoOffsetFromAslr => SecondNsoBase - AslrAddress;
        
        // 计算主NSO相对于固定codeStart的偏移
        public ulong MainNsoOffsetFromFixed => MainNsoBase - FixedCodeStart;
        
        // 基于偏移的模式判断（更准确）
        public bool IsLikelyNceMode 
        {
            get
            {
                // 如果有第二个NSO，使用第二个NSO的偏移判断（因为金手指通常基于第二个NSO）
                ulong offsetFromAslr = SecondNsoBase > 0 ? SecondNsoOffsetFromAslr : MainNsoOffsetFromAslr;
                
                // JIT模式下的典型偏移：0x104000（根据JIT工作日志）
                bool isJitOffset = offsetFromAslr == 0x104000;
                
                // NCE模式下的典型偏移：0xE7000（根据NCE日志）
                bool isNceOffset = offsetFromAslr == 0xE7000;
                
                // 如果偏移是典型的NCE偏移，则是NCE模式
                if (isNceOffset)
                {
                    return true;
                }
                
                // 如果偏移是典型的JIT偏移，则是JIT模式
                if (isJitOffset)
                {
                    return false;
                }
                
                // 默认根据地址高低判断
                return AslrAddress > 0x100000000UL;
            }
        }
        
        // 金手指编译使用的exeAddress（默认使用第二个NSO基址）
        public ulong CheatCompileExeAddress => SecondNsoBase > 0 ? SecondNsoBase : MainNsoBase;

        // 原有构造函数（向后兼容）
        public ProcessTamperInfo(KProcess process, IEnumerable<string> buildIds, IEnumerable<ulong> codeAddresses, 
                                ulong heapAddress, ulong aliasAddress, ulong aslrAddress)
            : this(process, buildIds, codeAddresses, heapAddress, aliasAddress, aslrAddress, 
                  codeAddresses?.FirstOrDefault() ?? 0, 0x8500000UL)
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
            
            // 计算第二个NSO基址（通常是金手指的目标）
            SecondNsoBase = codeAddresses?.ElementAtOrDefault(1) ?? 0;
            
            // 调试日志
            Logger.Info?.Print(LogClass.Loader,
                $"[ProcessTamperInfo] 创建完成: " +
                $"主NSO基址=0x{MainNsoBase:X}, " +
                $"第二个NSO基址=0x{SecondNsoBase:X}, " +
                $"ASLR基址=0x{AslrAddress:X}, " +
                $"偏移ASLR=0x{SecondNsoOffsetFromAslr:X}, " +
                $"模式={(IsLikelyNceMode ? "NCE" : "JIT")}, " +
                $"金手指基址=0x{CheatCompileExeAddress:X}");
        }
        
        // 辅助方法：获取地址诊断信息
        public string GetAddressDiagnostics()
        {
            return $"地址诊断:\n" +
                   $"  ASLR基址: 0x{AslrAddress:X}\n" +
                   $"  主NSO基址: 0x{MainNsoBase:X}\n" +
                   $"  第二个NSO基址: 0x{SecondNsoBase:X}\n" +
                   $"  原始codeStart: 0x{FixedCodeStart:X}\n" +
                   $"  第二个NSO偏移ASLR: 0x{SecondNsoOffsetFromAslr:X}\n" +
                   $"  预期JIT偏移: 0x104000\n" +
                   $"  预期NCE偏移: 0xE7000\n" +
                   $"  当前判断为NCE: {IsLikelyNceMode}\n" +
                   $"  金手指使用基址: 0x{CheatCompileExeAddress:X}";
        }
    }
}
