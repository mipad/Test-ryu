using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Tamper.Operations; // 添加这行

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
            // 0TMR00AA AAAAAAAA VVVVVVVV (VVVVVVVV)
            // T: Width of memory write(1, 2, 4, or 8 bytes).
            // M: Memory region to write to(0 = Main NSO, 1 = Heap).
            // R: Register to use as an offset from memory region base.
            // A: Immediate offset to use from memory region base.
            // V: Value to write.

            byte operationWidth = instruction[OperationWidthIndex];
            MemoryRegion memoryRegion = (MemoryRegion)instruction[MemoryRegionIndex];
            Register offsetRegister = context.GetRegister(instruction[OffsetRegisterIndex]);
            ulong offsetImmediate = InstructionHelper.GetImmediate(instruction, OffsetImmediateIndex, OffsetImmediateSize);

            // 添加详细日志
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: width={operationWidth}, region={memoryRegion}, " +
                $"offsetReg=R_{instruction[OffsetRegisterIndex]:X2}, offsetImm=0x{offsetImmediate:X16}");

            Pointer dstMem = MemoryHelper.EmitPointer(memoryRegion, offsetRegister, offsetImmediate, context);

            // 创建一个临时寄存器来存储地址
            Register tempAddressReg = context.GetRegister(0); // 使用寄存器0作为临时存储
            context.CurrentOperations.Add(new OpMov<ulong>(tempAddressReg, dstMem.GetPositionOperand()));
            
            // 添加地址转换日志
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: finalAddress=0x{tempAddressReg.Get<ulong>():X16}");

            int valueImmediateSize = operationWidth <= 4 ? ValueImmediateSize8 : ValueImmediateSize16;
            ulong valueImmediate = InstructionHelper.GetImmediate(instruction, ValueImmediateIndex, valueImmediateSize);
            Value<ulong> storeValue = new(valueImmediate);

            // 添加值日志
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: writing value 0x{valueImmediate:X16} to address 0x{tempAddressReg.Get<ulong>():X16}");

            InstructionHelper.EmitMov(operationWidth, context, dstMem, storeValue);
        }
    }
}
