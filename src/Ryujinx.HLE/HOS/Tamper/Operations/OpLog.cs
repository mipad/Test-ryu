using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.HLE.HOS.Tamper.Operations
{
    class OpLog<T> : IOperation where T : unmanaged
    {
        readonly int _logId;
        readonly IOperand _source;

        public OpLog(int logId, IOperand source)
        {
            _logId = logId;
            _source = source;
        }

        public void Execute()
        {
            T value = _source.Get<T>();
            string formattedValue;
            
            if (typeof(T) == typeof(byte))
            {
                formattedValue = ((byte)(object)value).ToString("X2");
            }
            else if (typeof(T) == typeof(ushort))
            {
                formattedValue = ((ushort)(object)value).ToString("X4");
            }
            else if (typeof(T) == typeof(uint))
            {
                formattedValue = ((uint)(object)value).ToString("X8");
            }
            else if (typeof(T) == typeof(ulong))
            {
                formattedValue = ((ulong)(object)value).ToString("X16");
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported for logging");
            }
            
            Logger.Debug?.Print(LogClass.TamperMachine, $"Tamper debug log id={_logId} value={formattedValue}");
        }
    }
}
