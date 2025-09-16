// Value.cs
using Ryujinx.HLE.HOS.Tamper.Operations;
using System;

namespace Ryujinx.HLE.HOS.Tamper
{
    class Value<TP> : IOperand where TP : unmanaged
    {
        private TP _value;

        public Value(TP value)
        {
            _value = value;
        }

        public T Get<T>() where T : unmanaged
        {
            // 避免使用动态类型转换
            if (typeof(T) == typeof(byte) && typeof(TP) == typeof(byte))
                return (T)(object)_value;
            else if (typeof(T) == typeof(ushort) && typeof(TP) == typeof(ushort))
                return (T)(object)_value;
            else if (typeof(T) == typeof(uint) && typeof(TP) == typeof(uint))
                return (T)(object)_value;
            else if (typeof(T) == typeof(ulong) && typeof(TP) == typeof(ulong))
                return (T)(object)_value;
            else if (typeof(T) == typeof(byte) && typeof(TP) == typeof(ushort))
                return (T)(object)(byte)(ushort)(object)_value;
            else if (typeof(T) == typeof(byte) && typeof(TP) == typeof(uint))
                return (T)(object)(byte)(uint)(object)_value;
            else if (typeof(T) == typeof(byte) && typeof(TP) == typeof(ulong))
                return (T)(object)(byte)(ulong)(object)_value;
            else if (typeof(T) == typeof(ushort) && typeof(TP) == typeof(byte))
                return (T)(object)(ushort)(byte)(object)_value;
            else if (typeof(T) == typeof(ushort) && typeof(TP) == typeof(uint))
                return (T)(object)(ushort)(uint)(object)_value;
            else if (typeof(T) == typeof(ushort) && typeof(TP) == typeof(ulong))
                return (T)(object)(ushort)(ulong)(object)_value;
            else if (typeof(T) == typeof(uint) && typeof(TP) == typeof(byte))
                return (T)(object)(uint)(byte)(object)_value;
            else if (typeof(T) == typeof(uint) && typeof(TP) == typeof(ushort))
                return (T)(object)(uint)(ushort)(object)_value;
            else if (typeof(T) == typeof(uint) && typeof(TP) == typeof(ulong))
                return (T)(object)(uint)(ulong)(object)_value;
            else if (typeof(T) == typeof(ulong) && typeof(TP) == typeof(byte))
                return (T)(object)(ulong)(byte)(object)_value;
            else if (typeof(T) == typeof(ulong) && typeof(TP) == typeof(ushort))
                return (T)(object)(ulong)(ushort)(object)_value;
            else if (typeof(T) == typeof(ulong) && typeof(TP) == typeof(uint))
                return (T)(object)(ulong)(uint)(object)_value;
            else
                throw new NotSupportedException($"Conversion from {typeof(TP)} to {typeof(T)} is not supported in Value.Get");
        }

        public void Set<T>(T value) where T : unmanaged
        {
            // 避免使用动态类型转换
            if (typeof(T) == typeof(byte) && typeof(TP) == typeof(byte))
                _value = (TP)(object)(byte)(object)value;
            else if (typeof(T) == typeof(ushort) && typeof(TP) == typeof(ushort))
                _value = (TP)(object)(ushort)(object)value;
            else if (typeof(T) == typeof(uint) && typeof(TP) == typeof(uint))
                _value = (TP)(object)(uint)(object)value;
            else if (typeof(T) == typeof(ulong) && typeof(TP) == typeof(ulong))
                _value = (TP)(object)(ulong)(object)value;
            else if (typeof(T) == typeof(byte) && typeof(TP) == typeof(ushort))
                _value = (TP)(object)(ushort)(byte)(object)value;
            else if (typeof(T) == typeof(byte) && typeof(TP) == typeof(uint))
                _value = (TP)(object)(uint)(byte)(object)value;
            else if (typeof(T) == typeof(byte) && typeof(TP) == typeof(ulong))
                _value = (TP)(object)(ulong)(byte)(object)value;
            else if (typeof(T) == typeof(ushort) && typeof(TP) == typeof(byte))
                _value = (TP)(object)(byte)(ushort)(object)value;
            else if (typeof(T) == typeof(ushort) && typeof(TP) == typeof(uint))
                _value = (TP)(object)(uint)(ushort)(object)value;
            else if (typeof(T) == typeof(ushort) && typeof(TP) == typeof(ulong))
                _value = (TP)(object)(ulong)(ushort)(object)value;
            else if (typeof(T) == typeof(uint) && typeof(TP) == typeof(byte))
                _value = (TP)(object)(byte)(uint)(object)value;
            else if (typeof(T) == typeof(uint) && typeof(TP) == typeof(ushort))
                _value = (TP)(object)(ushort)(uint)(object)value;
            else if (typeof(T) == typeof(uint) && typeof(TP) == typeof(ulong))
                _value = (TP)(object)(ulong)(uint)(object)value;
            else if (typeof(T) == typeof(ulong) && typeof(TP) == typeof(byte))
                _value = (TP)(object)(byte)(ulong)(object)value;
            else if (typeof(T) == typeof(ulong) && typeof(TP) == typeof(ushort))
                _value = (TP)(object)(ushort)(ulong)(object)value;
            else if (typeof(T) == typeof(ulong) && typeof(TP) == typeof(uint))
                _value = (TP)(object)(uint)(ulong)(object)value;
            else
                throw new NotSupportedException($"Conversion from {typeof(T)} to {typeof(TP)} is not supported in Value.Set");
        }
        
        public override string ToString()
        {
            return $"Value<{typeof(TP).Name}>=0x{FormatValue(_value)}";
        }
        
        // 格式化值为十六进制字符串
        private string FormatValue<T>(T value) where T : unmanaged
        {
            if (typeof(T) == typeof(byte))
                return $"{(byte)(object)value:X2}";
            else if (typeof(T) == typeof(ushort))
                return $"{(ushort)(object)value:X4}";
            else if (typeof(T) == typeof(uint))
                return $"{(uint)(object)value:X8}";
            else if (typeof(T) == typeof(ulong))
                return $"{(ulong)(object)value:X16}";
            else
                return value.ToString();
        }
    }
}
