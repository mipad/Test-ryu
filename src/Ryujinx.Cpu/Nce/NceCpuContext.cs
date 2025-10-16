using Ryujinx.Cpu.Signal;
using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ryujinx.Common.Logging;
using Ryujinx.Common.SystemInterop;

namespace Ryujinx.Cpu.Nce
{
    class NceCpuContext : ICpuContext
    {
        private static uint[] _getTpidrEl0Code = new uint[]
        {
            GetMrsTpidrEl0(0), // mrs x0, tpidr_el0
            0xd65f03c0u, // ret
        };

        private static uint GetMrsTpidrEl0(uint rd)
        {
            if (OperatingSystem.IsMacOS())
            {
                return 0xd53bd060u | rd; // TPIDRRO
            }
            else
            {
                return 0xd53bd040u | rd; // TPIDR
            }
        }

        readonly struct CodeWriter
        {
            private readonly List<uint> _fullCode;

            public CodeWriter()
            {
                _fullCode = new List<uint>();
            }

            public ulong Write(uint[] code)
            {
                ulong offset = (ulong)_fullCode.Count * sizeof(uint);
                _fullCode.AddRange(code);

                return offset;
            }

            public MemoryBlock CreateMemoryBlock()
            {
                ReadOnlySpan<byte> codeBytes = MemoryMarshal.Cast<uint, byte>(_fullCode.ToArray());

                MemoryBlock codeBlock = new(BitUtils.AlignUp((ulong)codeBytes.Length, 0x1000UL));

                codeBlock.Write(0, codeBytes);
                codeBlock.Reprotect(0, (ulong)codeBytes.Length, MemoryPermission.ReadAndExecute, true);

                return codeBlock;
            }
        }

        private delegate void ThreadStart(IntPtr nativeContextPtr);
        private delegate IntPtr GetTpidrEl0();
        private static MemoryBlock _codeBlock;
        private static ThreadStart _threadStart;
        private static GetTpidrEl0 _getTpidrEl0;

        private readonly ITickSource _tickSource;
        private readonly ICpuMemoryManager _memoryManager;

        // 线程计数器，用于分配不同的CPU核心
        private static int _threadCounter = 0;
        private static readonly object _threadCounterLock = new object();

        static NceCpuContext()
        {
            Logger.Info?.Print(LogClass.Cpu, "[NceCpuContext] Static constructor started");
            
            // 初始化 CPU 亲和性支持
            Libc.Initialize();
            
            CodeWriter codeWriter = new();

            uint[] threadStartCode = NcePatcher.GenerateThreadStartCode();
            uint[] ehSuspendCode = NcePatcher.GenerateSuspendExceptionHandler();

            ulong threadStartCodeOffset = codeWriter.Write(threadStartCode);
            ulong getTpidrEl0CodeOffset = codeWriter.Write(_getTpidrEl0Code);
            ulong ehSuspendCodeOffset = codeWriter.Write(ehSuspendCode);

            MemoryBlock codeBlock = null;

            NativeSignalHandler.InitializeSignalHandler((IntPtr oldSignalHandlerSegfaultPtr, IntPtr signalHandlerPtr) =>
            {
                uint[] ehWrapperCode = NcePatcher.GenerateWrapperExceptionHandler(oldSignalHandlerSegfaultPtr, signalHandlerPtr);
                ulong ehWrapperCodeOffset = codeWriter.Write(ehWrapperCode);
                codeBlock = codeWriter.CreateMemoryBlock();
                return codeBlock.GetPointer(ehWrapperCodeOffset, (ulong)ehWrapperCode.Length * sizeof(uint));
            });

            NativeSignalHandler.InstallUnixSignalHandler(NceThreadPal.UnixSuspendSignal, codeBlock.GetPointer(ehSuspendCodeOffset, (ulong)ehSuspendCode.Length * sizeof(uint)));

            _threadStart = Marshal.GetDelegateForFunctionPointer<ThreadStart>(codeBlock.GetPointer(threadStartCodeOffset, (ulong)threadStartCode.Length * sizeof(uint)));
            _getTpidrEl0 = Marshal.GetDelegateForFunctionPointer<GetTpidrEl0>(codeBlock.GetPointer(getTpidrEl0CodeOffset, (ulong)_getTpidrEl0Code.Length * sizeof(uint)));
            _codeBlock = codeBlock;

            Logger.Info?.Print(LogClass.Cpu, $"[NceCpuContext] Static constructor completed, CPU cores: {Environment.ProcessorCount}");
        }

        public NceCpuContext(ITickSource tickSource, ICpuMemoryManager memory, bool for64Bit)
        {
            _tickSource = tickSource;
            _memoryManager = memory;
            
            Logger.Info?.Print(LogClass.Cpu, $"[NceCpuContext] Instance created, 64-bit: {for64Bit}");
        }

        /// <inheritdoc/>
        public IExecutionContext CreateExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            Logger.Debug?.Print(LogClass.Cpu, "[NceCpuContext] Creating execution context");
            return new NceExecutionContext(exceptionCallbacks);
        }

        /// <inheritdoc/>
        public void Execute(IExecutionContext context, ulong address)
        {
            Logger.Info?.Print(LogClass.Cpu, $"[NceCpuContext] Execute called with address: 0x{address:X}");
            
            // 设置当前线程的CPU亲和性
            SetCurrentThreadAffinity();

            NceExecutionContext nec = (NceExecutionContext)context;
            NceNativeInterface.RegisterThread(nec, _tickSource);
            int tableIndex = NceThreadTable.Register(_getTpidrEl0(), nec.NativeContextPtr);

            nec.SetStartAddress(address);
            
            Logger.Info?.Print(LogClass.Cpu, $"[NceCpuContext] Starting thread execution, table index: {tableIndex}");
            _threadStart(nec.NativeContextPtr);
            
            Logger.Info?.Print(LogClass.Cpu, $"[NceCpuContext] Thread execution completed");
            nec.Exit();

            NceThreadTable.Unregister(tableIndex);
        }

        /// <summary>
        /// 设置当前执行线程的CPU亲和性
        /// </summary>
        private void SetCurrentThreadAffinity()
        {
            try
            {
                int threadIndex;
                lock (_threadCounterLock)
                {
                    threadIndex = _threadCounter++;
                }

                Logger.Info?.Print(LogClass.Cpu, $"[NceCpuContext] Setting CPU affinity for thread index: {threadIndex}");
                
                // 使用自动分配策略为当前线程设置CPU亲和性
                NceThreadPalUnix.SetAutoCurrentThreadAffinity(threadIndex);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"[NceCpuContext] Failed to set CPU affinity: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public void InvalidateCacheRegion(ulong address, ulong size)
        {
            // 缓存失效逻辑
        }

        /// <inheritdoc/>
        public IDiskCacheLoadState LoadDiskCache(string titleIdText, string displayVersion, bool enabled)
        {
            return new DiskCacheLoadState();
        }

        /// <inheritdoc/>
        public void PrepareCodeRange(ulong address, ulong size)
        {
            // 代码范围准备逻辑
        }

        public void Dispose()
        {
            Logger.Info?.Print(LogClass.Cpu, "[NceCpuContext] Disposed");
        }
    }
}
