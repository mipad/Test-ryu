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
        
        // 主NSO基址
        public ulong MainNsoBase { get; }
        
        // 原始codeStart
        public ulong FixedCodeStart { get; }
        
        // 偏移计算
        public ulong MainNsoOffsetFromAslr => MainNsoBase - AslrAddress;
        
        // 模式判断：基于偏移
        public bool IsLikelyNceMode 
        {
            get
            {
                ulong offset = MainNsoOffsetFromAslr;
                // NCE模式典型偏移：0xE7000
                // JIT模式典型偏移：0x104000
                return offset == 0xE7000 || (offset > 0x100000 && offset != 0x104000);
            }
        }
        
        // 构造函数（兼容旧版本）
        public ProcessTamperInfo(KProcess process, IEnumerable<string> buildIds, IEnumerable<ulong> codeAddresses, 
                                ulong heapAddress, ulong aliasAddress, ulong aslrAddress)
            : this(process, buildIds, codeAddresses, heapAddress, aliasAddress, aslrAddress, 
                  codeAddresses?.FirstOrDefault() ?? 0, 0x8500000UL)
        {
        }
        
        // 新构造函数
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
            
            Logger.Info?.Print(LogClass.Loader,
                $"[ProcessTamperInfo] 创建: " +
                $"主NSO=0x{MainNsoBase:X}, " +
                $"ASLR=0x{AslrAddress:X}, " +
                $"偏移=0x{MainNsoOffsetFromAslr:X}, " +
                $"模式={(IsLikelyNceMode ? "NCE" : "JIT")}");
        }
    }
}
