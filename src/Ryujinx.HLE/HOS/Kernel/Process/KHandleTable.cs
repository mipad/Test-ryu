using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    class KHandleTable
    {
        public const int SelfThreadHandle = (0x1ffff << 15) | 0;
        public const int SelfProcessHandle = (0x1ffff << 15) | 1;

        private KHandleEntry[] _table;

        private KHandleEntry _tableHead;
        private KHandleEntry _nextFreeEntry;

        private int _activeSlotsCount;

        private uint _size;

        private ushort _idCounter;

        public Result Initialize(uint size)
        {
            if (size > 1024)
            {
                return KernelResult.OutOfMemory;
            }

            if (size < 1)
            {
                size = 1024;
            }

            _size = size;

            _idCounter = 1;

            _table = new KHandleEntry[size];

            _tableHead = new KHandleEntry(0);

            KHandleEntry entry = _tableHead;

            for (int index = 0; index < size; index++)
            {
                _table[index] = entry;

                entry.Next = new KHandleEntry(index + 1);

                entry = entry.Next;
            }

            _table[size - 1].Next = null;

            _nextFreeEntry = _tableHead;

            return Result.Success;
        }

        public Result GenerateHandle(KAutoObject obj, out int handle)
        {
            handle = 0;

            lock (_table)
            {
                if (_activeSlotsCount >= _size)
                {
                    return KernelResult.HandleTableFull;
                }

                KHandleEntry entry = _nextFreeEntry;

                _nextFreeEntry = entry.Next;

                entry.Obj = obj;
                entry.HandleId = _idCounter;

                _activeSlotsCount++;

                handle = (_idCounter << 15) | entry.Index;

                obj.IncrementReferenceCount();

                // 记录特定句柄的创建
                if (handle == 1671214)
                {
                    KProcess currentProcess = KernelStatic.GetCurrentProcess();
                    Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.GenerateHandle: *** CREATED handle 1671214 *** in Process {currentProcess.Pid}, Object Type: {obj.GetType().Name}, Object Hash: {obj.GetHashCode()}");
                }

                if ((short)(_idCounter + 1) >= 0)
                {
                    _idCounter++;
                }
                else
                {
                    _idCounter = 1;
                }
            }

            return Result.Success;
        }

        public Result ReserveHandle(out int handle)
        {
            handle = 0;

            lock (_table)
            {
                if (_activeSlotsCount >= _size)
                {
                    return KernelResult.HandleTableFull;
                }

                KHandleEntry entry = _nextFreeEntry;

                _nextFreeEntry = entry.Next;

                _activeSlotsCount++;

                handle = (_idCounter << 15) | entry.Index;

                // 记录特定句柄的保留
                if (handle == 1671214)
                {
                    KProcess currentProcess = KernelStatic.GetCurrentProcess();
                    Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.ReserveHandle: *** RESERVED handle 1671214 *** in Process {currentProcess.Pid}");
                }

                if ((short)(_idCounter + 1) >= 0)
                {
                    _idCounter++;
                }
                else
                {
                    _idCounter = 1;
                }
            }

            return Result.Success;
        }

        public void CancelHandleReservation(int handle)
        {
            int index = handle & 0x7fff;

            // 记录特定句柄的取消保留
            if (handle == 1671214)
            {
                KProcess currentProcess = KernelStatic.GetCurrentProcess();
                Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.CancelHandleReservation: *** CANCELLED RESERVATION for handle 1671214 *** in Process {currentProcess.Pid}");
            }

            lock (_table)
            {
                KHandleEntry entry = _table[index];

                entry.Obj = null;
                entry.Next = _nextFreeEntry;

                _nextFreeEntry = entry;

                _activeSlotsCount--;
            }
        }

        public void SetReservedHandleObj(int handle, KAutoObject obj)
        {
            int index = (handle >> 0) & 0x7fff;
            int handleId = (handle >> 15);

            // 记录特定句柄的对象设置
            if (handle == 1671214)
            {
                KProcess currentProcess = KernelStatic.GetCurrentProcess();
                Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.SetReservedHandleObj: *** SET OBJECT for handle 1671214 *** in Process {currentProcess.Pid}, Object Type: {obj.GetType().Name}, Object Hash: {obj.GetHashCode()}");
            }

            lock (_table)
            {
                KHandleEntry entry = _table[index];

                entry.Obj = obj;
                entry.HandleId = (ushort)handleId;

                obj.IncrementReferenceCount();
            }
        }

        public bool CloseHandle(int handle)
        {
            if ((handle >> 30) != 0 ||
                handle == SelfThreadHandle ||
                handle == SelfProcessHandle)
            {
                return false;
            }

            int index = (handle >> 0) & 0x7fff;
            int handleId = (handle >> 15);

            KAutoObject obj = null;

            bool result = false;

            // 记录特定句柄的关闭
            if (handle == 1671214)
            {
                KProcess currentProcess = KernelStatic.GetCurrentProcess();
                Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.CloseHandle: *** CLOSING handle 1671214 *** in Process {currentProcess.Pid}");
            }

            lock (_table)
            {
                if (handleId != 0 && index < _size)
                {
                    KHandleEntry entry = _table[index];

                    if ((obj = entry.Obj) != null && entry.HandleId == handleId)
                    {
                        entry.Obj = null;
                        entry.Next = _nextFreeEntry;

                        _nextFreeEntry = entry;

                        _activeSlotsCount--;

                        result = true;
                    }
                }
            }

            if (result)
            {
                obj.DecrementReferenceCount();
                
                // 记录特定句柄成功关闭
                if (handle == 1671214)
                {
                    KProcess currentProcess = KernelStatic.GetCurrentProcess();
                    Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.CloseHandle: *** SUCCESSFULLY CLOSED handle 1671214 *** in Process {currentProcess.Pid}, Object Type: {obj.GetType().Name}");
                }
            }
            else if (handle == 1671214)
            {
                KProcess currentProcess = KernelStatic.GetCurrentProcess();
                Logger.Warning?.Print(LogClass.Kernel, $"KHandleTable.CloseHandle: *** FAILED to close handle 1671214 *** in Process {currentProcess.Pid}");
            }

            return result;
        }

        public T GetObject<T>(int handle) where T : KAutoObject
        {
            int index = (handle >> 0) & 0x7fff;
            int handleId = (handle >> 15);

            // 记录特定句柄的对象获取
            if (handle == 1671214)
            {
                KProcess currentProcess = KernelStatic.GetCurrentProcess();
                Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.GetObject: *** GETTING OBJECT for handle 1671214 *** in Process {currentProcess.Pid}, Requested Type: {typeof(T).Name}");
            }

            lock (_table)
            {
                if ((handle >> 30) == 0 && handleId != 0 && index < _size)
                {
                    KHandleEntry entry = _table[index];

                    if (entry.HandleId == handleId && entry.Obj is T obj)
                    {
                        // 记录特定句柄成功获取对象
                        if (handle == 1671214)
                        {
                            KProcess currentProcess = KernelStatic.GetCurrentProcess();
                            Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.GetObject: *** SUCCESSFULLY GOT OBJECT for handle 1671214 *** in Process {currentProcess.Pid}, Actual Type: {obj.GetType().Name}");
                        }
                        return obj;
                    }
                    else if (handle == 1671214)
                    {
                        KProcess currentProcess = KernelStatic.GetCurrentProcess();
                        if (entry.Obj != null)
                        {
                            Logger.Warning?.Print(LogClass.Kernel, $"KHandleTable.GetObject: *** TYPE MISMATCH for handle 1671214 *** in Process {currentProcess.Pid}, Actual Type: {entry.Obj.GetType().Name}, Requested Type: {typeof(T).Name}");
                        }
                        else
                        {
                            Logger.Warning?.Print(LogClass.Kernel, $"KHandleTable.GetObject: *** NULL OBJECT for handle 1671214 *** in Process {currentProcess.Pid}");
                        }
                    }
                }
                else if (handle == 1671214)
                {
                    KProcess currentProcess = KernelStatic.GetCurrentProcess();
                    Logger.Warning?.Print(LogClass.Kernel, $"KHandleTable.GetObject: *** INVALID HANDLE 1671214 *** in Process {currentProcess.Pid}, index: {index}, handleId: {handleId}");
                }
            }

            return default;
        }

        public KThread GetKThread(int handle)
        {
            if (handle == SelfThreadHandle)
            {
                return KernelStatic.GetCurrentThread();
            }
            else
            {
                return GetObject<KThread>(handle);
            }
        }

        public KProcess GetKProcess(int handle)
        {
            if (handle == SelfProcessHandle)
            {
                return KernelStatic.GetCurrentProcess();
            }
            else
            {
                return GetObject<KProcess>(handle);
            }
        }

        public void Destroy()
        {
            lock (_table)
            {
                for (int index = 0; index < _size; index++)
                {
                    KHandleEntry entry = _table[index];

                    if (entry.Obj != null)
                    {
                        // 记录特定句柄在销毁时的处理
                        int handle = (entry.HandleId << 15) | entry.Index;
                        if (handle == 1671214)
                        {
                            KProcess currentProcess = KernelStatic.GetCurrentProcess();
                            Logger.Debug?.Print(LogClass.Kernel, $"KHandleTable.Destroy: *** DESTROYING handle 1671214 *** in Process {currentProcess.Pid}, Object Type: {entry.Obj.GetType().Name}");
                        }

                        if (entry.Obj is IDisposable disposableObj)
                        {
                            disposableObj.Dispose();
                        }

                        entry.Obj.DecrementReferenceCount();
                        entry.Obj = null;
                        entry.Next = _nextFreeEntry;

                        _nextFreeEntry = entry;
                    }
                }
            }
        }
    }
}
