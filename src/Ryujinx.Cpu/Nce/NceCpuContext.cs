using ARMeilleure.Memory;
using Ryujinx.Cpu.Signal;
using Ryujinx.Common;
using Ryujinx.Memory;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                Logger.Debug?.Print(LogClass.Cpu, "Using TPIDRRO register for macOS/iOS.");
                return 0xd53bd060u | rd; // TPIDRRO
            }
            else
            {
                Logger.Debug?.Print(LogClass.Cpu, "Using TPIDR register for non-Apple platform.");
                return 0xd53bd040u | rd; // TPIDR
            }
        }

        readonly struct CodeWriter
        {
            [DllImport("libc", EntryPoint = "sys_icache_invalidate")]
            public static extern unsafe void sys_icache_invalidate(IntPtr start, IntPtr length);
        
            private readonly List<uint> _fullCode;

            public CodeWriter()
            {
                _fullCode = new List<uint>();
                Logger.Debug?.Print(LogClass.Cpu, "CodeWriter initialized.");
            }

            public ulong Write(uint[] code)
            {
                ulong offset = (ulong)_fullCode.Count * sizeof(uint);
                _fullCode.AddRange(code);

                Logger.Debug?.Print(LogClass.Cpu, $"CodeWriter: Written {code.Length} instructions at offset 0x{offset:X}. Total instructions: {_fullCode.Count}");
                return offset;
            }

            public MemoryBlock CreateMemoryBlock()
            {
                ReadOnlySpan<byte> codeBytes = MemoryMarshal.Cast<uint, byte>(_fullCode.ToArray());
                ulong alignedSize = BitUtils.AlignUp((ulong)codeBytes.Length, 0x1000UL);

                Logger.Info?.Print(LogClass.Cpu, $"CodeWriter: Creating memory block. Code size: {codeBytes.Length} bytes, Aligned size: {alignedSize} bytes");

                MemoryBlock codeBlock = new(alignedSize);

                codeBlock.Write(0, codeBytes);
                Logger.Debug?.Print(LogClass.Cpu, "CodeWriter: Code written to memory block.");

                codeBlock.Reprotect(0, (ulong)codeBytes.Length, MemoryPermission.ReadAndExecute, true);
                Logger.Debug?.Print(LogClass.Cpu, "CodeWriter: Memory block reprotected as ReadAndExecute.");
                
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    IntPtr codePtr = codeBlock.GetPointer(0, alignedSize);
                    Logger.Debug?.Print(LogClass.Cpu, $"Flushing instruction cache at 0x{codePtr:X}, size: {alignedSize} bytes");
                    
                    try
                    {
                        sys_icache_invalidate(codePtr, (IntPtr)alignedSize);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Cpu, $"Failed to flush instruction cache: {ex.Message}");
                    }
                }

                return codeBlock;
            }
        }

        private delegate void ThreadStart(IntPtr nativeContextPtr);
        private delegate IntPtr GetTpidrEl0();
        private static MemoryBlock _codeBlock;
        private static ThreadStart _threadStart;
        private static GetTpidrEl0 _getTpidrEl0;

        private readonly ITickSource _tickSource;
        private readonly IMemoryManager _memoryManager;

        static NceCpuContext()
        {
            Logger.Info?.Print(LogClass.Cpu, "==================== NceCpuContext Static Initialization Started ====================");
            Stopwatch initStopwatch = Stopwatch.StartNew();

            try
            {
                CodeWriter codeWriter = new();

                Logger.Debug?.Print(LogClass.Cpu, "Generating thread start code...");
                uint[] threadStartCode = NcePatcher.GenerateThreadStartCode();
                Logger.Debug?.Print(LogClass.Cpu, $"Thread start code generated: {threadStartCode.Length} instructions.");

                Logger.Debug?.Print(LogClass.Cpu, "Generating suspend exception handler code...");
                uint[] ehSuspendCode = NcePatcher.GenerateSuspendExceptionHandler();
                Logger.Debug?.Print(LogClass.Cpu, $"Suspend exception handler generated: {ehSuspendCode.Length} instructions.");

                ulong threadStartCodeOffset = codeWriter.Write(threadStartCode);
                Logger.Info?.Print(LogClass.Cpu, $"Thread start code written at offset 0x{threadStartCodeOffset:X}");

                ulong getTpidrEl0CodeOffset = codeWriter.Write(_getTpidrEl0Code);
                Logger.Info?.Print(LogClass.Cpu, $"TPIDR_EL0 getter code written at offset 0x{getTpidrEl0CodeOffset:X}");

                ulong ehSuspendCodeOffset = codeWriter.Write(ehSuspendCode);
                Logger.Info?.Print(LogClass.Cpu, $"Suspend exception handler written at offset 0x{ehSuspendCodeOffset:X}");

                MemoryBlock codeBlock = null;

                Logger.Info?.Print(LogClass.Cpu, "Initializing native signal handler...");
                NativeSignalHandler.InitializeSignalHandler((IntPtr oldSignalHandlerSegfaultPtr, IntPtr signalHandlerPtr) =>
                {
                    Logger.Debug?.Print(LogClass.Cpu, $"Signal handler callback invoked. Old handler: 0x{oldSignalHandlerSegfaultPtr:X}, New handler: 0x{signalHandlerPtr:X}");

                    Logger.Debug?.Print(LogClass.Cpu, "Generating wrapper exception handler code...");
                    uint[] ehWrapperCode = NcePatcher.GenerateWrapperExceptionHandler(oldSignalHandlerSegfaultPtr, signalHandlerPtr);
                    Logger.Debug?.Print(LogClass.Cpu, $"Wrapper exception handler generated: {ehWrapperCode.Length} instructions.");

                    ulong ehWrapperCodeOffset = codeWriter.Write(ehWrapperCode);
                    Logger.Info?.Print(LogClass.Cpu, $"Wrapper exception handler written at offset 0x{ehWrapperCodeOffset:X}");

                    codeBlock = codeWriter.CreateMemoryBlock();
                    IntPtr wrapperPtr = codeBlock.GetPointer(ehWrapperCodeOffset, (ulong)ehWrapperCode.Length * sizeof(uint));
                    Logger.Info?.Print(LogClass.Cpu, $"Wrapper exception handler pointer: 0x{wrapperPtr:X}");

                    return wrapperPtr;
                });

                Logger.Info?.Print(LogClass.Cpu, "Native signal handler initialized successfully.");

                IntPtr suspendHandlerPtr = codeBlock.GetPointer(ehSuspendCodeOffset, (ulong)ehSuspendCode.Length * sizeof(uint));
                Logger.Info?.Print(LogClass.Cpu, $"Installing Unix suspend signal handler at 0x{suspendHandlerPtr:X} for signal {NceThreadPal.UnixSuspendSignal}");
                NativeSignalHandler.InstallUnixSignalHandler(NceThreadPal.UnixSuspendSignal, suspendHandlerPtr);
                Logger.Info?.Print(LogClass.Cpu, "Unix suspend signal handler installed successfully.");

                IntPtr threadStartPtr = codeBlock.GetPointer(threadStartCodeOffset, (ulong)threadStartCode.Length * sizeof(uint));
                _threadStart = Marshal.GetDelegateForFunctionPointer<ThreadStart>(threadStartPtr);
                Logger.Debug?.Print(LogClass.Cpu, $"ThreadStart delegate created from pointer 0x{threadStartPtr:X}");

                IntPtr getTpidrEl0Ptr = codeBlock.GetPointer(getTpidrEl0CodeOffset, (ulong)_getTpidrEl0Code.Length * sizeof(uint));
                _getTpidrEl0 = Marshal.GetDelegateForFunctionPointer<GetTpidrEl0>(getTpidrEl0Ptr);
                Logger.Debug?.Print(LogClass.Cpu, $"GetTpidrEl0 delegate created from pointer 0x{getTpidrEl0Ptr:X}");

                _codeBlock = codeBlock;

                initStopwatch.Stop();
                Logger.Info?.Print(LogClass.Cpu, $"==================== NceCpuContext Static Initialization Completed in {initStopwatch.ElapsedMilliseconds}ms ====================");
            }
            catch (Exception ex)
            {
                initStopwatch.Stop();
                Logger.Error?.Print(LogClass.Cpu, $"FATAL ERROR during NceCpuContext static initialization after {initStopwatch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
                Logger.Error?.Print(LogClass.Cpu, $"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public NceCpuContext(ITickSource tickSource, IMemoryManager memory, bool for64Bit)
        {
            Logger.Info?.Print(LogClass.Cpu, $"Constructing NceCpuContext instance. 64-bit mode: {for64Bit}");
            Logger.Debug?.Print(LogClass.Cpu, $"TickSource type: {tickSource?.GetType().Name ?? "null"}, MemoryManager type: {memory?.GetType().Name ?? "null"}");

            if (tickSource == null)
            {
                Logger.Warning?.Print(LogClass.Cpu, "TickSource is null!");
            }

            if (memory == null)
            {
                Logger.Warning?.Print(LogClass.Cpu, "MemoryManager is null!");
            }

            _tickSource = tickSource;
            _memoryManager = memory;

            Logger.Info?.Print(LogClass.Cpu, "NceCpuContext instance constructed successfully.");
        }

        /// <inheritdoc/>
        public IExecutionContext CreateExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            Logger.Debug?.Print(LogClass.Cpu, "CreateExecutionContext called.");
            Logger.Debug?.Print(LogClass.Cpu, $"ExceptionCallbacks provided: {exceptionCallbacks}");

            try
            {
                var context = new NceExecutionContext(exceptionCallbacks);
                Logger.Info?.Print(LogClass.Cpu, $"NceExecutionContext created successfully. Native context pointer: 0x{context.NativeContextPtr:X}");
                return context;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Cpu, $"Failed to create NceExecutionContext: {ex.GetType().Name}: {ex.Message}");
                Logger.Error?.Print(LogClass.Cpu, $"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <inheritdoc/>
        public void Execute(IExecutionContext context, ulong address)
        {
            Logger.Info?.Print(LogClass.Cpu, $"==================== Execute Started ====================");
            Logger.Info?.Print(LogClass.Cpu, $"Stack Trace: {Environment.StackTrace}");
            Logger.Info?.Print(LogClass.Cpu, $"Entry address: 0x{address:X16}");

            Stopwatch executionStopwatch = Stopwatch.StartNew();

            try
            {
                NceExecutionContext nec = (NceExecutionContext)context;
                Logger.Debug?.Print(LogClass.Cpu, $"Execution context cast successful. Native context pointer: 0x{nec.NativeContextPtr:X}");


                Logger.Debug?.Print(LogClass.Cpu, "Registering thread with NceNativeInterface...");
                NceNativeInterface.RegisterThread(nec, _tickSource, _memoryManager);
                Logger.Info?.Print(LogClass.Cpu, "Thread registered with NceNativeInterface.");

                Logger.Debug?.Print(LogClass.Cpu, "Getting TPIDR_EL0 value...");
                IntPtr tpidrEl0 = _getTpidrEl0();
                Logger.Debug?.Print(LogClass.Cpu, $"TPIDR_EL0 value: 0x{tpidrEl0:X}");

                Logger.Debug?.Print(LogClass.Cpu, "Registering thread in NceThreadTable...");
                int tableIndex = NceThreadTable.Register(tpidrEl0, nec.NativeContextPtr);
                Logger.Info?.Print(LogClass.Cpu, $"Thread registered in NceThreadTable at index {tableIndex}");

                Logger.Debug?.Print(LogClass.Cpu, $"Setting start address to 0x{address:X16}...");
                nec.SetStartAddress(address);
                Logger.Debug?.Print(LogClass.Cpu, "Start address set successfully.");

                Logger.Info?.Print(LogClass.Cpu, "Starting thread execution...");
                Stopwatch threadStopwatch = Stopwatch.StartNew();

                _threadStart(nec.NativeContextPtr);

                threadStopwatch.Stop();
                Logger.Info?.Print(LogClass.Cpu, $"Thread execution returned after {threadStopwatch.ElapsedMilliseconds}ms");

                Logger.Debug?.Print(LogClass.Cpu, "Calling context Exit...");
                nec.Exit();
                Logger.Debug?.Print(LogClass.Cpu, "Context Exit completed.");

                Logger.Debug?.Print(LogClass.Cpu, $"Unregistering thread from NceThreadTable at index {tableIndex}...");
                NceThreadTable.Unregister(tableIndex);
                Logger.Info?.Print(LogClass.Cpu, "Thread unregistered from NceThreadTable.");

                executionStopwatch.Stop();
                Logger.Info?.Print(LogClass.Cpu, $"==================== Execute Completed Successfully in {executionStopwatch.ElapsedMilliseconds}ms ====================");
            }
            catch (Exception ex)
            {
                executionStopwatch.Stop();
                Logger.Error?.Print(LogClass.Cpu, $"==================== Execute Failed after {executionStopwatch.ElapsedMilliseconds}ms ====================");
                Logger.Error?.Print(LogClass.Cpu, $"Exception during Execute: {ex.GetType().Name}: {ex.Message}");
                Logger.Error?.Print(LogClass.Cpu, $"Stack trace: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    Logger.Error?.Print(LogClass.Cpu, $"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    Logger.Error?.Print(LogClass.Cpu, $"Inner stack trace: {ex.InnerException.StackTrace}");
                }

                throw;
            }
        }

        /// <inheritdoc/>
        public void InvalidateCacheRegion(ulong address, ulong size)
        {
            Logger.Debug?.Print(LogClass.Cpu, $"InvalidateCacheRegion: Address=0x{address:X16}, Size=0x{size:X} ({size} bytes)");
            
            if (size == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, "InvalidateCacheRegion called with size 0");
            }

            // Log large invalidations
            if (size > 1024 * 1024) // > 1MB
            {
                Logger.Info?.Print(LogClass.Cpu, $"Large cache invalidation: {size / (1024.0 * 1024.0):F2} MB");
            }
        }

        /// <inheritdoc/>
        public IDiskCacheLoadState LoadDiskCache(string titleIdText, string displayVersion, bool enabled)
        {
            Logger.Info?.Print(LogClass.Cpu, $"LoadDiskCache called:");
            Logger.Info?.Print(LogClass.Cpu, $"  TitleId: {titleIdText ?? "(null)"}");
            Logger.Info?.Print(LogClass.Cpu, $"  DisplayVersion: {displayVersion ?? "(null)"}");
            Logger.Info?.Print(LogClass.Cpu, $"  Enabled: {enabled}");

            if (!enabled)
            {
                Logger.Info?.Print(LogClass.Cpu, "Disk cache is disabled, returning dummy state.");
            }

            return new DummyDiskCacheLoadState();
        }

        /// <inheritdoc/>
        public void PrepareCodeRange(ulong address, ulong size)
        {
            Logger.Debug?.Print(LogClass.Cpu, $"PrepareCodeRange: Address=0x{address:X16}, Size=0x{size:X} ({size} bytes)");

            if (size == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, "PrepareCodeRange called with size 0");
            }

            // Log large preparations
            if (size > 1024 * 1024) // > 1MB
            {
                Logger.Info?.Print(LogClass.Cpu, $"Large code range preparation: {size / (1024.0 * 1024.0):F2} MB");
            }
        }

        public void Dispose()
        {
            Logger.Info?.Print(LogClass.Cpu, "Disposing NceCpuContext instance...");

            try
            {
                // Add any cleanup logic here if needed
                Logger.Info?.Print(LogClass.Cpu, "NceCpuContext instance disposed successfully.");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Cpu, $"Error during NceCpuContext disposal: {ex.GetType().Name}: {ex.Message}");
                Logger.Error?.Print(LogClass.Cpu, $"Stack trace: {ex.StackTrace}");
                throw;
            }
        }
    }
}