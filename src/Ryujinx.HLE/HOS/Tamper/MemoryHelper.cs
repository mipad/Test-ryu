using Ryujinx.Common.Logging;
using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Tamper.Operations;

namespace Ryujinx.HLE.HOS.Tamper
{
    class MemoryHelper
    {
        public static ulong GetAddressShift(MemoryRegion source, CompilationContext context)
        {
            ulong addressShift = source switch
            {
                MemoryRegion.NSO => context.ExeAddress,
                MemoryRegion.Heap => context.HeapAddress,
                MemoryRegion.Alias => context.AliasAddress,
                MemoryRegion.Asrl => context.AslrAddress,
                _ => throw new TamperCompilationException($"Invalid memory source {source} in Atmosphere cheat"),
            };
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"GetAddressShift: region={source}, address=0x{addressShift:X16}");
            
            return addressShift;
        }

        private static void EmitAdd(Value<ulong> finalValue, IOperand firstOperand, IOperand secondOperand, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitAdd: first=0x{firstOperand.Get<ulong>():X16}, second=0x{secondOperand.Get<ulong>():X16}");
            
            context.CurrentOperations.Add(new OpAdd<ulong>(finalValue, firstOperand, secondOperand));
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitAdd: result=0x{finalValue.Get<ulong>():X16}");
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
                $"EmitPointer: register={addressRegister}, value=0x{addressRegister.Get<ulong>():X16}");
            
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
            
            ulong baseAddress = GetAddressShift(memoryRegion, context);
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: baseAddress=0x{baseAddress:X16}, finalAddress=0x{baseAddress + offsetImmediate:X16}");
            
            offsetImmediate += baseAddress;
            return EmitPointer(offsetImmediate, context);
        }

        public static Pointer EmitPointer(MemoryRegion memoryRegion, Register offsetRegister, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: region={memoryRegion}, offsetReg={offsetRegister}");
            
            ulong offsetImmediate = GetAddressShift(memoryRegion, context);
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: baseAddress=0x{offsetImmediate:X16}");
            
            return EmitPointer(offsetRegister, offsetImmediate, context);
        }

        public static Pointer EmitPointer(MemoryRegion memoryRegion, Register offsetRegister, ulong offsetImmediate, CompilationContext context)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: region={memoryRegion}, offsetReg={offsetRegister}, offsetImm=0x{offsetImmediate:X16}");
            
            ulong baseAddress = GetAddressShift(memoryRegion, context);
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"EmitPointer: baseAddress=0x{baseAddress:X16}, finalAddress=0x{baseAddress + offsetImmediate:X16}");
            
            offsetImmediate += baseAddress;
            return EmitPointer(offsetRegister, offsetImmediate, context);
        }
    }
}
