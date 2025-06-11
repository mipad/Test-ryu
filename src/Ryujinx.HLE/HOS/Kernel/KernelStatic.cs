using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;
using System;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel
{
    static class KernelStatic
    {
        [ThreadStatic]
        private static KernelContext _context;

        [ThreadStatic]
        private static KThread _currentThread;

        public static Result StartInitialProcess(
            KernelContext context,
            ProcessCreationInfo creationInfo,
            ReadOnlySpan<uint> capabilities,
            int mainThreadPriority,
            ThreadStart customThreadStart)
        {
            KProcess process = new(context);

            Result result = process.Initialize(
                creationInfo,
                capabilities,
                context.ResourceLimit,
                MemoryRegion.Service,
                null,
                customThreadStart);

            if (result != Result.Success)
            {
                return result;
            }

            process.DefaultCpuCore = 3;

            context.Processes.TryAdd(process.Pid, process);

            return process.Start(mainThreadPriority, 0x1000UL);
        }

        internal static void SetKernelContext(KernelContext context, KThread thread)
        {
            _context = context;
            _currentThread = thread;
        }

        public static KThread GetCurrentThread()
        {
            return _currentThread;
        }
        
        // 添加获取进程ID的方法（如果KThread中Pid字段未设置）
public static int GetCurrentProcessId()
{
    return GetCurrentThread().Owner.Pid;
}

        internal static KProcess GetCurrentProcess()
        {
            return GetCurrentThread().Owner;
        }

        internal static KProcess GetProcessByPid(ulong pid)
        {
            if (_context.Processes.TryGetValue(pid, out KProcess process))
            {
                return process;
            }

            return null;
        }
    }
}
