using Ryujinx.Common;
using Ryujinx.Memory;
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
        
        // 新增：占位符位置记录
        private readonly List<int> _placeholderIndices;

        private readonly struct PatchTarget
        {
            public readonly int TextIndex;
            public readonly int PatchStartIndex;
            public readonly int PatchBranchIndex;
            public readonly bool HasPlaceholder; // 新增：标记是否需要修复跳转

            public PatchTarget(int textIndex, int patchStartIndex, int patchBranchIndex, bool hasPlaceholder = false)
            {
                TextIndex = textIndex;
                PatchStartIndex = patchStartIndex;
                PatchBranchIndex = patchBranchIndex;
                HasPlaceholder = hasPlaceholder;
            }
        }

        private readonly List<PatchTarget> _patchTargets;

        /// <inheritdoc/>
        public ulong Size => BitUtils.AlignUp((ulong)_code.Count * sizeof(uint), 0x1000UL);

        public NceCpuCodePatch()
        {
            _code = new();
            _placeholderIndices = new();
            _patchTargets = new();
        }

        // 新增：支持占位符的AddCode重载
        internal void AddCode(int textIndex, IEnumerable<uint> code, bool hasPlaceholder = false)
        {
            int patchStartIndex = _code.Count;
            _code.AddRange(code);
            
            int patchBranchIndex = _code.Count - 1;
            
            // 如果有占位符，记录其位置
            if (hasPlaceholder)
            {
                _placeholderIndices.Add(patchBranchIndex);
            }
            
            _patchTargets.Add(new PatchTarget(textIndex, patchStartIndex, patchBranchIndex, hasPlaceholder));
        }

        // 保持原有API兼容性
        internal void AddCode(int textIndex, IEnumerable<uint> code)
        {
            AddCode(textIndex, code, false);
        }

        /// <inheritdoc/>
        public void Write(IVirtualMemoryManager memoryManager, ulong patchAddress, ulong textAddress)
        {
            uint[] code = _code.ToArray();
            int placeholderIdx = 0;

            for (int i = 0; i < _patchTargets.Count; i++)
            {
                var patchTarget = _patchTargets[i];
                ulong instPatchStartAddress = patchAddress + (ulong)patchTarget.PatchStartIndex * sizeof(uint);
                ulong instPatchBranchAddress = patchAddress + (ulong)patchTarget.PatchBranchIndex * sizeof(uint);
                ulong instTextAddress = textAddress + (ulong)patchTarget.TextIndex * sizeof(uint);

                // 修复占位符跳转
                if (patchTarget.HasPlaceholder && placeholderIdx < _placeholderIndices.Count)
                {
                    int placeholderIndex = _placeholderIndices[placeholderIdx];
                    if (placeholderIndex == patchTarget.PatchBranchIndex)
                    {
                        // 计算从补丁返回到原程序的偏移
                        ulong returnAddress = instTextAddress + sizeof(uint);
                        int returnOffset = checked((int)((long)returnAddress - (long)instPatchBranchAddress));
                        
                        code[placeholderIndex] = 0x14000000u | EncodeSImm26_2(returnOffset);
                        placeholderIdx++;
                    }
                }

                // 计算从原程序跳转到补丁的偏移
                int branchOffset = checked((int)((long)instPatchStartAddress - (long)instTextAddress));
                
                // 修改原指令，跳转到补丁
                memoryManager.Write(instTextAddress, 0x14000000u | EncodeSImm26_2(branchOffset));
            }

            if (Size != 0)
            {
                memoryManager.Write(patchAddress, MemoryMarshal.Cast<uint, byte>(code));
                memoryManager.Reprotect(patchAddress, Size, MemoryPermission.ReadAndExecute);
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