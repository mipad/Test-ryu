using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Memory
{
    /// <summary>
    /// Represents a block of contiguous physical guest memory.
    /// </summary>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("android")]
    public sealed class MemoryBlock : IWritableBlock, IDisposable
    {
        // 增加内存分配标志位处理
        [Flags]
        public enum MemoryAllocationFlags
        {
            None = 0,
            Reserve = 1 << 0,
            Mirrorable = 1 << 1,
            NoMap = 1 << 2,
            ViewCompatible = 1 << 3,
            Jit = 1 << 4,
        }

        // 增加大页面支持
        private const ulong LargePageSize = 1 << 21; // 2MB
        
        private readonly bool _usesSharedMemory;
        private readonly bool _isMirror;
        private readonly bool _viewCompatible;
        private readonly bool _forJit;
        private IntPtr _sharedMemory;
        private IntPtr _pointer;

        /// <summary>
        /// Pointer to the memory block data.
        /// </summary>
        public IntPtr Pointer => _pointer;

        ///极
        /// Size of the memory block.
        /// </summary>
        public ulong Size { get; }

        /// <summary>
        /// Creates a new instance of the memory block class.
        /// </summary>
        public MemoryBlock(ulong size, MemoryAllocationFlags flags = MemoryAllocationFlags.None)
        {
            if (flags.HasFlag(MemoryAllocationFlags.Mirrorable))
            {
                if (OperatingSystem.IsAndroid())
                {
                    // Android 使用 ASharedMemory_create
                    _sharedMemory = MemoryManagementUnix.CreateSharedMemory(size, flags.HasFlag(MemoryAllocationFlags.Reserve));
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    // Linux/macOS 使用其他实现
                    _sharedMemory = MemoryManagement.CreateSharedMemory(size, flags.HasFlag(MemoryAllocationFlags.Reserve));
                }
                else
                {
                    throw new PlatformNotSupportedException("Shared memory is not supported on this platform.");
                }

                if (!flags.HasFlag(MemoryAllocationFlags.NoMap))
                {
                    _pointer = MemoryManagement.MapSharedMemory(_sharedMemory, size);
                }
                _usesSharedMemory = true;
            }
            else if (flags.HasFlag(MemoryAllocationFlags.Reserve))
            {
                _viewCompatible = flags.HasFlag(MemoryAllocationFlags.ViewCompatible);
                _forJit = flags.HasFlag(MemoryAllocationFlags.Jit);
                _pointer = MemoryManagement.Reserve(size, _forJit, _viewCompatible);
            }
            else
            {
                _forJit = flags.HasFlag(MemoryAllocationFlags.Jit);
                _pointer = MemoryManagement.Allocate(size, _forJit);
            }

            Size = size;
        }

        /// <summary>
        /// Creates a new instance with existing backing storage.
        /// </summary>
        private MemoryBlock(ulong size, IntPtr sharedMemory)
        {
            _pointer = MemoryManagement.MapSharedMemory(sharedMemory, size);
            Size = size;
            _usesSharedMemory = true;
            _isMirror = true;
        }

        /// <summary>
        /// Creates a memory mirror.
        /// </summary>
        public MemoryBlock CreateMirror()
        {
            if (_sharedMemory == IntPtr.Zero)
            {
                throw new NotSupportedException("Mirroring requires Mirrorable flag.");
            }
            return new MemoryBlock(Size, _sharedMemory);
        }

        /// <summary>
        /// Commits reserved memory.
        /// </summary>
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        public void Commit(ulong offset, ulong size)
        {
            MemoryManagement.Commit(GetPointerInternal(offset, size), size, _forJit);
        }

        /// <summary>
        /// Decommits memory.
        /// </summary>
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        public void Decommit(ulong offset, ulong size)
        {
            MemoryManagement.Decommit(GetPointerInternal(offset, size), size);
        }

        /// <summary>
        /// Maps a memory view.
        /// </summary>
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        public void MapView(MemoryBlock srcBlock, ulong srcOffset, ulong dstOffset, ulong size)
        {
            if (srcBlock._sharedMemory == IntPtr.Zero)
            {
                throw new ArgumentException("Source block is not mirrorable.");
            }
            MemoryManagement.MapView(srcBlock._sharedMemory, srcOffset, GetPointerInternal(dstOffset, size), size, this);
        }

        /// <summary>
        /// Reprotects memory.
        /// </summary>
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        /// <summary>
        /// Unmaps a view of memory from another memory block.
        /// </summary>
        /// <param name="srcBlock">Memory block from where the backing memory was taken during map</param>
        /// <param name="offset">Offset of the view previously mapped with <see cref="MapView"/></param>
        /// <param name="size">Size of the range to be unmapped</param>
        public void UnmapView(MemoryBlock srcBlock, ulong offset, ulong size)
        {
            MemoryManagement.UnmapView(srcBlock._sharedMemory, GetPointerInternal(offset, size), size, this);
        }

        /// <summary>
        /// Reprotects a region of memory.
        /// </summary>
        /// <param name="offset">Starting offset of the range to be reprotected</param>
        /// <param name="size">Size of the range to be reprotected</param>
        /// <param name="permission">New memory permissions</param>
        /// <param name="throwOnFail">True if a failed reprotect should throw</param>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when either <paramref name="offset"/> or <paramref name="size"/> are out of range</exception>
        /// <exception cref="MemoryProtectionException">Throw when <paramref name="permission"/> is invalid</exception>
        public void Reprotect(ulong offset, ulong size, MemoryPermission permission, bool throwOnFail = true)
        {
            MemoryManagement.Reprotect(GetPointerInternal(offset, size), size, permission, _viewCompatible, throwOnFail);
        }

        /// <summary>
        /// Reads bytes from the memory block.
        /// </summary>
        /// <param name="offset">Starting offset of the range being read</param>
        /// <param name="data">Span where the bytes being read will be copied to</param>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegion极ception">Throw when the memory region specified for the data is out of range</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(ulong offset, Span<byte> data)
        {
            GetSpan(offset, data.Length).CopyTo(data);
        }

        /// <summary>
        /// Reads data from the memory block.
        /// </summary>
        /// <typeparam name="T">Type of the data</typeparam>
        /// <param name="offset">Offset where the data is located</param>
        /// <returns>Data at the specified address</returns>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when the memory region specified for the data is out of range</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Read<T>(ulong offset) where T : unmanaged
        {
            return GetRef<T>(offset);
        }

        /// <summary>
        /// Writes bytes to the memory block.
        /// </summary>
        /// <param name="offset">Starting offset of the range being written</param>
        /// <param name="data">Span where the bytes being written will be copied from</param>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when the memory region specified for the data is out of range</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ulong offset, ReadOnlySpan<byte> data)
        {
            data.CopyTo(GetSpan(offset, data.Length));
        }

        /// <summary>
        /// Writes data to the memory block.
        /// </summary>
        /// <typeparam name="T">Type of the data being written</typeparam>
        /// <param name="offset">Offset to write the data into</param>
        /// <param name="data">Data to be written</param>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when the memory region specified for the data is out of range</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ulong offset, T data) where T : unmanaged
        {
            GetRef<T>(offset) = data;
        }

        /// <summary>
        /// Copies data from one memory location to another.
        /// </summary>
        /// <param name="dstOffset">Destination offset to write the data into</param>
        /// <param name="srcOffset">Source offset to read the data from</param>
        /// <param name="size">Size of the copy in bytes</param>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when <paramref name="srcOffset"/>, <paramref name="dstOffset"/> or <paramref name="size"/> is out of range</exception>
        public void Copy(ulong dstOffset, ulong srcOffset, ulong size)
        {
            const int MaxChunkSize = 1 << 24;

            for (ulong offset = 0; offset < size; offset += MaxChunkSize)
            {
                int copySize = (int)Math.Min(MaxChunkSize, size - offset);

                Write(dstOffset + offset, GetSpan(srcOffset + offset, copySize));
            }
        }

        /// <summary>
        /// Fills a region of memory with <paramref name="value"/>.
        /// </summary>
        /// <param name="offset">Offset of the region to fill with <paramref name="value"/></param>
        /// <param name="size">Size in bytes of the region to fill</param>
        /// <param name="value">Value to use for the fill</param>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when either <paramref name="offset"/> or <paramref name="size"/> are out of range</exception>
        public void Fill(ulong offset, ulong size, byte value)
        {
            const int MaxChunkSize = 1 << 24;

            for (ulong subOffset = 0; subOffset < size; subOffset += MaxChunkSize)
            {
                int copySize = (int)Math.Min(MaxChunkSize, size - subOffset);

                GetSpan(offset + subOffset, copySize).Fill(value);
            }
        }

        /// <summary>
        /// Gets a reference of the data at a given memory block region.
        /// </summary>
        /// <typeparam name="T">Data type</typeparam>
        /// <param name="offset">Offset of the memory region</param>
        /// <returns>A reference to the given memory region data</returns>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when either <paramref name="offset"/> or <paramref name="size"/> are out of range</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ref T GetRef<T>(ulong offset) where T : unmanaged
        {
            IntPtr ptr = _pointer;

            ObjectDisposedException.ThrowIf(ptr == IntPtr.Zero, this);

            int size = Unsafe.SizeOf<T>();

            ulong endOffset = offset + (ulong)size;

            if (endOffset > Size || endOffset < offset)
            {
                ThrowInvalidMemoryRegionException();
            }

            return ref Unsafe.AsRef<T>((void*)PtrAddr(ptr, offset));
        }

        /// <summary>
        /// Gets the pointer of a given memory block region.
        /// </summary>
        /// <param name="offset">Start offset of the memory region</param>
        /// <param name="size">Size in bytes of the region</param>
        /// <returns>The pointer to the memory region</returns>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when either <paramref name="offset"/> or <paramref name="size"/> are out of range</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IntPtr GetPointer(ulong offset, ulong size) => GetPointerInternal(offset, size);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IntPtr GetPointerInternal(ulong offset, ulong size)
        {
            IntPtr ptr = _pointer;

            ObjectDisposedException.ThrowIf(ptr == IntPtr.Zero, this);

            ulong endOffset = offset + size;

            if (endOffset > Size || endOffset < offset)
            {
                ThrowInvalidMemoryRegionException();
            }

            return PtrAddr(ptr, offset);
        }

        /// <summary>
        /// Gets the <see cref="Span{T}"/> of a given memory block region.
        /// </summary>
        /// <param name="offset">Start offset of the memory region</param>
        /// <param name="size">Size in bytes of the region</param>
        /// <returns>Span of the memory region</returns>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when either <paramref name="offset"/> or <paramref name="size"/> are out of range</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<byte> GetSpan(ulong offset, int size)
        {
            return new Span<byte>((void*)GetPointerInternal(offset, (ulong)size), size);
        }

        /// <summary>
        /// Gets the <see cref="Memory{T}"/> of a given memory block region.
        /// </summary>
        /// <param name="offset">Start offset of the memory region</param>
        /// <param name="size">Size in bytes of the region</param>
        /// <returns>Memory of the memory region</returns>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when either <paramref name="offset"/> or <paramref name="size"/> are out of range</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Memory<byte> GetMemory(ulong offset, int size)
        {
            return new NativeMemoryManager<byte>((byte*)GetPointerInternal(offset, (ulong)size), size).Memory;
        }

        /// <summary>
        /// Gets a writable region of a given memory block region.
        /// </summary>
        /// <param name="offset">Start offset of the memory region</param>
        /// <param name="size">Size in bytes of the region</param>
        /// <returns>Writable region of the memory region</returns>
        /// <exception cref="ObjectDisposedException">Throw when the memory block has already been disposed</exception>
        /// <exception cref="InvalidMemoryRegionException">Throw when either <paramref name="offset"/> or <paramref name="size"/> are out of range</exception>
        public WritableRegion GetWritableRegion(ulong offset, int size)
        {
            return new WritableRegion(null, offset, GetMemory(offset, size));
        }

        /// <summary>
        /// Adds a 64-bits offset to a native pointer.
        /// </summary>
        /// <param name="pointer">Native pointer</param>
        /// <param name="offset">Offset to add</param>
        /// <returns>Native pointer with the added offset</returns>
        private static IntPtr PtrAddr(IntPtr pointer, ulong offset)
        {
            return new IntPtr(pointer.ToInt64() + (long)offset);
        }

        /// <summary>
        /// Frees the memory allocated for this memory block.
        /// </summary>
        /// <remarks>
        /// It's an error to use the memory block after disposal.
        /// </remarks>
        public void Dispose()
        {
            FreeMemory();

            GC.SuppressFinalize(this);
        }

        ~MemoryBlock() => FreeMemory();

        private void FreeMemory()
        {
            IntPtr ptr = Interlocked.Exchange(ref _pointer, IntPtr.Zero);

            // If pointer is null, the memory was already freed or never allocated.
            if (ptr != IntPtr.Zero)
            {
                if (_usesSharedMemory)
                {
                    MemoryManagement.UnmapSharedMemory(ptr, Size);
                }
                else
                {
                    MemoryManagement.Free(ptr, Size);
                }
            }

            if (!_isMirror)
            {
                IntPtr sharedMemory = Interlocked.Exchange(ref _sharedMemory, IntPtr.Zero);

                if (sharedMemory != IntPtr.Zero)
                {
                    MemoryManagement.DestroySharedMemory(sharedMemory);
                }
            }
        }

        // 修改：移除对AllocateLargePage的调用
        private static bool TryAllocateLargePages(ulong size, out IntPtr ptr)
        {
            ptr = IntPtr.Zero;
            try
            {
                // 使用现有的Allocate方法作为回退
                ptr = MemoryManagement.Allocate(size, forJit: true);
                return ptr != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }
    
        /// <summary>
        /// Checks if the specified memory allocation flags are supported on the current platform.
        /// </summary>
        /// <param name="flags">Flags to be checked</param>
        /// <returns>True if the platform supports all the flags, false otherwise</returns>
        public static bool SupportsFlags(MemoryAllocationFlags flags)
        {
            if (flags.HasFlag(MemoryAllocationFlags.ViewCompatible))
            {
                if (OperatingSystem.IsWindows())
                {
                    return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134);
                }

                return OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || Ryujinx.Common.PlatformInfo.IsBionic;
            }

            return true;
        }

        public static ulong GetPageSize()
        {
            return (ulong)Environment.SystemPageSize;
        }

        private static void ThrowInvalidMemoryRegionException() => throw new InvalidMemoryRegionException();
    }
}
