// Pointer.cs
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
            try
            {
                ulong address = _address.Get<ulong>();
                return _process.ReadMemory<T>(address);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error reading memory: {ex.Message}");
            }
        }

        public void Set<T>(T value) where T : unmanaged
        {
            try
            {
                ulong address = _address.Get<ulong>();
                _process.WriteMemory(address, value);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error writing memory: {ex.Message}");
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
