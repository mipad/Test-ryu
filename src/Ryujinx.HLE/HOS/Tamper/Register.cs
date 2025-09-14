using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Tamper.Operations;
using System;

namespace Ryujinx.HLE.HOS.Tamper
{
    class Register : IOperand
    {
        private ulong _register = 0;
        private readonly string _alias;

        public Register(string alias)
        {
            _alias = alias;
            Logger.Debug?.Print(LogClass.TamperMachine, $"Created register: {_alias}");
        }

        public T Get<T>() where T : unmanaged
        {
            // 避免使用动态类型转换
            if (typeof(T) == typeof(byte))
            {
                byte value = (byte)_register;
                Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}.Get<byte>: 0x{value:X2}");
                return (T)(object)value;
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort value = (ushort)_register;
                Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}.Get<ushort>: 0x{value:X4}");
                return (T)(object)value;
            }
            else if (typeof(T) == typeof(uint))
            {
                uint value = (uint)_register;
                Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}.Get<uint>: 0x{value:X8}");
                return (T)(object)value;
            }
            else if (typeof(T) == typeof(ulong))
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}.Get<ulong>: 0x{_register:X16}");
                return (T)(object)_register;
            }
            else
                throw new NotSupportedException($"Type {typeof(T)} is not supported in Register.Get");
        }

        public void Set<T>(T value) where T : unmanaged
        {
            // 避免使用动态类型转换
            if (typeof(T) == typeof(byte))
            {
                byte byteValue = (byte)(object)value;
                Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}.Set<byte>: 0x{_register:X16} -> 0x{byteValue:X2}");
                _register = byteValue;
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort ushortValue = (ushort)(object)value;
                Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}.Set<ushort>: 0x{_register:X16} -> 0x{ushortValue:X4}");
                _register = ushortValue;
            }
            else if (typeof(T) == typeof(uint))
            {
                uint uintValue = (uint)(object)value;
                Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}.Set<uint>: 0x{_register:X16} -> 0x{uintValue:X8}");
                _register = uintValue;
            }
            else if (typeof(T) == typeof(ulong))
            {
                ulong ulongValue = (ulong)(object)value;
                Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}.Set<ulong>: 0x{_register:X16} -> 0x{ulongValue:X16}");
                _register = ulongValue;
            }
            else
                throw new NotSupportedException($"Type {typeof(T)} is not supported in Register.Set");
        }
        
        public override string ToString()
        {
            return $"{_alias}=0x{_register:X16}";
        }
    }
}
