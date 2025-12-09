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

        public NceCpuCodePatch()
        {
            _code = new List<uint>();
            _patchTargets = new List<PatchTarget>();
        }

        internal void AddCode(int textIndex, IEnumerable<uint> code)
        {
            int patchStartIndex = _code.Count;
            _code.AddRange(code);
            _patchTargets.Add(new PatchTarget(textIndex, patchStartIndex, _code.Count - 1));
        }

        /// <inheritdoc/>
        public void Write(IVirtualMemoryManager memoryManager, ulong patchAddress, ulong textAddress)
        {
            // 首先确保patchAddress区域可写
            try
            {
                memoryManager.Reprotect(patchAddress, Size, MemoryPermission.ReadAndWrite);
            }
            catch
            {
                // 如果已经可写，忽略错误
            }

            uint[] code = _code.ToArray();

            try
            {
                foreach (var patchTarget in _patchTargets)
                {
                    ulong instPatchStartAddress = patchAddress + (ulong)patchTarget.PatchStartIndex * sizeof(uint);
                    ulong instPatchBranchAddress = patchAddress + (ulong)patchTarget.PatchBranchIndex * sizeof(uint);
                    ulong instTextAddress = textAddress + (ulong)patchTarget.TextIndex * sizeof(uint);

                    // 确保原始指令地址可写
                    try
                    {
                        memoryManager.Reprotect(instTextAddress, (ulong)sizeof(uint), MemoryPermission.ReadAndWrite);
                    }
                    catch
                    {
                        // 如果已经可写，忽略错误
                    }

                    try
                    {
                        // 计算跳转偏移
                        long forwardOffset = (long)instPatchStartAddress - (long)instTextAddress;
                        long returnOffset = (long)(instTextAddress + sizeof(uint)) - (long)instPatchBranchAddress;

                        // 检查偏移是否在范围内（±128MB）
                        bool forwardInRange = Math.Abs(forwardOffset) < (1 << 27);
                        bool returnInRange = Math.Abs(returnOffset) < (1 << 27);

                        if (forwardInRange && returnInRange)
                        {
                            // 直接跳转
                            code[patchTarget.PatchBranchIndex] |= EncodeSImm26_2(checked((int)returnOffset));
                            memoryManager.Write(instTextAddress, 0x14000000u | EncodeSImm26_2(checked((int)forwardOffset)));
                        }
                        else
                        {
                            // 使用间接跳转
                            WriteIndirectJump(memoryManager, instTextAddress, instPatchStartAddress);
                            WriteIndirectJumpInCode(code, patchTarget.PatchBranchIndex, instPatchBranchAddress, instTextAddress + sizeof(uint));
                        }
                    }
                    finally
                    {
                        // 恢复原始指令地址权限
                        try
                        {
                            memoryManager.Reprotect(instTextAddress, (ulong)sizeof(uint), MemoryPermission.ReadAndExecute);
                        }
                        catch
                        {
                            // 忽略错误
                        }
                    }
                }

                // 写入补丁代码
                if (Size != 0)
                {
                    memoryManager.Write(patchAddress, MemoryMarshal.Cast<uint, byte>(code));
                    memoryManager.Reprotect(patchAddress, Size, MemoryPermission.ReadAndExecute);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NceCpuCodePatch.Write failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 写入间接跳转指令到内存
        /// </summary>
        private void WriteIndirectJump(IVirtualMemoryManager memoryManager, ulong fromAddress, ulong toAddress)
        {
            // LDR X17, [PC, #8] + BR X17 + 目标地址
            uint ldrInstr = 0x58000051u; // LDR X17, [PC, #8]
            uint brInstr = 0xD61F0220u;  // BR X17
            
            memoryManager.Write(fromAddress, ldrInstr);
            memoryManager.Write(fromAddress + 4, brInstr);
            memoryManager.Write(fromAddress + 8, toAddress);
        }

        /// <summary>
        /// 在补丁代码中生成间接跳转指令
        /// </summary>
        private void WriteIndirectJumpInCode(uint[] code, int patchBranchIndex, ulong fromAddress, ulong toAddress)
        {
            // 计算偏移到内联数据
            ulong dataAddress = fromAddress + 8; // 跳过LDR和BR指令（各4字节）
            long offset = (long)toAddress - (long)dataAddress;
            
            // 编码LDR指令：LDR X17, [PC, #imm]
            int imm19 = (int)(offset >> 2);
            uint ldrInstr = 0x58000000u | (0x11u << 5) | ((uint)imm19 & 0x7FFFFu);
            uint brInstr = 0xD61F0220u; // BR X17
            
            // 将指令写入补丁代码
            code[patchBranchIndex] = ldrInstr;
            
            // 确保有空间存储BR指令和目标地址
            if (patchBranchIndex + 3 < code.Length)
            {
                code[patchBranchIndex + 1] = brInstr;
                code[patchBranchIndex + 2] = (uint)(toAddress & 0xFFFFFFFF);
                code[patchBranchIndex + 3] = (uint)(toAddress >> 32);
            }
        }

        private static uint EncodeSImm26_2(int value)
        {
            uint imm = (uint)(value >> 2) & 0x3ffffff;
            Debug.Assert(((int)imm << 6) >> 4 == value, $"Failed to encode constant 0x{value:X}.");
            return imm;
        }
    }
}
