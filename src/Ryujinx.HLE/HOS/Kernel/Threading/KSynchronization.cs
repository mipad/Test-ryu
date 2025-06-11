using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.Horizon.Common;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    class KSynchronization
    {
        private readonly KernelContext _context;
        private readonly object _processWideLock = new object();

        public KSynchronization(KernelContext context)
        {
            _context = context;
        }

        // 新增进程宽键同步方法
        public Result WaitProcessWideKeyAtomic(ulong mutexAddress, ulong condVarAddress, int timeout)
        {
            KThread currentThread = KernelStatic.GetCurrentThread();
            
            // 1. 原子性地获取互斥锁
            if (mutexAddress != 0)
            {
                Result lockResult = ArbitrateLock(currentThread, mutexAddress);
                if (lockResult != Result.Success)
                {
                    return lockResult;
                }
            }

            lock (_processWideLock)
            {
                // 2. 检查条件变量状态
                int condVarValue = _context.MemoryManager.Read<int>(condVarAddress);
                if (condVarValue != 0)
                {
                    return Result.Success;
                }

                // 3. 设置线程等待状态
                currentThread.CondVarAddress = condVarAddress;
                currentThread.MutexAddress = mutexAddress;
                currentThread.WaitingSync = true;

                // 4. 带超时的等待
                bool signaled = Monitor.Wait(_processWideLock, timeout);
                
                if (!signaled)
                {
                    // 超时处理：重置状态并返回
                    currentThread.CondVarAddress = 0;
                    currentThread.MutexAddress = 0;
                    return KernelResult.TimedOut;
                }
                
                return Result.Success;
            }
        }

        // 新增进程宽键信号方法
        public Result SignalProcessWideKey(ulong condVarAddress, int count)
        {
            lock (_processWideLock)
            {
                // 1. 更新条件变量计数器
                int currentValue = _context.MemoryManager.Read<int>(condVarAddress);
                _context.MemoryManager.Write(condVarAddress, currentValue + 1);
                
                // 2. 根据计数值唤醒线程
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        Monitor.Pulse(_processWideLock);
                    }
                }
                else // count <= 0 (包括0xFFFFFFFF)
                {
                    Monitor.PulseAll(_processWideLock);
                }
            }
            return Result.Success;
        }

        // 新增互斥锁仲裁方法
        private Result ArbitrateLock(KThread thread, ulong mutexAddress)
{
    _context.CriticalSection.Enter();
    
    try
    {
        int mutexValue = _context.MemoryManager.Read<int>(mutexAddress);
        
        if (mutexValue == 0)
        {
            // 获取锁成功
            _context.MemoryManager.Write(mutexAddress, thread.ThreadHandleForUserMutex);
            thread.MutexLockCount = 1;
            thread.MutexAddress = mutexAddress;
            return Result.Success;
        }
        
        // 检查递归锁（当前线程是否已持有锁）
        if (mutexValue == thread.ThreadHandleForUserMutex)
        {
            thread.MutexLockCount++;
            return Result.Success;
        }
        
        return KernelResult.InvalidState;
    }
    finally
    {
        _context.CriticalSection.Leave();
    }
}

        // 修改现有WaitFor方法
        public Result WaitFor(Span<KSynchronizationObject> syncObjs, long timeout, out int handleIndex)
        {
            handleIndex = 0;
            KThread currentThread = KernelStatic.GetCurrentThread();

            // 检查线程终止状态
            if (currentThread.TerminationRequested)
            {
                return KernelResult.ThreadTerminating;
            }

            _context.CriticalSection.Enter();

            try
            {
                // 检查同步对象状态
                for (int index = 0; index < syncObjs.Length; index++)
                {
                    if (syncObjs[index].IsSignaled())
                    {
                        handleIndex = index;
                        return Result.Success;
                    }
                }

                if (timeout == 0)
                {
                    return KernelResult.TimedOut;
                }

                // 添加进程宽键状态检查
                if (currentThread.CondVarAddress != 0 || currentThread.MutexAddress != 0)
                {
                    return KernelResult.InvalidState;
                }

                LinkedListNode<KThread>[] syncNodesArray = ArrayPool<LinkedListNode<KThread>>.Shared.Rent(syncObjs.Length);
                Span<LinkedListNode<KThread>> syncNodes = syncNodesArray.AsSpan(0, syncObjs.Length);

                for (int index = 0; index < syncObjs.Length; index++)
                {
                    syncNodes[index] = syncObjs[index].AddWaitingThread(currentThread);
                }

                currentThread.WaitingSync = true;
                currentThread.SignaledObj = null;
                currentThread.ObjSyncResult = KernelResult.TimedOut;

                currentThread.Reschedule(ThreadSchedState.Paused);

                if (timeout > 0)
                {
                    _context.TimeManager.ScheduleFutureInvocation(currentThread, timeout);
                }

                _context.CriticalSection.Leave();

                currentThread.WaitingSync = false;

                if (timeout > 0)
                {
                    _context.TimeManager.UnscheduleFutureInvocation(currentThread);
                }

                _context.CriticalSection.Enter();

                Result result = currentThread.ObjSyncResult;

                handleIndex = -1;

                for (int index = 0; index < syncObjs.Length; index++)
                {
                    syncObjs[index].RemoveWaitingThread(syncNodes[index]);

                    if (syncObjs[index] == currentThread.SignaledObj)
                    {
                        handleIndex = index;
                    }
                }

                ArrayPool<LinkedListNode<KThread>>.Shared.Return(syncNodesArray, true);

                return result;
            }
            finally
            {
                _context.CriticalSection.Leave();
            }
        }

        // 修改现有SignalObject方法
        public void SignalObject(KSynchronizationObject syncObj)
        {
            _context.CriticalSection.Enter();

            try
            {
                if (syncObj.IsSignaled())
                {
                    LinkedListNode<KThread> node = syncObj.WaitingThreads.First;
                    while (node != null)
                    {
                        KThread thread = node.Value;
                        LinkedListNode<KThread> next = node.Next;

                        if ((thread.SchedFlags & ThreadSchedState.LowMask) == ThreadSchedState.Paused)
                        {
                            // 重置进程宽键状态
                            if (thread.CondVarAddress != 0)
                            {
                                thread.CondVarAddress = 0;
                                thread.MutexAddress = 0;
                            }

                            thread.SignaledObj = syncObj;
                            thread.ObjSyncResult = Result.Success;
                            thread.Reschedule(ThreadSchedState.Running);
                        }

                        node = next;
                    }
                }
            }
            finally
            {
                _context.CriticalSection.Leave();
            }
        }
    }
}
