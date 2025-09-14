using Ryujinx.Common.Logging;
using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Tamper.Operations;

namespace Ryujinx.HLE.HOS.Tamper
{
    class MemoryHelper
    {
        public static ulong GetBaseAddress(MemoryRegion source, CompilationContext context)
        {
            ulong address = source switch
            {
                MemoryRegion.NSO => context.ExeAddress,
                MemoryRegion.Heap => context.HeapAddress,
                MemoryRegion.Alias => context.AliasAddress,
                MemoryRegion.Asrl => context.AslrAddress,
                _ => throw new TamperCompilationException($"Invalid memory source {source} in Atmosphere cheat"),
            };
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"GetBaseAddress: region={source}, address=0x{address:X16}");
            
            return address;
        }

        public static ulong GetAddressShift(MemoryRegion source, CompilationContext context)
        {
            return GetBaseAddress(source, context);
        }

        private static void EmitAdd(Value<ulong> finalValue, IOperand firstOperand, IOperand secondOperand, CompilationContext context)
        {
            // 不要在编译时尝试获取值，而是添加操作
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitAdd: Adding operations for first and second operands");
            
            context.CurrentOperations.Add(new OpAdd<ulong>(finalValue, firstOperand, secondOperand));
            
            // 添加一个调试操作来记录运行时结果
            context.CurrentOperations.Add(new DebugOperation(() => 
                $"Add result: 0x{firstOperand.Get<ulong>():X16} + 0x{secondOperand.Get<ulong>():X16} = 0x{finalValue.Get<ulong>():X16}"));
        }

        public static Pointer EmitPointer(ulong addressImmediate, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: immediate=0x{addressImmediate:X16}");
            
            Value<ulong> addressImmediateValue = new(addressImmediate);
            return new Pointer(addressImmediateValue, context.Process);
        }

        public static Pointer EmitPointer(Register addressRegister, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: register={addressRegister}");
            
            return new Pointer(addressRegister, context.Process);
        }

        public static Pointer EmitPointer(Register addressRegister, ulong offsetImmediate, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: register={addressRegister}, offset=0x{offsetImmediate:X16}");
            
            Value<ulong> offsetImmediateValue = new(offsetImmediate);
            Value<ulong> finalAddressValue = new(0);
            EmitAdd(finalAddressValue, addressRegister, offsetImmediateValue, context);

            return new Pointer(finalAddressValue, context.Process);
        }

        public static Pointer EmitPointer(Register addressRegister, Register offsetRegister, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: addressReg={addressRegister}, offsetReg={offsetRegister}");
            
            Value<ulong> finalAddressValue = new(0);
            EmitAdd(finalAddressValue, addressRegister, offsetRegister, context);

            return new Pointer(finalAddressValue, context.Process);
        }

        public static Pointer EmitPointer(Register addressRegister, Register offsetRegister, ulong offsetImmediate, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: addressReg={addressRegister}, offsetReg={offsetRegister}, offsetImm=0x{offsetImmediate:X16}");
            
            Value<ulong> offsetImmediateValue = new(offsetImmediate);
            Value<ulong> finalOffsetValue = new(0);
            EmitAdd(finalOffsetValue, offsetRegister, offsetImmediateValue, context);
            Value<ulong> finalAddressValue = new(0);
            EmitAdd(finalAddressValue, addressRegister, finalOffsetValue, context);

            return new Pointer(finalAddressValue, context.Process);
        }

        public static Pointer EmitPointer(MemoryRegion memoryRegion, ulong offsetImmediate, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: region={memoryRegion}, offset=0x{offsetImmediate:X16}");
            
            ulong baseAddress = GetBaseAddress(memoryRegion, context);
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: baseAddress=0x{baseAddress:X16}, finalAddress=0x{baseAddress + offsetImmediate:X16}");
            
            offsetImmediate += baseAddress;
            return EmitPointer(offsetImmediate, context);
        }

        public static Pointer EmitPointer(MemoryRegion memoryRegion, Register offsetRegister, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: region={memoryRegion}, offsetReg={offsetRegister}");
            
            ulong baseAddress = GetBaseAddress(memoryRegion, context);
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: baseAddress=0x{baseAddress:X16}");
            
            return EmitPointer(offsetRegister, baseAddress, context);
        }

        public static Pointer EmitPointer(MemoryRegion memoryRegion, Register offsetRegister, ulong offsetImmediate, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: region={memoryRegion}, offsetReg={offsetRegister}, offsetImm=0x{offsetImmediate:X16}");
            
            ulong baseAddress = GetBaseAddress(memoryRegion, context);
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: baseAddress=0x{baseAddress:X16}");
            
            return EmitPointer(offsetRegister, baseAddress + offsetImmediate, context);
        }
    }

    // 添加一个调试操作类，用于在运行时记录信息
    class DebugOperation : IOperation
    {
        private readonly System.Func<string> _messageFunc;

        public DebugOperation(System.Func<string> messageFunc)
        {
            _messageFunc = messageFunc;
        }

        public void Execute()
        {
            Logger.Debug?.Print(LogClass.TamperMachine, _messageFunc());
        }
    }
}
