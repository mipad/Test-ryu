using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    // 包装器，使CircularBufferHolder可以当作BufferHolder使用
    class CircularBufferWrapperHolder : BufferHolder
    {
        private readonly CircularBufferHolder _circularBuffer;

        public CircularBufferWrapperHolder(
            VulkanRenderer gd, 
            Device device, 
            VkBuffer buffer, 
            CircularBufferHolder circularBuffer,
            int size) 
            : base(gd, device, buffer, default, size, BufferAllocationType.HostMapped, BufferAllocationType.HostMapped)
        {
            _circularBuffer = circularBuffer;
        }

        public override Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, bool isWrite = false, bool isSSBO = false)
        {
            return _circularBuffer.GetBuffer(commandBuffer, isWrite, isSSBO);
        }

        public override Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, int offset, int size, bool isWrite = false)
        {
            return _circularBuffer.GetBuffer(commandBuffer, offset, size, isWrite);
        }

        public override void SetData(int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs = null, Action endRenderPass = null, bool allowCbsWait = true)
        {
            _circularBuffer.SetData(offset, data, cbs, endRenderPass, allowCbsWait);
        }

        public override PinnedSpan<byte> GetData(int offset, int size)
        {
            return _circularBuffer.GetData(offset, size);
        }

        public override Auto<DisposableBufferView> CreateView(VkFormat format, int offset, int size, Action invalidateView)
        {
            return _circularBuffer.CreateView(format, offset, size, invalidateView);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _circularBuffer?.Dispose();
                // 注意：我们不需要调用base.Dispose()，因为基类的缓冲区是虚拟的
            }
        }

        public override void UseMirrors()
        {
            _circularBuffer.UseMirrors();
        }

        public override Auto<MemoryAllocation> GetAllocation()
        {
            return _circularBuffer.GetAllocation();
        }

        public override (DeviceMemory, ulong) GetDeviceMemoryAndOffset()
        {
            return _circularBuffer.GetDeviceMemoryAndOffset();
        }

        public override BufferHandle GetHandle()
        {
            return _circularBuffer.GetHandle();
        }
    }
}
