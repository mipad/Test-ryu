// OpMov.cs
using System;

namespace Ryujinx.HLE.HOS.Tamper.Operations
{
    class OpMov<T> : IOperation where T : unmanaged
    {
        readonly IOperand _destination;
        readonly IOperand _source;

        public OpMov(IOperand destination, IOperand source)
        {
            _destination = destination;
            _source = source;
        }

        public void Execute()
        {
            // 使用显式类型转换而不是依赖 dynamic
            if (typeof(T) == typeof(byte))
            {
                byte value = _source.Get<byte>();
                _destination.Set(value);
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort value = _source.Get<ushort>();
                _destination.Set(value);
            }
            else if (typeof(T) == typeof(uint))
            {
                uint value = _source.Get<uint>();
                _destination.Set(value);
            }
            else if (typeof(T) == typeof(ulong))
            {
                ulong value = _source.Get<ulong>();
                _destination.Set(value);
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported for MOV operation");
            }
        }
    }
}
