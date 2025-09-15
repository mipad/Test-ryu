using Ryujinx.Common.Logging;
using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Tamper.Operations;

namespace Ryujinx.HLE.HOS.Tamper.CodeEmitters
{
    /// <summary>
    /// Code type 0 allows writing a static value to a memory address.
    /// </summary>
    class StoreConstantToAddress
    {
        // 字节索引
        private const int WidthByteIndex = 0;      // 第一个字节包含宽度和类型
        private const int RegionByteIndex = 1;     // 第二个字节包含区域和寄存器
        private const int OffsetStartByteIndex = 2; // 偏移量从第三个字节开始
        private const int ValueStartByteIndex = 6;  // 值从第七个字节开始

        private const int OffsetByteSize = 4;      // 偏移量是4个字节（32位）
        private const int ValueByteSize = 4;       // 值也是4个字节（32位）

        public static void Emit(byte[] instruction, CompilationContext context)
        {
            // 0TMR00AA AAAAAAAA VVVVVVVV (VVVVVVVV)
            // T: Width of memory write(1, 2, 4, or 8 bytes).
            // M: Memory region to write to(0 = Main NSO, 1 = Heap, 2 = Alias, 3 = Aslr).
            // R: Register to use as an offset from memory region base.
            // A: Immediate offset to use from memory region base.
            // V: Value to write.

            // 从第一个字节提取宽度代码（低4位）
            byte widthCode = (byte)(instruction[WidthByteIndex] & 0x0F);
            
            // 从第二个字节提取区域代码（高4位）和寄存器索引（低4位）
            byte regionCode = (byte)(instruction[RegionByteIndex] >> 4);
            byte registerIndex = (byte)(instruction[RegionByteIndex] & 0x0F);
            
            // 将宽度代码转换为实际宽度（特殊处理：4表示4字节）
            byte operationWidth = widthCode switch
            {
                0 => 1, // 1字节
                1 => 2, // 2字节
                2 => 4, // 4字节
                3 => 8, // 8字节
                4 => 4, // 特殊处理：4也表示4字节
                _ => throw new TamperCompilationException($"Invalid width code {widthCode} in StoreConstantToAddress instruction")
            };
            
            // 将区域代码转换为内存区域
            MemoryRegion memoryRegion = regionCode switch
            {
                0 => MemoryRegion.NSO,   // MAIN
                1 => MemoryRegion.Heap,  // HEAP
                2 => MemoryRegion.Alias, // ALIAS
                3 => MemoryRegion.Asrl,  // ASLR
                _ => throw new TamperCompilationException($"Invalid region code {regionCode} in StoreConstantToAddress instruction")
            };

            Register offsetRegister = context.GetRegister(registerIndex);
            
            // 提取偏移量立即值（4字节，从第三个字节开始）
            ulong offsetImmediate = 0;
            for (int i = 0; i < OffsetByteSize; i++)
            {
                offsetImmediate <<= 8;
                offsetImmediate |= instruction[OffsetStartByteIndex + i];
            }

            // 添加详细日志
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: width={operationWidth}, region={memoryRegion}, " +
                $"offsetReg=R_{registerIndex:X1}, offsetImm=0x{offsetImmediate:X8}");

            // 记录寄存器当前值
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Register R_{registerIndex:X1} current value: 0x{offsetRegister.Get<ulong>():X16}");

            // 获取基地址
            ulong baseAddress = MemoryHelper.GetBaseAddress(memoryRegion, context);
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Base address for region {memoryRegion}: 0x{baseAddress:X16}");

            // 计算预期地址
            ulong expectedAddress = baseAddress + offsetRegister.Get<ulong>() + offsetImmediate;
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Expected address calculation: 0x{baseAddress:X16} + 0x{offsetRegister.Get<ulong>():X16} + 0x{offsetImmediate:X8} = 0x{expectedAddress:X16}");

            Pointer dstMem = MemoryHelper.EmitPointer(memoryRegion, offsetRegister, offsetImmediate, context);

            // 提取值立即值（4字节，从第七个字节开始）
            ulong valueImmediate = 0;
            for (int i = 0; i < ValueByteSize; i++)
            {
                valueImmediate <<= 8;
                valueImmediate |= instruction[ValueStartByteIndex + i];
            }

            Value<ulong> storeValue = new(valueImmediate);

            // 添加值日志
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: writing value 0x{valueImmediate:X8} to calculated address");

            // 添加一个调试操作来记录实际写入的地址
            context.CurrentOperations.Add(new DebugOperation(
                $"Writing 0x{valueImmediate:X8} to memory address calculated from R_{registerIndex:X1} + 0x{offsetImmediate:X8}"));

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
