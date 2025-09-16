using System;
using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Tamper.CodeEmitters
{
    /// <summary>
    /// Code type 0 allows writing a static value to a memory address.
    /// </summary>
    class StoreConstantToAddress
    {
        private const int OperationWidthIndex = 1;
        private const int MemoryRegionIndex = 2;
        private const int OffsetRegisterIndex = 3;
        private const int OffsetImmediateIndex = 6;
        private const int ValueImmediateIndex = 16;

        private const int OffsetImmediateSize = 10;
        private const int ValueImmediateSize8 = 8;
        private const int ValueImmediateSize16 = 16;

        public static void Emit(byte[] instruction, CompilationContext context)
        {
            // 记录原始指令
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: 原始指令: {BitConverter.ToString(instruction)}");

            // 0TMR00AA AAAAAAAA VVVVVVVV (VVVVVVVV)
            // T: Width of memory write(1, 2, 4, or 8 bytes).
            // M: Memory region to write to(0 = Main NSO, 1 = Heap).
            // R: Register to use as an offset from memory region base.
            // A: Immediate offset to use from memory region base.
            // V: Value to write.

            byte operationWidth = instruction[OperationWidthIndex];
            MemoryRegion memoryRegion = (MemoryRegion)instruction[MemoryRegionIndex];
            byte registerIndex = instruction[OffsetRegisterIndex];
            Register offsetRegister = context.GetRegister(registerIndex);
            ulong offsetImmediate = InstructionHelper.GetImmediate(instruction, OffsetImmediateIndex, OffsetImmediateSize);

            // 记录解析的参数
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: 操作宽度={operationWidth}, 内存区域={memoryRegion}, " +
                $"偏移寄存器=R_{registerIndex:X2}, 偏移立即数=0x{offsetImmediate:X}");

            // 记录寄存器当前值（如果可用）
            try
            {
                ulong registerValue = offsetRegister.Get<ulong>();
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"寄存器 R_{registerIndex:X2} 当前值: 0x{registerValue:X16}");
            }
            catch
            {
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"无法获取寄存器 R_{registerIndex:X2} 的当前值（可能尚未初始化）");
            }

            Pointer dstMem = MemoryHelper.EmitPointer(memoryRegion, offsetRegister, offsetImmediate, context);

            int valueImmediateSize = operationWidth <= 4 ? ValueImmediateSize8 : ValueImmediateSize16;
            ulong valueImmediate = InstructionHelper.GetImmediate(instruction, ValueImmediateIndex, valueImmediateSize);
            Value<ulong> storeValue = new(valueImmediate);

            // 记录要写入的值和目标地址信息
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: 将值 0x{valueImmediate:X} (大小={operationWidth}字节) 写入内存");

            // 简单的日志记录，不使用 DebugOperation
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"将 0x{valueImmediate:X} 写入内存地址 (区域={memoryRegion}, 寄存器=R_{registerIndex:X2}, 偏移=0x{offsetImmediate:X})");

            InstructionHelper.EmitMov(operationWidth, context, dstMem, storeValue);
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                "StoreConstantToAddress: 指令处理完成");
        }
    }
}
