using System;
using System.Runtime.CompilerServices;

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
            // 使用 Unsafe 类进行高效且类型安全的转换
            if (typeof(T) == typeof(TP))
            {
                return Unsafe.As<TP, T>(ref _value);
            }
            
            // 对于不同类型之间的转换，使用标准的转换方法
            return ConvertValue<T>(_value);
        }

        public void Set<T>(T value) where T : unmanaged
        {
            // 使用 Unsafe 类进行高效且类型安全的转换
            if (typeof(T) == typeof(TP))
            {
                _value = Unsafe.As<T, TP>(ref value);
                return;
            }
            
            // 对于不同类型之间的转换，使用标准的转换方法
            _value = ConvertValue<TP>(value);
        }

        private static TTarget ConvertValue<TSource, TTarget>(TSource value)
            where TSource : unmanaged
            where TTarget : unmanaged
        {
            // 实现标准的值转换逻辑
            // 可以根据需要添加具体的转换规则
            if (typeof(TTarget) == typeof(byte)) return (TTarget)(object)Convert.ToByte(value);
            if (typeof(TTarget) == typeof(ushort)) return (TTarget)(object)Convert.ToUInt16(value);
            if (typeof(TTarget) == typeof(uint)) return (TTarget)(object)Convert.ToUInt32(value);
            if (typeof(TTarget) == typeof(ulong)) return (TTarget)(object)Convert.ToUInt64(value);
            
            throw new NotSupportedException($"Conversion from {typeof(TSource)} to {typeof(TTarget)} is not supported");
        }

        // 重载方法以便于使用
        private static T ConvertValue<T>(object value) where T : unmanaged
        {
            if (value is byte b) return (T)Convert.ChangeType(b, typeof(T));
            if (value is ushort s) return (T)Convert.ChangeType(s, typeof(T));
            if (value is uint i) return (T)Convert.ChangeType(i, typeof(T));
            if (value is ulong l) return (T)Convert.ChangeType(l, typeof(T));
            
            throw new NotSupportedException($"Conversion from {value.GetType()} to {typeof(T)} is not supported");
        }
    }
}
