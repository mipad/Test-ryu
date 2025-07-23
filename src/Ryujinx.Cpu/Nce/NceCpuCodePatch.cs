using Ryujinx.Common;
//using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Native Code Execution CPU code patch with enhanced safety checks.
    /// </summary>
    public sealed class NceCpuCodePatch : IDisposable
    {
        private readonly List<uint> _code;
        private bool _disposed;

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

        private readonly List<PatchTarget> _patchTargets;

        public ulong Size => BitUtils.AlignUp((ulong)_code.Count * sizeof(uint), 0x1000UL);

        public NceCpuCodePatch()
        {
            _code = new List<uint>();
            _patchTargets = new List<PatchTarget>();
        }

        internal void AddCode(int textIndex, IEnumerable<uint> code)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NceCpuCodePatch));
            }

            if (textIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(textIndex));
            }

            int patchStartIndex = _code.Count;
            _code.AddRange(code ?? throw new ArgumentNullException(nameof(code)));
            _patchTargets.Add(new PatchTarget(textIndex, patchStartIndex, _code.Count - 1));
        }

        public void Write(IVirtualMemoryManager memoryManager, ulong patchAddress, ulong textAddress)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NceCpuCodePatch));
            }

            if (memoryManager == null)
            {
                throw new ArgumentNullException(nameof(memoryManager));
            }

            // Critical address validation
            if (patchAddress == 0 || textAddress == 0)
            {
                //Logger.Error?.Print(LogClass.Cpu, 
                   // $"NULL address detected! Patch:0x{patchAddress:X}, Text:0x{textAddress:X}");
                throw new InvalidMemoryRegionException("NULL address is not allowed");
            }

            if (Size == 0)
            {
                //Logger.Warning?.Print(LogClass.Cpu, "Attempted to write empty code patch");
                return;
            }

            uint[] code = _code.ToArray();

            foreach (var patchTarget in _patchTargets)
            {
                ulong instPatchStartAddress = patchAddress + (ulong)patchTarget.PatchStartIndex * sizeof(uint);
                ulong instPatchBranchAddress = patchAddress + (ulong)patchTarget.PatchBranchIndex * sizeof(uint);
                ulong instTextAddress = textAddress + (ulong)patchTarget.TextIndex * sizeof(uint);

                try
                {
                    // Verify target memory range
                    memoryManager.ValidateAddress(instTextAddress, sizeof(uint));
                    memoryManager.ValidateAddress(instPatchStartAddress, sizeof(uint));
                    memoryManager.ValidateAddress(instPatchBranchAddress, sizeof(uint));

                    uint prevInst = memoryManager.Read<uint>(instTextAddress);

                    // Encode branch instruction with overflow check
                    int branchOffset = checked((int)((long)instTextAddress - (long)instPatchBranchAddress + sizeof(uint)));
                    code[patchTarget.PatchBranchIndex] |= EncodeSImm26_2(branchOffset);

                    int jumpOffset = checked((int)((long)instPatchStartAddress - (long)instTextAddress));
                    memoryManager.Write(instTextAddress, 0x14000000u | EncodeSImm26_2(jumpOffset));

                    // Verify the write
                    uint newInst = memoryManager.Read<uint>(instTextAddress);
                    if (newInst != (0x14000000u | EncodeSImm26_2(jumpOffset)))
                    {
                      //  Logger.Error?.Print(LogClass.Cpu, 
                           // $"Patch verification failed at 0x{instTextAddress:X}");
                        throw new MemoryPatchException("Patch application failed");
                    }
                }
                catch (Exception ex) when (ex is InvalidMemoryRegionException || ex is OverflowException)
                {
                   // Logger.Error?.Print(LogClass.Cpu, 
                      //  $"Failed to patch at 0x{instTextAddress:X}: {ex.Message}");
                    throw;
                }
            }

            try
            {
                memoryManager.Write(patchAddress, MemoryMarshal.Cast<uint, byte>(code));
                memoryManager.Reprotect(patchAddress, Size, MemoryPermission.ReadAndExecute);
            }
            catch (Exception ex)
            {
                //Logger.Error?.Print(LogClass.Cpu, 
                  //  $"Failed to write patch code at 0x{patchAddress:X}: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _code.Clear();
                _patchTargets.Clear();
                _disposed = true;
            }
        }

        [Conditional("DEBUG")]
        private static void DebugValidateOffset(int offset)
        {
            uint encoded = EncodeSImm26_2(offset);
            int decoded = (int)(encoded << 2);
            Debug.Assert(decoded == offset, $"Offset encoding mismatch: 0x{offset:X} -> 0x{decoded:X}");
        }

        private static uint EncodeSImm26_2(int value)
        {
            DebugValidateOffset(value);
            uint imm = (uint)(value >> 2) & 0x3FFFFFF;
            return imm;
        }
    }

    public class MemoryPatchException : Exception
    {
        public MemoryPatchException(string message) : base(message) { }
    }
}
