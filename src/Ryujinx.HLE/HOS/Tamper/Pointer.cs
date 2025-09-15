using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Tamper.Operations;
using System;
using System.Diagnostics;

namespace Ryujinx.HLE.HOS.Tamper
{
    class Pointer : IOperand
    {
        private readonly IOperand _address;
        private readonly ITamperedProcess _process;
        private readonly string _creationContext;

        public Pointer(IOperand address, ITamperedProcess process, string creationContext = "")
        {
            _address = address;
            _process = process;
            
            // 记录创建上下文（调用堆栈）
            _creationContext = !string.IsNullOrEmpty(creationContext) ? 
                creationContext : GetCallerContext();
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"Pointer created for context: {_creationContext}");
        }

        public T Get<T>() where T : unmanaged
        {
            try
            {
                ulong address = _address.Get<ulong>();
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer.Get: Attempting to read {typeof(T).Name} from address 0x{address:X16}");
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer context: {_creationContext}");
                
                T value = _process.ReadMemory<T>(address);
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer.Get: Successfully read 0x{value:X} from address 0x{address:X16}");
                
                return value;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, 
                    $"Pointer.Get: Error reading memory - {ex.Message}");
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public void Set<T>(T value) where T : unmanaged
        {
            try
            {
                ulong address = _address.Get<ulong>();
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer.Set: Attempting to write 0x{value:X} ({typeof(T).Name}) to address 0x{address:X16}");
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer context: {_creationContext}");
                
                _process.WriteMemory(address, value);
                
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Pointer.Set: Successfully wrote 0x{value:X} to address 0x{address:X16}");
                
                // 验证写入是否成功
                T verifyValue = _process.ReadMemory<T>(address);
                if (!verifyValue.Equals(value))
                {
                    Logger.Warning?.Print(LogClass.TamperMachine, 
                        $"Pointer.Set: Write verification failed! Expected 0x{value:X}, got 0x{verifyValue:X}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, 
                    $"Pointer.Set: Error writing memory - {ex.Message}");
                Logger.Debug?.Print(LogClass.TamperMachine, 
                    $"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public IOperand GetPositionOperand()
        {
            return _address;
        }
        
        // 获取调用者上下文信息
        private string GetCallerContext()
        {
            try
            {
                StackTrace stackTrace = new StackTrace(2, true); // 跳过2帧（当前方法和构造函数）
                StackFrame frame = stackTrace.GetFrame(0);
                
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
        
        // 添加ToString方法以便调试
        public override string ToString()
        {
            try
            {
                ulong address = _address.Get<ulong>();
                return $"Pointer[0x{address:X16}] ({_creationContext})";
            }
            catch
            {
                return $"Pointer[Unable to resolve address] ({_creationContext})";
            }
        }
    }
}
