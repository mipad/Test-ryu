// Register.cs
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
            {
                byte value = (byte)_register;
                return (T)(object)value;
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort value = (ushort)_register;
                return (T)(object)value;
            }
            else if (typeof(T) == typeof(uint))
            {
                uint value = (uint)_register;
                return (T)(object)value;
            }
            else if (typeof(T) == typeof(ulong))
            {
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
                _register = byteValue;
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort ushortValue = (ushort)(object)value;
                _register = ushortValue;
            }
            else if (typeof(T) == typeof(uint))
            {
                uint uintValue = (uint)(object)value;
                _register = uintValue;
            }
            else if (typeof(T) == typeof(ulong))
            {
                ulong ulongValue = (ulong)(object)value;
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
