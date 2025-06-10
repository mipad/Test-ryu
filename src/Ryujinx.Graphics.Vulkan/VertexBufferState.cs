using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Vulkan
{
    internal struct VertexBufferState
    {
        private const int VertexBufferMaxMirrorable = 0x20000;

        public static VertexBufferState Null => new(null, 0, 0, 0);

        private readonly int _offset;
        private readonly int _size;
        private readonly int _stride;

        private readonly BufferHandle _handle;
        private Auto<DisposableBuffer> _buffer;

        internal readonly int DescriptorIndex;
        internal int AttributeScalarAlignment;

        public VertexBufferState(Auto<DisposableBuffer> buffer, int descriptorIndex, int offset, int size, int stride = 0)
        {
            _buffer = buffer;
            _handle = BufferHandle.Null;

            _offset = offset;
            _size = size;
            _stride = stride;

            DescriptorIndex = descriptorIndex;
            AttributeScalarAlignment = 1;

            buffer?.IncrementReferenceCount();
        }

        public VertexBufferState(BufferHandle handle, int descriptorIndex, int offset, int size, int stride = 0)
        {
            // This buffer state may be rewritten at bind time, so it must be retrieved on bind.

            _buffer = null;
            _handle = handle;

            _offset = offset;
            _size = size;
            _stride = stride;

            DescriptorIndex = descriptorIndex;
            AttributeScalarAlignment = 1;
        }

        public void BindVertexBuffer(VulkanRenderer gd, CommandBufferScoped cbs, uint binding, ref PipelineState state, VertexBufferUpdater updater)
        {
            // =================== 安全修复开始 ===================
            // 处理步长(Stride)为零的情况
            int safeStride = _stride;
            if (safeStride == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Vertex buffer binding {binding} has invalid stride 0. Using fallback stride=1");
                safeStride = 1; // 防止除零崩溃
            }

            // 处理缓冲区大小为0的情况
            if (_size == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Vertex buffer binding {binding} has size 0. Skipping bind");
                updater.BindVertexBuffer(cbs, binding, null, 0, 0, 0);
                return;
            }
            // =================== 安全修复结束 ===================

            var autoBuffer = _buffer;

            if (_handle != BufferHandle.Null)
            {
                // May need to restride the vertex buffer.

                // =================== 安全修复开始 ===================
                // 使用安全步长进行计算
                if (gd.NeedsVertexBufferAlignment(AttributeScalarAlignment, out int alignment) && (safeStride % alignment) != 0)
                // =================== 安全修复结束 ===================
                {
                    autoBuffer = gd.BufferManager.GetAlignedVertexBuffer(cbs, _handle, _offset, _size, safeStride, alignment);

                    if (autoBuffer != null)
                    {
                        int stride = (safeStride + (alignment - 1)) & -alignment;
                        
                        // =================== 安全修复开始 ===================
                        // 确保除法安全（safeStride已保证不为0）
                        int vertexCount = _size / safeStride;
                        int newSize = vertexCount * stride;
                        // =================== 安全修复结束 ===================

                        var buffer = autoBuffer.Get(cbs, 0, newSize).Value;

                        updater.BindVertexBuffer(cbs, binding, buffer, 0, (ulong)newSize, (ulong)stride);

                        _buffer = autoBuffer;

                        state.Internal.VertexBindingDescriptions[DescriptorIndex].Stride = (uint)stride;
                    }

                    return;
                }

                autoBuffer = gd.BufferManager.GetBuffer(cbs.CommandBuffer, _handle, false, out int size);

                // The original stride must be reapplied in case it was rewritten.
                // =================== 安全修复开始 ===================
                state.Internal.VertexBindingDescriptions[DescriptorIndex].Stride = (uint)safeStride;
                // =================== 安全修复结束 ===================

                if (_offset >= size)
                {
                    autoBuffer = null;
                }
            }

            if (autoBuffer != null)
            {
                int offset = _offset;
                bool mirrorable = _size <= VertexBufferMaxMirrorable;
                var buffer = mirrorable ? autoBuffer.GetMirrorable(cbs, ref offset, _size, out _).Value : autoBuffer.Get(cbs, offset, _size).Value;

                // =================== 安全修复开始 ===================
                updater.BindVertexBuffer(cbs, binding, buffer, (ulong)offset, (ulong)_size, (ulong)safeStride);
                // =================== 安全修复结束 ===================
            }
            else
            {
                // =================== 安全修复开始 ===================
                // 处理缓冲区获取失败的情况
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Vertex buffer binding {binding} failed to get buffer. Binding null buffer");
                updater.BindVertexBuffer(cbs, binding, null, 0, 0, 0);
                // =================== 安全修复结束 ===================
            }
        }

        public readonly bool BoundEquals(Auto<DisposableBuffer> buffer)
        {
            return _buffer == buffer;
        }

        public readonly bool Overlaps(Auto<DisposableBuffer> buffer, int offset, int size)
        {
            return buffer == _buffer && offset < _offset + _size && offset + size > _offset;
        }

        public readonly bool Matches(Auto<DisposableBuffer> buffer, int descriptorIndex, int offset, int size, int stride = 0)
        {
            return _buffer == buffer && DescriptorIndex == descriptorIndex && _offset == offset && _size == size && _stride == stride;
        }

        public void Swap(Auto<DisposableBuffer> from, Auto<DisposableBuffer> to)
        {
            if (_buffer == from)
            {
                _buffer.DecrementReferenceCount();
                to.IncrementReferenceCount();

                _buffer = to;
            }
        }

        public readonly void Dispose()
        {
            // Only dispose if this buffer is not refetched on each bind.

            if (_handle == BufferHandle.Null)
            {
                _buffer?.DecrementReferenceCount();
            }
        }
    }
}
