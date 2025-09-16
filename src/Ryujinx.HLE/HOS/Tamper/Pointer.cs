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
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Pointer created with address operand: {address}");
        }

        public T Get<T>() where T : unmanaged
        {
            try
            {
                ulong address = _address.Get<ulong>();
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer.Get: Reading {typeof(T).Name} from address 0x{address:X16}");
                
                T value = _process.ReadMemory<T>(address);
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer.Get: Read 0x{value:X} from address 0x{address:X16}");
                
                return value;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, 
                    $"Pointer.Get: Error reading memory - {ex.Message}");
                throw;
            }
        }

        public void Set<T>(T value) where T : unmanaged
        {
            try
            {
                ulong address = _address.Get<ulong>();
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer.Set: Writing 0x{value:X} ({typeof(T).Name}) to address 0x{address:X16}");
                
                _process.WriteMemory(address, value);
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer.Set: Wrote 0x{value:X} to address 0x{address:X16}");
                
                // 可选：验证写入是否成功
                T verifyValue = _process.ReadMemory<T>(address);
                if (!verifyValue.Equals(value))
                {
                    Logger.Warning?.Print(LogClass.TamperMachine, 
                        $"Pointer.Set: Write verification failed! Expected 0x{value:X}, got 0x{verifyValue:X}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, 
                    $"Pointer.Set: Error writing memory - {ex.Message}");
                throw;
            }
        }
        
        public override string ToString()
        {
            try
            {
                ulong address = _address.Get<ulong>();
                return $"Pointer[0x{address:X16}]";
            }
            catch
            {
                return "Pointer[Unable to resolve address]";
            }
        }
    }
}
