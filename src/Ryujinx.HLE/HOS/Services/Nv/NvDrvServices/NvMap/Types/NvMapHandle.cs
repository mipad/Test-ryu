using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap
{
    internal class NvMapHandle
    {
        public int Handle { get; internal set; }
        public int Id { get; internal set; }
        public uint Size { get; set; }
        public int Align { get; set; }
        public int Kind { get; set; }
        public ulong Address { get; set; }
        public bool Allocated { get; set; }
        public ulong DmaMapAddress { get; set; }

        private long _referenceCount;

        public NvMapHandle()
        {
            _referenceCount = 1; // Default reference count
            Handle = 0;
            Id = 0;
            Size = 0;
            Align = 0;
            Kind = 0;
            Address = 0;
            Allocated = false;
            DmaMapAddress = 0;
        }

        public NvMapHandle(uint size) : this()
        {
            Size = size;
        }

        /// <summary>
        /// Increments the reference count for this handle in a thread-safe manner
        /// </summary>
        public void IncrementRefCount()
        {
            Interlocked.Increment(ref _referenceCount);
        }

        /// <summary>
        /// Decrements the reference count for this handle in a thread-safe manner
        /// </summary>
        /// <returns>The new reference count after decrementing</returns>
        public long DecrementRefCount()
        {
            return Interlocked.Decrement(ref _referenceCount);
        }

        /// <summary>
        /// Gets the current reference count in a thread-safe manner
        /// </summary>
        public long GetRefCount()
        {
            return Interlocked.Read(ref _referenceCount);
        }
    }
}
