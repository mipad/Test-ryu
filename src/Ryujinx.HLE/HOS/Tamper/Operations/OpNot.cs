using System;

namespace Ryujinx.HLE.HOS.Tamper.Operations
{
    class OpNot<T> : IOperation where T : unmanaged
    {
        readonly IOperand _destination;
        readonly IOperand _source;

        public OpNot(IOperand destination, IOperand source)
        {
            _destination = destination;
            _source = source;
        }

        public void Execute()
        {
            T sourceValue = _source.Get<T>();
            T result;
            
            if (typeof(T) == typeof(byte))
            {
                byte sourceByte = (byte)(object)sourceValue;
                result = (T)(object)(byte)(~sourceByte);
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort sourceUShort = (ushort)(object)sourceValue;
                result = (T)(object)(ushort)(~sourceUShort);
            }
            else if (typeof(T) == typeof(uint))
            {
                uint sourceUInt = (uint)(object)sourceValue;
                result = (T)(object)(uint)(~sourceUInt);
            }
            else if (typeof(T) == typeof(ulong))
            {
                ulong sourceULong = (ulong)(object)sourceValue;
                result = (T)(object)(ulong)(~sourceULong);
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported for NOT operation");
            }
            
            _destination.Set(result);
        }
    }
}
