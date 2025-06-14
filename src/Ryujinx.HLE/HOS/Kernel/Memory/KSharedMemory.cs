using Ryujinx.Common;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.Horizon.Common;
using System.Diagnostics;

namespace Ryujinx.HLE.HOS.Kernel.Memory
{
    class KSharedMemory : KAutoObject
    {
        private readonly KPageList _pageList;
        private readonly ulong _ownerPid;
        private readonly KMemoryPermission _ownerPermission;
        private readonly KMemoryPermission _userPermission;

        public KSharedMemory(
            KernelContext context,
            SharedMemoryStorage storage,
            ulong ownerPid,
            KMemoryPermission ownerPermission,
            KMemoryPermission userPermission) : base(context)
        {
            _pageList = storage.GetPageList();
            _ownerPid = ownerPid;
            _ownerPermission = ownerPermission;
            _userPermission = userPermission;
        }

        public Result MapIntoProcess(
            KPageTableBase memoryManager,
            ulong address,
            ulong size,
            KProcess process,
            KMemoryPermission permission)
        {
            // 1. 验证地址对齐 - 使用位操作替代PageAlignment类
            if ((address & (KPageTableBase.PageSize - 1)) != 0)
            {
                //Logger.Warning?.Print(LogClass.KernelSvc, 
                    //$"MapSharedMemory: Address 0x{address:X} not page aligned");
                return KernelResult.InvalidAddress;
            }

            // 2. 计算实际页数
            ulong pageCount = BitUtils.DivRoundUp<ulong>(size, KPageTableBase.PageSize);
            ulong actualPageCount = _pageList.GetPagesCount();
            
            // 3. 验证大小匹配
            if (actualPageCount != pageCount)
            {
               // Logger.Warning?.Print(LogClass.KernelSvc, 
                   // $"MapSharedMemory: Size mismatch (req: {pageCount} pages, actual: {actualPageCount} pages)");
                return KernelResult.InvalidSize;
            }

            // 4. 验证地址范围有效性 - 使用现有方法替代IsInsideAddressSpace
            if (address + size > process.CpuMemoryManager.AddressSpaceSize)
            {
               // Logger.Warning?.Print(LogClass.KernelSvc, 
                   // $"MapSharedMemory: Invalid address range 0x{address:X}-0x{(address + size):X}");
                return KernelResult.InvalidMemRange;
            }

            // 5. 增强权限验证
            KMemoryPermission expectedPermission = process.Pid == _ownerPid
                ? _ownerPermission
                : _userPermission;

            if (permission != expectedPermission)
            {
               // Logger.Warning?.Print(LogClass.KernelSvc, 
                  //  $"MapSharedMemory: Permission mismatch (req: {permission}, exp: {expectedPermission})");
                return KernelResult.InvalidPermission;
            }

            // 6. 验证内存状态 - 使用现有的CheckMemoryState方法
            Result result = memoryManager.CheckMemoryState(
                address, 
                size, 
                MemoryState.Mask, 
                MemoryState.Unmapped,
                permission, 
                permission,
                MemoryAttribute.Mask, 
                MemoryAttribute.None);
            
            if (result != Result.Success)
            {
               // Logger.Warning?.Print(LogClass.KernelSvc, 
                   // $"MapSharedMemory: Invalid memory state at 0x{address:X}");
                return result;
            }

            // 7. 执行映射
            return memoryManager.MapPages(address, _pageList, MemoryState.SharedMemory, permission);
        }

        public Result UnmapFromProcess(KPageTableBase memoryManager, ulong address, ulong size, KProcess process)
        {
            // 1. 验证地址对齐
            if ((address & (KPageTableBase.PageSize - 1)) != 0)
            {
                return KernelResult.InvalidAddress;
            }

            // 2. 计算实际页数
            ulong pageCount = BitUtils.DivRoundUp<ulong>(size, KPageTableBase.PageSize);
            
            // 3. 验证大小匹配
            if (_pageList.GetPagesCount() != pageCount)
            {
                return KernelResult.InvalidSize;
            }

            // 4. 验证地址范围有效性
            if (address + size > process.CpuMemoryManager.AddressSpaceSize)
            {
                return KernelResult.InvalidMemRange;
            }

            // 5. 执行取消映射
            return memoryManager.UnmapPages(address, _pageList, MemoryState.SharedMemory);
        }
    }
}
