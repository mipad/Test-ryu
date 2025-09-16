using Ryujinx.Common.Logging;
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
            try
            {
                T lhsValue = _lhs.Get<T>();
                T rhsValue = _rhs.Get<T>();
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"OpAdd<{typeof(T).Name}>.Execute: {FormatValue(lhsValue)} + {FormatValue(rhsValue)}");
                
                // 使用 switch 表达式进行类型安全的加法运算
                T result = typeof(T).Name switch
                {
                    "Byte" => (T)(object)(byte)((byte)(object)lhsValue + (byte)(object)rhsValue),
                    "UInt16" => (T)(object)(ushort)((ushort)(object)lhsValue + (ushort)(object)rhsValue),
                    "UInt32" => (T)(object)(uint)((uint)(object)lhsValue + (uint)(object)rhsValue),
                    "UInt64" => (T)(object)(ulong)((ulong)(object)lhsValue + (ulong)(object)rhsValue),
                    _ => throw new NotSupportedException($"Type {typeof(T)} is not supported for ADD operation")
                };
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"OpAdd<{typeof(T).Name}>.Execute: result = {FormatValue(result)}");
                
                _destination.Set(result);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, 
                    $"OpAdd<{typeof(T).Name}>.Execute failed: {ex.Message}");
                throw;
            }
        }
        
        // 格式化值为十六进制字符串
        private string FormatValue<TValue>(TValue value) where TValue : unmanaged
        {
            if (typeof(TValue) == typeof(byte))
                return $"0x{(byte)(object)value:X2}";
            else if (typeof(TValue) == typeof(ushort))
                return $"0x{(ushort)(object)value:X4}";
            else if (typeof(TValue) == typeof(uint))
                return $"0x{(uint)(object)value:X8}";
            else if (typeof(TValue) == typeof(ulong))
                return $"0x{(ulong)(object)value:X16}";
            else
                return value.ToString();
        }
    }
}
