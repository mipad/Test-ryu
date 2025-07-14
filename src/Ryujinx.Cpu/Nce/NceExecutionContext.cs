using ARMeilleure.State;
using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Cpu.Nce
{
    class NceExecutionContext : IExecutionContext
    {
        private const ulong AlternateStackSize = 0x4000;

        private readonly NceNativeContext _context;
        private readonly ExceptionCallbacks _exceptionCallbacks;

        // === 新增诊断字段 ===
        private ulong _lastNullAccessPc;
        private int _nullAccessCount;
        private readonly Stopwatch _diagnosticTimer = Stopwatch.StartNew();
        private ulong _startAddress;
        // ===================
        
        internal IntPtr NativeContextPtr => _context.BasePtr;

        public ulong Pc => 0UL;

        public long TpidrEl0
        {
            get => (long)_context.GetStorage().TpidrEl0;
            set => _context.GetStorage().TpidrEl0 = (ulong)value;
        }

        public long TpidrroEl0
        {
            get => (long)_context.GetStorage().TpidrroEl0;
            set => _context.GetStorage().TpidrroEl0 = (ulong)value;
        }

        public uint Pstate
        {
            get => _context.GetStorage().Pstate;
            set => _context.GetStorage().Pstate = value;
        }

        public uint Fpcr
        {
            get => _context.GetStorage().Fpcr;
            set => _context.GetStorage().Fpcr = value;
        }

        public uint Fpsr
        {
            get => _context.GetStorage().Fpsr;
            set => _context.GetStorage().Fpsr = value;
        }

        public bool IsAarch32
        {
            get => false;
            set
            {
                if (value)
                {
                    throw new NotSupportedException();
                }
            }
        }

        public bool Running { get; private set; }

        private delegate bool SupervisorCallHandler(int imm);
        private SupervisorCallHandler _svcHandler;

        private MemoryBlock _alternateStackMemory;

        public NceExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            _svcHandler = OnSupervisorCall;
            IntPtr svcHandlerPtr = Marshal.GetFunctionPointerForDelegate(_svcHandler);

            _context = new NceNativeContext();

            ref var storage = ref _context.GetStorage();
            storage.SvcCallHandler = svcHandlerPtr;
            storage.InManaged = 1u;
            storage.CtrEl0 = 0x8444c004; // TODO: Get value from host CPU instead of using guest one?

            Running = true;
            _exceptionCallbacks = exceptionCallbacks;
            
            // === 新增：初始化诊断计数器 ===
            _nullAccessCount = 0;
            _lastNullAccessPc = 0;
            // =============================
        }

        public ulong GetX(int index) => _context.GetStorage().X[index];
        
        public void SetX(int index, ulong value)
        {
            // === 新增：空寄存器写入检测 ===
            if (value == 0 && (index == 0 || index == 30)) // X0 或 LR(X30)
            {
                LogNullRegisterWrite(index, "X");
            }
            // ===========================
            _context.GetStorage().X[index] = value;
        }

        public V128 GetV(int index) => _context.GetStorage().V[index];
        
        public void SetV(int index, V128 value)
        {
            // === 修复：使用全零检查代替 IsZero() ===
            if (value == V128.Zero && index == 0) // 检查是否为全零向量
            {
                LogNullRegisterWrite(index, "V");
            }
            // =====================================
            _context.GetStorage().V[index] = value;
        }

        // TODO
        public bool GetPstateFlag(PState flag) => false;
        public void SetPstateFlag(PState flag, bool value) { }

        // TODO
        public bool GetFPstateFlag(FPState flag) => false;
        public void SetFPstateFlag(FPState flag, bool value) { }

        public void SetStartAddress(ulong address)
        {
            // === 记录启动地址用于诊断 ===
            _startAddress = address;
            // ===========================
            
            ref var storage = ref _context.GetStorage();
            storage.X[30] = address;
            storage.HostThreadHandle = NceThreadPal.GetCurrentThreadHandle();

            RegisterAlternateStack();
            
            // === 新增：启动日志 ===
            Logger.Debug?.Print(LogClass.Cpu, 
                $"[NCE] Execution started at 0x{address:X}, Thread: {Environment.CurrentManagedThreadId}");
            // ======================
        }

        public void Exit()
        {
            _context.GetStorage().HostThreadHandle = IntPtr.Zero;

            UnregisterAlternateStack();
            
            // === 新增：退出日志 ===
            Logger.Debug?.Print(LogClass.Cpu, 
                $"[NCE] Execution exited, Start: 0x{_startAddress:X}, NullWrites: {_nullAccessCount}");
            // =====================
        }

        private void RegisterAlternateStack()
        {
            // We need to use an alternate stack to handle the suspend signal,
            // as the guest stack may be in a state that is not suitable for the signal handlers.

            _alternateStackMemory = new MemoryBlock(AlternateStackSize);
            NativeSignalHandler.InstallUnixAlternateStackForCurrentThread(_alternateStackMemory.GetPointer(0UL, AlternateStackSize), AlternateStackSize);
        }

        private void UnregisterAlternateStack()
        {
            NativeSignalHandler.UninstallUnixAlternateStackForCurrentThread();
            _alternateStackMemory.Dispose();
            _alternateStackMemory = null;
        }

        public bool OnSupervisorCall(int imm)
        {
            // === 新增：系统调用诊断 ===
            if (imm == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"[NCE SVC] Null immediate: imm=0x0, Thread: {Environment.CurrentManagedThreadId}");
            }
            
            // 捕获系统调用时的寄存器状态
            if (_nullAccessCount > 0)
            {
                Logger.Debug?.Print(LogClass.Cpu, 
                    $"[NCE SVC] Previous null writes: {_nullAccessCount}\n" +
                    $"Registers:\n{CaptureRegisterState()}");
            }
            // =========================
            
            _exceptionCallbacks.SupervisorCallback?.Invoke(this, 0UL, imm);
            return Running;
        }

        public bool OnInterrupt()
        {
            // === 新增：中断时诊断 ===
            if (_nullAccessCount > 0)
            {
                Logger.Info?.Print(LogClass.Cpu, 
                    $"[NCE INT] Interrupt with {_nullAccessCount} null writes\n" +
                    $"Full State:\n{CaptureRegisterState()}");
            }
            // ======================
            
            _exceptionCallbacks.InterruptCallback?.Invoke(this);
            return Running;
        }

        public void RequestInterrupt()
        {
            // === 新增：中断请求诊断 ===
            Logger.Debug?.Print(LogClass.Cpu, 
                $"[NCE] Interrupt requested, NullWrites: {_nullAccessCount}, Thread: {Environment.CurrentManagedThreadId}");
            // =========================
            
            IntPtr threadHandle = _context.GetStorage().HostThreadHandle;
            if (threadHandle != IntPtr.Zero)
            {
                ref uint inManaged = ref _context.GetStorage().InManaged;
                uint oldValue = Interlocked.Or(ref inManaged, 2);

                if (oldValue == 0)
                {
                    // === 新增：线程挂起日志 ===
                    Logger.Trace?.Print(LogClass.Cpu, 
                        $"[NCE] Suspending thread {threadHandle.ToInt64():X}");
                    // ========================
                    
                    NceThreadPal.SuspendThread(threadHandle);
                }
            }
        }

        public void StopRunning()
        {
            // === 新增：停止时诊断报告 ===
            if (_nullAccessCount > 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"[NCE STOP] Execution stopped with {_nullAccessCount} null writes");
            }
            // ==========================
            
            Running = false;
        }

        public void Dispose()
        {
            // === 新增：释放时最终报告 ===
            if (_nullAccessCount > 0)
            {
                Logger.Info?.Print(LogClass.Cpu, 
                    $"[NCE DISPOSE] Context disposed. Total null writes: {_nullAccessCount}");
            }
            // ==========================
            
            _context.Dispose();
        }
        
        // === 新增诊断方法 ===
        
        /// <summary>
        /// 记录空寄存器写入事件
        /// </summary>
        private void LogNullRegisterWrite(int index, string regType)
        {
            _nullAccessCount++;
            _lastNullAccessPc = _startAddress; // NCE中没有直接PC访问
            
            string callStack = CaptureCallStack(5); // 捕获最近5帧
            
            Logger.Warning?.Print(LogClass.Cpu, 
                $"[NCE NULL WRITE] {regType}{index}=0, " +
                $"StartPC=0x{_startAddress:X}, Count={_nullAccessCount}, " +
                $"Thread: {Environment.CurrentManagedThreadId}\n" +
                $"Call Stack:\n{callStack}");
        }
        
        /// <summary>
        /// 捕获调用堆栈
        /// </summary>
        private string CaptureCallStack(int maxFrames)
        {
            try
            {
                var stackTrace = new StackTrace(skipFrames: 2, fNeedFileInfo: true);
                var sb = new StringBuilder();
                
                for (int i = 0; i < Math.Min(stackTrace.FrameCount, maxFrames); i++)
                {
                    var frame = stackTrace.GetFrame(i);
                    var method = frame.GetMethod();
                    sb.AppendLine($"  {method.DeclaringType?.Name}.{method.Name}");
                }
                
                return sb.ToString();
            }
            catch
            {
                return "Stack capture failed";
            }
        }
        
        /// <summary>
        /// 捕获完整寄存器状态
        /// </summary>
        public string CaptureRegisterState()
        {
            try
            {
                var sb = new StringBuilder();
                ref var storage = ref _context.GetStorage();
                
                // 通用寄存器
                for (int i = 0; i < 31; i++)
                {
                    ulong value = storage.X[i];
                    sb.AppendLine($"X{i}: 0x{value:X16}" + (value == 0 ? " [NULL]" : ""));
                }
                
                // 向量寄存器
                for (int i = 0; i < 32; i++)
                {
                    if (i % 8 == 0) sb.AppendLine();
                    sb.Append($"V{i}: {storage.V[i]} ");
                }
                sb.AppendLine();
                
                // 系统寄存器
                sb.AppendLine($"TpidrEl0: 0x{storage.TpidrEl0:X16}");
                sb.AppendLine($"TpidrroEl0: 0x{storage.TpidrroEl0:X16}");
                sb.AppendLine($"Pstate: 0x{storage.Pstate:X8}");
                sb.AppendLine($"Fpcr: 0x{storage.Fpcr:X8}");
                sb.AppendLine($"Fpsr: 0x{storage.Fpsr:X8}");
                
                return sb.ToString();
            }
            catch
            {
                return "Register capture failed";
            }
        }
        
        /// <summary>
        /// 获取空访问诊断信息
        /// </summary>
        public string GetNullAccessDiagnostics()
        {
            return $"Null writes: {_nullAccessCount}, " +
                   $"Last at PC: 0x{_lastNullAccessPc:X}, " +
                   $"Thread: {Environment.CurrentManagedThreadId}";
        }
    }
}
