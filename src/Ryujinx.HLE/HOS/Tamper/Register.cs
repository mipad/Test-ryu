using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Tamper.Operations;
using System;

namespace Ryujinx.HLE.HOS.Tamper
{
    class Register : IOperand
    {
        private ulong _register = 0;
        private readonly string _alias;
        private readonly string _creationContext;

        public Register(string alias, string creationContext = "")
        {
            _alias = alias;
            _creationContext = !string.IsNullOrEmpty(creationContext) ? 
                creationContext : GetCallerContext();
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Created register: {_alias} (Context: {_creationContext})");
            
            // 记录初始值
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"{_alias} initial value: 0x{_register:X16}");
        }

        public T Get<T>() where T : unmanaged
        {
            try
            {
                // 避免使用动态类型转换
                if (typeof(T) == typeof(byte))
                {
                    byte value = (byte)_register;
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias}.Get<byte>: 0x{value:X2} (Full register: 0x{_register:X16})");
                    return (T)(object)value;
                }
                else if (typeof(T) == typeof(ushort))
                {
                    ushort value = (ushort)_register;
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias}.Get<ushort>: 0x{value:X4} (Full register: 0x{_register:X16})");
                    return (T)(object)value;
                }
                else if (typeof(T) == typeof(uint))
                {
                    uint value = (uint)_register;
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias}.Get<uint>: 0x{value:X8} (Full register: 0x{_register:X16})");
                    return (T)(object)value;
                }
                else if (typeof(T) == typeof(ulong))
                {
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias}.Get<ulong>: 0x{_register:X16}");
                    return (T)(object)_register;
                }
                else
                    throw new NotSupportedException($"Type {typeof(T)} is not supported in Register.Get");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, 
                    $"{_alias}.Get<{typeof(T).Name}> failed: {ex.Message}");
                throw;
            }
        }

        public void Set<T>(T value) where T : unmanaged
        {
            try
            {
                ulong oldValue = _register;
                
                // 避免使用动态类型转换
                if (typeof(T) == typeof(byte))
                {
                    byte byteValue = (byte)(object)value;
                    _register = byteValue;
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias}.Set<byte>: 0x{oldValue:X16} -> 0x{byteValue:X2}");
                }
                else if (typeof(T) == typeof(ushort))
                {
                    ushort ushortValue = (ushort)(object)value;
                    _register = ushortValue;
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias}.Set<ushort>: 0x{oldValue:X16} -> 0x{ushortValue:X4}");
                }
                else if (typeof(T) == typeof(uint))
                {
                    uint uintValue = (uint)(object)value;
                    _register = uintValue;
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias}.Set<uint>: 0x{oldValue:X16} -> 0x{uintValue:X8}");
                }
                else if (typeof(T) == typeof(ulong))
                {
                    ulong ulongValue = (ulong)(object)value;
                    _register = ulongValue;
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias}.Set<ulong>: 0x{oldValue:X16} -> 0x{ulongValue:X16}");
                }
                else
                    throw new NotSupportedException($"Type {typeof(T)} is not supported in Register.Set");
                
                // 验证设置是否成功
                if (oldValue != _register)
                {
                    Logger.Debug?.Print(LogClass.TamperMachine, 
                        $"{_alias} value changed successfully");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.TamperMachine, 
                        $"{_alias} value did not change after Set operation");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, 
                    $"{_alias}.Set<{typeof(T).Name}> failed: {ex.Message}");
                throw;
            }
        }
        
        public override string ToString()
        {
            return $"{_alias}=0x{_register:X16}";
        }
        
        // 获取调用者上下文信息
        private string GetCallerContext()
        {
            try
            {
                System.Diagnostics.StackTrace stackTrace = new System.Diagnostics.StackTrace(2, true);
                System.Diagnostics.StackFrame frame = stackTrace.GetFrame(0);
                
                if (frame != null)
                {
                    var method = frame.GetMethod();
                    return $"{method.DeclaringType?.Name}.{method.Name} (line {frame.GetFileLineNumber()})";
                }
            }
            catch
            {
                // 忽略获取调用上下文时的错误
            }
            
            return "Unknown context";
        }
    }
}
