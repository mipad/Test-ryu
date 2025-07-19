using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
#if ANDROID
using Android.OS;
using Android.Runtime;
#endif

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
        private readonly bool _usesSharedMemory;
        private readonly bool _isMirror;
        private readonly bool _viewCompatible;
        private readonly bool _forJit;
        private readonly bool _forNce;
        private IntPtr _sharedMemory;
        private IntPtr _pointer;
        private IntPtr _rxPointer;

        /// <summary>
        /// Pointer to the memory block data.
        /// </summary>
        public IntPtr Pointer => _pointer;

        /// <summary>
        /// Pointer to the RX mapping (for execution), or IntPtr.Zero if not dual-mapped.
        /// </summary>
        public IntPtr RxPointer => _rxPointer;
        
        /// <summary>
        /// Size of the memory block.
        /// </summary>
        public ulong Size { get; }

        /// <summary>
        /// Creates a new instance of the memory block class.
        /// </summary>
        public MemoryBlock(ulong size, MemoryAllocationFlags flags = MemoryAllocationFlags.None)
        {
            _rxPointer = IntPtr.Zero;
            _forJit = flags.HasFlag(MemoryAllocationFlags.Jit);
            _forNce = flags.HasFlag(MemoryAllocationFlags.Nce);

            if (flags.HasFlag(MemoryAllocationFlags.Mirrorable))
            {
                if (OperatingSystem.IsAndroid())
                {
                    _sharedMemory = MemoryManagementUnix.CreateSharedMemory(size, flags.HasFlag(MemoryAllocationFlags.Reserve));
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
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
                _pointer = MemoryManagement.Reserve(size, _forJit || _forNce, _viewCompatible);
            }
            else
            {
                _pointer = MemoryManagement.Allocate(size, _forJit || _forNce);
            }

            Size = size;
            
            // 为 JIT 或 NCE 创建双映射
            if ((_forJit || _forNce) && !_usesSharedMemory)
            {
                CreateDualMapping(size);
            }
        }

        /// <summary>
        /// 创建双映射（RW 和 RX 映射）
        /// </summary>
        private void CreateDualMapping(ulong size)
        {
            // 仅在某些平台支持双映射
            if (!IsDualMappingSupported())
                return;
            
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    CreateDualMappingWindows(size);
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid())
                {
                    CreateDualMappingUnix(size);
                }
            }
            catch (PlatformNotSupportedException)
            {
                // 平台不支持双映射
                _rxPointer = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Windows 平台的双映射实现
        /// </summary>
        private void CreateDualMappingWindows(ulong size)
        {
            const uint PAGE_EXECUTE_READ = 0x20;
            const uint MEM_COMMIT = 0x1000;
            const uint MEM_RESERVE = 0x2000;
            
            _rxPointer = VirtualAlloc(
                IntPtr.Zero,
                (UIntPtr)size,
                MEM_RESERVE | MEM_COMMIT,
                PAGE_EXECUTE_READ
            );
            
            if (_rxPointer == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new PlatformNotSupportedException($"Dual mapping failed with error 0x{error:X8}");
            }
            
            // 复制数据到 RX 映射
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)_pointer,
                    (void*)_rxPointer,
                    (long)size,
                    (long)size
                );
            }
        }

        /// <summary>
        /// Unix 平台的双映射实现
        /// </summary>
        private void CreateDualMappingUnix(ulong size)
        {
            const int PROT_READ = 0x1;
            const int PROT_EXEC = 0x4;
            const int MAP_PRIVATE = 0x02;
            const int MAP_ANONYMOUS = 0x20;
            
            _rxPointer = mmap(
                IntPtr.Zero,
                size,
                PROT_READ | PROT_EXEC,
                MAP_PRIVATE | MAP_ANONYMOUS,
                -1,
                0
            );
            
            if (_rxPointer == new IntPtr(-1))
            {
                int errno = Marshal.GetLastWin32Error();
                throw new PlatformNotSupportedException($"Dual mapping failed with errno {errno}");
            }
            
            // 复制数据到 RX 映射
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)_pointer,
                    (void*)_rxPointer,
                    (long)size,
                    (long)size
                );
            }
        }

        /// <summary>
        /// 检查平台是否支持双映射
        /// </summary>
        private static bool IsDualMappingSupported()
        {
            // 允许安卓平台支持双映射
            return OperatingSystem.IsWindows() || 
                   OperatingSystem.IsLinux() || 
                   OperatingSystem.IsMacOS() ||
                   OperatingSystem.IsAndroid();
        }

        /// <summary>
        /// Creates a new instance with existing backing storage.
        /// </summary>
        private MemoryBlock(ulong size, IntPtr sharedMemory)
        {
            _rxPointer = IntPtr.Zero;
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
            MemoryManagement.Commit(GetPointerInternal(offset, size), size, _forJit || _forNce);
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
        /// <exception cref="InvalidMemoryRegionException">Throw when the memory region specified for the data is out of range</exception>
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
            IntPtr rxPtr = Interlocked.Exchange(ref _rxPointer, IntPtr.Zero);

            // 释放 RX 映射
            if (rxPtr != IntPtr.Zero)
            {
                if (OperatingSystem.IsWindows())
                {
                    VirtualFree(rxPtr, UIntPtr.Zero, MEM_RELEASE);
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid())
                {
                    munmap(rxPtr, (IntPtr)Size);
                }
            }

            // 如果指针是 null，内存已被释放或从未分配
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

        // Windows P/Invoke
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(
            IntPtr lpAddress,
            UIntPtr dwSize,
            uint flAllocationType,
            uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(
            IntPtr lpAddress,
            UIntPtr dwSize,
            uint dwFreeType);

        private const uint MEM_RELEASE = 0x8000;

        // Unix P/Invoke
        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr mmap(
            IntPtr addr,
            ulong length,
            int prot,
            int flags,
            int fd,
            ulong offset);

        [DllImport("libc", SetLastError = true)]
        private static extern int munmap(IntPtr addr, IntPtr length);
    }
}
