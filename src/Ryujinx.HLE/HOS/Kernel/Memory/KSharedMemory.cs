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
            // 1. 验证地址对齐
            if ((address & (KPageTableBase.PageSize - 1)) != 0)
            {
               // Logger.Warning?.Print(LogClass.KernelSvc, 
                   // $"MapSharedMemory: Address 0x{address:X} not page aligned");
                return KernelResult.InvalidAddress;
            }

            // 2. 计算实际页数
            ulong pageCount = BitUtils.DivRoundUp<ulong>(size, KPageTableBase.PageSize);
            ulong actualPageCount = _pageList.GetPagesCount();
            
            // 3. 验证大小匹配
            if (actualPageCount != pageCount)
            {
               // Logger.Warning?.Print(LogClass.KernelSvc, 
                    //$"MapSharedMemory: Size mismatch (req: {pageCount} pages, actual: {actualPageCount} pages)");
                return KernelResult.InvalidSize;
            }

            // 4. 验证地址范围有效性 - 使用现有的地址空间大小
            ulong addressSpaceSize = memoryManager.GetAddrSpaceSize();
            if (address + size > addressSpaceSize)
            {
               // Logger.Warning?.Print(LogClass.KernelSvc, 
                   // $"MapSharedMemory: Invalid address range 0x{address:X}-0x{(address + size):X} " +
                    $"(max: 0x{addressSpaceSize:X})");
                return KernelResult.InvalidMemRange;
            }

            // 5. 增强权限验证
            KMemoryPermission expectedPermission = process.Pid == _ownerPid
                ? _ownerPermission
                : _userPermission;

            if (permission != expectedPermission)
            {
               // Logger.Warning?.Print(LogClass.KernelSvc, 
                   // $"MapSharedMemory: Permission mismatch (req: {permission}, exp: {expectedPermission})");
                return KernelResult.InvalidPermission;
            }

            // 6. 使用更简单的方法验证内存状态
            // 替代CheckMemoryState：尝试直接映射，如果失败则返回错误
            try
            {
                // 7. 执行映射
                return memoryManager.MapPages(address, _pageList, MemoryState.SharedMemory, permission);
            }
            catch (InvalidMemoryRegionException)
            {
              //  Logger.Warning?.Print(LogClass.KernelSvc, 
                  //  $"MapSharedMemory: Invalid memory state at 0x{address:X}");
                return KernelResult.InvalidMemRange;
            }
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
            ulong addressSpaceSize = memoryManager.GetAddrSpaceSize();
            if (address + size > addressSpaceSize)
            {
                return KernelResult.InvalidMemRange;
            }

            // 5. 执行取消映射
            return memoryManager.UnmapPages(address, _pageList, MemoryState.SharedMemory);
        }
    }
}
