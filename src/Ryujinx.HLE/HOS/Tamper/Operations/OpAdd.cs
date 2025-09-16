// OpAdd.cs
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
            
            // 使用 switch 表达式进行类型安全的加法运算
            T result = typeof(T).Name switch
            {
                "Byte" => (T)(object)(byte)((byte)(object)lhsValue + (byte)(object)rhsValue),
                "UInt16" => (T)(object)(ushort)((ushort)(object)lhsValue + (ushort)(object)rhsValue),
                "UInt32" => (T)(object)(uint)((uint)(object)lhsValue + (uint)(object)rhsValue),
                "UInt64" => (T)(object)(ulong)((ulong)(object)lhsValue + (ulong)(object)rhsValue),
                _ => throw new NotSupportedException($"Type {typeof(T)} is not supported for ADD operation")
            };
            
            _destination.Set(result);
        }
    }
}
