using ARMeilleure.Memory;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LightningJit.Cache
{
    static partial class JitCache
    {
        private static readonly int _pageSize = (int)MemoryBlock.GetPageSize();
        private static readonly int _pageMask = _pageSize - 1;

        private const int CodeAlignment = 4; // Bytes.
        private const int CacheSize = 2047 * 1024 * 1024;

        private static ReservedRegion _jitRegion;
        private static bool _initialized;

        // Android/Linux 系统调用
        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(IntPtr addr, IntPtr len, int prot);
        
        [DllImport("libc", SetLastError = true)]
        private static extern void __clear_cache(IntPtr start, IntPtr end);

        private const int PROT_READ = 0x1;
        private const int PROT_WRITE = 0x2;
        private const int PROT_EXEC = 0x4;
        private const int PROT_RW = PROT_READ | PROT_WRITE;
        private const int PROT_RX = PROT_READ | PROT_EXEC;

        private static readonly CacheMemoryAllocator _cacheAllocator;
        private static readonly List<CacheEntry> _cacheEntries = new();
        private static readonly object _lock = new();

        static JitCache()
        {
            _cacheAllocator = new CacheMemoryAllocator(CacheSize);
        }

        public static void Initialize(IJitMemoryAllocator allocator)
        {
            if (_initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                _jitRegion = new ReservedRegion(allocator, CacheSize);
                
                // 初始映射为 RWX（Android 需要）
                _jitRegion.Block.MapAsRwx(0, (ulong)CacheSize);
                
                _initialized = true;
            }
        }

        public unsafe static IntPtr Map(ReadOnlySpan<byte> code)
        {
            lock (_lock)
            {
                Debug.Assert(_initialized);

                int funcOffset = Allocate(code.Length);
                IntPtr funcPtr = _jitRegion.Pointer + funcOffset;

                // 始终使用 Android 标准方法
                SetMemoryProtection(funcOffset, code.Length, PROT_RW);
                code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));
                SetMemoryProtection(funcOffset, code.Length, PROT_RX);
                InvalidateCache(funcPtr, code.Length);

                Add(funcOffset, code.Length);

                return funcPtr;
            }
        }

        public static void Unmap(IntPtr pointer)
        {
            lock (_lock)
            {
                Debug.Assert(_initialized);

                int funcOffset = (int)(pointer.ToInt64() - _jitRegion.Pointer.ToInt64());

                if (TryFind(funcOffset, out CacheEntry entry, out int entryIndex) && entry.Offset == funcOffset)
                {
                    _cacheAllocator.Free(funcOffset, AlignCodeSize(entry.Size));
                    _cacheEntries.RemoveAt(entryIndex);
                    
                    // Android 不需要额外清理，内存可重用
                }
            }
        }

        private static void SetMemoryProtection(int offset, int size, int prot)
        {
            int regionStart = offset & ~_pageMask;
            int regionSize = ((offset + size + _pageMask) & ~_pageMask) - regionStart;

            IntPtr start = _jitRegion.Pointer + regionStart;
            IntPtr len = (IntPtr)regionSize;
            
            if (mprotect(start, len, prot) != 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"mprotect failed with error {error}");
            }
        }

        private static void InvalidateCache(IntPtr start, int size)
        {
            IntPtr end = start + size;
            __clear_cache(start, end);
        }

        private static int Allocate(int codeSize)
        {
            codeSize = AlignCodeSize(codeSize);

            int allocOffset = _cacheAllocator.Allocate(codeSize);

            if (allocOffset < 0)
            {
                throw new OutOfMemoryException("JIT Cache exhausted.");
            }

            _jitRegion.ExpandIfNeeded((ulong)allocOffset + (ulong)codeSize);

            return allocOffset;
        }

        private static int AlignCodeSize(int codeSize)
        {
            return checked(codeSize + (CodeAlignment - 1)) & ~(CodeAlignment - 1);
        }

        private static void Add(int offset, int size)
        {
            CacheEntry entry = new(offset, size);

            int index = _cacheEntries.BinarySearch(entry);

            if (index < 0)
            {
                index = ~index;
            }

            _cacheEntries.Insert(index, entry);
        }

        public static bool TryFind(int offset, out CacheEntry entry, out int entryIndex)
        {
            lock (_lock)
            {
                int index = _cacheEntries.BinarySearch(new CacheEntry(offset, 0));

                if (index < 0)
                {
                    index = ~index - 1;
                }

                if (index >= 0)
                {
                    entry = _cacheEntries[index];
                    entryIndex = index;
                    return true;
                }
            }

            entry = default;
            entryIndex = 0;
            return false;
        }
    }
}
