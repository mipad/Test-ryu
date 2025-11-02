using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    class BufferHolder : IDisposable, IMirrorable<DisposableBuffer>, IMirrorable<DisposableBufferView>
    {
        private const int MaxUpdateBufferSize = 0x10000;

        private const int SetCountThreshold = 100;
        private const int WriteCountThreshold = 50;
        private const int FlushCountThreshold = 5;

        public const int DeviceLocalSizeThreshold = 256 * 1024; // 256kb

        public const AccessFlags DefaultAccessFlags =
            AccessFlags.IndirectCommandReadBit |
            AccessFlags.ShaderReadBit |
            AccessFlags.ShaderWriteBit |
            AccessFlags.TransferReadBit |
            AccessFlags.TransferWriteBit |
            AccessFlags.UniformReadBit;

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly MemoryAllocation _allocation;
        private readonly Auto<DisposableBuffer> _buffer;
        private readonly Auto<MemoryAllocation> _allocationAuto;
        private readonly bool _allocationImported;
        private readonly ulong _bufferHandle;

        private CacheByRange<BufferHolder> _cachedConvertedBuffers;

        public int Size { get; }

        private readonly IntPtr _map;

        private readonly MultiFenceHolder _waitable;

        private bool _lastAccessIsWrite;

        private readonly BufferAllocationType _baseType;
        private readonly BufferAllocationType _activeType;

        private readonly ReaderWriterLockSlim _flushLock;
        private FenceHolder _flushFence;
        private int _flushWaiting;

        private byte[] _pendingData;
        private BufferMirrorRangeList _pendingDataRanges;
        private Dictionary<ulong, StagingBufferReserved> _mirrors;
        private bool _useMirrors;

        // 内存映射文件相关字段
        private MemoryMappedFile _memoryMappedFile;
        private MemoryMappedViewAccessor _memoryMappedAccessor;
        private unsafe byte* _memoryMappedPointer;
        private bool _isMemoryMappedBuffer;

        public BufferHolder(VulkanRenderer gd, Device device, VkBuffer buffer, MemoryAllocation allocation, int size, BufferAllocationType type, BufferAllocationType currentType)
        {
            _gd = gd;
            _device = device;
            _allocation = allocation;
            _allocationAuto = new Auto<MemoryAllocation>(allocation);
            _waitable = new MultiFenceHolder(size);
            _buffer = new Auto<DisposableBuffer>(new DisposableBuffer(gd.Api, device, buffer), this, _waitable, _allocationAuto);
            _bufferHandle = buffer.Handle;
            Size = size;
            _map = allocation.HostPointer;

            _baseType = type;
            _activeType = currentType;

            _flushLock = new ReaderWriterLockSlim();
            _useMirrors = gd.IsTBDR;

            // 初始化缓存
            _cachedConvertedBuffers = new CacheByRange<BufferHolder>();
            _pendingDataRanges = new BufferMirrorRangeList();
        }

        public BufferHolder(VulkanRenderer gd, Device device, VkBuffer buffer, Auto<MemoryAllocation> allocation, int size, BufferAllocationType type, BufferAllocationType currentType, int offset)
        {
            _gd = gd;
            _device = device;
            _allocation = allocation.GetUnsafe();
            _allocationAuto = allocation;
            _allocationImported = true;
            _waitable = new MultiFenceHolder(size);
            _buffer = new Auto<DisposableBuffer>(new DisposableBuffer(gd.Api, device, buffer), this, _waitable, _allocationAuto);
            _bufferHandle = buffer.Handle;
            Size = size;
            _map = _allocation.HostPointer + offset;

            _baseType = type;
            _activeType = currentType;

            _flushLock = new ReaderWriterLockSlim();

            // 初始化缓存
            _cachedConvertedBuffers = new CacheByRange<BufferHolder>();
            _pendingDataRanges = new BufferMirrorRangeList();
        }

        public BufferHolder(VulkanRenderer gd, Device device, VkBuffer buffer, int size, Auto<MemoryAllocation>[] storageAllocations)
        {
            _gd = gd;
            _device = device;
            _waitable = new MultiFenceHolder(size);
            _buffer = new Auto<DisposableBuffer>(new DisposableBuffer(gd.Api, device, buffer), _waitable, storageAllocations);
            _bufferHandle = buffer.Handle;
            Size = size;

            _baseType = BufferAllocationType.Sparse;
            _activeType = BufferAllocationType.Sparse;

            _flushLock = new ReaderWriterLockSlim();

            // 初始化缓存
            _cachedConvertedBuffers = new CacheByRange<BufferHolder>();
            _pendingDataRanges = new BufferMirrorRangeList();
        }

        /// <summary>
        /// 设置内存映射文件标记
        /// </summary>
        public unsafe void SetMemoryMappedFile(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, byte* pointer, int size)
        {
            _memoryMappedFile = mmf;
            _memoryMappedAccessor = accessor;
            _memoryMappedPointer = pointer;
            _isMemoryMappedBuffer = true;
            Logger.Warning?.Print(LogClass.Gpu, $"缓冲区使用内存映射文件: 大小=0x{size:X}, 指针=0x{(ulong)pointer:X}");
        }

        public unsafe Auto<DisposableBufferView> CreateView(VkFormat format, int offset, int size, Action invalidateView)
        {
            // 内存映射文件缓冲区不支持创建视图
            if (_isMemoryMappedBuffer)
            {
                Logger.Warning?.Print(LogClass.Gpu, "内存映射文件缓冲区不支持创建视图");
                return null;
            }

            var bufferViewCreateInfo = new BufferViewCreateInfo
            {
                SType = StructureType.BufferViewCreateInfo,
                Buffer = new VkBuffer(_bufferHandle),
                Format = format,
                Offset = (uint)offset,
                Range = (uint)size,
            };

            _gd.Api.CreateBufferView(_device, in bufferViewCreateInfo, null, out var bufferView).ThrowOnError();

            return new Auto<DisposableBufferView>(new DisposableBufferView(_gd.Api, _device, bufferView), this, _waitable, _buffer);
        }

        public unsafe void InsertBarrier(CommandBuffer commandBuffer, bool isWrite)
        {
            // 内存映射文件缓冲区不需要屏障
            if (_isMemoryMappedBuffer) return;

            // If the last access is write, we always need a barrier to be sure we will read or modify
            // the correct data.
            // If the last access is read, and current one is a write, we need to wait until the
            // read finishes to avoid overwriting data still in use.
            // Otherwise, if the last access is a read and the current one too, we don't need barriers.
            bool needsBarrier = isWrite || _lastAccessIsWrite;

            _lastAccessIsWrite = isWrite;

            if (needsBarrier)
            {
                MemoryBarrier memoryBarrier = new()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = DefaultAccessFlags,
                    DstAccessMask = DefaultAccessFlags,
                };

                _gd.Api.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.AllCommandsBit,
                    0,
                    1,
                    in memoryBarrier,
                    0,
                    null,
                    0,
                    null);
            }
        }

        private static ulong ToMirrorKey(int offset, int size)
        {
            return ((ulong)offset << 32) | (uint)size;
        }

        private static (int offset, int size) FromMirrorKey(ulong key)
        {
            return ((int)(key >> 32), (int)key);
        }

        private unsafe bool TryGetMirror(CommandBufferScoped cbs, ref int offset, int size, out Auto<DisposableBuffer> buffer)
        {
            // 内存映射文件缓冲区不支持镜像
            if (_isMemoryMappedBuffer)
            {
                buffer = null;
                return false;
            }

            size = Math.Min(size, Size - offset);

            // Does this binding need to be mirrored?
            if (!_pendingDataRanges.OverlapsWith(offset, size))
            {
                buffer = null;
                return false;
            }

            var key = ToMirrorKey(offset, size);

            if (_mirrors != null && _mirrors.TryGetValue(key, out StagingBufferReserved reserved))
            {
                buffer = reserved.Buffer.GetBuffer();
                offset = reserved.Offset;

                return true;
            }

            // Is this mirror allowed to exist? Can't be used for write in any in-flight write.
            if (_waitable.IsBufferRangeInUse(offset, size, true))
            {
                // Some of the data is not mirrorable, so upload the whole range.
                ClearMirrors(cbs, offset, size);

                buffer = null;
                return false;
            }

            // Build data for the new mirror.

            var baseData = new Span<byte>((void*)(_map + offset), size);
            var modData = _pendingData.AsSpan(offset, size);

            StagingBufferReserved? newMirror = _gd.BufferManager.StagingBuffer.TryReserveData(cbs, size);

            if (newMirror != null)
            {
                var mirror = newMirror.Value;
                _pendingDataRanges.FillData(baseData, modData, offset, new Span<byte>((void*)(mirror.Buffer._map + mirror.Offset), size));

                if (_mirrors == null)
                {
                    _mirrors = new Dictionary<ulong, StagingBufferReserved>();
                }

                if (_mirrors.Count == 0)
                {
                    _gd.PipelineInternal.RegisterActiveMirror(this);
                }

                _mirrors.Add(key, mirror);

                buffer = mirror.Buffer.GetBuffer();
                offset = mirror.Offset;

                return true;
            }
            else
            {
                // Data could not be placed on the mirror, likely out of space. Force the data to flush.
                ClearMirrors(cbs, offset, size);

                buffer = null;
                return false;
            }
        }

        public Auto<DisposableBuffer> GetBuffer()
        {
            return _buffer;
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, bool isWrite = false, bool isSSBO = false)
        {
            if (isWrite)
            {
                SignalWrite(0, Size);
            }

            return _buffer;
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, int offset, int size, bool isWrite = false)
        {
            if (isWrite)
            {
                SignalWrite(offset, size);
            }

            return _buffer;
        }

        public Auto<DisposableBuffer> GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored)
        {
            // 内存映射文件缓冲区不支持镜像
            if (_isMemoryMappedBuffer)
            {
                mirrored = false;
                return _buffer;
            }

            if (_useMirrors && _pendingData != null && TryGetMirror(cbs, ref offset, size, out Auto<DisposableBuffer> result))
            {
                mirrored = true;
                return result;
            }

            mirrored = false;
            return _buffer;
        }

        Auto<DisposableBufferView> IMirrorable<DisposableBufferView>.GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored)
        {
            // Cannot mirror buffer views right now.

            throw new NotImplementedException();
        }

        public void ClearMirrors()
        {
            // 内存映射文件缓冲区不支持镜像
            if (_isMemoryMappedBuffer) return;

            // Clear mirrors without forcing a flush. This happens when the command buffer is switched,
            // as all reserved areas on the staging buffer are released.

            if (_pendingData != null && _mirrors != null)
            {
                _mirrors.Clear();
            };
        }

        public void ClearMirrors(CommandBufferScoped cbs, int offset, int size)
        {
            // 内存映射文件缓冲区不支持镜像
            if (_isMemoryMappedBuffer) return;

            // Clear mirrors in the given range, and submit overlapping pending data.

            if (_pendingData != null)
            {
                bool hadMirrors = _mirrors != null && _mirrors.Count > 0 && RemoveOverlappingMirrors(offset, size);

                if (_pendingDataRanges.Count() != 0)
                {
                    UploadPendingData(cbs, offset, size);
                }

                if (hadMirrors)
                {
                    _gd.PipelineInternal.Rebind(_buffer, offset, size);
                }
            };
        }

        public void UseMirrors()
        {
            _useMirrors = true;
        }

        private void UploadPendingData(CommandBufferScoped cbs, int offset, int size)
        {
            var ranges = _pendingDataRanges.FindOverlaps(offset, size);

            if (ranges != null)
            {
                _pendingDataRanges.Remove(offset, size);

                foreach (var range in ranges)
                {
                    int rangeOffset = Math.Max(offset, range.Offset);
                    int rangeSize = Math.Min(offset + size, range.End) - rangeOffset;

                    if (_gd.PipelineInternal.CurrentCommandBuffer.CommandBuffer.Handle == cbs.CommandBuffer.Handle)
                    {
                        SetData(rangeOffset, _pendingData.AsSpan(rangeOffset, rangeSize), cbs, _gd.PipelineInternal.EndRenderPassDelegate, false);
                    }
                    else
                    {
                        SetData(rangeOffset, _pendingData.AsSpan(rangeOffset, rangeSize), cbs, null, false);
                    }
                }
            }
        }

        public Auto<MemoryAllocation> GetAllocation()
        {
            return _allocationAuto;
        }

        public (DeviceMemory, ulong) GetDeviceMemoryAndOffset()
        {
            return (_allocation.Memory, _allocation.Offset);
        }

        public void SignalWrite(int offset, int size)
        {
            if (offset == 0 && size == Size)
            {
                _cachedConvertedBuffers.Clear();
            }
            else
            {
                _cachedConvertedBuffers.ClearRange(offset, size);
            }
        }

        public BufferHandle GetHandle()
        {
            var handle = _bufferHandle;
            return Unsafe.As<ulong, BufferHandle>(ref handle);
        }

        public IntPtr Map(int offset, int mappingSize)
        {
            // 如果是内存映射文件缓冲区，返回内存映射指针
            if (_isMemoryMappedBuffer && _memoryMappedPointer != null)
            {
                unsafe
                {
                    return (IntPtr)(_memoryMappedPointer + offset);
                }
            }

            return _map;
        }

        private void ClearFlushFence()
        {
            // Assumes _flushLock is held as writer.

            if (_flushFence != null)
            {
                if (_flushWaiting == 0)
                {
                    _flushFence.Put();
                }

                _flushFence = null;
            }
        }

        private void WaitForFlushFence()
        {
            if (_flushFence == null)
            {
                return;
            }

            // If storage has changed, make sure the fence has been reached so that the data is in place.
            _flushLock.ExitReadLock();
            _flushLock.EnterWriteLock();

            if (_flushFence != null)
            {
                var fence = _flushFence;
                Interlocked.Increment(ref _flushWaiting);

                // Don't wait in the lock.

                _flushLock.ExitWriteLock();

                fence.Wait();

                _flushLock.EnterWriteLock();

                if (Interlocked.Decrement(ref _flushWaiting) == 0)
                {
                    fence.Put();
                }

                _flushFence = null;
            }

            // Assumes the _flushLock is held as reader, returns in same state.
            _flushLock.ExitWriteLock();
            _flushLock.EnterReadLock();
        }

        public unsafe PinnedSpan<byte> GetData(int offset, int size)
        {
            // 内存映射文件缓冲区直接返回内存映射数据
            if (_isMemoryMappedBuffer && _memoryMappedPointer != null)
            {
                var mappedResult = new Span<byte>(_memoryMappedPointer + offset, Math.Min(size, Size - offset));
                return PinnedSpan<byte>.UnsafeFromSpan(mappedResult);
            }

            _flushLock.EnterReadLock();

            WaitForFlushFence();

            Span<byte> dataResult;

            if (_map != IntPtr.Zero)
            {
                dataResult = GetDataStorage(offset, size);

                // Need to be careful here, the buffer can't be unmapped while the data is being used.
                _buffer.IncrementReferenceCount();

                _flushLock.ExitReadLock();

                return PinnedSpan<byte>.UnsafeFromSpan(dataResult, _buffer.DecrementReferenceCount);
            }

            BackgroundResource resource = _gd.BackgroundResources.Get();

            if (_gd.CommandBufferPool.OwnedByCurrentThread)
            {
                _gd.FlushAllCommands();

                dataResult = resource.GetFlushBuffer().GetBufferData(_gd.CommandBufferPool, this, offset, size);
            }
            else
            {
                dataResult = resource.GetFlushBuffer().GetBufferData(resource.GetPool(), this, offset, size);
            }

            _flushLock.ExitReadLock();

            // Flush buffer is pinned until the next GetBufferData on the thread, which is fine for current uses.
            return PinnedSpan<byte>.UnsafeFromSpan(dataResult);
        }

        public unsafe Span<byte> GetDataStorage(int offset, int size)
        {
            int mappingSize = Math.Min(size, Size - offset);

            // 将指针检查和使用都放在unsafe上下文中
            unsafe
            {
                if (_isMemoryMappedBuffer && _memoryMappedPointer != null)
                {
                    return new Span<byte>(_memoryMappedPointer + offset, mappingSize);
                }

                if (_map != IntPtr.Zero)
                {
                    return new Span<byte>((void*)(_map + offset), mappingSize);
                }
            }

            throw new InvalidOperationException("The buffer is not host mapped.");
        }

        public bool RemoveOverlappingMirrors(int offset, int size)
        {
            if (_mirrors == null) return false;

            List<ulong> toRemove = null;
            foreach (var key in _mirrors.Keys)
            {
                (int keyOffset, int keySize) = FromMirrorKey(key);
                if (!(offset + size <= keyOffset || offset >= keyOffset + keySize))
                {
                    toRemove ??= new List<ulong>();

                    toRemove.Add(key);
                }
            }

            if (toRemove != null)
            {
                foreach (var key in toRemove)
                {
                    _mirrors.Remove(key);
                }

                return true;
            }

            return false;
        }

        public unsafe void SetData(int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs = null, Action endRenderPass = null, bool allowCbsWait = true)
        {
            // 添加边界保护，防止设备/交换链重置后的越界写入
            if (offset < 0 || offset >= Size)
            {
                return;
            }

            int dataSize = Math.Min(data.Length, Size - offset);
            if (dataSize == 0)
            {
                return;
            }

            // 从这里开始使用裁剪后的数据切片
            ReadOnlySpan<byte> dataSlice = data[..dataSize];

            // 内存映射文件缓冲区直接写入内存映射文件
            if (_isMemoryMappedBuffer && _memoryMappedPointer != null)
            {
                unsafe
                {
                    dataSlice.CopyTo(new Span<byte>(_memoryMappedPointer + offset, dataSize));
                }
                return;
            }

            bool allowMirror = _useMirrors && allowCbsWait && cbs != null && _activeType <= BufferAllocationType.HostMapped;

            if (_map != IntPtr.Zero)
            {
                // If persistently mapped, set the data directly if the buffer is not currently in use.
                bool isRented = _buffer.HasRentedCommandBufferDependency(_gd.CommandBufferPool);

                // If the buffer is rented, take a little more time and check if the use overlaps this handle.
                bool needsFlush = isRented && _waitable.IsBufferRangeInUse(offset, dataSize, false);

                if (!needsFlush)
                {
                    WaitForFences(offset, dataSize);

                    unsafe
                    {
                        dataSlice.CopyTo(new Span<byte>((void*)(_map + offset), dataSize));
                    }

                    if (_pendingData != null)
                    {
                        bool removed = _pendingDataRanges.Remove(offset, dataSize);
                        if (RemoveOverlappingMirrors(offset, dataSize) || removed)
                        {
                            // If any mirrors were removed, rebind the buffer range.
                            _gd.PipelineInternal.Rebind(_buffer, offset, dataSize);
                        }
                    }

                    SignalWrite(offset, dataSize);

                    return;
                }
            }

            // If the buffer does not have an in-flight write (including an inline update), then upload data to a pendingCopy.
            if (allowMirror && !_waitable.IsBufferRangeInUse(offset, dataSize, true))
            {
                if (_pendingData == null)
                {
                    _pendingData = new byte[Size];
                    _mirrors = new Dictionary<ulong, StagingBufferReserved>();
                }

                dataSlice.CopyTo(_pendingData.AsSpan(offset, dataSize));
                _pendingDataRanges.Add(offset, dataSize);

                // Remove any overlapping mirrors.
                RemoveOverlappingMirrors(offset, dataSize);

                // Tell the graphics device to rebind any constant buffer that overlaps the newly modified range, as it should access a mirror.
                _gd.PipelineInternal.Rebind(_buffer, offset, dataSize);

                return;
            }

            if (_pendingData != null)
            {
                _pendingDataRanges.Remove(offset, dataSize);
            }

            if (cbs != null &&
                _gd.PipelineInternal.RenderPassActive &&
                !(_buffer.HasCommandBufferDependency(cbs.Value) &&
                _waitable.IsBufferRangeInUse(cbs.Value.CommandBufferIndex, offset, dataSize)))
            {
                // If the buffer hasn't been used on the command buffer yet, try to preload the data.
                // This avoids ending and beginning render passes on each buffer data upload.

                cbs = _gd.PipelineInternal.GetPreloadCommandBuffer();
                endRenderPass = null;
            }

            if (cbs == null ||
                !VulkanConfiguration.UseFastBufferUpdates ||
                dataSize > MaxUpdateBufferSize ||
                !TryPushData(cbs.Value, endRenderPass, offset, dataSlice))
            {
                if (allowCbsWait)
                {
                    _gd.BufferManager.StagingBuffer.PushData(_gd.CommandBufferPool, cbs, endRenderPass, this, offset, dataSlice);
                }
                else
                {
                    bool rentCbs = cbs == null;
                    if (rentCbs)
                    {
                        cbs = _gd.CommandBufferPool.Rent();
                    }

                    if (!_gd.BufferManager.StagingBuffer.TryPushData(cbs.Value, endRenderPass, this, offset, dataSlice))
                    {
                        // Need to do a slow upload.
                        BufferHolder srcHolder = _gd.BufferManager.Create(_gd, dataSize, baseType: BufferAllocationType.HostMapped);
                        srcHolder.SetDataUnchecked(0, dataSlice);

                        var srcBuffer = srcHolder.GetBuffer();
                        var dstBuffer = this.GetBuffer(cbs.Value.CommandBuffer, true);

                        Copy(_gd, cbs.Value, srcBuffer, dstBuffer, 0, offset, dataSize);

                        srcHolder.Dispose();
                    }

                    if (rentCbs)
                    {
                        cbs.Value.Dispose();
                    }
                }
            }
        }

        public unsafe void SetDataUnchecked(int offset, ReadOnlySpan<byte> data)
        {
            int dataSize = Math.Min(data.Length, Size - offset);
            if (dataSize == 0)
            {
                return;
            }

            // 内存映射文件缓冲区直接写入内存映射文件
            if (_isMemoryMappedBuffer && _memoryMappedPointer != null)
            {
                unsafe
                {
                    data[..dataSize].CopyTo(new Span<byte>(_memoryMappedPointer + offset, dataSize));
                }
                return;
            }

            if (_map != IntPtr.Zero)
            {
                unsafe
                {
                    data[..dataSize].CopyTo(new Span<byte>((void*)(_map + offset), dataSize));
                }
            }
            else
            {
                _gd.BufferManager.StagingBuffer.PushData(_gd.CommandBufferPool, null, null, this, offset, data[..dataSize]);
            }
        }

        public unsafe void SetDataUnchecked<T>(int offset, ReadOnlySpan<T> data) where T : unmanaged
        {
            SetDataUnchecked(offset, MemoryMarshal.AsBytes(data));
        }

        public unsafe void SetDataInline(CommandBufferScoped cbs, Action endRenderPass, int dstOffset, ReadOnlySpan<byte> data)
        {
            // 为内联更新添加边界检查
            if (dstOffset < 0 || dstOffset >= Size)
            {
                return;
            }

            int dataSize = Math.Min(data.Length, Size - dstOffset);
            if (dataSize <= 0)
            {
                return;
            }

            // 内存映射文件缓冲区直接写入内存映射文件
            if (_isMemoryMappedBuffer && _memoryMappedPointer != null)
            {
                unsafe
                {
                    data[..dataSize].CopyTo(new Span<byte>(_memoryMappedPointer + dstOffset, dataSize));
                }
                return;
            }

            if (!TryPushData(cbs, endRenderPass, dstOffset, data[..dataSize]))
            {
                throw new ArgumentException($"Invalid offset 0x{dstOffset:X} or data size 0x{dataSize:X}.");
            }
        }

        private unsafe bool TryPushData(CommandBufferScoped cbs, Action endRenderPass, int dstOffset, ReadOnlySpan<byte> data)
        {
            if ((dstOffset & 3) != 0 || (data.Length & 3) != 0)
            {
                return false;
            }

            endRenderPass?.Invoke();

            var dstBuffer = GetBuffer(cbs.CommandBuffer, dstOffset, data.Length, true).Get(cbs, dstOffset, data.Length, true).Value;

            InsertBufferBarrier(
                _gd,
                cbs.CommandBuffer,
                dstBuffer,
                DefaultAccessFlags,
                AccessFlags.TransferWriteBit,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                dstOffset,
                data.Length);

            fixed (byte* pData = data)
            {
                for (ulong offset = 0; offset < (ulong)data.Length;)
                {
                    ulong size = Math.Min(MaxUpdateBufferSize, (ulong)data.Length - offset);
                    _gd.Api.CmdUpdateBuffer(cbs.CommandBuffer, dstBuffer, (ulong)dstOffset + offset, size, pData + offset);
                    offset += size;
                }
            }

            InsertBufferBarrier(
                _gd,
                cbs.CommandBuffer,
                dstBuffer,
                AccessFlags.TransferWriteBit,
                DefaultAccessFlags,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.AllCommandsBit,
                dstOffset,
                data.Length);

            return true;
        }

        public static unsafe void Copy(
            VulkanRenderer gd,
            CommandBufferScoped cbs,
            Auto<DisposableBuffer> src,
            Auto<DisposableBuffer> dst,
            int srcOffset,
            int dstOffset,
            int size,
            bool registerSrcUsage = true)
        {
            // 增强的参数验证和错误处理
            if (gd == null)
            {
                throw new ArgumentNullException(nameof(gd), "Graphics device is null.");
            }
            
            if (cbs.CommandBuffer.Handle == 0)
            {
                throw new ArgumentException("Invalid command buffer.", nameof(cbs));
            }

            if (src == null)
            {
                throw new ArgumentNullException(nameof(src), "Source buffer is null.");
            }

            if (dst == null)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"复制操作跳过: 目标缓冲区为null, 大小=0x{size:X}, 源偏移=0x{srcOffset:X}, 目标偏移=0x{dstOffset:X}");
                return;
            }

            if (srcOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(srcOffset), "Source offset cannot be negative.");
            }

            if (dstOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dstOffset), "Destination offset cannot be negative.");
            }

            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Copy size must be greater than zero.");
            }

            try
            {
                // 获取缓冲区
                var srcBuffer = registerSrcUsage ? 
                    src.Get(cbs, srcOffset, size).Value : 
                    src.GetUnsafe().Value;
                
                var dstBuffer = dst.Get(cbs, dstOffset, size, true).Value;

                // 验证缓冲区句柄
                if (srcBuffer.Handle == 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"复制操作跳过: 源缓冲区句柄无效, 大小=0x{size:X}");
                    return;
                }

                if (dstBuffer.Handle == 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"复制操作跳过: 目标缓冲区句柄无效, 大小=0x{size:X}");
                    return;
                }

                // 设置目标缓冲区屏障 (准备写入)
                InsertBufferBarrier(
                    gd,
                    cbs.CommandBuffer,
                    dstBuffer,
                    DefaultAccessFlags,
                    AccessFlags.TransferWriteBit,
                    PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.TransferBit,
                    dstOffset,
                    size);

                // 执行缓冲区复制
                var region = new BufferCopy((ulong)srcOffset, (ulong)dstOffset, (ulong)size);
                
                gd.Api.CmdCopyBuffer(cbs.CommandBuffer, srcBuffer, dstBuffer, 1, &region);

                // 设置目标缓冲区屏障 (完成写入)
                InsertBufferBarrier(
                    gd,
                    cbs.CommandBuffer,
                    dstBuffer,
                    AccessFlags.TransferWriteBit,
                    DefaultAccessFlags,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.AllCommandsBit,
                    dstOffset,
                    size);
            }
            catch (Exception ex) when (ex is ObjectDisposedException)
            {
                throw new InvalidOperationException("Attempted to use a disposed buffer.", ex);
            }
            catch (NullReferenceException)
            {
                // 处理设备/表面重置后的空引用异常
                Logger.Warning?.Print(LogClass.Gpu, $"复制操作跳过: 空引用异常, 大小=0x{size:X}");
                return;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"复制操作异常: {ex.Message}, 大小=0x{size:X}");
                return;
            }
        }

        public static unsafe void InsertBufferBarrier(
            VulkanRenderer gd,
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            AccessFlags srcAccessMask,
            AccessFlags dstAccessMask,
            PipelineStageFlags srcStageMask,
            PipelineStageFlags dstStageMask,
            int offset,
            int size)
        {
            BufferMemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = srcAccessMask,
                DstAccessMask = dstAccessMask,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = (ulong)offset,
                Size = (ulong)size,
            };

            gd.Api.CmdPipelineBarrier(
                commandBuffer,
                srcStageMask,
                dstStageMask,
                0,
                0,
                null,
                1,
                in memoryBarrier,
                0,
                null);
        }

        public void WaitForFences()
        {
            _waitable.WaitForFences(_gd.Api, _device);
        }

        public void WaitForFences(int offset, int size)
        {
            _waitable.WaitForFences(_gd.Api, _device, offset, size);
        }

        private bool BoundToRange(int offset, ref int size)
        {
            if (offset >= Size)
            {
                return false;
            }

            size = Math.Min(Size - offset, size);

            return true;
        }

        public Auto<DisposableBuffer> GetBufferI8ToI16(CommandBufferScoped cbs, int offset, int size)
        {
            if (!BoundToRange(offset, ref size))
            {
                return null;
            }

            var key = new I8ToI16CacheKey(_gd);

            if (!_cachedConvertedBuffers.TryGetValue(offset, size, key, out var holder))
            {
                holder = _gd.BufferManager.Create(_gd, (size * 2 + 3) & ~3, baseType: BufferAllocationType.DeviceLocal);

                _gd.PipelineInternal.EndRenderPass();
                _gd.HelperShader.ConvertI8ToI16(_gd, cbs, this, holder, offset, size);

                key.SetBuffer(holder.GetBuffer());

                _cachedConvertedBuffers.Add(offset, size, key, holder);
            }

            return holder.GetBuffer();
        }

        public Auto<DisposableBuffer> GetAlignedVertexBuffer(CommandBufferScoped cbs, int offset, int size, int stride, int alignment)
        {
            if (!BoundToRange(offset, ref size))
            {
                return null;
            }

            var key = new AlignedVertexBufferCacheKey(_gd, stride, alignment);

            if (!_cachedConvertedBuffers.TryGetValue(offset, size, key, out var holder))
            {
                int alignedStride = (stride + (alignment - 1)) & -alignment;

                holder = _gd.BufferManager.Create(_gd, (size / stride) * alignedStride, baseType: BufferAllocationType.DeviceLocal);

                _gd.PipelineInternal.EndRenderPass();
                _gd.HelperShader.ChangeStride(_gd, cbs, this, holder, offset, size, stride, alignedStride);

                key.SetBuffer(holder.GetBuffer());

                _cachedConvertedBuffers.Add(offset, size, key, holder);
            }

            return holder.GetBuffer();
        }

        public Auto<DisposableBuffer> GetBufferTopologyConversion(CommandBufferScoped cbs, int offset, int size, IndexBufferPattern pattern, int indexSize)
        {
            if (!BoundToRange(offset, ref size))
            {
                return null;
            }

            var key = new TopologyConversionCacheKey(_gd, pattern, indexSize);

            if (!_cachedConvertedBuffers.TryGetValue(offset, size, key, out var holder))
            {
                // The destination index size is always I32.

                int indexCount = size / indexSize;

                int convertedCount = pattern.GetConvertedCount(indexCount);

                holder = _gd.BufferManager.Create(_gd, convertedCount * 4, baseType: BufferAllocationType.DeviceLocal);

                _gd.PipelineInternal.EndRenderPass();
                _gd.HelperShader.ConvertIndexBuffer(_gd, cbs, this, holder, pattern, indexSize, offset, indexCount);

                key.SetBuffer(holder.GetBuffer());

                _cachedConvertedBuffers.Add(offset, size, key, holder);
            }

            return holder.GetBuffer();
        }

        public bool TryGetCachedConvertedBuffer(int offset, int size, ICacheKey key, out BufferHolder holder)
        {
            return _cachedConvertedBuffers.TryGetValue(offset, size, key, out holder);
        }

        public void AddCachedConvertedBuffer(int offset, int size, ICacheKey key, BufferHolder holder)
        {
            _cachedConvertedBuffers.Add(offset, size, key, holder);
        }

        public void AddCachedConvertedBufferDependency(int offset, int size, ICacheKey key, Dependency dependency)
        {
            _cachedConvertedBuffers.AddDependency(offset, size, key, dependency);
        }

        public void RemoveCachedConvertedBuffer(int offset, int size, ICacheKey key)
        {
            _cachedConvertedBuffers.Remove(offset, size, key);
        }

        public void Dispose()
        {
            // 如果是内存映射文件缓冲区，释放内存映射文件资源
            if (_isMemoryMappedBuffer)
            {
                unsafe
                {
                    if (_memoryMappedPointer != null)
                    {
                        _memoryMappedAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                        _memoryMappedPointer = null;
                    }
                }
                _memoryMappedAccessor?.Dispose();
                _memoryMappedFile?.Dispose();
                Logger.Warning?.Print(LogClass.Gpu, $"释放内存映射文件缓冲区: 大小=0x{Size:X}");
            }

            _gd.PipelineInternal?.FlushCommandsIfWeightExceeding(_buffer, (ulong)Size);

            _buffer.Dispose();
            _cachedConvertedBuffers.Dispose();
            if (_allocationImported)
            {
                _allocationAuto.DecrementReferenceCount();
            }
            else
            {
                _allocationAuto?.Dispose();
            }

            _flushLock.EnterWriteLock();

            ClearFlushFence();

            _flushLock.ExitWriteLock();
        }
    }
}
