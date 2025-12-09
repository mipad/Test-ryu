using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Native Code Execution CPU code patch.
    /// </summary>
    public class NceCpuCodePatch
    {
        private readonly List<uint> _code;
        private readonly List<PatchTarget> _patchTargets;
        private readonly List<(ulong Address, uint[] Code)> _trampolines;

        private readonly struct PatchTarget
        {
            public readonly int TextIndex;
            public readonly int PatchStartIndex;
            public readonly int PatchBranchIndex;

            public PatchTarget(int textIndex, int patchStartIndex, int patchBranchIndex)
            {
                TextIndex = textIndex;
                PatchStartIndex = patchStartIndex;
                PatchBranchIndex = patchBranchIndex;
            }
        }

        /// <inheritdoc/>
        public ulong Size => BitUtils.AlignUp<ulong>((ulong)_code.Count * sizeof(uint), 0x1000UL);
        
        /// <summary>
        /// Gets the total size including trampolines.
        /// </summary>
        public ulong TotalSize => Size + (ulong)_trampolines.Count * 4 * sizeof(uint);

        public NceCpuCodePatch()
        {
            _code = new List<uint>();
            _patchTargets = new List<PatchTarget>();
            _trampolines = new List<(ulong Address, uint[] Code)>();
        }

        internal void AddCode(int textIndex, IEnumerable<uint> code)
        {
            int patchStartIndex = _code.Count;
            _code.AddRange(code);
            _patchTargets.Add(new PatchTarget(textIndex, patchStartIndex, _code.Count - 1));
        }

        /// <summary>
        /// Write all patches to memory.
        /// </summary>
        public void Write(IVirtualMemoryManager memoryManager, ulong patchAddress, ulong textAddress)
        {
            uint[] code = _code.ToArray();
            _trampolines.Clear();

            // 记录需要修改权限的页面
            var modifiedPages = new HashSet<ulong>();
            
            try
            {
                foreach (var patchTarget in _patchTargets)
                {
                    ulong instPatchStartAddress = patchAddress + (ulong)patchTarget.PatchStartIndex * sizeof(uint);
                    ulong instPatchBranchAddress = patchAddress + (ulong)patchTarget.PatchBranchIndex * sizeof(uint);
                    ulong instTextAddress = textAddress + (ulong)patchTarget.TextIndex * sizeof(uint);
                    
                    // 保存原始页面权限并设置为可写
                    ulong textPage = BitUtils.AlignDown<ulong>(instTextAddress, 0x1000);
                    modifiedPages.Add(textPage);
                    
                    // 计算跳转偏移
                    long forwardOffset = (long)instPatchStartAddress - (long)instTextAddress;
                    long returnOffset = (long)(instTextAddress + 4) - (long)instPatchBranchAddress;
                    
                    // 检查偏移是否在范围内
                    bool forwardInRange = Math.Abs(forwardOffset) < (1 << 27);
                    bool returnInRange = Math.Abs(returnOffset) < (1 << 27);
                    
                    if (!forwardInRange || !returnInRange)
                    {
                        // 使用跳板（trampoline）进行间接跳转
                        ulong trampolineAddress = textAddress + (ulong)((_patchTargets.Count + _trampolines.Count) * 0x1000);
                        uint[] trampolineCode = CreateTrampoline(instPatchStartAddress, trampolineAddress);
                        _trampolines.Add((trampolineAddress, trampolineCode));
                        
                        // 修改原始指令，跳转到跳板
                        long trampolineOffset = (long)trampolineAddress - (long)instTextAddress;
                        if (Math.Abs(trampolineOffset) < (1 << 27))
                        {
                            memoryManager.Write(instTextAddress, 0x14000000u | EncodeSImm26_2(checked((int)trampolineOffset)));
                        }
                        else
                        {
                            // 如果跳板距离也超出范围，使用间接跳转
                            memoryManager.Write(instTextAddress, CreateIndirectJump(instTextAddress, trampolineAddress));
                        }
                        
                        // 返回跳转使用直接跳转或间接跳转
                        if (returnInRange)
                        {
                            code[patchTarget.PatchBranchIndex] = 0x14000000u | EncodeSImm26_2(checked((int)returnOffset));
                        }
                        else
                        {
                            code[patchTarget.PatchBranchIndex] = CreateIndirectJump(instPatchBranchAddress, instTextAddress + 4);
                        }
                    }
                    else
                    {
                        // 使用直接跳转
                        code[patchTarget.PatchBranchIndex] |= EncodeSImm26_2(checked((int)returnOffset));
                        memoryManager.Write(instTextAddress, 0x14000000u | EncodeSImm26_2(checked((int)forwardOffset)));
                    }
                }

                // 写入主补丁代码
                if (Size != 0)
                {
                    memoryManager.Write(patchAddress, MemoryMarshal.Cast<uint, byte>(code));
                }
                
                // 写入跳板代码
                foreach (var trampoline in _trampolines)
                {
                    memoryManager.Write(trampoline.Address, MemoryMarshal.Cast<uint, byte>(trampoline.Code));
                }
                
                // 设置内存权限
                memoryManager.Reprotect(patchAddress, Size, MemoryPermission.ReadAndExecute);
                foreach (var trampoline in _trampolines)
                {
                    memoryManager.Reprotect(trampoline.Address, (ulong)trampoline.Code.Length * sizeof(uint), MemoryPermission.ReadAndExecute);
                }
            }
            finally
            {
                // 恢复原始页面的权限
                foreach (var page in modifiedPages)
                {
                    memoryManager.Reprotect(page, 0x1000, MemoryPermission.ReadAndExecute);
                }
            }
        }
        
        /// <summary>
        /// Write patches with automatic nearby allocation.
        /// </summary>
        public bool WriteNear(IVirtualMemoryManager memoryManager, ulong textAddress, out ulong patchAddress)
        {
            patchAddress = 0;
            
            // 尝试在textAddress附近分配补丁代码
            const ulong maxDistance = 0x8000000; // 128MB
            const ulong searchStep = 0x1000; // 4KB
            
            // 向上搜索
            for (ulong addr = textAddress; addr < textAddress + maxDistance; addr += searchStep)
            {
                if (TryWriteAtAddress(memoryManager, addr, textAddress))
                {
                    patchAddress = addr;
                    return true;
                }
            }
            
            // 向下搜索
            for (ulong addr = textAddress > maxDistance ? textAddress - maxDistance : 0; 
                 addr < textAddress; 
                 addr += searchStep)
            {
                if (TryWriteAtAddress(memoryManager, addr, textAddress))
                {
                    patchAddress = addr;
                    return true;
                }
            }
            
            return false;
        }
        
        private bool TryWriteAtAddress(IVirtualMemoryManager memoryManager, ulong patchAddress, ulong textAddress)
        {
            try
            {
                // 检查该地址区域是否可用
                for (ulong i = 0; i < Size; i += 0x1000)
                {
                    if (memoryManager.IsRangeMapped(patchAddress + i, 0x1000))
                    {
                        return false;
                    }
                }
                
                Write(memoryManager, patchAddress, textAddress);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        private uint[] CreateTrampoline(ulong targetAddress, ulong trampolineAddress)
        {
            // 创建跳板代码：跳转到目标地址
            long offset = (long)targetAddress - (long)trampolineAddress;
            
            if (Math.Abs(offset) < (1 << 27))
            {
                // 直接跳转
                return new uint[] 
                { 
                    0x14000000u | EncodeSImm26_2(checked((int)offset))
                };
            }
            else
            {
                // 间接跳转
                return new uint[]
                {
                    0x58000051u, // LDR X17, [PC, #8]
                    0xD61F0220u, // BR X17
                    (uint)(targetAddress & 0xFFFFFFFF),
                    (uint)(targetAddress >> 32)
                };
            }
        }
        
        private uint CreateIndirectJump(ulong fromAddress, ulong toAddress)
        {
            // LDR X17, [PC, #8] + BR X17 + target address
            return 0x58000051u; // LDR X17, [PC, #8]
        }

        private static uint EncodeSImm26_2(int value)
        {
            uint imm = (uint)(value >> 2) & 0x3ffffff;
            Debug.Assert(((int)imm << 6) >> 4 == value, $"Failed to encode constant 0x{value:X}.");
            return imm;
        }
    }
}
