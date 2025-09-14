using System;

namespace Ryujinx.HLE.HOS.Tamper.Operations
{
    class OpRsh<T> : IOperation where T : unmanaged
    {
        readonly IOperand _destination;
        readonly IOperand _lhs;
        readonly IOperand _rhs;

        public OpRsh(IOperand destination, IOperand lhs, IOperand rhs)
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
                result = (T)(object)(byte)(lhsByte >> (int)rhsByte);
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort lhsUShort = (ushort)(object)lhsValue;
                ushort rhsUShort = (ushort)(object)rhsValue;
                result = (T)(object)(ushort)(lhsUShort >> (int)rhsUShort);
            }
            else if (typeof(T) == typeof(uint))
            {
                uint lhsUInt = (uint)(object)lhsValue;
                uint rhsUInt = (uint)(object)rhsValue;
                result = (T)(object)(uint)(lhsUInt >> (int)rhsUInt);
            }
            else if (typeof(T) == typeof(ulong))
            {
                ulong lhsULong = (ulong)(object)lhsValue;
                ulong rhsULong = (ulong)(object)rhsValue;
                result = (T)(object)(ulong)(lhsULong >> (int)rhsULong);
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported for RSH operation");
            }
            
            _destination.Set(result);
        }
    }
}
