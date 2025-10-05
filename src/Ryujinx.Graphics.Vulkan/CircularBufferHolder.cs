using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    // 环形缓冲区包装器
    class CircularBufferHolder : IDisposable
    {
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly BufferHolder _physicalBuffer;
        private readonly int _virtualSize;
        private readonly int _circularSize;
        private int _writePointer;
        private readonly Dictionary<int, (int offset, int size)> _virtualToPhysicalMap;
        private readonly object _lock = new object();

        public int Size => _virtualSize;

        public CircularBufferHolder(VulkanRenderer gd, Device device, BufferHolder physicalBuffer, int virtualSize, int circularSize)
        {
            _gd = gd;
            _device = device;
            _physicalBuffer = physicalBuffer;
            _virtualSize = virtualSize;
            _circularSize = circularSize;
            _writePointer = 0;
            _virtualToPhysicalMap = new Dictionary<int, (int offset, int size)>();
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, bool isWrite = false, bool isSSBO = false)
        {
            return _physicalBuffer.GetBuffer(commandBuffer, isWrite, isSSBO);
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, int offset, int size, bool isWrite = false)
        {
            lock (_lock)
            {
                if (_virtualToPhysicalMap.TryGetValue(offset, out var physicalMapping))
                {
                    int actualSize = Math.Min(size, physicalMapping.size);
                    return _physicalBuffer.GetBuffer(commandBuffer, physicalMapping.offset, actualSize, isWrite);
                }
            }
            return _physicalBuffer.GetBuffer(commandBuffer, 0, Math.Min(size, _circularSize), isWrite);
        }

        public void SetData(int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs = null, Action endRenderPass = null, bool allowCbsWait = true)
        {
            int dataSize = Math.Min(data.Length, _virtualSize - offset);
            if (dataSize == 0) return;

            lock (_lock)
            {
                // 检查是否需要回绕
                if (_writePointer + dataSize > _circularSize)
                {
                    _writePointer = 0; // 回到开头
                    _virtualToPhysicalMap.Clear(); // 清空映射表，因为旧数据将被覆盖
                }

                // 记录虚拟偏移到物理偏移的映射
                _virtualToPhysicalMap[offset] = (_writePointer, dataSize);

                // 实际写入物理缓冲区
                _physicalBuffer.SetData(_writePointer, data.Slice(0, dataSize), cbs, endRenderPass, allowCbsWait);

                _writePointer += dataSize;
            }
        }

        public PinnedSpan<byte> GetData(int offset, int size)
        {
            lock (_lock)
            {
                if (_virtualToPhysicalMap.TryGetValue(offset, out var physicalMapping))
                {
                    int actualSize = Math.Min(size, physicalMapping.size);
                    return _physicalBuffer.GetData(physicalMapping.offset, actualSize);
                }
            }
            return new PinnedSpan<byte>();
        }

        public Auto<DisposableBufferView> CreateView(VkFormat format, int offset, int size, Action invalidateView)
        {
            lock (_lock)
            {
                if (_virtualToPhysicalMap.TryGetValue(offset, out var physicalMapping))
                {
                    int actualSize = Math.Min(size, physicalMapping.size);
                    return _physicalBuffer.CreateView(format, physicalMapping.offset, actualSize, invalidateView);
                }
            }
            return _physicalBuffer.CreateView(format, 0, Math.Min(size, _circularSize), invalidateView);
        }

        public void Dispose()
        {
            _physicalBuffer?.Dispose();
            lock (_lock)
            {
                _virtualToPhysicalMap?.Clear();
            }
        }

        // 代理其他必要的方法
        public void UseMirrors()
        {
            _physicalBuffer.UseMirrors();
        }

        public Auto<MemoryAllocation> GetAllocation()
        {
            return _physicalBuffer.GetAllocation();
        }

        public (DeviceMemory, ulong) GetDeviceMemoryAndOffset()
        {
            return _physicalBuffer.GetDeviceMemoryAndOffset();
        }

        public BufferHandle GetHandle()
        {
            return _physicalBuffer.GetHandle();
        }
    }
}