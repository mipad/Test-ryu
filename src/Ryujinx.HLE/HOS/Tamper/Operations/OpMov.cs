using Ryujinx.Common.Logging;
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
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Created OpMov<{typeof(T).Name}> with destination: {destination}, source: {source}");
        }

        public void Execute()
        {
            try
            {
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Executing OpMov<{typeof(T).Name}> from {_source} to {_destination}");
                
                // 使用显式类型转换而不是依赖 dynamic
                if (typeof(T) == typeof(byte))
                {
                    byte value = _source.Get<byte>();
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"Moving byte value: {value}");
                    _destination.Set(value);
                }
                else if (typeof(T) == typeof(ushort))
                {
                    ushort value = _source.Get<ushort>();
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"Moving ushort value: {value}");
                    _destination.Set(value);
                }
                else if (typeof(T) == typeof(uint))
                {
                    uint value = _source.Get<uint>();
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"Moving uint value: {value}");
                    _destination.Set(value);
                }
                else if (typeof(T) == typeof(ulong))
                {
                    ulong value = _source.Get<ulong>();
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"Moving ulong value: {value}");
                    _destination.Set(value);
                }
                else
                {
                    throw new NotSupportedException($"Type {typeof(T)} is not supported for MOV operation");
                }
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"OpMov<{typeof(T).Name}> completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, 
                    $"Error in OpMov<{typeof(T).Name}> execution: {ex.Message}");
                throw;
            }
        }
    }
}
