using Ryujinx.Cpu.Nce.Arm64;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    public static class NcePatcher
    {
        private const int ScratchBaseReg = 19;
        private const uint IntCalleeSavedRegsMask = 0x1ff80000; // X19 to X28
        private const uint FpCalleeSavedRegsMask = 0xff00; // D8 to D15

        // ================== 优化部分：汇编器对象池 ==================
        private static readonly ConcurrentStack<Assembler> _assemblerPool = new();
        private const int MaxPoolSize = 4;

        private static Assembler RentAssembler()
        {
            if (_assemblerPool.TryPop(out var assembler))
            {
                ResetAssembler(assembler);
                return assembler;
            }
            return new Assembler();
        }

        private static void ReturnAssembler(Assembler assembler)
        {
            if (_assemblerPool.Count < MaxPoolSize)
            {
                _assemblerPool.Push(assembler);
            }
        }

        private static void ResetAssembler(Assembler assembler)
        {
            // 通过反射清空Assembler内部指令列表
            var field = typeof(Assembler).GetField("_instructions", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(System.Collections.Generic.List<uint>))
            {
                var list = (System.Collections.Generic.List<uint>)field.GetValue(assembler);
                list?.Clear();
            }
        }

        // ================== 优化部分：补丁模板缓存 ==================
        private enum PatchType : byte
        {
            Svc,
            MrsTpidrroEl0,
            MrsTpidrEl0,
            MrsCtrEl0,
            MrsCntpctEl0,
            MsrTpidrEl0
        }

        private record struct PatchCacheKey(PatchType Type, uint Parameter);

        private static readonly ConcurrentDictionary<PatchCacheKey, uint[]> _patchTemplateCache = new();

        private static uint[] GetOrCreatePatchTemplate(PatchType type, uint parameter)
        {
            var key = new PatchCacheKey(type, parameter);
            
            if (_patchTemplateCache.TryGetValue(key, out var template))
            {
#if DEBUG
                Logger.Debug?.Print(LogClass.Cpu, $"Template cache hit: {type} {parameter}");
#endif
                return template;
            }

#if DEBUG
            Logger.Debug?.Print(LogClass.Cpu, $"Template cache miss, generating: {type} {parameter}");
#endif
            
            var assembler = RentAssembler();
            try
            {
                uint[] templateCode = type switch
                {
                    PatchType.Svc => WriteSvcPatchTemplate(assembler, parameter),
                    PatchType.MrsTpidrroEl0 => WriteMrsTpidrroEl0PatchTemplate(assembler, parameter),
                    PatchType.MrsTpidrEl0 => WriteMrsTpidrEl0PatchTemplate(assembler, parameter),
                    PatchType.MrsCtrEl0 => WriteMrsCtrEl0PatchTemplate(assembler, parameter),
                    PatchType.MrsCntpctEl0 => WriteMrsCntpctEl0PatchTemplate(assembler, parameter),
                    PatchType.MsrTpidrEl0 => WriteMsrTpidrEl0PatchTemplate(assembler, parameter),
                    _ => throw new ArgumentOutOfRangeException(nameof(type))
                };

                // 缓存模板（不包含最后的返回跳转）
                var cachedTemplate = new uint[templateCode.Length - 1];
                Array.Copy(templateCode, cachedTemplate, cachedTemplate.Length);
                
                _patchTemplateCache[key] = cachedTemplate;
                return cachedTemplate;
            }
            finally
            {
                ReturnAssembler(assembler);
            }
        }

        private static uint[] CreateFinalPatch(uint[] template, int returnOffset)
        {
            var finalCode = new uint[template.Length + 1];
            Array.Copy(template, finalCode, template.Length);
            finalCode[^1] = 0x14000000u | EncodeSImm26_2(returnOffset);
            return finalCode;
        }

        // ================== 主要API（保持兼容） ==================
        public static NceCpuCodePatch CreatePatch(ReadOnlySpan<byte> textSection)
        {
            return CreatePatchOptimized(textSection);
        }

        private static NceCpuCodePatch CreatePatchOptimized(ReadOnlySpan<byte> textSection)
        {
            NceCpuCodePatch codePatch = new();
            var textUint = MemoryMarshal.Cast<byte, uint>(textSection);

            for (int i = 0; i < textUint.Length; i++)
            {
                uint inst = textUint[i];
                ulong address = (ulong)i * sizeof(uint);

                if ((inst & ~(0xffffu << 5)) == 0xd4000001u) // svc #0
                {
                    uint svcId = (ushort)(inst >> 5);
                    var template = GetOrCreatePatchTemplate(PatchType.Svc, svcId);
                    codePatch.AddCode(i, template, true); // 标记需要修复跳转
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched SVC #{svcId} at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd53bd060) // mrs x0, tpidrro_el0
                {
                    uint rd = inst & 0x1f;
                    var template = GetOrCreatePatchTemplate(PatchType.MrsTpidrroEl0, rd);
                    codePatch.AddCode(i, template, true);
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MRS x{rd}, tpidrro_el0 at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd53bd040) // mrs x0, tpidr_el0
                {
                    uint rd = inst & 0x1f;
                    var template = GetOrCreatePatchTemplate(PatchType.MrsTpidrEl0, rd);
                    codePatch.AddCode(i, template, true);
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MRS x{rd}, tpidr_el0 at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd53b0020 && OperatingSystem.IsMacOS()) // mrs x0, ctr_el0
                {
                    uint rd = inst & 0x1f;
                    var template = GetOrCreatePatchTemplate(PatchType.MrsCtrEl0, rd);
                    codePatch.AddCode(i, template, true);
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MRS x{rd}, ctr_el0 at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd53be020) // mrs x0, cntpct_el0
                {
                    uint rd = inst & 0x1f;
                    var template = GetOrCreatePatchTemplate(PatchType.MrsCntpctEl0, rd);
                    codePatch.AddCode(i, template, true);
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MRS x{rd}, cntpct_el0 at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd51bd040) // msr tpidr_el0, x0
                {
                    uint rd = inst & 0x1f;
                    var template = GetOrCreatePatchTemplate(PatchType.MsrTpidrEl0, rd);
                    codePatch.AddCode(i, template, true);
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MSR tpidr_el0, x{rd} at 0x{address:X}.");
                }
            }

            return codePatch;
        }

        // ================== 模板生成函数 ==================
        private static uint[] WriteSvcPatchTemplate(Assembler asm, uint svcId)
        {
            WriteManagedCall(asm, (asm, ctx, tmp, tmp2) =>
            {
                for (int i = 0; i < 8; i++)
                {
                    asm.StrRiUn(Gpr(i), ctx, NceNativeContext.GetXOffset(i));
                }

                WriteInManagedLockAcquire(asm, ctx, tmp, tmp2);

                asm.Mov(Gpr(0, OperandType.I32), svcId);
                asm.LdrRiUn(tmp, ctx, NceNativeContext.GetSvcCallHandlerOffset());
                asm.Blr(tmp);

                Operand lblContinue = asm.CreateLabel();
                Operand lblQuit = asm.CreateLabel();

                asm.Cbnz(Gpr(0, OperandType.I32), lblContinue);

                asm.MarkLabel(lblQuit);

                CreateRegisterSaveRestoreForManaged().WriteEpilogue(asm);

                asm.Ret(Gpr(30));

                asm.MarkLabel(lblContinue);

                WriteInManagedLockRelease(asm, ctx, tmp, tmp2, ThreadExitMethod.Label, lblQuit);

                for (int i = 0; i < 8; i++)
                {
                    asm.LdrRiUn(Gpr(i), ctx, NceNativeContext.GetXOffset(i));
                }
            }, 0xff);

            asm.Dw(0xFFFFFFFF); // 跳转占位符
            return asm.GetCode();
        }

        private static uint[] WriteMrsTpidrroEl0PatchTemplate(Assembler asm, uint rd)
        {
            return WriteMrsContextReadTemplate(asm, rd, NceNativeContext.GetTpidrroEl0Offset());
        }

        private static uint[] WriteMrsTpidrEl0PatchTemplate(Assembler asm, uint rd)
        {
            return WriteMrsContextReadTemplate(asm, rd, NceNativeContext.GetTpidrEl0Offset());
        }

        private static uint[] WriteMrsCtrEl0PatchTemplate(Assembler asm, uint rd)
        {
            return WriteMrsContextReadTemplate(asm, rd, NceNativeContext.GetCtrEl0Offset());
        }

        private static uint[] WriteMrsCntpctEl0PatchTemplate(Assembler asm, uint rd)
        {
            WriteManagedCall(asm, (asm, ctx, tmp, tmp2) =>
            {
                WriteInManagedLockAcquire(asm, ctx, tmp, tmp2);

                asm.Mov(tmp, (ulong)NceNativeInterface.GetTickCounterAccessFunctionPointer());
                asm.Blr(tmp);
                asm.StrRiUn(Gpr(0), ctx, NceNativeContext.GetTempStorageOffset());

                WriteInManagedLockRelease(asm, ctx, tmp, tmp2, ThreadExitMethod.GenerateReturn);

                asm.LdrRiUn(Gpr((int)rd), ctx, NceNativeContext.GetTempStorageOffset());
            }, 1u << (int)rd);

            asm.Dw(0xFFFFFFFF); // 跳转占位符
            return asm.GetCode();
        }

        private static uint[] WriteMsrTpidrEl0PatchTemplate(Assembler asm, uint rd)
        {
            Span<int> scratchRegs = stackalloc int[3];
            PickScratchRegs(scratchRegs, 1u << (int)rd);

            RegisterSaveRestore rsr = new((1 << scratchRegs[0]) | (1 << scratchRegs[1]) | (1 << scratchRegs[2]));

            rsr.WritePrologue(asm);

            WriteLoadContext(asm, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[2]));
            asm.StrRiUn(Gpr((int)rd), Gpr(scratchRegs[0]), NceNativeContext.GetTpidrEl0Offset());

            rsr.WriteEpilogue(asm);

            asm.Dw(0xFFFFFFFF); // 跳转占位符
            return asm.GetCode();
        }

        private static uint[] WriteMrsContextReadTemplate(Assembler asm, uint rd, int contextOffset)
        {
            Span<int> scratchRegs = stackalloc int[3];
            PickScratchRegs(scratchRegs, 1u << (int)rd);

            RegisterSaveRestore rsr = new((1 << scratchRegs[0]) | (1 << scratchRegs[1]) | (1 << scratchRegs[2]));

            rsr.WritePrologue(asm);

            WriteLoadContext(asm, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[2]));
            asm.Add(Gpr((int)rd), Gpr(scratchRegs[0]), Const((ulong)contextOffset));

            rsr.WriteEpilogue(asm);

            asm.LdrRiUn(Gpr((int)rd), Gpr((int)rd), 0);

            asm.Dw(0xFFFFFFFF); // 跳转占位符
            return asm.GetCode();
        }

        // ================== 辅助方法（保持不变） ==================
        private static void WriteLoadContext(Assembler asm, Operand tmp0, Operand tmp1, Operand tmp2)
        {
            asm.Mov(tmp0, (ulong)NceThreadTable.EntriesPointer);

            if (OperatingSystem.IsMacOS())
            {
                asm.MrsTpidrroEl0(tmp1);
            }
            else
            {
                asm.MrsTpidrEl0(tmp1);
            }

            Operand lblFound = asm.CreateLabel();
            Operand lblLoop = asm.CreateLabel();

            asm.MarkLabel(lblLoop);

            asm.LdrRiPost(tmp2, tmp0, 16);
            asm.Cmp(tmp1, tmp2);
            asm.B(lblFound, ArmCondition.Eq);
            asm.B(lblLoop);

            asm.MarkLabel(lblFound);

            asm.Ldur(tmp0, tmp0, -8);
        }

        private static void WriteManagedCall(Assembler asm, Action<Assembler, Operand, Operand, Operand> writeCall, uint blacklistedRegMask)
        {
            int intMask = 0x7fffffff & (int)~blacklistedRegMask;
            int vecMask = unchecked((int)0xffffffff);

            Span<int> scratchRegs = stackalloc int[3];
            PickScratchRegs(scratchRegs, blacklistedRegMask);

            RegisterSaveRestore rsr = new(intMask, vecMask, OperandType.V128);

            rsr.WritePrologue(asm);

            WriteLoadContext(asm, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[2]));

            asm.MovSp(Gpr(scratchRegs[1]), Gpr(Assembler.SpRegister));
            asm.StrRiUn(Gpr(scratchRegs[1]), Gpr(scratchRegs[0]), NceNativeContext.GetGuestSPOffset());
            asm.LdrRiUn(Gpr(scratchRegs[1]), Gpr(scratchRegs[0]), NceNativeContext.GetHostSPOffset());
            asm.MovSp(Gpr(Assembler.SpRegister), Gpr(scratchRegs[1]));

            writeCall(asm, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[2]));

            asm.LdrRiUn(Gpr(scratchRegs[1]), Gpr(scratchRegs[0]), NceNativeContext.GetGuestSPOffset());
            asm.MovSp(Gpr(Assembler.SpRegister), Gpr(scratchRegs[1]));

            rsr.WriteEpilogue(asm);
        }

        private static void PickScratchRegs(Span<int> scratchRegs, uint blacklistedRegMask)
        {
            int scratchReg = ScratchBaseReg;

            for (int i = 0; i < scratchRegs.Length; i++)
            {
                while ((blacklistedRegMask & (1u << scratchReg)) != 0)
                {
                    scratchReg++;
                }

                if (scratchReg >= 29)
                {
                    throw new ArgumentException($"No enough register for {scratchRegs.Length} scratch register, started from {ScratchBaseReg}");
                }

                scratchRegs[i] = scratchReg++;
            }
        }

        private static Operand Gpr(int register, OperandType type = OperandType.I64)
        {
            return new Operand(register, RegisterType.Integer, type);
        }

        private static Operand Const(ulong value)
        {
            return new Operand(OperandType.I64, value);
        }

        private static uint EncodeSImm26_2(int value)
        {
            uint imm = (uint)(value >> 2) & 0x3ffffff;
            if (((int)imm << 6) >> 4 != value)
            {
                throw new Exception($"Failed to encode constant 0x{value:X}.");
            }
            return imm;
        }

        private enum ThreadExitMethod
        {
            None,
            GenerateReturn,
            Label
        }

        private static void WriteInManagedLockAcquire(Assembler asm, Operand ctx, Operand tmp, Operand tmp2)
        {
            Operand tmpUint = new Operand(tmp.GetRegister().Index, RegisterType.Integer, OperandType.I32);
            Operand tmp2Uint = new Operand(tmp2.GetRegister().Index, RegisterType.Integer, OperandType.I32);

            Operand lblLoop = asm.CreateLabel();

            asm.MarkLabel(lblLoop);

            asm.Add(tmp, ctx, Const((ulong)NceNativeContext.GetInManagedOffset()));
            asm.Ldaxr(tmp2Uint, tmp);
            asm.Cbnz(tmp2Uint, lblLoop);
            asm.Mov(tmp2Uint, Const(OperandType.I32, 1));
            asm.Stlxr(tmp2Uint, tmp, tmpUint);
            asm.Cbnz(tmpUint, lblLoop);
        }

        private static void WriteInManagedLockRelease(Assembler asm, Operand ctx, Operand tmp, Operand tmp2, ThreadExitMethod exitMethod, Operand lblQuit = default)
        {
            Operand tmpUint = new Operand(tmp.GetRegister().Index, RegisterType.Integer, OperandType.I32);
            Operand tmp2Uint = new Operand(tmp2.GetRegister().Index, RegisterType.Integer, OperandType.I32);

            Operand lblLoop = asm.CreateLabel();
            Operand lblInterrupt = asm.CreateLabel();
            Operand lblDone = asm.CreateLabel();

            asm.MarkLabel(lblLoop);

            asm.Add(tmp, ctx, Const((ulong)NceNativeContext.GetInManagedOffset()));
            asm.Ldaxr(tmp2Uint, tmp);
            asm.Cmp(tmp2Uint, Const(OperandType.I32, 3));
            asm.B(lblInterrupt, ArmCondition.Eq);
            asm.Stlxr(Gpr(Assembler.ZrRegister, OperandType.I32), tmp, tmpUint);
            asm.Cbnz(tmpUint, lblLoop);
            asm.B(lblDone);

            asm.MarkLabel(lblInterrupt);

            asm.Mov(tmp2Uint, Const(OperandType.I32, 1));
            asm.Stlxr(tmp2Uint, tmp, tmpUint);
            asm.Cbnz(tmpUint, lblLoop);
            asm.Mov(tmp, (ulong)NceNativeInterface.GetSuspendThreadHandlerFunctionPointer());
            asm.Blr(tmp);

            if (exitMethod == ThreadExitMethod.None)
            {
                asm.B(lblLoop);
            }
            else
            {
                asm.Cbnz(Gpr(0, OperandType.I32), lblLoop);

                if (exitMethod == ThreadExitMethod.Label)
                {
                    asm.B(lblQuit);
                }
                else if (exitMethod == ThreadExitMethod.GenerateReturn)
                {
                    CreateRegisterSaveRestoreForManaged().WriteEpilogue(asm);
                    asm.Ret(Gpr(30));
                }
            }

            asm.MarkLabel(lblDone);
        }

        private static RegisterSaveRestore CreateRegisterSaveRestoreForManaged()
        {
            return new RegisterSaveRestore((int)IntCalleeSavedRegsMask, unchecked((int)FpCalleeSavedRegsMask), OperandType.FP64, hasCall: true);
        }
    }
}