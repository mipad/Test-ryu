using ARMeilleure.Memory;
using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ryujinx.Cpu.LightningJit.Cache
{
    class DualMappedNoWxCache : IDisposable
    {
        private const int CodeAlignment = 4; // Bytes.
        private const int SharedCacheSize = 512 * 1024 * 1024;
        private const int LocalCacheSize = 128 * 1024 * 1024;

        // How many calls to the same function we allow until we pad the shared cache to force the function to become available there
        // and allow the guest to take the fast path.
        private const int MinCallsForPad = 8;

        private class MemoryCache : IDisposable
        {
            private readonly DualMappedJitAllocator _allocator;
            private readonly CacheMemoryAllocator _cacheAllocator;
            public DualMappedJitAllocator Allocator => _allocator;
            public IntPtr RwPointer => _allocator.RwPtr;
            public IntPtr RxPointer => _allocator.RxPtr;

            public CacheMemoryAllocator CacheAllocator => _cacheAllocator;
            public IntPtr Pointer => _allocator.RwPtr; 

            public MemoryCache(ulong size)
            {
                _allocator = new DualMappedJitAllocator(size);
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

                return allocOffset;
            }

            public void Free(int offset, int size)
            {
                _cacheAllocator.Free(offset, size);
            }

            public void SysIcacheInvalidate(int offset, int size)
            {
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    JitSupportDarwin.SysIcacheInvalidate(_allocator.RxPtr + offset, size);
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
                    _allocator.Dispose();
                    _cacheAllocator.Clear();
                }
            }

            public void Dispose()
            {
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }

        private readonly IStackWalker _stackWalker;
        private Translator _translator;
        private readonly List<MemoryCache> _sharedCaches;
        private readonly List<MemoryCache> _localCaches;
        private readonly Dictionary<ulong, PageAlignedRangeList> _pendingMaps;
        private readonly object _lock;

        class ThreadLocalCacheEntry
        {
            public readonly int Offset;
            public readonly int Size;
            public readonly IntPtr FuncPtr;
            public readonly int CacheIndex;
            private int _useCount;

            public ThreadLocalCacheEntry(int offset, int size, IntPtr funcPtr, int cacheIndex)
            {
                Offset = offset;
                Size = size;
                FuncPtr = funcPtr;
                CacheIndex = cacheIndex;
                _useCount = 0;
            }

            public int IncrementUseCount()
            {
                return ++_useCount;
            }
        }

        [ThreadStatic]
        private static Dictionary<ulong, ThreadLocalCacheEntry> _threadLocalCache;

        public DualMappedNoWxCache(IJitMemoryAllocator allocator, IStackWalker stackWalker)
        {
            _stackWalker = stackWalker;
            _sharedCaches = new List<MemoryCache> { new(SharedCacheSize) };
            _localCaches = new List<MemoryCache> { new(LocalCacheSize) };
            _pendingMaps = new Dictionary<ulong, PageAlignedRangeList>();
            _lock = new();
        }

        public void SetTranslator(Translator translator) {
            _translator = translator;
        }

        private PageAlignedRangeList GetPendingMapForCache(int cacheIndex)
        {
            ulong cacheKey = (ulong)cacheIndex;
            if (!_pendingMaps.TryGetValue(cacheKey, out var pendingMap))
            {
                pendingMap = new PageAlignedRangeList(
                    (offset, size) => _sharedCaches[cacheIndex].SysIcacheInvalidate(offset, size),
                    (address, func) => RegisterFunction(address, func));
                _pendingMaps[cacheKey] = pendingMap;
            }
            return pendingMap;
        }

        private bool HasInAnyPendingMap(ulong guestAddress)
        {
            foreach (var pendingMap in _pendingMaps.Values)
            {
                if (pendingMap.Has(guestAddress))
                {
                    return true;
                }
            }
            return false;
        }

        private int AllocateInSharedCache(int codeLength)
        {
            for (int i = 0; i < _sharedCaches.Count; i++)
            {
                try
                {
                    return (i << 28) | _sharedCaches[i].Allocate(codeLength);
                }
                catch (OutOfMemoryException)
                {
                }
            }

            lock (_lock)
            {
                var allocator = _sharedCaches[0].Allocator;
                _sharedCaches.Add(new(SharedCacheSize));
                return (_sharedCaches.Count - 1) << 28 | _sharedCaches[_sharedCaches.Count - 1].Allocate(codeLength);
            }
        }

        private int AllocateInLocalCache(int codeLength)
        {
            for (int i = 0; i < _localCaches.Count; i++)
            {
                try
                {
                    return (i << 28) | _localCaches[i].Allocate(codeLength);
                }
                catch (OutOfMemoryException)
                {
                    
                }
            }

            lock (_lock)
            {
                var allocator = _localCaches[0].Allocator;
                _localCaches.Add(new(LocalCacheSize));
                return (_localCaches.Count - 1) << 28 | _localCaches[_localCaches.Count - 1].Allocate(codeLength);
            }
        }

        private static (int cacheIndex, int offset) SplitCacheOffset(int combinedOffset)
        {
            return (combinedOffset >> 28, combinedOffset & 0xFFFFFFF);
        }

        public unsafe IntPtr Map(IntPtr framePointer, ReadOnlySpan<byte> code, ulong guestAddress, ulong guestSize)
        {
            try
            {
                if (TryGetThreadLocalFunction(guestAddress, out IntPtr funcPtr))
                {
                    return funcPtr;
                }

                lock (_lock)
                {
                    if (!HasInAnyPendingMap(guestAddress) && !Translator.Functions.ContainsKey(guestAddress))
                    {
                        int combinedOffset = AllocateInSharedCache(code.Length);
                        var (cacheIndex, funcOffset) = SplitCacheOffset(combinedOffset);

                        MemoryCache cache = _sharedCaches[cacheIndex];
                        funcPtr = cache.Pointer + funcOffset;
                        code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));
                        funcPtr = cache.RxPointer + funcOffset;

                        TranslatedFunction function = new(funcPtr, guestSize);

                        GetPendingMapForCache(cacheIndex).Add(funcOffset, code.Length, guestAddress, function);
                    }

                    ClearThreadLocalCache(framePointer);

                    return AddThreadLocalFunction(code, guestAddress);
                }
            }
            catch
            {
                lock (_lock)
                {
                    var funcPtr = IntPtr.Zero;
                    int combinedOffset = AllocateInSharedCache(code.Length);
                    var (cacheIndex, funcOffset) = SplitCacheOffset(combinedOffset);

                    MemoryCache cache = _sharedCaches[cacheIndex];
                    funcPtr = cache.Pointer + funcOffset;
                    code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));
                    funcPtr = cache.RxPointer + funcOffset;

                    TranslatedFunction function = new(funcPtr, guestSize);

                    GetPendingMapForCache(cacheIndex).Add(funcOffset, code.Length, guestAddress, function);

                    ClearThreadLocalCache(framePointer);

                    return AddThreadLocalFunction(code, guestAddress);
                }
            }
        }

        public unsafe IntPtr MapPageAligned(ReadOnlySpan<byte> code)
        {
            lock (_lock)
            {
                int sizeAligned = BitUtils.AlignUp(code.Length, (int)MemoryBlock.GetPageSize());
                
                // Try to allocate in existing caches
                for (int cacheIndex = 0; cacheIndex < _sharedCaches.Count; cacheIndex++)
                {
                    try
                    {
                        var pendingMap = GetPendingMapForCache(cacheIndex);
                        pendingMap.Pad(_sharedCaches[cacheIndex].CacheAllocator);

                        int funcOffset2 = _sharedCaches[cacheIndex].Allocate(sizeAligned);
                        
                        Debug.Assert((funcOffset2 & ((int)MemoryBlock.GetPageSize() - 1)) == 0);

                        IntPtr funcPtr2 = _sharedCaches[cacheIndex].Pointer + funcOffset2;
                        code.CopyTo(new Span<byte>((void*)funcPtr2, code.Length));
                        funcPtr2 = _sharedCaches[cacheIndex].RxPointer + funcOffset2;

                        _sharedCaches[cacheIndex].SysIcacheInvalidate(funcOffset2, sizeAligned);

                        return funcPtr2;
                    }
                    catch (OutOfMemoryException)
                    {
                        // Try next cache
                    }
                }
                
                if (DualMappedJitAllocator.hasTXM)
                {
                    throw new InvalidOperationException("Trying to map JIT memory with a debugger after being detached when on TXM.");
                }

                // All existing caches are full, create a new one
                var newCache = new MemoryCache(SharedCacheSize);
                _sharedCaches.Add(newCache);
                int newCacheIndex = _sharedCaches.Count - 1;
                
                var newPendingMap = GetPendingMapForCache(newCacheIndex);
                newPendingMap.Pad(newCache.CacheAllocator);

                int funcOffset = newCache.Allocate(sizeAligned);
                
                Debug.Assert((funcOffset & ((int)MemoryBlock.GetPageSize() - 1)) == 0);

                IntPtr funcPtr = newCache.Pointer + funcOffset;
                code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));
                funcPtr = newCache.RxPointer + funcOffset;

                newCache.SysIcacheInvalidate(funcOffset, sizeAligned);

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
                        foreach (var pendingMap in _pendingMaps.Values)
                        {
                            // Get the cache index from the pendingMap key
                            if (_pendingMaps.FirstOrDefault(x => x.Value == pendingMap).Key is ulong cacheIndex)
                            {
                                // Use the correct shared cache for padding based on the cache index
                                pendingMap.Pad(_sharedCaches[(int)cacheIndex].CacheAllocator);
                            }
                        }
                    }
                }

                funcPtr = entry.FuncPtr;

                return true;
            }

            funcPtr = IntPtr.Zero;

            return false;
        }

        private void ClearThreadLocalCache(IntPtr framePointer)
        {
            // Try to delete functions that are already on the shared cache
            // and no longer being executed.

            if (_threadLocalCache == null)
            {
                return;
            }

            IntPtr[] cachePointers = new IntPtr[_localCaches.Count];
            int[] cacheSizes = new int[_localCaches.Count];

            for (int i = 0; i < _localCaches.Count; i++)
            {
                cachePointers[i] = _localCaches[i].RxPointer;
                cacheSizes[i] = LocalCacheSize;
            }

            IntPtr[] sharedPointers = new IntPtr[_sharedCaches.Count];
            int[] sharedSizes = new int[_sharedCaches.Count];

            for (int i = 0; i < _sharedCaches.Count; i++)
            {
                sharedPointers[i] = _sharedCaches[i].RxPointer;
                sharedSizes[i] = SharedCacheSize;
            }

            // Collect call stack entries from all caches
            HashSet<ulong> callStackAddresses = new HashSet<ulong>();
            
            for (int i = 0; i < Math.Max(_localCaches.Count, _sharedCaches.Count); i++)
            {
                IntPtr localPtr = i < _localCaches.Count ? cachePointers[i] : IntPtr.Zero;
                int localSize = i < _localCaches.Count ? cacheSizes[i] : 0;
                IntPtr sharedPtr = i < _sharedCaches.Count ? sharedPointers[i] : IntPtr.Zero;
                int sharedSize = i < _sharedCaches.Count ? sharedSizes[i] : 0;
                
                IEnumerable<ulong> callStack = _stackWalker.GetCallStack(
                    framePointer,
                    localPtr,
                    localSize,
                    sharedPtr,
                    sharedSize
                );
                
                foreach (ulong address in callStack)
                {
                    callStackAddresses.Add(address);
                }
            }

            List<(ulong, ThreadLocalCacheEntry)> toDelete = new();

            foreach ((ulong address, ThreadLocalCacheEntry entry) in _threadLocalCache)
            {
                // We only want to delete if the function is already on the shared cache,
                // otherwise we will keep translating the same function over and over again.
                bool canDelete = !HasInAnyPendingMap(address);
                if (!canDelete)
                {
                    continue;
                }

                // We can only delete if the function is not part of the current thread call stack,
                // otherwise we will crash the program when the thread returns to it.
                foreach (ulong funcAddress in callStackAddresses)
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

            int pageSize = (int)MemoryBlock.GetPageSize();

            foreach ((ulong address, ThreadLocalCacheEntry entry) in toDelete)
            {
                _threadLocalCache.Remove(address);

                int sizeAligned = BitUtils.AlignUp(entry.Size, pageSize);
                var (cacheIndex, offset) = SplitCacheOffset(entry.Offset);

                _localCaches[cacheIndex].Free(offset, sizeAligned);
            }
        }

        public void ClearEntireThreadLocalCache()
        {
            // Thread is exiting, delete everything.

            if (_threadLocalCache == null)
            {
                return;
            }

            int pageSize = (int)MemoryBlock.GetPageSize();

            foreach ((_, ThreadLocalCacheEntry entry) in _threadLocalCache)
            {
                int sizeAligned = BitUtils.AlignUp(entry.Size, pageSize);
                var (cacheIndex, offset) = SplitCacheOffset(entry.Offset);

                _localCaches[cacheIndex].Free(offset, sizeAligned);
            }

            _threadLocalCache.Clear();
            _threadLocalCache = null;
        }

        private unsafe IntPtr AddThreadLocalFunction(ReadOnlySpan<byte> code, ulong guestAddress)
        {
            int alignedSize = BitUtils.AlignUp(code.Length, (int)MemoryBlock.GetPageSize());
            int combinedOffset = AllocateInLocalCache(alignedSize);
            var (cacheIndex, funcOffset) = SplitCacheOffset(combinedOffset);

            Debug.Assert((funcOffset & (int)(MemoryBlock.GetPageSize() - 1)) == 0);

            IntPtr funcPtr = _localCaches[cacheIndex].Pointer + funcOffset;
            code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));
            funcPtr = _localCaches[cacheIndex].RxPointer + funcOffset;

            (_threadLocalCache ??= new()).Add(guestAddress, new(funcOffset, code.Length, funcPtr, cacheIndex));

            _localCaches[cacheIndex].SysIcacheInvalidate(funcOffset, alignedSize);

            return funcPtr;
        }

        private void RegisterFunction(ulong address, TranslatedFunction func)
        {
            TranslatedFunction oldFunc = Translator.Functions.GetOrAdd(address, func.GuestSize, func);

            Debug.Assert(oldFunc == func);

            _translator.RegisterFunction(address, func);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var cache in _localCaches)
                {
                    cache.Dispose();
                }
                
                foreach (var cache in _sharedCaches)
                {
                    cache.Dispose();
                }
                
                _localCaches.Clear();
                _sharedCaches.Clear();
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}