using ARMeilleure.Memory;
using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LightningJit.Cache
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("ios")]
    [SupportedOSPlatform("maccatalyst")]
    class NoWxCache : IDisposable
    {
        private const int CodeAlignment = 4; // Bytes.
        private const int SharedCacheSize = 2047 * 1024 * 1024;
        private const int LocalCacheSize = 128 * 1024 * 1024;

        // How many calls to the same function we allow until we pad the shared cache to force the function to become available there
        // and allow the guest to take the fast path.
        private const int MinCallsForPad = 8;

        private class MemoryCache : IDisposable
        {
            private readonly ReservedRegion _region;
            private readonly CacheMemoryAllocator _cacheAllocator;

            public CacheMemoryAllocator Allocator => _cacheAllocator;
            public IntPtr Pointer => _region.Block.Pointer;

            public MemoryCache(IJitMemoryAllocator allocator, ulong size)
            {
                _region = new(allocator, size);
                _cacheAllocator = new((int)size);
            }

            public int Allocate(int codeSize)
            {
                codeSize = AlignCodeSize(codeSize);

                int allocOffset = _cacheAllocator.Allocate(codeSize);

                if (allocOffset < 0)
                {
                    throw new OutOfMemoryException("JIT Cache exhausted.");
                }

                _region.ExpandIfNeeded((ulong)allocOffset + (ulong)codeSize);

                return allocOffset;
            }

            public void Free(int offset, int size)
            {
                _cacheAllocator.Free(offset, size);
            }

            [SupportedOSPlatform("windows")]
            [SupportedOSPlatform("linux")]
            [SupportedOSPlatform("macos")]
            [SupportedOSPlatform("android")]
            [SupportedOSPlatform("ios")]
            [SupportedOSPlatform("maccatalyst")]
            public void ReprotectAsRw(int offset, int size)
            {
                ulong pageSize = GetPageSize();
                Debug.Assert(offset >= 0 && ((ulong)offset & (pageSize - 1)) == 0);
                Debug.Assert(size > 0 && ((ulong)size & (pageSize - 1)) == 0);

                _region.Block.MapAsRw((ulong)offset, (ulong)size);
            }

            [SupportedOSPlatform("windows")]
            [SupportedOSPlatform("linux")]
            [SupportedOSPlatform("macos")]
            [SupportedOSPlatform("android")]
            [SupportedOSPlatform("ios")]
            [SupportedOSPlatform("maccatalyst")]
            public void ReprotectAsRx(int offset, int size)
            {
                ulong pageSize = GetPageSize();
                Debug.Assert(offset >= 0 && ((ulong)offset & (pageSize - 1)) == 0);
                Debug.Assert(size > 0 && ((ulong)size & (pageSize - 1)) == 0);

                _region.Block.MapAsRx((ulong)offset, (ulong)size);

                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
                {
                    JitSupportDarwin.SysIcacheInvalidate(_region.Block.Pointer + offset, size);
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }
            }

            private static int AlignCodeSize(int codeSize)
            {
                return checked(codeSize + (CodeAlignment - 1)) & ~(CodeAlignment - 1);
            }

            protected virtual void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _region.Dispose();
                    _cacheAllocator.Clear();
                }
            }

            public void Dispose()
            {
                // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }

        private readonly IStackWalker _stackWalker;
        private readonly Translator _translator;
        private readonly MemoryCache _sharedCache;
        private readonly MemoryCache _localCache;
        private readonly PageAlignedRangeList _pendingMap;
        private readonly object _lock;

        class ThreadLocalCacheEntry
        {
            public readonly int Offset;
            public readonly int Size;
            public readonly IntPtr FuncPtr;
            private int _useCount;

            public ThreadLocalCacheEntry(int offset, int size, IntPtr funcPtr)
            {
                Offset = offset;
                Size = size;
                FuncPtr = funcPtr;
                _useCount = 0;
            }

            public int IncrementUseCount()
            {
                return ++_useCount;
            }
        }

        [ThreadStatic]
        private static Dictionary<ulong, ThreadLocalCacheEntry> _threadLocalCache;

        public NoWxCache(IJitMemoryAllocator allocator, IStackWalker stackWalker, Translator translator)
        {
            _stackWalker = stackWalker;
            _translator = translator;
            _sharedCache = new(allocator, SharedCacheSize);
            _localCache = new(allocator, LocalCacheSize);
            _pendingMap = new(_sharedCache.ReprotectAsRx, RegisterFunction);
            _lock = new();
        }

        public unsafe IntPtr Map(IntPtr framePointer, ReadOnlySpan<byte> code, ulong guestAddress, ulong guestSize)
        {
            if (TryGetThreadLocalFunction(guestAddress, out IntPtr funcPtr))
            {
                return funcPtr;
            }

            lock (_lock)
            {
                if (!_pendingMap.Has(guestAddress) && !_translator.Functions.ContainsKey(guestAddress))
                {
                    int funcOffset = _sharedCache.Allocate(code.Length);

                    funcPtr = _sharedCache.Pointer + funcOffset;
                    code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));

                    TranslatedFunction function = new(funcPtr, guestSize);

                    _pendingMap.Add(funcOffset, code.Length, guestAddress, function);
                }

                ClearThreadLocalCache(framePointer);

                return AddThreadLocalFunction(code, guestAddress);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("maccatalyst")]
        public unsafe IntPtr MapPageAligned(ReadOnlySpan<byte> code)
        {
            lock (_lock)
            {
                // Ensure we will get an aligned offset from the allocator.
                _pendingMap.Pad(_sharedCache.Allocator);

                ulong pageSize = GetPageSize();
                int sizeAligned = BitUtils.AlignUp(code.Length, (int)pageSize);
                int funcOffset = _sharedCache.Allocate(sizeAligned);

                Debug.Assert(((ulong)funcOffset & (pageSize - 1)) == 0);

                IntPtr funcPtr = _sharedCache.Pointer + funcOffset;
                code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));

                _sharedCache.ReprotectAsRx(funcOffset, sizeAligned);

                return funcPtr;
            }
        }

        private bool TryGetThreadLocalFunction(ulong guestAddress, out IntPtr funcPtr)
        {
            if ((_threadLocalCache ??= new()).TryGetValue(guestAddress, out var entry))
            {
                if (entry.IncrementUseCount() >= MinCallsForPad)
                {
                    // Function is being called often, let's make it available in the shared cache so that the guest code
                    // can take the fast path and stop calling the emulator to get the function from the thread local cache.
                    // To do that we pad all "pending" function until they complete a page of memory, allowing us to reprotect them as RX.

                    lock (_lock)
                    {
                        _pendingMap.Pad(_sharedCache.Allocator);
                    }
                }

                funcPtr = entry.FuncPtr;

                return true;
            }

            funcPtr = IntPtr.Zero;

            return false;
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("maccatalyst")]
        private void ClearThreadLocalCache(IntPtr framePointer)
        {
            // Try to delete functions that are already on the shared cache
            // and no longer being executed.

            if (_threadLocalCache == null)
            {
                return;
            }

            IEnumerable<ulong> callStack = _stackWalker.GetCallStack(
                framePointer,
                _localCache.Pointer,
                LocalCacheSize,
                _sharedCache.Pointer,
                SharedCacheSize);

            List<(ulong, ThreadLocalCacheEntry)> toDelete = new();

            foreach ((ulong address, ThreadLocalCacheEntry entry) in _threadLocalCache)
            {
                // We only want to delete if the function is already on the shared cache,
                // otherwise we will keep translating the same function over and over again.
                bool canDelete = !_pendingMap.Has(address);
                if (!canDelete)
                {
                    continue;
                }

                // We can only delete if the function is not part of the current thread call stack,
                // otherwise we will crash the program when the thread returns to it.
                foreach (ulong funcAddress in callStack)
                {
                    if (funcAddress >= (ulong)entry.FuncPtr && funcAddress < (ulong)entry.FuncPtr + (ulong)entry.Size)
                    {
                        canDelete = false;
                        break;
                    }
                }

                if (canDelete)
                {
                    toDelete.Add((address, entry));
                }
            }

            ulong pageSize = GetPageSize();

            foreach ((ulong address, ThreadLocalCacheEntry entry) in toDelete)
            {
                _threadLocalCache.Remove(address);

                int sizeAligned = BitUtils.AlignUp(entry.Size, (int)pageSize);

                _localCache.Free(entry.Offset, sizeAligned);
                _localCache.ReprotectAsRw(entry.Offset, sizeAligned);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("maccatalyst")]
        public void ClearEntireThreadLocalCache()
        {
            // Thread is exiting, delete everything.

            if (_threadLocalCache == null)
            {
                return;
            }

            ulong pageSize = GetPageSize();

            foreach ((_, ThreadLocalCacheEntry entry) in _threadLocalCache)
            {
                int sizeAligned = BitUtils.AlignUp(entry.Size, (int)pageSize);

                _localCache.Free(entry.Offset, sizeAligned);
                _localCache.ReprotectAsRw(entry.Offset, sizeAligned);
            }

            _threadLocalCache.Clear();
            _threadLocalCache = null;
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("maccatalyst")]
        private unsafe IntPtr AddThreadLocalFunction(ReadOnlySpan<byte> code, ulong guestAddress)
        {
            ulong pageSize = GetPageSize();
            int alignedSize = BitUtils.AlignUp(code.Length, (int)pageSize);
            int funcOffset = _localCache.Allocate(alignedSize);

            Debug.Assert(((ulong)funcOffset & (pageSize - 1)) == 0);

            IntPtr funcPtr = _localCache.Pointer + funcOffset;
            code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));

            (_threadLocalCache ??= new()).Add(guestAddress, new(funcOffset, code.Length, funcPtr));

            _localCache.ReprotectAsRx(funcOffset, alignedSize);

            return funcPtr;
        }

        private void RegisterFunction(ulong address, TranslatedFunction func)
        {
            TranslatedFunction oldFunc = _translator.Functions.GetOrAdd(address, func.GuestSize, func);

            Debug.Assert(oldFunc == func);

            _translator.RegisterFunction(address, func);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("maccatalyst")]
        private static ulong GetPageSize()
        {
            return MemoryBlock.GetPageSize();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _localCache.Dispose();
                _sharedCache.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
