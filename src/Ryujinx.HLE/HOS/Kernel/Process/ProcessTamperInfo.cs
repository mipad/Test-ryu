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
        
        // 计算主NSO相对于ASLR的偏移
        public ulong MainNsoOffsetFromAslr => MainNsoBase - AslrAddress;
        
        // 计算主NSO相对于固定codeStart的偏移
        public ulong MainNsoOffsetFromFixed => MainNsoBase - FixedCodeStart;
        
        // 基于偏移的模式判断（更准确）
        public bool IsLikelyNceMode 
        {
            get
            {
                ulong offsetFromAslr = MainNsoOffsetFromAslr;
                
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
                
                // 默认根据地址高低判断（Android上可能不准确）
                // 但在Android上，ASLR地址大于4GB不一定就是NCE模式
                // 所以使用偏移作为主要判断依据
                return offsetFromAslr > 0x100000 && offsetFromAslr != 0x104000;
            }
        }
        
        // 金手指编译使用的exeAddress
        public ulong CheatCompileExeAddress => MainNsoBase;

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
            
            // 调试日志
            Logger.Info?.Print(LogClass.Loader,
                $"[ProcessTamperInfo] 创建完成: " +
                $"主NSO基址=0x{MainNsoBase:X}, " +
                $"ASLR基址=0x{AslrAddress:X}, " +
                $"偏移=0x{MainNsoOffsetFromAslr:X}, " +
                $"模式={(IsLikelyNceMode ? "NCE" : "JIT")}");
        }
        
        // 辅助方法：获取地址诊断信息
        public string GetAddressDiagnostics()
        {
            return $"地址诊断:\n" +
                   $"  ASLR基址: 0x{AslrAddress:X}\n" +
                   $"  主NSO基址: 0x{MainNsoBase:X}\n" +
                   $"  原始codeStart: 0x{FixedCodeStart:X}\n" +
                   $"  主NSO偏移ASLR: 0x{MainNsoOffsetFromAslr:X}\n" +
                   $"  预期JIT偏移: 0x104000\n" +
                   $"  预期NCE偏移: 0xE7000\n" +
                   $"  当前判断为NCE: {IsLikelyNceMode}";
        }
    }
}
