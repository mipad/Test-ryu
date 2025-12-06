using ARMeilleure.Memory;
using Ryujinx.Cpu.Signal;
using Ryujinx.Common;
using Ryujinx.Memory;
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
                return 0xd53bd060u | rd; // TPIDRRO
            }
            else
            {
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
                ulong alignedSize = BitUtils.AlignUp((ulong)codeBytes.Length, 0x1000UL);

                MemoryBlock codeBlock = new(alignedSize);

                codeBlock.Write(0, codeBytes);

                codeBlock.Reprotect(0, (ulong)codeBytes.Length, MemoryPermission.ReadAndExecute, true);
                
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    IntPtr codePtr = codeBlock.GetPointer(0, alignedSize);
                    
                    try
                    {
                        sys_icache_invalidate(codePtr, (IntPtr)alignedSize);
                    }
                    catch (Exception)
                    {
                        // Ignore cache flush errors
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
        private readonly ICpuMemoryManager _memoryManager;

        static NceCpuContext()
        {
            Stopwatch initStopwatch = Stopwatch.StartNew();

            try
            {
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
                    IntPtr wrapperPtr = codeBlock.GetPointer(ehWrapperCodeOffset, (ulong)ehWrapperCode.Length * sizeof(uint));

                    return wrapperPtr;
                });

                IntPtr suspendHandlerPtr = codeBlock.GetPointer(ehSuspendCodeOffset, (ulong)ehSuspendCode.Length * sizeof(uint));
                NativeSignalHandler.InstallUnixSignalHandler(NceThreadPal.UnixSuspendSignal, suspendHandlerPtr);

                IntPtr threadStartPtr = codeBlock.GetPointer(threadStartCodeOffset, (ulong)threadStartCode.Length * sizeof(uint));
                _threadStart = Marshal.GetDelegateForFunctionPointer<ThreadStart>(threadStartPtr);

                IntPtr getTpidrEl0Ptr = codeBlock.GetPointer(getTpidrEl0CodeOffset, (ulong)_getTpidrEl0Code.Length * sizeof(uint));
                _getTpidrEl0 = Marshal.GetDelegateForFunctionPointer<GetTpidrEl0>(getTpidrEl0Ptr);

                _codeBlock = codeBlock;

                initStopwatch.Stop();
            }
            catch (Exception ex)
            {
                initStopwatch.Stop();
                throw;
            }
        }

        public NceCpuContext(ITickSource tickSource, ICpuMemoryManager memory, bool for64Bit)
        {
            _tickSource = tickSource;
            _memoryManager = memory;
        }

        /// <inheritdoc/>
        public IExecutionContext CreateExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            try
            {
                var context = new NceExecutionContext(exceptionCallbacks);
                return context;
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <inheritdoc/>
        public void Execute(IExecutionContext context, ulong address)
        {
            Stopwatch executionStopwatch = Stopwatch.StartNew();

            try
            {
                NceExecutionContext nec = (NceExecutionContext)context;

                NceNativeInterface.RegisterThread(nec, _tickSource, _memoryManager);

                IntPtr tpidrEl0 = _getTpidrEl0();

                int tableIndex = NceThreadTable.Register(tpidrEl0, nec.NativeContextPtr);

                nec.SetStartAddress(address);

                Stopwatch threadStopwatch = Stopwatch.StartNew();

                _threadStart(nec.NativeContextPtr);

                threadStopwatch.Stop();

                nec.Exit();

                NceThreadTable.Unregister(tableIndex);

                executionStopwatch.Stop();
            }
            catch (Exception ex)
            {
                executionStopwatch.Stop();
                throw;
            }
        }

        /// <inheritdoc/>
        public void InvalidateCacheRegion(ulong address, ulong size)
        {
            // Cache invalidation logic without logging
        }

        /// <inheritdoc/>
        public IDiskCacheLoadState LoadDiskCache(string titleIdText, string displayVersion, bool enabled)
        {
            return new DummyDiskCacheLoadState();
        }

        /// <inheritdoc/>
        public void PrepareCodeRange(ulong address, ulong size)
        {
            // Code range preparation logic without logging
        }

        public void Dispose()
        {
            try
            {
                // Add any cleanup logic here if needed
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
}
