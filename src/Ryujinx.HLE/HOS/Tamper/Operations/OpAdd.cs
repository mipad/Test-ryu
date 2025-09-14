using System;

namespace Ryujinx.HLE.HOS.Tamper.Operations
{
    class OpAdd<T> : IOperation where T : unmanaged
    {
        readonly IOperand _destination;
        readonly IOperand _lhs;
        readonly IOperand _rhs;

        public OpAdd(IOperand destination, IOperand lhs, IOperand rhs)
        {
            _destination = destination;
            _lhs = lhs;
            _rhs = rhs;
        }

        public void Execute()
        {
            T lhsValue = _lhs.Get<T>();
            T rhsValue = _rhs.Get<T>();
            T result;
            
            if (typeof(T) == typeof(byte))
            {
                byte lhsByte = (byte)(object)lhsValue;
                byte rhsByte = (byte)(object)rhsValue;
                result = (T)(object)(byte)(lhsByte + rhsByte);
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort lhsUShort = (ushort)(object)lhsValue;
                ushort rhsUShort = (ushort)(object)rhsValue;
                result = (T)(object)(ushort)(lhsUShort + rhsUShort);
            }
            else if (typeof(T) == typeof(uint))
            {
                uint lhsUInt = (uint)(object)lhsValue;
                uint rhsUInt = (uint)(object)rhsValue;
                result = (T)(object)(uint)(lhsUInt + rhsUInt);
            }
            else if (typeof(T) == typeof(ulong))
            {
                ulong lhsULong = (ulong)(object)lhsValue;
                ulong rhsULong = (ulong)(object)rhsValue;
                result = (T)(object)(ulong)(lhsULong + rhsULong);
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported for ADD operation");
            }
            
            _destination.Set(result);
        }
    }
}
