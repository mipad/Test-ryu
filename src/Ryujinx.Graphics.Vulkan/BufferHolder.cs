using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
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

        // 改进的内存监控
        private static long _totalAllocatedMemory = 0;
        private static readonly object _memoryLock = new object();
        private readonly long _allocatedSize;
        private static int _allocationFailures = 0;
        private static DateTime _lastFailureTime = DateTime.MinValue;

        // 新增：GC控制
        private static int _gcCount = 0;
        private static DateTime _lastGCTime = DateTime.MinValue;

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

            // 内存跟踪
            _allocatedSize = size;
            lock (_memoryLock)
            {
                _totalAllocatedMemory += _allocatedSize;
                Logger.Debug?.Print(LogClass.Gpu, $"Buffer allocated: 0x{size:X} bytes, Type: {currentType}, Total: 0x{_totalAllocatedMemory:X} bytes");
            }
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
            
            _allocatedSize = size;
            lock (_memoryLock)
            {
                _totalAllocatedMemory += _allocatedSize;
                Logger.Debug?.Print(LogClass.Gpu, $"Buffer allocated: 0x{size:X} bytes, Type: {currentType}, Total: 0x{_totalAllocatedMemory:X} bytes");
            }
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
            
            _allocatedSize = size;
            lock (_memoryLock)
            {
                _totalAllocatedMemory += _allocatedSize;
                Logger.Debug?.Print(LogClass.Gpu, $"Buffer allocated: 0x{size:X} bytes, Type: {_activeType}, Total: 0x{_totalAllocatedMemory:X} bytes");
            }
        }

        // 静态方法获取内存使用情况
        public static long GetTotalAllocatedMemory()
        {
            lock (_memoryLock)
            {
                return _totalAllocatedMemory;
            }
        }

        public static int GetAllocationFailureCount()
        {
            lock (_memoryLock)
            {
                return _allocationFailures;
            }
        }

        public static void RecordAllocationFailure()
        {
            lock (_memoryLock)
            {
                _allocationFailures++;
                _lastFailureTime = DateTime.UtcNow;
                
                if (_allocationFailures > 10)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"High allocation failure count: {_allocationFailures}, last failure at {_lastFailureTime}");
                }
            }
        }

        public static void ResetAllocationFailures()
        {
            lock (_memoryLock)
            {
                _allocationFailures = 0;
                Logger.Info?.Print(LogClass.Gpu, "Allocation failure counter reset");
            }
        }

        // 改进的可用内存估算
        public static long GetAvailableMemoryEstimate(VulkanRenderer gd)
        {
            try
            {
                // 使用更保守的估算方法
                long totalMemory = GetTotalPhysicalMemory();
                long allocatedMemory = GetTotalAllocatedMemory();
                
                // 保留至少512MB的可用空间
                long available = totalMemory - allocatedMemory - (512 * 1024 * 1024);
                
                // 确保不会返回负值
                return Math.Max(available, 64 * 1024 * 1024); // 至少64MB
            }
            catch
            {
                // 如果无法获取系统信息，返回保守估计
                return 256 * 1024 * 1024; // 256MB
            }
        }

        // 获取物理内存总量
        private static long GetTotalPhysicalMemory()
        {
            try
            {
                // 在移动设备上，我们使用更保守的估计
                // 假设大多数现代手机至少有4GB RAM
                return 4L * 1024 * 1024 * 1024; // 4GB
            }
            catch
            {
                return 2L * 1024 * 1024 * 1024; // 2GB 作为保守估计
            }
        }

        // 改进的垃圾回收方法
        public static bool ForceGarbageCollection(BufferManager bufferManager, int requiredSize, bool aggressive = false)
        {
            // 避免过于频繁的GC
            var now = DateTime.UtcNow;
            if ((now - _lastGCTime).TotalSeconds < 5) // 5秒内不重复GC
            {
                Logger.Debug?.Print(LogClass.Gpu, "GC skipped: too recent");
                return false;
            }

            _lastGCTime = now;
            _gcCount++;

            Logger.Info?.Print(LogClass.Gpu, 
                $"GC #{_gcCount} requested, required: 0x{requiredSize:X}, aggressive: {aggressive}");

            long memoryBefore = GetTotalAllocatedMemory();
            
            if (aggressive && _gcCount % 3 == 0) // 每3次失败才使用激进模式
            {
                Logger.Warning?.Print(LogClass.Gpu, "Using aggressive GC mode");
                
                // 激进模式：多次GC
                for (int i = 0; i < 2; i++) // 减少到2次
                {
                    GC.Collect(2, GCCollectionMode.Forced, true);
                    GC.WaitForPendingFinalizers();
                    
                    // 移除Thread.Sleep，在移动设备上可能不必要
                }
            }
            else
            {
                // 普通模式：单次GC
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
            }
            
            long memoryAfter = GetTotalAllocatedMemory();
            long freed = memoryBefore - memoryAfter;
            
            Logger.Info?.Print(LogClass.Gpu, 
                $"GC #{_gcCount} freed 0x{freed:X} bytes " +
                $"(before: 0x{memoryBefore:X}, after: 0x{memoryAfter:X})");

            bool success = freed >= requiredSize || memoryAfter < memoryBefore;
            
            if (success)
            {
                Logger.Info?.Print(LogClass.Gpu, $"GC #{_gcCount} successful");
            }
            else
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"GC #{_gcCount} insufficient. Freed: 0x{freed:X}, Required: 0x{requiredSize:X}");
            }
            
            return success;
        }

        // 改进的创建缓冲区回退策略
        public static BufferHolder CreateWithFallback(
            VulkanRenderer gd,
            Device device,
            int size,
            BufferAllocationType preferredType,
            out BufferAllocationType actualType)
        {
            actualType = preferredType;
            int attempt = 0;
            const int maxAttempts = 3; // 减少尝试次数
            
            // 简化回退策略
            var fallbackStrategy = new (BufferAllocationType type, string description)[]
            {
                (preferredType, "Original preferred type"),
                (BufferAllocationType.HostMapped, "Fallback to HostMapped"),
                (BufferAllocationType.HostMappedNoCache, "Fallback to HostMappedNoCache")
            };
            
            foreach (var strategy in fallbackStrategy)
            {
                if (attempt >= maxAttempts) break;
                
                attempt++;
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Buffer creation attempt {attempt}/{maxAttempts}: {strategy.description}, Size: 0x{size:X}");

                try
                {
                    var result = gd.BufferManager.CreateBacking(gd, size, strategy.type);
                    
                    if (result.buffer.Handle != 0)
                    {
                        actualType = result.resultType;
                        var holder = new BufferHolder(gd, device, result.buffer, result.allocation, size, preferredType, actualType);
                        
                        Logger.Info?.Print(LogClass.Gpu, $"Buffer created successfully with type: {actualType}");
                        ResetAllocationFailures();
                        return holder;
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"Buffer creation returned null buffer for type: {strategy.type}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Buffer creation failed with {strategy.type}: {ex.GetType().Name} - {ex.Message}");
                    
                    // 记录更详细的异常信息
                    if (ex.InnerException != null)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Inner exception: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
                    }
                }
                
                // 只有在真正需要时才进行GC
                if (attempt < maxAttempts && ShouldPerformGC())
                {
                    Logger.Info?.Print(LogClass.Gpu, "Performing garbage collection before next attempt...");
                    ForceGarbageCollection(gd.BufferManager, size / 4, false); // 减少要求的大小
                }
                else if (attempt < maxAttempts)
                {
                    Logger.Info?.Print(LogClass.Gpu, "Skipping GC, waiting for next attempt...");
                }
            }
            
            // 所有尝试都失败了
            RecordAllocationFailure();
            actualType = BufferAllocationType.Auto;
            
            Logger.Error?.Print(LogClass.Gpu, 
                $"All buffer creation attempts failed for size 0x{size:X}. " +
                $"Total failures: {GetAllocationFailureCount()}");
                
            return null;
        }

        // 判断是否应该执行GC
        private static bool ShouldPerformGC()
        {
            lock (_memoryLock)
            {
                // 基于失败次数和时间间隔来决定
                if (_allocationFailures > 5) return true;
                
                var timeSinceLastGC = DateTime.UtcNow - _lastGCTime;
                return timeSinceLastGC.TotalSeconds > 10; // 至少10秒间隔
            }
        }

        // 其余方法保持不变...
        // [原有的大量代码保持不变，包括CreateView, InsertBarrier, TryGetMirror等方法]
        
        public unsafe Auto<DisposableBufferView> CreateView(VkFormat format, int offset, int size, Action invalidateView)
        {
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
            size = Math.Min(size, Size - offset);

            if (!_pendingDataRanges.OverlapsWith(offset, size))
            {
                buffer = null;
                return false;
            }

            var key = ToMirrorKey(offset, size);

            if (_mirrors.TryGetValue(key, out StagingBufferReserved reserved))
            {
                buffer = reserved.Buffer.GetBuffer();
                offset = reserved.Offset;

                return true;
            }

            if (_waitable.IsBufferRangeInUse(offset, size, true))
            {
                ClearMirrors(cbs, offset, size);

                buffer = null;
                return false;
            }

            var baseData = new Span<byte>((void*)(_map + offset), size);
            var modData = _pendingData.AsSpan(offset, size);

            StagingBufferReserved? newMirror = _gd.BufferManager.StagingBuffer.TryReserveData(cbs, size);

            if (newMirror != null)
            {
                var mirror = newMirror.Value;
                _pendingDataRanges.FillData(baseData, modData, offset, new Span<byte>((void*)(mirror.Buffer._map + mirror.Offset), size));

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
            throw new NotImplementedException();
        }

        public void ClearMirrors()
        {
            if (_pendingData != null)
            {
                _mirrors.Clear();
            };
        }

        public void ClearMirrors(CommandBufferScoped cbs, int offset, int size)
        {
            if (_pendingData != null)
            {
                bool hadMirrors = _mirrors.Count > 0 && RemoveOverlappingMirrors(offset, size);

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
            return _map;
        }

        private void ClearFlushFence()
        {
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

            _flushLock.ExitReadLock();
            _flushLock.EnterWriteLock();

            if (_flushFence != null)
            {
                var fence = _flushFence;
                Interlocked.Increment(ref _flushWaiting);

                _flushLock.ExitWriteLock();

                fence.Wait();

                _flushLock.EnterWriteLock();

                if (Interlocked.Decrement(ref _flushWaiting) == 0)
                {
                    fence.Put();
                }

                _flushFence = null;
            }

            _flushLock.ExitWriteLock();
            _flushLock.EnterReadLock();
        }

        public PinnedSpan<byte> GetData(int offset, int size)
        {
            _flushLock.EnterReadLock();

            WaitForFlushFence();

            Span<byte> result;

            if (_map != IntPtr.Zero)
            {
                result = GetDataStorage(offset, size);

                _buffer.IncrementReferenceCount();

                _flushLock.ExitReadLock();

                return PinnedSpan<byte>.UnsafeFromSpan(result, _buffer.DecrementReferenceCount);
            }

            BackgroundResource resource = _gd.BackgroundResources.Get();

            if (_gd.CommandBufferPool.OwnedByCurrentThread)
            {
                _gd.FlushAllCommands();

                result = resource.GetFlushBuffer().GetBufferData(_gd.CommandBufferPool, this, offset, size);
            }
            else
            {
                result = resource.GetFlushBuffer().GetBufferData(resource.GetPool(), this, offset, size);
            }

            _flushLock.ExitReadLock();

            return PinnedSpan<byte>.UnsafeFromSpan(result);
        }

        public unsafe Span<byte> GetDataStorage(int offset, int size)
        {
            int mappingSize = Math.Min(size, Size - offset);

            if (_map != IntPtr.Zero)
            {
                return new Span<byte>((void*)(_map + offset), mappingSize);
            }

            throw new InvalidOperationException("The buffer is not host mapped.");
        }

        public bool RemoveOverlappingMirrors(int offset, int size)
        {
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
            if (offset < 0 || offset >= Size)
            {
                return;
            }

            int dataSize = Math.Min(data.Length, Size - offset);
            if (dataSize == 0)
            {
                return;
            }

            ReadOnlySpan<byte> dataSlice = data[..dataSize];

            bool allowMirror = _useMirrors && allowCbsWait && cbs != null && _activeType <= BufferAllocationType.HostMapped;

            if (_map != IntPtr.Zero)
            {
                bool isRented = _buffer.HasRentedCommandBufferDependency(_gd.CommandBufferPool);

                bool needsFlush = isRented && _waitable.IsBufferRangeInUse(offset, dataSize, false);

                if (!needsFlush)
                {
                    WaitForFences(offset, dataSize);

                    dataSlice.CopyTo(new Span<byte>((void*)(_map + offset), dataSize));

                    if (_pendingData != null)
                    {
                        bool removed = _pendingDataRanges.Remove(offset, dataSize);
                        if (RemoveOverlappingMirrors(offset, dataSize) || removed)
                        {
                            _gd.PipelineInternal.Rebind(_buffer, offset, dataSize);
                        }
                    }

                    SignalWrite(offset, dataSize);

                    return;
                }
            }

            if (allowMirror && !_waitable.IsBufferRangeInUse(offset, dataSize, true))
            {
                if (_pendingData == null)
                {
                    _pendingData = new byte[Size];
                    _mirrors = new Dictionary<ulong, StagingBufferReserved>();
                }

                dataSlice.CopyTo(_pendingData.AsSpan(offset, dataSize));
                _pendingDataRanges.Add(offset, dataSize);

                RemoveOverlappingMirrors(offset, dataSize);

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

            if (_map != IntPtr.Zero)
            {
                data[..dataSize].CopyTo(new Span<byte>((void*)(_map + offset), dataSize));
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

        public void SetDataInline(CommandBufferScoped cbs, Action endRenderPass, int dstOffset, ReadOnlySpan<byte> data)
        {
            if (dstOffset < 0 || dstOffset >= Size)
            {
                return;
            }

            int dataSize = Math.Min(data.Length, Size - dstOffset);
            if (dataSize <= 0)
            {
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
                throw new ArgumentNullException(nameof(dst), "Destination buffer is null.");
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
                var srcBuffer = registerSrcUsage ? 
                    src.Get(cbs, srcOffset, size).Value : 
                    src.GetUnsafe().Value;
                
                var dstBuffer = dst.Get(cbs, dstOffset, size, true).Value;

                if (srcBuffer.Handle == 0)
                {
                    throw new InvalidOperationException("Invalid source buffer handle (VkBuffer is null).");
                }

                if (dstBuffer.Handle == 0)
                {
                    throw new InvalidOperationException("Invalid destination buffer handle (VkBuffer is null).");
                }

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

                var region = new BufferCopy((ulong)srcOffset, (ulong)dstOffset, (ulong)size);
                
                gd.Api.CmdCopyBuffer(cbs.CommandBuffer, srcBuffer, dstBuffer, 1, &region);

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

            // 释放时更新内存统计
            lock (_memoryLock)
            {
                _totalAllocatedMemory -= _allocatedSize;
                Logger.Debug?.Print(LogClass.Gpu, $"Buffer disposed: 0x{_allocatedSize:X} bytes, Type: {_activeType}, Total: 0x{_totalAllocatedMemory:X} bytes");
            }

            _flushLock.EnterWriteLock();

            ClearFlushFence();

            _flushLock.ExitWriteLock();
        }
    }
}