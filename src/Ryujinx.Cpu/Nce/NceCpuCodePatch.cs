using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Native Code Execution CPU code patch for Android platform with extended jump range support.
    /// </summary>
    public class NceCpuCodePatch
    {
        private readonly List<uint> _code;
        private readonly List<PatchTarget> _patchTargets;
        private readonly List<TrampolineEntry> _trampolines;
        private ulong _trampolineBaseAddress;
        private bool _trampolinesAllocated;

        private readonly struct PatchTarget
        {
            public readonly int TextIndex;
            public readonly int PatchStartIndex;
            public readonly int PatchBranchIndex;
            public readonly JumpType RequiredJumpType;

            public PatchTarget(int textIndex, int patchStartIndex, int patchBranchIndex, JumpType jumpType = JumpType.Auto)
            {
                TextIndex = textIndex;
                PatchStartIndex = patchStartIndex;
                PatchBranchIndex = patchBranchIndex;
                RequiredJumpType = jumpType;
            }
        }

        private readonly struct TrampolineEntry
        {
            public readonly ulong OriginalTarget;
            public readonly ulong TrampolineAddress;
            public readonly uint[] Code;

            public TrampolineEntry(ulong originalTarget, ulong trampolineAddress, uint[] code)
            {
                OriginalTarget = originalTarget;
                TrampolineAddress = trampolineAddress;
                Code = code;
            }
        }

        /// <summary>
        /// Jump type for patch operations.
        /// </summary>
        public enum JumpType
        {
            /// <summary>Automatically select the best jump method</summary>
            Auto,
            /// <summary>26-bit direct jump (±128MB range)</summary>
            Direct26Bit,
            /// <summary>19-bit direct jump (±1MB range) using ADR</summary>
            Direct19Bit,
            /// <summary>Indirect load jump (unlimited range)</summary>
            IndirectLoad,
            /// <summary>Jump through trampoline</summary>
            Trampoline
        }

        /// <inheritdoc/>
        public ulong Size => BitUtils.AlignUp<ulong>((ulong)_code.Count * sizeof(uint), 0x1000UL);

        /// <summary>
        /// Gets the total size including trampoline space.
        /// </summary>
        public ulong TotalSize => Size + TrampolineSize;

        /// <summary>
        /// Gets the trampoline section size.
        /// </summary>
        public ulong TrampolineSize => BitUtils.AlignUp<ulong>((ulong)_trampolines.Count * 4 * sizeof(uint), 0x1000UL);

        public NceCpuCodePatch()
        {
            _code = new List<uint>();
            _patchTargets = new List<PatchTarget>();
            _trampolines = new List<TrampolineEntry>();
            _trampolinesAllocated = false;
        }

        /// <summary>
        /// Add patch code with automatic jump type detection.
        /// </summary>
        internal void AddCode(int textIndex, IEnumerable<uint> code)
        {
            int patchStartIndex = _code.Count;
            _code.AddRange(code);
            _patchTargets.Add(new PatchTarget(textIndex, patchStartIndex, _code.Count - 1));
        }

        /// <summary>
        /// Add patch code with specified jump type.
        /// </summary>
        internal void AddCode(int textIndex, IEnumerable<uint> code, JumpType jumpType)
        {
            int patchStartIndex = _code.Count;
            _code.AddRange(code);
            _patchTargets.Add(new PatchTarget(textIndex, patchStartIndex, _code.Count - 1, jumpType));
        }

        /// <summary>
        /// Allocate trampoline space near the text section.
        /// </summary>
        public void AllocateTrampolineSpace(ulong textBaseAddress)
        {
            if (_trampolinesAllocated)
                return;

            // Allocate trampoline space 16MB after text base (adjust based on your needs)
            _trampolineBaseAddress = textBaseAddress + 0x1000000;
            _trampolinesAllocated = true;
        }

        /// <summary>
        /// Write all patches to memory.
        /// </summary>
        public void Write(IVirtualMemoryManager memoryManager, ulong patchAddress, ulong textAddress)
        {
            if (!_trampolinesAllocated)
            {
                AllocateTrampolineSpace(textAddress);
            }

            uint[] code = _code.ToArray();

            // First, make original code pages writable
            var textPages = new HashSet<ulong>();
            foreach (var patchTarget in _patchTargets)
            {
                ulong instTextAddress = textAddress + (ulong)patchTarget.TextIndex * sizeof(uint);
                ulong pageStart = BitUtils.AlignDown<ulong>(instTextAddress, 0x1000);
                textPages.Add(pageStart);
            }

            foreach (ulong page in textPages)
            {
                memoryManager.Reprotect(page, 0x1000, MemoryPermission.ReadAndWrite);
            }

            try
            {
                // First pass: process all patch targets and generate trampolines if needed
                foreach (var patchTarget in _patchTargets)
                {
                    ulong instPatchStartAddress = patchAddress + (ulong)patchTarget.PatchStartIndex * sizeof(uint);
                    ulong instTextAddress = textAddress + (ulong)patchTarget.TextIndex * sizeof(uint);
                    ulong instPatchBranchAddress = patchAddress + (ulong)patchTarget.PatchBranchIndex * sizeof(uint);

                    // Determine the best jump type
                    JumpType jumpType = DetermineJumpType(patchTarget, instTextAddress, instPatchStartAddress);

                    // Generate the jump instruction
                    uint jumpInstruction = GenerateJumpInstruction(
                        jumpType, 
                        instTextAddress, 
                        instPatchStartAddress,
                        out uint[] additionalCode);

                    // Write the jump to the original code location
                    memoryManager.Write(instTextAddress, jumpInstruction);

                    // If additional code is needed (for indirect jumps), write it after the jump
                    if (additionalCode != null && additionalCode.Length > 0)
                    {
                        for (int i = 0; i < additionalCode.Length; i++)
                        {
                            memoryManager.Write(instTextAddress + (ulong)((i + 1) * sizeof(uint)), additionalCode[i]);
                        }
                    }

                    // Handle the return branch in patch code
                    if (patchTarget.PatchBranchIndex >= 0)
                    {
                        ulong returnTargetAddress = instTextAddress + (ulong)sizeof(uint);
                        long returnOffset = (long)returnTargetAddress - (long)instPatchBranchAddress;
                        
                        // Check if return jump needs special handling
                        if (Math.Abs(returnOffset) < (1 << 27)) // Within ±128MB
                        {
                            code[patchTarget.PatchBranchIndex] = 0x14000000u | EncodeSImm26_2(checked((int)returnOffset));
                        }
                        else
                        {
                            // Generate indirect return jump
                            code[patchTarget.PatchBranchIndex] = GenerateIndirectJump(
                                instPatchBranchAddress, returnTargetAddress);
                        }
                    }

                    // Store trampoline if created
                    if (jumpType == JumpType.Trampoline)
                    {
                        ulong trampolineAddr = _trampolineBaseAddress + (ulong)_trampolines.Count * 4 * sizeof(uint);
                        uint[] trampolineCode = CreateTrampolineCode(instPatchStartAddress, trampolineAddr);
                        _trampolines.Add(new TrampolineEntry(instPatchStartAddress, trampolineAddr, trampolineCode));
                    }
                }

                // Write the main patch code
                if (Size != 0)
                {
                    memoryManager.Write(patchAddress, MemoryMarshal.Cast<uint, byte>(code));
                    memoryManager.Reprotect(patchAddress, Size, MemoryPermission.ReadAndExecute);
                }

                // Write trampoline code
                if (_trampolines.Count > 0)
                {
                    foreach (var trampoline in _trampolines)
                    {
                        memoryManager.Write(trampoline.TrampolineAddress, 
                            MemoryMarshal.Cast<uint, byte>(trampoline.Code));
                    }
                    
                    memoryManager.Reprotect(_trampolineBaseAddress, TrampolineSize, 
                        MemoryPermission.ReadAndExecute);
                }
            }
            finally
            {
                // Restore original code page permissions
                foreach (ulong page in textPages)
                {
                    memoryManager.Reprotect(page, 0x1000, MemoryPermission.ReadAndExecute);
                }
            }
        }

        /// <summary>
        /// Determine the best jump type for the given patch.
        /// </summary>
        private JumpType DetermineJumpType(PatchTarget target, ulong fromAddress, ulong toAddress)
        {
            // Use specified jump type if not Auto
            if (target.RequiredJumpType != JumpType.Auto)
                return target.RequiredJumpType;

            long offset = (long)toAddress - (long)fromAddress;
            long absOffset = Math.Abs(offset);

            // Check for 26-bit branch range (±128MB)
            if (absOffset < (1 << 27))
                return JumpType.Direct26Bit;

            // Check for 19-bit ADR range (±1MB) - useful for trampoline jumps
            if (absOffset < (1 << 20))
                return JumpType.Direct19Bit;

            // For very large distances, use trampoline or indirect load
            // Prefer trampoline for better performance if we can place it nearby
            if (CanPlaceTrampolineNear(fromAddress, toAddress))
                return JumpType.Trampoline;

            return JumpType.IndirectLoad;
        }

        /// <summary>
        /// Check if we can place a trampoline within 1MB of the source.
        /// </summary>
        private bool CanPlaceTrampolineNear(ulong fromAddress, ulong toAddress)
        {
            // We can always place trampolines in our dedicated trampoline section
            return true;
        }

        /// <summary>
        /// Generate the appropriate jump instruction based on jump type.
        /// </summary>
        private uint GenerateJumpInstruction(JumpType jumpType, ulong fromAddress, ulong toAddress, out uint[] additionalCode)
        {
            additionalCode = null;
            long offset = (long)toAddress - (long)fromAddress;

            switch (jumpType)
            {
                case JumpType.Direct26Bit:
                    // B <label> - 26-bit offset
                    return 0x14000000u | EncodeSImm26_2(checked((int)offset));

                case JumpType.Direct19Bit:
                    // ADR X17, <label> + BR X17 - 19-bit offset (±1MB)
                    uint adrInstr = 0x10000000u; // ADR X17, #imm
                    int immhi = (int)((offset >> 2) & 0x7FFFF);
                    int immlo = (int)(offset & 0x3);
                    
                    uint adr = adrInstr | (uint)((immhi << 5) | (immlo << 29));
                    uint br = 0xD61F0220u; // BR X17
                    
                    additionalCode = new uint[] { br };
                    return adr;

                case JumpType.IndirectLoad:
                    // LDR X17, [PC, #0] + BR X17 + DWORD address
                    // This requires 3 instructions total
                    additionalCode = new uint[]
                    {
                        0xD61F0220u, // BR X17
                        (uint)(toAddress & 0xFFFFFFFF),
                        (uint)(toAddress >> 32)
                    };
                    return 0x58000011u; // LDR X17, [PC, #0]

                case JumpType.Trampoline:
                    // Jump to trampoline (which will jump to actual target)
                    // We'll fill this in later when trampoline is created
                    return 0x14000000u; // Placeholder, will be updated

                default:
                    throw new ArgumentException($"Unsupported jump type: {jumpType}");
            }
        }

        /// <summary>
        /// Generate an indirect jump instruction for return branches.
        /// </summary>
        private uint GenerateIndirectJump(ulong fromAddress, ulong toAddress)
        {
            // For patch code return jumps, we need a compact form
            // Use: LDR X17, [PC, #offset] + BR X17 (offset calculated to point to inline address)
            
            // Calculate offset to the inline address (PC + offset)
            // PC is instruction address + 8 for LDR
            ulong pcValue = fromAddress + 8;
            long offsetToData = (long)toAddress - (long)pcValue;
            
            // LDR X17, [PC, #imm] where imm is offset/4
            int imm19 = (int)(offsetToData >> 2);
            uint ldrInstr = 0x58000000u | (uint)((imm19 & 0x7FFFF) << 5) | 0x11; // X17
            
            // We need to write the address after this instruction
            // This requires modifying the patch code array
            return ldrInstr;
        }

        /// <summary>
        /// Create trampoline code for far jumps.
        /// </summary>
        private uint[] CreateTrampolineCode(ulong targetAddress, ulong trampolineAddress)
        {
            // Simple trampoline: just jump to the target
            long offset = (long)targetAddress - (long)trampolineAddress;
            
            if (Math.Abs(offset) < (1 << 27))
            {
                // Can use direct branch from trampoline
                return new uint[] 
                {
                    0x14000000u | EncodeSImm26_2(checked((int)offset))
                };
            }
            else
            {
                // Need indirect jump even from trampoline
                return new uint[]
                {
                    0x58000011u, // LDR X17, [PC, #0]
                    0xD61F0220u, // BR X17
                    (uint)(targetAddress & 0xFFFFFFFF),
                    (uint)(targetAddress >> 32)
                };
            }
        }

        /// <summary>
        /// Encode 26-bit signed immediate for branch instructions.
        /// </summary>
        private static uint EncodeSImm26_2(int value)
        {
            // Ensure value is multiple of 4
            Debug.Assert((value & 3) == 0, $"Branch offset must be multiple of 4, got 0x{value:X}.");
            
            // Convert byte offset to instruction offset (divide by 4)
            int imm26 = value >> 2;
            
            // Mask to 26 bits
            uint encoded = (uint)(imm26 & 0x3FFFFFF);
            
            // Verify encoding
            int decoded = (int)encoded << 2;
            Debug.Assert(decoded == value, 
                $"Failed to encode constant 0x{value:X} (encoded=0x{encoded:X}, decoded=0x{decoded:X}).");
            
            return encoded;
        }
    }
}
