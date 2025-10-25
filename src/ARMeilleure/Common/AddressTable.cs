using ARMeilleure.Diagnostics;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ARMeilleure.Common
{
    /// <summary>
    /// Represents a table of guest address to a value.
    /// </summary>
    /// <typeparam name="TEntry">Type of the value</typeparam>
    public unsafe class AddressTable<TEntry> : IDisposable where TEntry : unmanaged
    {
        /// <summary>
        /// Represents a level in an <see cref="AddressTable{TEntry}"/>.
        /// </summary>
        public readonly struct Level
        {
            /// <summary>
            /// Gets the index of the <see cref="Level"/> in the guest address.
            /// </summary>
            public int Index { get; }

            /// <summary>
            /// Gets the length of the <see cref="Level"/> in the guest address.
            /// </summary>
            public int Length { get; }

            /// <summary>
            /// Gets the mask which masks the bits used by the <see cref="Level"/>.
            /// </summary>
            public ulong Mask => ((1ul << Length) - 1) << Index;

            /// <summary>
            /// Initializes a new instance of the <see cref="Level"/> structure with the specified
            /// <paramref name="index"/> and <paramref name="length"/>.
            /// </summary>
            /// <param name="index">Index of the <see cref="Level"/></param>
            /// <param name="length">Length of the <see cref="Level"/></param>
            public Level(int index, int length)
            {
                (Index, Length) = (index, length);
            }

            /// <summary>
            /// Gets the value of the <see cref="Level"/> from the specified guest <paramref name="address"/>.
            /// </summary>
            /// <param name="address">Guest address</param>
            /// <returns>Value of the <see cref="Level"/> from the specified guest <paramref name="address"/></returns>
            public long GetValue(ulong address)
            {
                return (long)((address & Mask) >> Index);
            }
        }

        private bool _disposed;
        private TEntry** _table;
        private readonly List<IntPtr> _pages;

        /// <summary>
        /// Gets the bits used by the <see cref="Levels"/> of the <see cref="AddressTable{TEntry}"/> instance.
        /// </summary>
        public ulong Mask { get; }

        /// <summary>
        /// Gets the <see cref="Level"/>s used by the <see cref="AddressTable{TEntry}"/> instance.
        /// </summary>
        public Level[] Levels { get; }

        /// <summary>
        /// Gets or sets the default fill value of newly created leaf pages.
        /// </summary>
        public TEntry Fill { get; set; }

        /// <summary>
        /// Gets the base address of the <see cref="EntryTable{TEntry}"/>.
        /// </summary>
        /// <exception cref="ObjectDisposedException"><see cref="EntryTable{TEntry}"/> instance was disposed</exception>
        public IntPtr Base
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                lock (_pages)
                {
                    return (IntPtr)GetRootPage();
                }
            }
        }

        /// <summary>
        /// Constructs a new instance of the <see cref="AddressTable{TEntry}"/> class with the specified list of
        /// <see cref="Level"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="levels"/> is null</exception>
        /// <exception cref="ArgumentException">Length of <paramref name="levels"/> is less than 2</exception>
        public AddressTable(Level[] levels)
        {
            ArgumentNullException.ThrowIfNull(levels);

            if (levels.Length < 2)
            {
                throw new ArgumentException("Table must be at least 2 levels deep.", nameof(levels));
            }

            _pages = new List<IntPtr>(capacity: 16);

            Levels = levels;
            Mask = 0;

            foreach (var level in Levels)
            {
                Mask |= level.Mask;
            }

            // Android 特定优化：限制掩码大小
            if (OperatingSystem.IsAndroid())
            {
                ulong androidLimit = 0x7FFFFFFFF; // 扩展到32GB范围
                if (Mask >= androidLimit)
                {
                    // 使用完全限定的日志类名避免冲突
                    ARMeilleure.Diagnostics.Logger?.WriteLine($"Android AddressTable mask limited from 0x{Mask:X} to 0x{androidLimit:X}");
                    Mask = androidLimit;
                }
            }
        }

        /// <summary>
        /// Determines if the specified <paramref name="address"/> is in the range of the
        /// <see cref="AddressTable{TEntry}"/>.
        /// </summary>
        /// <param name="address">Guest address</param>
        /// <returns><see langword="true"/> if is valid; otherwise <see langword="false"/></returns>
        public bool IsValid(ulong address)
        {
            // Android 放宽验证：接受更大的地址范围
            if (OperatingSystem.IsAndroid())
            {
                // 方法1: 完全接受所有地址
                return true;
                
                // 方法2: 接受扩展范围
                // ulong extendedMask = 0x7FFFFFFFF; // 32GB
                // return (address & ~extendedMask) == 0;
            }
            
            return (address & ~Mask) == 0;
        }

        /// <summary>
        /// Gets a reference to the value at the specified guest <paramref name="address"/>.
        /// </summary>
        /// <param name="address">Guest address</param>
        /// <returns>Reference to the value at the specified guest <paramref name="address"/></returns>
        /// <exception cref="ObjectDisposedException"><see cref="EntryTable{TEntry}"/> instance was disposed</exception>
        /// <exception cref="ArgumentException"><paramref name="address"/> is not mapped</exception>
        public ref TEntry GetValue(ulong address)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Android 放宽验证
            if (!OperatingSystem.IsAndroid() && !IsValid(address))
            {
                throw new ArgumentException($"Address 0x{address:X} is not mapped onto the table.", nameof(address));
            }

            lock (_pages)
            {
                // Android 地址包装
                ulong effectiveAddress = address;
                if (OperatingSystem.IsAndroid())
                {
                    effectiveAddress = address & 0x7FFFFFFFF; // 包装到32GB范围内
                    if (address != effectiveAddress)
                    {
                        ARMeilleure.Diagnostics.Logger?.WriteLine($"Address wrapped: 0x{address:X} -> 0x{effectiveAddress:X}");
                    }
                }
                
                return ref GetPage(effectiveAddress)[Levels[^1].GetValue(effectiveAddress)];
            }
        }

        /// <summary>
        /// Gets the leaf page for the specified guest <paramref name="address"/>.
        /// </summary>
        /// <param name="address">Guest address</param>
        /// <returns>Leaf page for the specified guest <paramref name="address"/></returns>
        private TEntry* GetPage(ulong address)
        {
            TEntry** page = GetRootPage();

            for (int i = 0; i < Levels.Length - 1; i++)
            {
                ref Level level = ref Levels[i];
                ref TEntry* nextPage = ref page[level.GetValue(address)];

                if (nextPage == null)
                {
                    ref Level nextLevel = ref Levels[i + 1];

                    nextPage = i == Levels.Length - 2 ?
                        (TEntry*)Allocate(1 << nextLevel.Length, Fill, leaf: true) :
                        (TEntry*)Allocate(1 << nextLevel.Length, IntPtr.Zero, leaf: false);
                }

                page = (TEntry**)nextPage;
            }

            return (TEntry*)page;
        }

        /// <summary>
        /// Lazily initialize and get the root page of the <see cref="AddressTable{TEntry}"/>.
        /// </summary>
        /// <returns>Root page of the <see cref="AddressTable{TEntry}"/></returns>
        private TEntry** GetRootPage()
        {
            if (_table == null)
            {
                _table = (TEntry**)Allocate(1 << Levels[0].Length, fill: IntPtr.Zero, leaf: false);
            }

            return _table;
        }

        /// <summary>
        /// Allocates a block of memory of the specified type and length.
        /// </summary>
        /// <typeparam name="T">Type of elements</typeparam>
        /// <param name="length">Number of elements</param>
        /// <param name="fill">Fill value</param>
        /// <param name="leaf"><see langword="true"/> if leaf; otherwise <see langword="false"/></param>
        /// <returns>Allocated block</returns>
        private IntPtr Allocate<T>(int length, T fill, bool leaf) where T : unmanaged
        {
            var size = sizeof(T) * length;
            var page = (IntPtr)NativeAllocator.Instance.Allocate((uint)size);
            var span = new Span<T>((void*)page, length);

            span.Fill(fill);

            _pages.Add(page);

            TranslatorEventSource.Log.AddressTableAllocated(size, leaf);

            return page;
        }

        /// <summary>
        /// Releases all resources used by the <see cref="AddressTable{TEntry}"/> instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases all unmanaged and optionally managed resources used by the <see cref="AddressTable{TEntry}"/>
        /// instance.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> to dispose managed resources also; otherwise just unmanaged resouces</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                foreach (var page in _pages)
                {
                    Marshal.FreeHGlobal(page);
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Frees resources used by the <see cref="AddressTable{TEntry}"/> instance.
        /// </summary>
        ~AddressTable()
        {
            Dispose(false);
        }
    }
}
