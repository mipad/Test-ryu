using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Memory.Range
{
    public readonly struct RangeItem<TValue>(TValue value) where TValue : IRange
    {
        public readonly ulong Address = value.Address;
        public readonly ulong EndAddress = value.Address + value.Size;

        public readonly TValue Value = value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool OverlapsWith(ulong address, ulong endAddress)
        {
            return Address < endAddress && address < EndAddress;
        }
    }
    
    /// <summary>
    /// Result of an Overlaps Finder function.
    /// </summary>
    /// <remarks>
    /// startIndex is inclusive.
    /// endIndex is exclusive.
    /// </remarks>
    public readonly struct OverlapResult
    {
        public readonly int StartIndex = -1;
        public readonly int EndIndex = -1;
        public int Count => EndIndex - StartIndex;

        public OverlapResult(int startIndex, int endIndex)
        {
            this.StartIndex = startIndex;
            this.EndIndex = endIndex;
        }
    }

    /// <summary>
    /// Sorted list of ranges that supports binary search.
    /// </summary>
    /// <typeparam name="T">Type of the range.</typeparam>
    public class RangeList<T> : IEnumerable<T> where T : IRange
    {
        private const int BackingInitialSize = 1024;

        private RangeItem<T>[] _items;
        private readonly int _backingGrowthSize;

        public int Count { get; protected set; }
        
        public readonly ReaderWriterLockSlim Lock = new();
        
        private const int QuickAccessLength = 8;
        private int _offset;
        private int _count;
        private RangeItem<T>[] _quickAccess = new RangeItem<T>[QuickAccessLength];

        /// <summary>
        /// Creates a new range list.
        /// </summary>
        /// <param name="backingInitialSize">The initial size of the backing array</param>
        public RangeList(int backingInitialSize = BackingInitialSize)
        {
            _backingGrowthSize = backingInitialSize;
            _items = new RangeItem<T>[backingInitialSize];
        }

        /// <summary>
        /// Adds a new item to the list.
        /// </summary>
        /// <param name="item">The item to be added</param>
        public void Add(T item)
        {
            int index = BinarySearch(item.Address);

            if (index < 0)
            {
                index = ~index;
            }

            Insert(index, new RangeItem<T>(item));
        }

        /// <summary>
        /// Updates an item's end address on the list. Address must be the same.
        /// </summary>
        /// <param name="item">The item to be updated</param>
        /// <returns>True if the item was located and updated, false otherwise</returns>
        protected bool Update(T item)
        {
            int index = BinarySearch(item.Address);

            if (index >= 0)
            {
                while (index < Count)
                {
                    if (_items[index].Value.Equals(item))
                    {
                        _items[index] = new RangeItem<T>(item);
                        
                        _quickAccess = new RangeItem<T>[QuickAccessLength];
                        _count = 0;
                        _offset = 0;

                        return true;
                    }

                    if (_items[index].Address > item.Address)
                    {
                        break;
                    }

                    index++;
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Insert(int index, RangeItem<T> item)
        {
            if (Count + 1 > _items.Length)
            {
                Array.Resize(ref _items, _items.Length + _backingGrowthSize);
            }

            if (index >= Count)
            {
                if (index == Count)
                {
                    _items[Count++] = item;
                }
            }
            else
            {
                Array.Copy(_items, index, _items, index + 1, Count - index);

                _items[index] = item;
                Count++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void RemoveAt(int index)
        {
            if (index < --Count)
            {
                Array.Copy(_items, index + 1, _items, index, Count - index);
            }

            _quickAccess = new RangeItem<T>[QuickAccessLength];
            _count = 0;
            _offset = 0;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveRange(OverlapResult overlapResult)
        {
            if (overlapResult.EndIndex < Count)
            {
                Array.Copy(_items, overlapResult.EndIndex, _items, overlapResult.StartIndex, Count - overlapResult.EndIndex);
                Count -= overlapResult.Count;
            }
            else if (overlapResult.EndIndex == Count)
            {
                Count = overlapResult.StartIndex;
            }
            
            _quickAccess = new RangeItem<T>[QuickAccessLength];
            _count = 0;
            _offset = 0;
        }

        /// <summary>
        /// Removes an item from the list.
        /// </summary>
        /// <param name="item">The item to be removed</param>
        /// <returns>True if the item was removed, or false if it was not found</returns>
        public bool Remove(T item)
        {
            int index = BinarySearch(item.Address);

            if (index >= 0)
            {
                while (index < Count)
                {
                    if (_items[index].Value.Equals(item))
                    {
                        RemoveAt(index);

                        return true;
                    }

                    if (_items[index].Address > item.Address)
                    {
                        break;
                    }

                    index++;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets an item on the list overlapping the specified memory range.
        /// </summary>
        /// <remarks>
        /// This has no ordering guarantees of the returned item.
        /// It only ensures that the item returned overlaps the specified memory range.
        /// </remarks>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size in bytes of the range</param>
        /// <returns>The overlapping item, or the default value for the type if none found</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T FindOverlapFast(ulong address, ulong size)
        {
            for (int i = 0; i < _quickAccess.Length; i++)
            {
                ref RangeItem<T> item = ref _quickAccess[(i + _offset) % _quickAccess.Length];

                if (item.OverlapsWith(address, address + size))
                {
                    return item.Value;
                }
            }
            
            int index = BinarySearch(address, address + size);

            if (_count < _quickAccess.Length)
            {
                _quickAccess[_count++] = _items[index];
            }
            else
            {
                _quickAccess[_offset++ % _quickAccess.Length] = _items[index];
            }

            if (index < 0)
            {
                return default;
            }

            return _items[index].Value;
        }
        
        /// <summary>
        /// Gets all items on the list overlapping the specified memory range.
        /// </summary>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size in bytes of the range</param>
        /// <param name="output">Output array where matches will be written. It is automatically resized to fit the results</param>
        /// <returns>Range information of overlapping items found</returns>
        public OverlapResult FindOverlaps(ulong address, ulong size, ref RangeItem<T>[] output)
        {
            int outputIndex = 0;

            ulong endAddress = address + size;
            
            int startIndex = BinarySearch(address);
            if (startIndex < 0)
                startIndex = ~startIndex;
            int endIndex = -1;

            for (int i = startIndex; i < Count; i++)
            {
                ref RangeItem<T> item = ref _items[i];

                if (item.Address >= endAddress)
                {
                    endIndex = i;
                    break;
                }

                if (item.OverlapsWith(address, endAddress))
                {
                    outputIndex++;
                }
            }

            if (endIndex == -1 && outputIndex > 0)
            {
                endIndex = Count;
            }

            if (outputIndex > 0 && outputIndex == endIndex - startIndex)
            {
                Array.Resize(ref output, outputIndex);
                Array.Copy(_items, endIndex - outputIndex, output, 0, outputIndex);
                
                return new OverlapResult(startIndex, endIndex);
            }
            else if (outputIndex > 0)
            {
                Array.Resize(ref output, outputIndex);
                int arrIndex = 0;
                for (int i = startIndex; i < endIndex; i++)
                {
                    output[arrIndex++] = _items[i];
                }
                
                return new OverlapResult(endIndex - outputIndex, endIndex);
            }
            
            return new OverlapResult();
        }

        /// <summary>
        /// Gets all items on the list overlapping the specified memory range.
        /// </summary>
        /// <remarks>
        /// This method only returns correct results if none of the items on the list overlaps with
        /// each other. If that is not the case, this method should not be used.
        /// This method is faster than the regular method to find all overlaps.
        /// </remarks>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size in bytes of the range</param>
        /// <param name="output">Output array where matches will be written. It is automatically resized to fit the results</param>
        /// <returns>Range information of overlapping items found</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OverlapResult FindOverlapsNonOverlapping(ulong address, ulong size, ref RangeItem<T>[] output)
        {
            // This is a bit faster than FindOverlaps, but only works
            // when none of the items on the list overlaps with each other.

            ulong endAddress = address + size;

            (int index, int endIndex) = BinarySearchEdges(address, endAddress);

            if (index >= 0)
            {
                Array.Resize(ref output, endIndex - index);
                Array.Copy(_items, index, output, 0, endIndex - index);
            }

            return new OverlapResult(index, endIndex);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OverlapResult FindOverlapsNonOverlappingAsSpan(ulong address, ulong size, out ReadOnlySpan<RangeItem<T>> span)
        {
            // This is a bit faster than FindOverlaps, but only works
            // when none of the items on the list overlaps with each other.

            ulong endAddress = address + size;

            (int index, int endIndex) = BinarySearchEdges(address, endAddress);
            
            if (index >= 0)
            {
                span = new ReadOnlySpan<RangeItem<T>>(_items, index, endIndex - index);
                return new OverlapResult(index, endIndex);
            }

            span = ReadOnlySpan<RangeItem<T>>.Empty;
            return new OverlapResult(index, endIndex);
        }

        /// <summary>
        /// Gets the range of all items on the list overlapping the specified memory range.
        /// </summary>
        /// <remarks>
        /// This method only returns correct results if none of the items on the list overlaps with
        /// each other. If that is not the case, this method should not be used.
        /// This method is faster than the regular method to find all overlaps.
        /// </remarks>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size in bytes of the range</param>
        /// <returns>Range information of overlapping items found</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OverlapResult FindOverlapsNonOverlapping(ulong address, ulong size)
        {
            // This is a bit faster than FindOverlaps, but only works
            // when none of the items on the list overlaps with each other.

            ulong endAddress = address + size;

            (int index, int endIndex) = BinarySearchEdges(address, endAddress);

            return new OverlapResult(index, endIndex);
        }

        /// <summary>
        /// Performs binary search on the internal list of items.
        /// </summary>
        /// <param name="address">Address to find</param>
        /// <returns>List index of the item, or complement index of nearest item with lower value on the list</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int BinarySearch(ulong address)
        {
            int left = 0;
            int right = Count - 1;

            while (left <= right)
            {
                int range = right - left;

                int middle = left + (range >> 1);

                ref RangeItem<T> item = ref _items[middle];

                if (item.Address == address)
                {
                    return middle;
                }

                if (address < item.Address)
                {
                    right = middle - 1;
                }
                else
                {
                    left = middle + 1;
                }
            }

            return ~left;
        }
        
        /// <summary>
        /// Performs binary search for items overlapping a given memory range.
        /// </summary>
        /// <param name="address">Start address of the range</param>
        /// <param name="endAddress">End address of the range</param>
        /// <returns>List index of the item, or complement index of nearest item with lower value on the list</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int BinarySearch(ulong address, ulong endAddress)
        {
            int left = 0;
            int right = Count - 1;

            while (left <= right)
            {
                int range = right - left;

                int middle = left + (range >> 1);

                ref RangeItem<T> item = ref _items[middle];

                if (item.OverlapsWith(address, endAddress))
                {
                    return middle;
                }

                if (address < item.Address)
                {
                    right = middle - 1;
                }
                else
                {
                    left = middle + 1;
                }
            }

            return ~left;
        }

        /// <summary>
        /// Performs binary search for items overlapping a given memory range.
        /// </summary>
        /// <param name="address">Start address of the range</param>
        /// <param name="endAddress">End address of the range</param>
        /// <returns>Range information (inclusive, exclusive) of items that overlaps, or complement index of nearest item with lower value on the list</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (int, int) BinarySearchEdges(ulong address, ulong endAddress)
        {
            if (Count == 0)
                return (~0, ~0);

            if (Count == 1)
            {
                ref RangeItem<T> item = ref _items[0];

                if (item.OverlapsWith(address, endAddress))
                {
                    return (0, 1);
                }

                if (address < item.Address)
                {
                    return (~0, ~0);
                }
                else
                {
                    return (~1, ~1);
                }
            }

            int left = 0;
            int right = Count - 1;

            int leftEdge = -1;
            int rightEdgeMatch = -1;
            int rightEdgeNoMatch = -1;

            while (left <= right)
            {
                int range = right - left;

                int middle = left + (range >> 1);

                ref RangeItem<T> item = ref _items[middle];

                bool match = item.OverlapsWith(address, endAddress);

                if (range == 0)
                {
                    if (match)
                    {
                        leftEdge = middle;
                        break;
                    }
                    else if (address < item.Address)
                    {
                        return (~right, ~right);
                    }
                    else
                    {
                        return (~(right + 1), ~(right + 1));
                    }
                }

                if (match)
                {
                    right = middle;
                    if (rightEdgeMatch == -1)
                        rightEdgeMatch = middle;
                }
                else if (address < item.Address)
                {
                    right = middle - 1;
                    rightEdgeNoMatch = middle;
                }
                else
                {
                    left = middle + 1;
                }
            }

            if (left > right)
            {
                return (~left, ~left);
            }

            if (rightEdgeMatch == -1)
            {
                return (leftEdge, leftEdge + 1);
            }

            left = rightEdgeMatch;
            right = rightEdgeNoMatch > 0 ? rightEdgeNoMatch : Count - 1;

            while (left <= right)
            {
                int range = right - left;

                int middle = right - (range >> 1);

                ref RangeItem<T> item = ref _items[middle];

                bool match = item.OverlapsWith(address, endAddress);

                if (range == 0)
                {
                    if (match)
                        return (leftEdge, middle + 1);
                    else
                        return (leftEdge, middle);
                }

                if (match)
                {
                    left = middle;
                }
                else if (address < item.Address)
                {
                    right = middle - 1;
                }
                else
                {
                    left = middle + 1;
                }
            }

            return (leftEdge, right + 1);
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return _items[i].Value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return _items[i].Value;
            }
        }
    }
}
