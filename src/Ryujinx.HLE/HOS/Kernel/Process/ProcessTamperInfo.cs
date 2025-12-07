using System.Collections.Generic;
using System.Linq;
using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    internal class ProcessTamperInfo  // 改回 internal
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
        
        // 计算主NSO相对于ASLR的偏移
        public ulong MainNsoOffsetFromAslr => MainNsoBase - AslrAddress;
        
        // 计算主NSO相对于固定codeStart的偏移
        public ulong MainNsoOffsetFromFixed => MainNsoBase - FixedCodeStart;
        
        // NCE模式判断
        public bool IsLikelyNceMode => AslrAddress > 0x100000000UL;
        
        // 金手指编译使用的exeAddress
        public ulong CheatCompileExeAddress => MainNsoBase;

        // 原有构造函数
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
        }
    }
}
