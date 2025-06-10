using System;
using Ryujinx.Common.Logging;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Graphics.Vulkan
{
    internal class VertexBufferUpdater : IDisposable
    {
        private readonly VulkanRenderer _gd;

        private uint _baseBinding;
        private uint _count;

        // 修复 1: 将数组大小从 16 扩展到 32
        private const int MaxVertexBuffers = 33;
        private readonly NativeArray<VkBuffer> _buffers;
        private readonly NativeArray<ulong> _offsets;
        private readonly NativeArray<ulong> _sizes;
        private readonly NativeArray<ulong> _strides;

        public VertexBufferUpdater(VulkanRenderer gd)
        {
            _gd = gd;

            // 修复 2: 使用新的常量 MaxVertexBuffers 替代 Constants.MaxVertexBuffers
            _buffers = new NativeArray<VkBuffer>(MaxVertexBuffers);
            _offsets = new NativeArray<ulong>(MaxVertexBuffers);
            _sizes = new NativeArray<ulong>(MaxVertexBuffers);
            _strides = new NativeArray<ulong>(MaxVertexBuffers);
        }

        public void BindVertexBuffer(CommandBufferScoped cbs, uint binding, VkBuffer buffer, ulong offset, ulong size, ulong stride)
        {
            // 修复 3: 添加安全边界检查
            if (_count >= MaxVertexBuffers)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Too many vertex buffer bindings ({_count + 1}), forcing commit at binding {binding}");
                Commit(cbs);
            }
            
            if (_count == 0)
            {
                _baseBinding = binding;
            }
            else if (_baseBinding + _count != binding)
            {
                Commit(cbs);
                _baseBinding = binding;
            }

            int index = (int)_count;

            // 修复 4: 添加数组边界保护
            if (index < MaxVertexBuffers)
            {
                _buffers[index] = buffer;
                _offsets[index] = offset;
                _sizes[index] = size;
                _strides[index] = stride;

                _count++;
            }
            else
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Vertex binding index {index} exceeds maximum ({MaxVertexBuffers})");
            }
        }

        public unsafe void Commit(CommandBufferScoped cbs)
        {
            if (_count != 0)
            {
                if (_gd.Capabilities.SupportsExtendedDynamicState)
                {
                    _gd.ExtendedDynamicStateApi.CmdBindVertexBuffers2(
                        cbs.CommandBuffer,
                        _baseBinding,
                        _count,
                        _buffers.Pointer,
                        _offsets.Pointer,
                        _sizes.Pointer,
                        _strides.Pointer);
                }
                else
                {
                    _gd.Api.CmdBindVertexBuffers(cbs.CommandBuffer, _baseBinding, _count, _buffers.Pointer, _offsets.Pointer);
                }

                _count = 0;
            }
        }

        public void Dispose()
        {
            _buffers.Dispose();
            _offsets.Dispose();
            _sizes.Dispose();
            _strides.Dispose();
        }
    }
}
