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

        // ========== 优化部分：汇编器对象池 ==========
        private static readonly ConcurrentStack<Assembler> _assemblerPool = new();
        private const int MaxPoolSize = 4;

        private static Assembler RentAssembler()
        {
            if (_assemblerPool.TryPop(out var assembler))
            {
                // 清空Assembler内部指令列表
                var field = typeof(Assembler).GetField("_code", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(System.Collections.Generic.List<uint>))
                {
                    var list = (System.Collections.Generic.List<uint>)field.GetValue(assembler);
                    list?.Clear();
                }
                
                // 清空标签列表
                var labelsField = typeof(Assembler).GetField("_labels",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (labelsField != null && labelsField.FieldType == typeof(System.Collections.Generic.List<Assembler.LabelState>))
                {
                    var labels = (System.Collections.Generic.List<Assembler.LabelState>)labelsField.GetValue(assembler);
                    labels?.Clear();
                }
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

        // ========== 优化部分：补丁模板缓存 ==========
        private enum PatchType : byte
        {
            Svc,
            MrsTpidrroEl0,
            MrsTpidrEl0,
            MrsCtrEl0,
            MrsCntpctEl0,
            MsrTpidrEl0
        }

        private readonly struct PatchCacheKey : IEquatable<PatchCacheKey>
        {
            public readonly PatchType Type;
            public readonly uint Parameter;

            public PatchCacheKey(PatchType type, uint parameter)
            {
                Type = type;
                Parameter = parameter;
            }

            public bool Equals(PatchCacheKey other) => Type == other.Type && Parameter == other.Parameter;
            public override int GetHashCode() => HashCode.Combine(Type, Parameter);
            public override bool Equals(object obj) => obj is PatchCacheKey other && Equals(other);
        }

        private static readonly ConcurrentDictionary<PatchCacheKey, uint[]> _patchTemplateCache = new();

        private static uint[] GetOrCreatePatchTemplate(PatchType type, uint parameter)
        {
            var key = new PatchCacheKey(type, parameter);
            
            if (_patchTemplateCache.TryGetValue(key, out var template))
            {
                return template;
            }

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

                // 缓存模板（不包含最后的B指令）
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

        private static uint[] CreateFinalPatch(uint[] template)
        {
            var finalCode = new uint[template.Length + 1];
            Array.Copy(template, finalCode, template.Length);
            finalCode[^1] = 0x14000000u; // B指令占位符
            return finalCode;
        }

        // ========== 保持原有API兼容 ==========
        public static NceCpuCodePatch CreatePatch(ReadOnlySpan<byte> textSection)
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
                    codePatch.AddCode(i, WriteSvcPatch(svcId));
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched SVC #{svcId} at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd53bd060) // mrs x0, tpidrro_el0
                {
                    uint rd = inst & 0x1f;
                    codePatch.AddCode(i, WriteMrsTpidrroEl0Patch(rd));
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MRS x{rd}, tpidrro_el0 at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd53bd040) // mrs x0, tpidr_el0
                {
                    uint rd = inst & 0x1f;
                    codePatch.AddCode(i, WriteMrsTpidrEl0Patch(rd));
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MRS x{rd}, tpidr_el0 at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd53b0020 && OperatingSystem.IsMacOS()) // mrs x0, ctr_el0
                {
                    uint rd = inst & 0x1f;
                    codePatch.AddCode(i, WriteMrsCtrEl0Patch(rd));
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MRS x{rd}, ctr_el0 at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd53be020) // mrs x0, cntpct_el0
                {
                    uint rd = inst & 0x1f;
                    codePatch.AddCode(i, WriteMrsCntpctEl0Patch(rd));
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MRS x{rd}, cntpct_el0 at 0x{address:X}.");
                }
                else if ((inst & ~0x1f) == 0xd51bd040) // msr tpidr_el0, x0
                {
                    uint rd = inst & 0x1f;
                    codePatch.AddCode(i, WriteMsrTpidrEl0Patch(rd));
                    Logger.Debug?.Print(LogClass.Cpu, $"Patched MSR tpidr_el0, x{rd} at 0x{address:X}.");
                }
            }

            return codePatch;
        }

        // ========== 模板生成函数（替换原有的Write...Patch方法） ==========
        private static uint[] WriteSvcPatch(uint svcId)
        {
            var template = GetOrCreatePatchTemplate(PatchType.Svc, svcId);
            return CreateFinalPatch(template);
        }

        private static uint[] WriteMrsTpidrroEl0Patch(uint rd)
        {
            var template = GetOrCreatePatchTemplate(PatchType.MrsTpidrroEl0, rd);
            return CreateFinalPatch(template);
        }

        private static uint[] WriteMrsTpidrEl0Patch(uint rd)
        {
            var template = GetOrCreatePatchTemplate(PatchType.MrsTpidrEl0, rd);
            return CreateFinalPatch(template);
        }

        private static uint[] WriteMrsCtrEl0Patch(uint rd)
        {
            var template = GetOrCreatePatchTemplate(PatchType.MrsCtrEl0, rd);
            return CreateFinalPatch(template);
        }

        private static uint[] WriteMrsCntpctEl0Patch(uint rd)
        {
            var template = GetOrCreatePatchTemplate(PatchType.MrsCntpctEl0, rd);
            return CreateFinalPatch(template);
        }

        private static uint[] WriteMsrTpidrEl0Patch(uint rd)
        {
            var template = GetOrCreatePatchTemplate(PatchType.MsrTpidrEl0, rd);
            return CreateFinalPatch(template);
        }

        // ========== 实际的模板生成逻辑 ==========
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

            asm.B(0); // B指令占位符
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

            asm.B(0); // B指令占位符
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

            asm.B(0); // B指令占位符
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

            asm.B(0); // B指令占位符
            return asm.GetCode();
        }

        // ========== 原有辅助方法（保持不变） ==========
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

        private static void WriteLoadContextSafe(Assembler asm, Operand lblFail, Operand tmp0, Operand tmp1, Operand tmp2, Operand tmp3)
        {
            asm.Mov(tmp0, (ulong)NceThreadTable.EntriesPointer);
            asm.Ldur(tmp3, tmp0, -8);
            asm.Add(tmp3, tmp0, tmp3, ArmShiftType.Lsl, 4);

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

            asm.Cmp(tmp0, tmp3);
            asm.B(lblFail, ArmCondition.GeUn);
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

        private static Operand Vec(int register, OperandType type = OperandType.V128)
        {
            return new Operand(register, RegisterType.Vector, type);
        }

        private static Operand Const(ulong value)
        {
            return new Operand(OperandType.I64, value);
        }

        private static Operand Const(OperandType type, ulong value)
        {
            return new Operand(type, value);
        }

        private static uint GetImm26(ulong sourceAddress, ulong targetAddress)
        {
            long offset = (long)(targetAddress - sourceAddress);
            long offsetTrunc = (offset >> 2) & 0x3FFFFFF;

            if ((offsetTrunc << 38) >> 36 != offset)
            {
                throw new Exception($"Offset out of range: 0x{sourceAddress:X} -> 0x{targetAddress:X} (0x{offset:X})");
            }

            return (uint)offsetTrunc;
        }

        private static int GetOffset(ulong sourceAddress, ulong targetAddress)
        {
            long offset = (long)(targetAddress - sourceAddress);

            return checked((int)offset);
        }

        private static uint[] GetCopy(uint[] code)
        {
            uint[] codeCopy = new uint[code.Length];
            code.CopyTo(codeCopy, 0);

            return codeCopy;
        }

        // ========== 原有线程启动和异常处理代码 ==========
        internal static uint[] GenerateThreadStartCode()
        {
            var asm = RentAssembler();
            try
            {
                CreateRegisterSaveRestoreForManaged().WritePrologue(asm);

                asm.MovSp(Gpr(1), Gpr(Assembler.SpRegister));
                asm.StrRiUn(Gpr(1), Gpr(0), NceNativeContext.GetHostSPOffset());

                for (int i = 2; i < 30; i += 2)
                {
                    asm.LdpRiUn(Gpr(i), Gpr(i + 1), Gpr(0), NceNativeContext.GetXOffset(i));
                }

                for (int i = 0; i < 32; i += 2)
                {
                    asm.LdpRiUn(Vec(i), Vec(i + 1), Gpr(0), NceNativeContext.GetVOffset(i));
                }

                asm.LdpRiUn(Gpr(30), Gpr(1), Gpr(0), NceNativeContext.GetXOffset(30));
                asm.MovSp(Gpr(Assembler.SpRegister), Gpr(1));

                asm.StrRiUn(Gpr(Assembler.ZrRegister, OperandType.I32), Gpr(0), NceNativeContext.GetInManagedOffset());

                asm.LdpRiUn(Gpr(0), Gpr(1), Gpr(0), NceNativeContext.GetXOffset(0));
                asm.Br(Gpr(30));

                return asm.GetCode();
            }
            finally
            {
                ReturnAssembler(asm);
            }
        }

        internal static uint[] GenerateSuspendExceptionHandler()
        {
            var asm = RentAssembler();
            try
            {
                Span<int> scratchRegs = stackalloc int[4];
                PickScratchRegs(scratchRegs, 0u);

                RegisterSaveRestore rsr = new((1 << scratchRegs[0]) | (1 << scratchRegs[1]) | (1 << scratchRegs[2]) | (1 << scratchRegs[3]), hasCall: true);

                rsr.WritePrologue(asm);

                Operand lblAgain = asm.CreateLabel();
                Operand lblFail = asm.CreateLabel();

                WriteLoadContextSafe(asm, lblFail, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[2]), Gpr(scratchRegs[3]));

                asm.LdrRiUn(Gpr(scratchRegs[1]), Gpr(scratchRegs[0]), NceNativeContext.GetHostSPOffset());
                asm.MovSp(Gpr(scratchRegs[2]), Gpr(Assembler.SpRegister));
                asm.MovSp(Gpr(Assembler.SpRegister), Gpr(scratchRegs[1]));

                asm.Cmp(Gpr(0, OperandType.I32), Const((ulong)NceThreadPal.UnixSuspendSignal));
                asm.B(lblFail, ArmCondition.Ne);

                // SigUsr2
                asm.Mov(Gpr(scratchRegs[1], OperandType.I32), 1);
                asm.StrRiUn(Gpr(scratchRegs[1], OperandType.I32), Gpr(scratchRegs[0]), NceNativeContext.GetInManagedOffset());

                asm.MarkLabel(lblAgain);

                asm.Mov(Gpr(scratchRegs[3]), (ulong)NceNativeInterface.GetSuspendThreadHandlerFunctionPointer());
                asm.Blr(Gpr(scratchRegs[3]));

                // TODO: Check return value, exit if we must.
                WriteInManagedLockReleaseForSuspendHandler(asm, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[3]), lblAgain);

                asm.MovSp(Gpr(Assembler.SpRegister), Gpr(scratchRegs[2]));

                rsr.WriteEpilogue(asm);

                asm.Ret(Gpr(30));

                asm.MarkLabel(lblFail);

                rsr.WriteEpilogue(asm);

                asm.Ret(Gpr(30));

                return asm.GetCode();
            }
            finally
            {
                ReturnAssembler(asm);
            }
        }

        internal static uint[] GenerateWrapperExceptionHandler(IntPtr oldSignalHandlerSegfaultPtr, IntPtr signalHandlerPtr)
        {
            var asm = RentAssembler();
            try
            {
                Span<int> scratchRegs = stackalloc int[4];
                PickScratchRegs(scratchRegs, 0u);

                RegisterSaveRestore rsr = new((1 << scratchRegs[0]) | (1 << scratchRegs[1]) | (1 << scratchRegs[2]) | (1 << scratchRegs[3]), hasCall: true);

                rsr.WritePrologue(asm);

                Operand lblFail = asm.CreateLabel();

                WriteLoadContextSafe(asm, lblFail, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[2]), Gpr(scratchRegs[3]));

                asm.LdrRiUn(Gpr(scratchRegs[1]), Gpr(scratchRegs[0]), NceNativeContext.GetHostSPOffset());
                asm.MovSp(Gpr(scratchRegs[2]), Gpr(Assembler.SpRegister));
                asm.MovSp(Gpr(Assembler.SpRegister), Gpr(scratchRegs[1]));

                // SigSegv
                WriteInManagedLockAcquire(asm, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[3]));

                asm.Mov(Gpr(scratchRegs[3]), (ulong)signalHandlerPtr);
                asm.Blr(Gpr(scratchRegs[3]));

                WriteInManagedLockRelease(asm, Gpr(scratchRegs[0]), Gpr(scratchRegs[1]), Gpr(scratchRegs[3]), ThreadExitMethod.None);

                asm.MovSp(Gpr(Assembler.SpRegister), Gpr(scratchRegs[2]));

                rsr.WriteEpilogue(asm);

                asm.Ret(Gpr(30));

                asm.MarkLabel(lblFail);

                rsr.WriteEpilogue(asm);

                asm.Mov(Gpr(3), (ulong)oldSignalHandlerSegfaultPtr);
                asm.Br(Gpr(3));

                return asm.GetCode();
            }
            finally
            {
                ReturnAssembler(asm);
            }
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
            asm.Cbnz(tmpUint, lblLoop); // Retry if store failed.
        }

        private enum ThreadExitMethod
        {
            None,
            GenerateReturn,
            Label
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
            asm.Cbnz(tmpUint, lblLoop); // Retry if store failed.
            asm.B(lblDone);

            asm.MarkLabel(lblInterrupt);

            // If we got here, a interrupt was requested while it was in managed code.
            asm.Mov(tmp2Uint, Const(OperandType.I32, 1));
            asm.Stlxr(tmp2Uint, tmp, tmpUint);
            asm.Cbnz(tmpUint, lblLoop); // Retry if store failed.
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

        private static void WriteInManagedLockReleaseForSuspendHandler(Assembler asm, Operand ctx, Operand tmp, Operand tmp2, Operand lblAgain)
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
            asm.Cbnz(tmpUint, lblLoop); // Retry if store failed.
            asm.B(lblDone);

            asm.MarkLabel(lblInterrupt);

            asm.Mov(tmp2Uint, Const(OperandType.I32, 1));
            asm.Stlxr(tmp2Uint, tmp, tmpUint);
            asm.Cbnz(tmpUint, lblLoop); // Retry if store failed.
            asm.B(lblAgain);

            asm.MarkLabel(lblDone);
        }

        private static RegisterSaveRestore CreateRegisterSaveRestoreForManaged()
        {
            return new RegisterSaveRestore((int)IntCalleeSavedRegsMask, unchecked((int)FpCalleeSavedRegsMask), OperandType.FP64, hasCall: true);
        }
    }
}