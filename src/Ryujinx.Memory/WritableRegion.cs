using Ryujinx.Common.Memory;
using System;

namespace Ryujinx.Memory
{
    public sealed class WritableRegion : IDisposable
    {
        private readonly IWritableBlock? _block;      // 允许 _block 为 null
        private readonly ulong _va;
        private readonly MemoryOwner<byte>? _memoryOwner; // 明确声明为可空
        private readonly bool _tracked;

        private bool NeedsWriteback => _block != null;

        public Memory<byte> Memory { get; }

        // 构造函数 1：接受 Memory<byte>
        public WritableRegion(IWritableBlock? block, ulong va, Memory<byte> memory, bool tracked = false)
        {
            _block = block;
            _va = va;
            _tracked = tracked;
            Memory = memory;
            _memoryOwner = null; // 显式初始化为 null
        }

        // 构造函数 2：接受 MemoryOwner<byte>
        public WritableRegion(IWritableBlock? block, ulong va, MemoryOwner<byte> memoryOwner, bool tracked = false)
            : this(block, va, memoryOwner.Memory, tracked)
        {
            _memoryOwner = memoryOwner;
        }

        public void Dispose()
        {
            if (NeedsWriteback && _block != null) // 双重空检查
            {
                if (_tracked)
                {
                    _block.Write(_va, Memory.Span);
                }
                else
                {
                    _block.WriteUntracked(_va, Memory.Span);
                }
            }

            _memoryOwner?.Dispose(); // 安全调用
        }
    }
}
