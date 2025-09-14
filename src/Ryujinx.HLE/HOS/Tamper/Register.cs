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
        }

        public T Get<T>() where T : unmanaged
        {
            // 避免使用动态类型转换
            if (typeof(T) == typeof(byte))
                return (T)(object)(byte)_register;
            else if (typeof(T) == typeof(ushort))
                return (T)(object)(ushort)_register;
            else if (typeof(T) == typeof(uint))
                return (T)(object)(uint)_register;
            else if (typeof(T) == typeof(ulong))
                return (T)(object)_register;
            else
                throw new NotSupportedException($"Type {typeof(T)} is not supported in Register.Get");
        }

        public void Set<T>(T value) where T : unmanaged
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"{_alias}: {value}");

            // 避免使用动态类型转换
            if (typeof(T) == typeof(byte))
                _register = (byte)(object)value;
            else if (typeof(T) == typeof(ushort))
                _register = (ushort)(object)value;
            else if (typeof(T) == typeof(uint))
                _register = (uint)(object)value;
            else if (typeof(T) == typeof(ulong))
                _register = (ulong)(object)value;
            else
                throw new NotSupportedException($"Type {typeof(T)} is not supported in Register.Set");
        }
    }
}
