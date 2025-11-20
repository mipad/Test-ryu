using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Memory.Range
{
    public class RangeItem<TValue>(TValue value) where TValue : IRange
    {
        public RangeItem<TValue> Next;
        public RangeItem<TValue> Previous;
        
        public readonly ulong Address = value.Address;
        public readonly ulong EndAddress = value.Address + value.Size;

        public readonly TValue Value = value;

        public readonly List<ulong> QuickAccessAddresses = [];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool OverlapsWith(ulong address, ulong endAddress)
        {
            return Address < endAddress && address < EndAddress;
        }
    }
    
    class AddressEqualityComparer : IEqualityComparer<ulong>
    {
        public bool Equals(ulong u1, ulong u2)
        {
            return u1 == u2;
        }

        public int GetHashCode(ulong value) => (int)(value >> 5);
        
        public static readonly AddressEqualityComparer Comparer = new();
    }
    
    /// <summary>
    /// Result of an Overlaps Finder function. WARNING: if the result is from the optimized
    /// Overlaps Finder, the StartIndex will be -1 even when the result isn't empty
    /// </summary>
    /// <remarks>
    /// startIndex is inclusive.
    /// endIndex is exclusive.
    /// </remarks>
    public readonly struct OverlapResult<T> where T : IRange
    {
        public readonly int StartIndex = -1;
        public readonly int EndIndex = -1;
        public readonly RangeItem<T> QuickResult;
        public int Count => EndIndex - StartIndex;

        public OverlapResult(int startIndex, int endIndex, RangeItem<T> quickResult = null)
        {
            this.StartIndex = startIndex;
            this.EndIndex = endIndex;
            this.QuickResult = quickResult;
        }
    }

    /// <summary>
    /// Sorted list of ranges that supports binary search.
    /// </summary>
    /// <typeparam name="T">Type of the range.</typeparam>
    public class RangeList<T> : RangeListBase<T> where T : IRange
    {
        public readonly ReaderWriterLockSlim Lock = new();
        
        private readonly Dictionary<ulong, RangeItem<T>> _quickAccess = new(AddressEqualityComparer.Comparer);

        /// <summary>
        /// Creates a new range list.
        /// </summary>
        public RangeList() { }
        
        /// <summary>
        /// Creates a new range list.
        /// </summary>
        /// <param name="backingInitialSize">The initial size of the backing array</param>
        public RangeList(int backingInitialSize) : base(backingInitialSize) { }

        /// <summary>
        /// Adds a new item to the list.
        /// </summary>
        /// <param name="item">The item to be added</param>
        public override void Add(T item)
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
        protected override bool Update(T item)
        {
            int index = BinarySearch(item.Address);

            if (index >= 0)
            {
                while (index < Count)
                {
                    if (Items[index].Value.Equals(item))
                    {
                        foreach (ulong address in Items[index].QuickAccessAddresses)
                        {
                            _quickAccess.Remove(address);
                        }
                        
                        Items[index] = new RangeItem<T>(item);

                        return true;
                    }

                    if (Items[index].Address > item.Address)
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
            Debug.Assert(item.Address != item.EndAddress);
            
            Debug.Assert(item.Address % 32 == 0);
            
            if (Count + 1 > Items.Length)
            {
                Array.Resize(ref Items, Items.Length + BackingGrowthSize);
            }

            if (index >= Count)
            {
                if (index == Count)
                {
                    if (index != 0)
                    {
                        item.Previous = Items[index - 1];
                        Items[index - 1].Next = item;
                    }
                    Items[index] = item;
                    Count++;
                }
            }
            else
            {
                Array.Copy(Items, index, Items, index + 1, Count - index);

                Items[index] = item;
                if (index != 0)
                {
                    item.Previous = Items[index - 1];
                    Items[index - 1].Next = item;
                }
                
                item.Next = Items[index + 1];
                Items[index + 1].Previous = item;
                
                Count++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemoveAt(int index)
        {
            foreach (ulong address in Items[index].QuickAccessAddresses)
            {
                _quickAccess.Remove(address);
            }

            if (index < Count - 1)
            {
                Items[index + 1].Previous = index > 0 ? Items[index - 1] : null;
            }

            if (index > 0)
            {
                Items[index - 1].Next = index < Count - 1 ? Items[index + 1] : null;
            }
            
            if (index < --Count)
            {
                Array.Copy(Items, index + 1, Items, index, Count - index);
            }
        }

        /// <summary>
        /// Removes a range of items from the item list
        /// </summary>
        /// <param name="startItem">The first item in the range of items to be removed</param>
        /// <param name="endItem">The last item in the range of items to be removed</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void RemoveRange(RangeItem<T> startItem, RangeItem<T> endItem)
        {
            if (endItem.Next is not null)
            {
                endItem.Next.Previous = startItem.Previous;
            }
            
            if (startItem.Previous is not null)
            {
                startItem.Previous.Next = endItem.Next;
            }
            
            RangeItem<T> current = startItem;
            while (current != endItem.Next)
            {
                foreach (ulong address in current.QuickAccessAddresses)
                {
                    _quickAccess.Remove(address);
                }
                
                current = current.Next;
            }
            
            RangeItem<T>[] array = [];
            OverlapResult<T> overlapResult = FindOverlaps(startItem.Address, endItem.EndAddress, ref array);
            
            if (overlapResult.EndIndex < Count)
            {
                Array.Copy(Items, overlapResult.EndIndex, Items, overlapResult.StartIndex, Count - overlapResult.EndIndex);
                Count -= overlapResult.Count;
            }
            else if (overlapResult.EndIndex == Count)
            {
                Count = overlapResult.StartIndex;
            }
            else
            {
                Debug.Assert(false);
            }
        }

        /// <summary>
        /// Removes an item from the list.
        /// </summary>
        /// <param name="item">The item to be removed</param>
        /// <returns>True if the item was removed, or false if it was not found</returns>
        public override bool Remove(T item)
        {
            int index = BinarySearch(item.Address);

            if (index >= 0)
            {
                while (index < Count)
                {
                    if (Items[index].Value.Equals(item))
                    {
                        RemoveAt(index);

                        return true;
                    }

                    if (Items[index].Address > item.Address)
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
        public override RangeItem<T> FindOverlap(ulong address, ulong size)
        {
            int index = BinarySearchLeftEdge(address, address + size);

            if (index < 0)
            {
                return null;
            }

            return Items[index];
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
        public override RangeItem<T> FindOverlapFast(ulong address, ulong size)
        {
            if (_quickAccess.TryGetValue(address, out RangeItem<T> quickResult))
            {
                return quickResult;
            }
            
            int index = BinarySearch(address, address + size);

            if (index < 0)
            {
                return null;
            }

            if (Items[index].OverlapsWith(address, address + 1))
            {
                _quickAccess.Add(address, Items[index]);
                Items[index].QuickAccessAddresses.Add(address);
            }

            return Items[index];
        }
        
        /// <summary>
        /// Gets all items on the list overlapping the specified memory range.
        /// </summary>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size in bytes of the range</param>
        /// <param name="output">Output array where matches will be written. It is automatically resized to fit the results</param>
        /// <returns>Range information of overlapping items found</returns>
        private OverlapResult<T> FindOverlaps(ulong address, ulong size, ref RangeItem<T>[] output)
        {
            int outputCount = 0;

            ulong endAddress = address + size;
            
            int startIndex = BinarySearch(address, endAddress);
            if (startIndex < 0)
                startIndex = ~startIndex;
            int endIndex = -1;

            for (int i = startIndex; i < Count; i++)
            {
                ref RangeItem<T> item = ref Items[i];

                if (item.Address >= endAddress)
                {
                    endIndex = i;
                    break;
                }

                if (item.OverlapsWith(address, endAddress))
                {
                    outputCount++;
                }
            }

            if (endIndex == -1 && outputCount > 0)
            {
                endIndex = Count;
            }

            if (outputCount > 0 && outputCount == endIndex - startIndex)
            {
                Array.Resize(ref output, outputCount);
                Array.Copy(Items, endIndex - outputCount, output, 0, outputCount);
                
                return new OverlapResult<T>(startIndex, endIndex);
            }
            else if (outputCount > 0)
            {
                Array.Resize(ref output, outputCount);
                int arrIndex = 0;
                for (int i = startIndex; i < endIndex; i++)
                {
                    output[arrIndex++] = Items[i];
                }
                
                return new OverlapResult<T>(endIndex - outputCount, endIndex);
            }
            
            return new OverlapResult<T>();
        }

        public override IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return Items[i].Value;
            }
        }
    }
}
