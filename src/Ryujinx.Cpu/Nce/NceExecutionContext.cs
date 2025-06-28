using ARMeilleure.State;
using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Cpu.Nce
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("windows")]
    class NceExecutionContext : IExecutionContext, IDisposable
    {
        private const ulong AlternateStackSize = 0x4000;

        private readonly NceNativeContext _context;
        private readonly ExceptionCallbacks _exceptionCallbacks;
        private bool _disposed;

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
            storage.CtrEl0 = 0x8444c004;

            Running = true;
            _exceptionCallbacks = exceptionCallbacks;
        }

        public ulong GetX(int index) => _context.GetStorage().X[index];
        public void SetX(int index, ulong value) => _context.GetStorage().X[index] = value;

        public V128 GetV(int index) => _context.GetStorage().V[index];
        public void SetV(int index, V128 value) => _context.GetStorage().V[index] = value;

        public bool GetPstateFlag(PState flag) => false;
        public void SetPstateFlag(PState flag, bool value) { }

        public bool GetFPstateFlag(FPState flag) => false;
        public void SetFPstateFlag(FPState flag, bool value) { }

        public void SetStartAddress(ulong address)
        {
            ref var storage = ref _context.GetStorage();
            storage.X[30] = address;
            storage.HostThreadHandle = NceThreadPal.GetCurrentThreadHandle();

            RegisterAlternateStack();
        }

        public void Exit()
        {
            _context.GetStorage().HostThreadHandle = IntPtr.Zero;

            UnregisterAlternateStack();
        }

        private void RegisterAlternateStack()
        {
            if (OperatingSystem.IsWindows() || 
                OperatingSystem.IsLinux() || 
                OperatingSystem.IsMacOS() || 
                OperatingSystem.IsAndroid())
            {
                _alternateStackMemory = new MemoryBlock(AlternateStackSize);
                NativeSignalHandler.InstallUnixAlternateStackForCurrentThread(
                    _alternateStackMemory.GetPointer(0UL, AlternateStackSize), 
                    AlternateStackSize);
            }
        }

        private void UnregisterAlternateStack()
        {
            if (OperatingSystem.IsWindows() || 
                OperatingSystem.IsLinux() || 
                OperatingSystem.IsMacOS() || 
                OperatingSystem.IsAndroid())
            {
                NativeSignalHandler.UninstallUnixAlternateStackForCurrentThread();
                _alternateStackMemory?.Dispose();
                _alternateStackMemory = null;
            }
        }

        public bool OnSupervisorCall(int imm)
        {
            _exceptionCallbacks.SupervisorCallback?.Invoke(this, 0UL, imm);
            return Running;
        }

        public bool OnInterrupt()
        {
            _exceptionCallbacks.InterruptCallback?.Invoke(this);
            return Running;
        }

        public void RequestInterrupt()
        {
            if (_disposed)
            {
                return;
            }

            IntPtr threadHandle = _context.GetStorage().HostThreadHandle;
            if (threadHandle != IntPtr.Zero)
            {
                ref uint inManaged = ref _context.GetStorage().InManaged;
                uint oldValue = Interlocked.Or(ref inManaged, 2);

                if (oldValue == 0 && 
                    (OperatingSystem.IsWindows() || 
                     OperatingSystem.IsLinux() || 
                     OperatingSystem.IsMacOS() || 
                     OperatingSystem.IsAndroid()))
                {
                    NceThreadPal.SuspendThread(threadHandle);
                }
            }
        }

        public void StopRunning()
        {
            Running = false;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _context.Dispose();
                
                if (_alternateStackMemory != null)
                {
                    _alternateStackMemory.Dispose();
                    _alternateStackMemory = null;
                }
            }
        }
    }
}
