using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Tamper.Operations;

namespace Ryujinx.HLE.HOS.Tamper.CodeEmitters
{
    /// <summary>
    /// Code type 0 allows writing a static value to a memory address.
    /// </summary>
    class StoreConstantToAddress
    {
        private const int OperationWidthIndex = 1;
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

            byte widthAndRegion = instruction[OperationWidthIndex];
            
            // 解析宽度和内存区域
            byte widthCode = (byte)(widthAndRegion & 0x3); // 低2位表示宽度
            byte regionCode = (byte)((widthAndRegion >> 2) & 0x3); // 高2位表示内存区域
            
            // 将宽度代码转换为实际宽度
            byte operationWidth = widthCode switch
            {
                0 => 1, // 1字节
                1 => 2, // 2字节
                2 => 4, // 4字节
                3 => 8, // 8字节
                _ => throw new TamperCompilationException($"Invalid width code {widthCode} in StoreConstantToAddress instruction")
            };
            
            // 将区域代码转换为内存区域
            MemoryRegion memoryRegion = regionCode switch
            {
                0 => MemoryRegion.NSO, // MAIN
                1 => MemoryRegion.Heap, // HEAP
                2 => MemoryRegion.Alias, // ALIAS
                3 => MemoryRegion.Aslr, // ASLR
                _ => throw new TamperCompilationException($"Invalid region code {regionCode} in StoreConstantToAddress instruction")
            };

            Register offsetRegister = context.GetRegister(instruction[OffsetRegisterIndex]);
            ulong offsetImmediate = InstructionHelper.GetImmediate(instruction, OffsetImmediateIndex, OffsetImmediateSize);

            // 添加详细日志
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: width={operationWidth}, region={memoryRegion}, " +
                $"offsetReg=R_{instruction[OffsetRegisterIndex]:X2}, offsetImm=0x{offsetImmediate:X16}");

            // 记录寄存器当前值
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Register R_{instruction[OffsetRegisterIndex]:X2} current value: 0x{offsetRegister.Get<ulong>():X16}");

            // 获取基地址
            ulong baseAddress = MemoryHelper.GetBaseAddress(memoryRegion, context);
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Base address for region {memoryRegion}: 0x{baseAddress:X16}");

            // 计算预期地址
            ulong expectedAddress = baseAddress + offsetRegister.Get<ulong>() + offsetImmediate;
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Expected address calculation: 0x{baseAddress:X16} + 0x{offsetRegister.Get<ulong>():X16} + 0x{offsetImmediate:X16} = 0x{expectedAddress:X16}");

            Pointer dstMem = MemoryHelper.EmitPointer(memoryRegion, offsetRegister, offsetImmediate, context);

            int valueImmediateSize = operationWidth <= 4 ? ValueImmediateSize8 : ValueImmediateSize16;
            ulong valueImmediate = InstructionHelper.GetImmediate(instruction, ValueImmediateIndex, valueImmediateSize);
            Value<ulong> storeValue = new(valueImmediate);

            // 添加值日志
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: writing value 0x{valueImmediate:X16} to calculated address");

            // 添加一个调试操作来记录实际写入的地址
            context.CurrentOperations.Add(new DebugOperation($"Writing 0x{valueImmediate:X16} to memory address calculated from R_{instruction[OffsetRegisterIndex]:X2} + 0x{offsetImmediate:X16}"));

            InstructionHelper.EmitMov(operationWidth, context, dstMem, storeValue);
        }
    }

    // 添加一个简单的调试操作类
    class DebugOperation : IOperation
    {
        private readonly string _message;

        public DebugOperation(string message)
        {
            _message = message;
        }

        public void Execute()
        {
            Logger.Debug?.Print(LogClass.TamperMachine, _message);
        }
    }
}
