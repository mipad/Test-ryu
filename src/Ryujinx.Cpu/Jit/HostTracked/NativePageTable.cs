using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.Jit.HostTracked
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("windows")]
    sealed class NativePageTable : IDisposable
    {
        private delegate ulong TrackingEventDelegate(ulong address, ulong size, bool write);

        private const int PageBits = 12;
        private const int PageSize = 1 << PageBits;
        private const int PageMask = PageSize - 1;

        private const int PteSize = 8;

        private readonly int _entriesPerPtPage;
        private readonly int _pageCommitmentBits;
        private readonly MemoryBlock _nativePageTable;
        private readonly ulong[] _pageCommitmentBitmap;
        private readonly ulong _hostPageSize;

        private bool _disposed;

        public IntPtr PageTablePointer
        {
            get
            {
#if LINUX || ANDROID || MACOS || WINDOWS
                return _nativePageTable.Pointer;
#else
                throw new PlatformNotSupportedException();
#endif
            }
        }

        public NativePageTable(ulong asSize)
{
#if LINUX || ANDROID || MACOS || WINDOWS
    ulong hostPageSize = MemoryBlock.GetPageSize();

    _entriesPerPtPage = (int)(hostPageSize / sizeof(ulong));
    int bitsPerPtPage = BitOperations.Log2((uint)_entriesPerPtPage);
    _pageCommitmentBits = PageBits + bitsPerPtPage;

    _hostPageSize = hostPageSize;
    _nativePageTable = new MemoryBlock((asSize / PageSize) * PteSize + _hostPageSize, MemoryAllocationFlags.Reserve);
    _pageCommitmentBitmap = new ulong[(asSize >> _pageCommitmentBits) / (sizeof(ulong) * 8)];
#else
    throw new PlatformNotSupportedException();
#endif
}

        public void Map(ulong va, ulong pa, ulong size, AddressSpacePartitioned addressSpace, MemoryBlock backingMemory, bool privateMap)
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            while (size != 0)
            {
                EnsureCommitment(va);

                if (privateMap)
                {
                    _nativePageTable.Write((va / PageSize) * PteSize, GetPte(va, addressSpace.GetPointer(va, PageSize)));
                }
                else
                {
                    _nativePageTable.Write((va / PageSize) * PteSize, GetPte(va, backingMemory.GetPointer(pa, PageSize)));
                }

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
#else
            throw new PlatformNotSupportedException();
#endif
        }

        public void Unmap(ulong va, ulong size)
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            IntPtr guardPagePtr = GetGuardPagePointer();

            while (size != 0)
            {
                _nativePageTable.Write((va / PageSize) * PteSize, GetPte(va, guardPagePtr));

                va += PageSize;
                size -= PageSize;
            }
#else
            throw new PlatformNotSupportedException();
#endif
        }

        public ulong Read(ulong va)
{
    #if LINUX || ANDROID || MACOS || WINDOWS
    ulong pte = _nativePageTable.Read<ulong>((va / PageSize) * PteSize);
    pte += va & ~(ulong)PageMask;
    return pte + (va & PageMask);
    #else
    throw new PlatformNotSupportedException();
    #endif
}

      public void Update(ulong va, IntPtr ptr, ulong size)
  {
  #if LINUX || ANDROID || MACOS || WINDOWS
      ulong remainingSize = size;

      while (remainingSize != 0)
      {
          EnsureCommitment(va);
          _nativePageTable.Write((va / PageSize) * PteSize, GetPte(va, ptr));

          va += PageSize;
          ptr += PageSize;
          remainingSize -= PageSize;
      }
  #else
      throw new PlatformNotSupportedException();
  #endif
  }

        private void EnsureCommitment(ulong va)
        {
            ulong bit = va >> _pageCommitmentBits;

            int index = (int)(bit / (sizeof(ulong) * 8));
            int shift = (int)(bit % (sizeof(ulong) * 8));

            ulong mask = 1UL << shift;

            ulong oldMask = _pageCommitmentBitmap[index];

            if ((oldMask & mask) == 0)
            {
                lock (_pageCommitmentBitmap)
  {
      oldMask = _pageCommitmentBitmap[index];
      if ((oldMask & mask) != 0)
      {
          return;
      }

      _nativePageTable.Commit(bit * _hostPageSize, _hostPageSize);

      Span<ulong> pageSpan = MemoryMarshal.Cast<byte, ulong>(_nativePageTable.GetSpan(bit * _hostPageSize, (int)_hostPageSize));

      Debug.Assert(pageSpan.Length == _entriesPerPtPage);

      IntPtr guardPagePtr = GetGuardPagePointer();

      for (int i = 0; i < pageSpan.Length; i++)
      {
          pageSpan[i] = GetPte((bit << _pageCommitmentBits) | ((ulong)i * PageSize), guardPagePtr);
      }

      _pageCommitmentBitmap[index] = oldMask | mask;
  }
            }
        }

        private IntPtr GetGuardPagePointer()
  {
  #if LINUX || ANDROID || MACOS || WINDOWS
      return _nativePageTable.GetPointer(_nativePageTable.Size - _hostPageSize, _hostPageSize);
  #else
      throw new PlatformNotSupportedException();
  #endif
  }

        private static ulong GetPte(ulong va, IntPtr ptr)
        {
            Debug.Assert((va & PageMask) == 0);

            return (ulong)ptr - va;
        }

        public ulong GetPhysicalAddress(ulong va)
  {
  #if LINUX || ANDROID || MACOS || WINDOWS
      return _pageTable.Read(va) + (va & PageMask);
  #else
      throw new PlatformNotSupportedException();
  #endif
  }

  private ulong VirtualMemoryEvent(ulong address, ulong size, bool write)
  {
  #if LINUX || ANDROID || MACOS || WINDOWS
      if (address < _nativePageTable.Size - _hostPageSize)
      {
          ulong va = address * (PageSize / sizeof(ulong));
          EnsureCommitment(va);
          return (ulong)_nativePageTable.Pointer + address;
      }
      else
      {
          throw new InvalidMemoryRegionException();
      }
  #else
      throw new PlatformNotSupportedException();
  #endif
  }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
#if LINUX || ANDROID || MACOS || WINDOWS
                    NativeSignalHandler.RemoveTrackedRegion((nuint)_nativePageTable.Pointer);
                    _nativePageTable.Dispose();
#endif
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
