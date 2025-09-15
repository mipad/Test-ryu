[file name]: Pointer.cs
[file content begin]
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Tamper.Operations;
using System;

namespace Ryujinx.HLE.HOS.Tamper
{
    class Pointer : IOperand
    {
        private readonly IOperand _address;
        private readonly ITamperedProcess _process;

        public Pointer(IOperand address, ITamperedProcess process)
        {
            _address = address;
            _process = process;
        }

        public T Get<T>() where T : unmanaged
        {
            ulong address = _address.Get<ulong>();
            T value = _process.ReadMemory<T>(address);
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Pointer.Get: Read 0x{value:X} from address 0x{address:X16}");
            
            return value;
        }

        public void Set<T>(T value) where T : unmanaged
        {
            ulong address = _address.Get<ulong>();
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Pointer.Set: Writing 0x{value:X} to address 0x{address:X16}");
            
            _process.WriteMemory(address, value);
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Pointer.Set: Successfully wrote 0x{value:X} to address 0x{address:X16}");
        }

        public IOperand GetPositionOperand()
        {
            return _address;
        }
    }
}
[file content end]
