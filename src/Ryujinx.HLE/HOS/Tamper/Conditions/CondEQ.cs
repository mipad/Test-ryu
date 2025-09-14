using Ryujinx.HLE.HOS.Tamper.Operations;
using System;

namespace Ryujinx.HLE.HOS.Tamper.Conditions
{
    class CondEQ<T> : ICondition where T : unmanaged
    {
        private readonly IOperand _lhs;
        private readonly IOperand _rhs;

        public CondEQ(IOperand lhs, IOperand rhs)
        {
            _lhs = lhs;
            _rhs = rhs;
        }

        public bool Evaluate()
        {
            T lhsValue = _lhs.Get<T>();
            T rhsValue = _rhs.Get<T>();
            
            if (typeof(T) == typeof(byte))
            {
                byte lhsByte = (byte)(object)lhsValue;
                byte rhsByte = (byte)(object)rhsValue;
                return lhsByte == rhsByte;
            }
            else if (typeof(T) == typeof(ushort))
            {
                ushort lhsUShort = (ushort)(object)lhsValue;
                ushort rhsUShort = (ushort)(object)rhsValue;
                return lhsUShort == rhsUShort;
            }
            else if (typeof(T) == typeof(uint))
            {
                uint lhsUInt = (uint)(object)lhsValue;
                uint rhsUInt = (uint)(object)rhsValue;
                return lhsUInt == rhsUInt;
            }
            else if (typeof(T) == typeof(ulong))
            {
                ulong lhsULong = (ulong)(object)lhsValue;
                ulong rhsULong = (ulong)(object)rhsValue;
                return lhsULong == rhsULong;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported for EQ condition");
            }
        }
    }
}
