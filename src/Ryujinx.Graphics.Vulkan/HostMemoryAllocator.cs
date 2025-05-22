using Ryujinx.Common;
using Ryujinx.Common.Collections;
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    internal class HostMemoryAllocator : IDisposable
    {
        private readonly struct HostMemoryAllocation
        {
            public readonly Auto<MemoryAllocation> Allocation;
            public readonly IntPtr Pointer;
            public readonly ulong Size;

            public ulong Start => (ulong)Pointer;
            public ulong End => (ulong)Pointer + Size;

            public HostMemoryAllocation(Auto<MemoryAllocation> allocation, IntPtr pointer, ulong size)
            {
                Allocation = allocation;
                Pointer = pointer;
                Size = size;
            }
        }

        private readonly MemoryAllocator _allocator;
        private readonly Vk _api;
        private readonly ExtExternalMemoryHost _hostMemoryApi;
        private readonly Device _device;
        private readonly object _lock = new();

        private readonly List<HostMemoryAllocation> _allocations;
        private readonly IntervalTree<ulong, HostMemoryAllocation> _allocationTree;

        public HostMemoryAllocator(MemoryAllocator allocator, Vk api, ExtExternalMemoryHost hostMemoryApi, Device device)
        {
            _allocator = allocator;
            _api = api;
            _hostMemoryApi = hostMemoryApi;
            _device = device;

            _allocations = new List<HostMemoryAllocation>();
            _allocationTree = new IntervalTree<ulong, HostMemoryAllocation>();
        }

        public unsafe bool TryImport(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            IntPtr pointer,
            ulong size)
        {
            lock (_lock)
            {
                // 检查现有分配是否覆盖请求范围
                var allocations = new HostMemoryAllocation[10];

                ulong start = (ulong)pointer;
                ulong end = start + size;

                int count = _allocationTree.Get(start, end, ref allocations);

                // 查找完全覆盖请求范围的分配
                for (int i = 0; i < count; i++)
                {
                    HostMemoryAllocation existing = allocations[i];

                    if (start >= existing.Start && end <= existing.End)
                    {
                        try
                        {
                            existing.Allocation.IncrementReferenceCount();
                            return true;
                        }
                        catch (InvalidOperationException)
                        {
                            // 分配已被释放，继续搜索
                        }
                    }
                }

                // 对齐指针到系统页大小
                nint pageAlignedPointer = BitUtils.AlignDown(pointer, Environment.SystemPageSize);
                nint pageAlignedEnd = BitUtils.AlignUp((nint)((ulong)pointer + size), Environment.SystemPageSize);
                ulong pageAlignedSize = (ulong)(pageAlignedEnd - pageAlignedPointer);

                // 获取主机指针属性
                Result getResult = _hostMemoryApi.GetMemoryHostPointerProperties(
                    _device,
                    ExternalMemoryHandleTypeFlags.HostAllocationBitExt,
                    (void*)pageAlignedPointer,
                    out MemoryHostPointerPropertiesEXT properties
                );

                if (getResult != Result.Success)
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, $"获取主机指针属性失败: {getResult}");
                    return false;
                }

                // 寻找合适的内存类型
                int memoryTypeIndex = _allocator.FindSuitableMemoryTypeIndex(
                    properties.MemoryTypeBits & requirements.MemoryTypeBits,
                    flags
                );

                if (memoryTypeIndex < 0)
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, "未找到匹配的内存类型");
                    return false;
                }

                // 配置内存导入信息
                ImportMemoryHostPointerInfoEXT importInfo = new()
                {
                    SType = StructureType.ImportMemoryHostPointerInfoExt,
                    HandleType = ExternalMemoryHandleTypeFlags.HostAllocationBitExt,
                    PHostPointer = (void*)pageAlignedPointer,
                };

                MemoryAllocateInfo memoryAllocateInfo = new()
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = pageAlignedSize,
                    MemoryTypeIndex = (uint)memoryTypeIndex,
                    PNext = &importInfo,
                };

                // 分配设备内存
                Result result = _api.AllocateMemory(_device, in memoryAllocateInfo, null, out DeviceMemory deviceMemory);

                if (result != Result.Success)
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, $"内存分配失败: {result}");
                    return false;
                }

                // 记录分配信息
                var allocation = new MemoryAllocation(this, deviceMemory, pageAlignedPointer, 0, pageAlignedSize);
                var allocAuto = new Auto<MemoryAllocation>(allocation);
                var hostAlloc = new HostMemoryAllocation(allocAuto, pageAlignedPointer, pageAlignedSize);

                allocAuto.IncrementReferenceCount();
                allocAuto.Dispose(); // 通过引用计数保持活跃

                // 注册分配
                _allocationTree.Add(hostAlloc.Start, hostAlloc.End, hostAlloc);
                _allocations.Add(hostAlloc);
            }

            return true;
        }

        public (Auto<MemoryAllocation>, ulong) GetExistingAllocation(IntPtr pointer, ulong size)
        {
            lock (_lock)
            {
                var allocations = new HostMemoryAllocation[10];
                ulong start = (ulong)pointer;
                ulong end = start + size;

                int count = _allocationTree.Get(start, end, ref allocations);

                for (int i = 0; i < count; i++)
                {
                    HostMemoryAllocation existing = allocations[i];
                    if (start >= existing.Start && end <= existing.End)
                    {
                        return (existing.Allocation, start - existing.Start);
                    }
                }

                throw new InvalidOperationException($"未找到匹配的主机分配: 0x{pointer:x16}:0x{size:x16}");
            }
        }

        public void Free(DeviceMemory memory, ulong offset, ulong size)
        {
            lock (_lock)
            {
                _allocations.RemoveAll(allocation =>
                {
                    if (allocation.Allocation.GetUnsafe().Memory.Handle == memory.Handle)
                    {
                        _allocationTree.Remove(allocation.Start, allocation);
                        return true;
                    }
                    return false;
                });
            }

            _api.FreeMemory(_device, memory, ReadOnlySpan<AllocationCallbacks>.Empty);
        }

        // ==================== 新增的 Dispose 方法 ====================
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var allocation in _allocations)
                {
                    DeviceMemory memory = allocation.Allocation.GetUnsafe().Memory;
                    _api.FreeMemory(_device, memory, ReadOnlySpan<AllocationCallbacks>.Empty);
                }

                _allocations.Clear();
                _allocationTree.Clear();
            }
        }
    }
}
