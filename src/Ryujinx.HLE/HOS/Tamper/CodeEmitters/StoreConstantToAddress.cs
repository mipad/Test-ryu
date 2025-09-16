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
        // 半字节索引
        private const int WidthNibbleIndex = 1;    // 第二个半字节是宽度代码
        private const int RegionNibbleIndex = 2;   // 第三个半字节是内存区域代码
        private const int RegisterNibbleIndex = 3; // 第四个半字节是寄存器索引
        private const int OffsetStartNibbleIndex = 6; // 偏移量从第7个半字节开始
        private const int ValueStartNibbleIndex = 14; // 值从第15个半字节开始

        private const int OffsetNibbleSize = 8;   // 偏移量是8个半字节（32位）
        private const int ValueNibbleSize = 8;    // 值也是8个半字节（32位）

        public static void Emit(byte[] instruction, CompilationContext context)
        {
            // 0TMR00AA AAAAAAAA VVVVVVVV (VVVVVVVV)
            // T: Width of memory write(1, 2, 4, or 8 bytes).
            // M: Memory region to write to(0 = Main NSO, 1 = Heap, 2 = Alias, 3 = Aslr).
            // R: Register to use as an offset from memory region base.
            // A: Immediate offset to use from memory region base.
            // V: Value to write.

            // 使用半字节索引提取信息
            byte widthCode = GetNibble(instruction, WidthNibbleIndex);
            byte regionCode = GetNibble(instruction, RegionNibbleIndex);
            byte registerIndex = GetNibble(instruction, RegisterNibbleIndex);
            
            // 将宽度代码转换为实际宽度
            byte operationWidth = widthCode switch
            {
                0 => 1, // 1字节
                1 => 2, // 2字节
                2 => 4, // 4字节
                3 => 8, // 8字节
                4 => 4, // 4字节（某些金手指使用4表示4字节宽度）
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
            
            // 使用半字节索引提取偏移量立即值
            ulong offsetImmediate = GetImmediateFromNibbles(instruction, OffsetStartNibbleIndex, OffsetNibbleSize);

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

            // 使用半字节索引提取值立即值
            ulong valueImmediate = GetImmediateFromNibbles(instruction, ValueStartNibbleIndex, ValueNibbleSize);
            Value<ulong> storeValue = new(valueImmediate);

            // 添加值日志
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"StoreConstantToAddress: writing value 0x{valueImmediate:X8} to calculated address");

            // 添加一个调试操作来记录实际写入的地址
            context.CurrentOperations.Add(new DebugOperation(
                $"Writing 0x{valueImmediate:X8} to memory address calculated from R_{registerIndex:X1} + 0x{offsetImmediate:X8}"));

            InstructionHelper.EmitMov(operationWidth, context, dstMem, storeValue);
        }

        // 从半字节数组中获取指定半字节的值
        private static byte GetNibble(byte[] nibbles, int nibbleIndex)
        {
            int byteIndex = nibbleIndex / 2;
            bool isHighNibble = (nibbleIndex % 2) == 0;
            
            if (byteIndex >= nibbles.Length)
            {
                throw new TamperCompilationException($"Nibble index {nibbleIndex} out of range");
            }
            
            byte value = nibbles[byteIndex];
            return isHighNibble ? (byte)(value >> 4) : (byte)(value & 0x0F);
        }

        // 从半字节数组中提取立即值
        private static ulong GetImmediateFromNibbles(byte[] nibbles, int startNibbleIndex, int nibbleCount)
        {
            ulong value = 0;

            for (int i = 0; i < nibbleCount; i++)
            {
                value <<= 4;
                value |= GetNibble(nibbles, startNibbleIndex + i);
            }

            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Extracted immediate value: 0x{value:X} from nibble position {startNibbleIndex} with {nibbleCount} nibbles");

            return value;
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
