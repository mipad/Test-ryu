using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Services.Time.Clock;
using Ryujinx.HLE.HOS.Services.Time.Clock.Types;
using Ryujinx.HLE.HOS.Services.Time.Types;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Time
{
    class TimeSharedMemory
    {
        private Switch _device;
        private KSharedMemory _sharedMemory;
        private SharedMemoryStorage _timeSharedMemoryStorage;
#pragma warning disable IDE0052 // Remove unread private member
        private int _timeSharedMemorySize;
#pragma warning restore IDE0052

        private const uint SteadyClockContextOffset = 0x00;
        private const uint LocalSystemClockContextOffset = 0x38;
        private const uint NetworkSystemClockContextOffset = 0x80;
        private const uint AutomaticCorrectionEnabledOffset = 0xC8;
        private const uint ContinuousAdjustmentTimePointOffset = 0xD0;

        public void Initialize(Switch device, KSharedMemory sharedMemory, SharedMemoryStorage timeSharedMemoryStorage, int timeSharedMemorySize)
        {
            _device = device;
            _sharedMemory = sharedMemory;
            _timeSharedMemoryStorage = timeSharedMemoryStorage;
            _timeSharedMemorySize = timeSharedMemorySize;

            // Clean the shared memory
            timeSharedMemoryStorage.ZeroFill();
        }

        public KSharedMemory GetSharedMemory()
        {
            return _sharedMemory;
        }

        public void SetupStandardSteadyClock(ITickSource tickSource, UInt128 clockSourceId, TimeSpanType currentTimePoint)
        {
            UpdateSteadyClock(tickSource, clockSourceId, currentTimePoint);
        }

        public void SetAutomaticCorrectionEnabled(bool isAutomaticCorrectionEnabled)
        {
            // We convert the bool to byte here as a bool in C# takes 4 bytes...
            WriteObjectToSharedMemory(AutomaticCorrectionEnabledOffset, 0, Convert.ToByte(isAutomaticCorrectionEnabled));
        }

        public void SetSteadyClockRawTimePoint(ITickSource tickSource, TimeSpanType currentTimePoint)
        {
            SteadyClockContext context = ReadObjectFromSharedMemory<SteadyClockContext>(SteadyClockContextOffset, 4);

            UpdateSteadyClock(tickSource, context.ClockSourceId, currentTimePoint);
        }

        private void UpdateSteadyClock(ITickSource tickSource, UInt128 clockSourceId, TimeSpanType currentTimePoint)
        {
            TimeSpanType ticksTimeSpan = TimeSpanType.FromTicks(tickSource.Counter, tickSource.Frequency);

            ContinuousAdjustmentTimePoint adjustmentTimePoint = new()
            {
                ClockOffset = (ulong)ticksTimeSpan.NanoSeconds,
                Multiplier = 1,
                DivisorLog2 = 0,
                Context = new SystemClockContext
                {
                    Offset = 0,
                    SteadyTimePoint = new SteadyClockTimePoint
                    {
                        ClockSourceId = clockSourceId,
                        TimePoint = 0,
                    },
                },
               
            },

        };
            WriteObjectToSharedMemory(ContinuousAdjustmentTimePointOffset, 4, adjustmentTimePoint);

            SteadyClockContext context = new()
            {
                InternalOffset = (ulong)(currentTimePoint.NanoSeconds - ticksTimeSpan.NanoSeconds),
                ClockSourceId = clockSourceId,
            };

            WriteObjectToSharedMemory(SteadyClockContextOffset, 4, context);
        }

        public void UpdateLocalSystemClockContext(SystemClockContext context)
        {
            WriteObjectToSharedMemory(LocalSystemClockContextOffset, 4, context);
        }

        public void UpdateNetworkSystemClockContext(SystemClockContext context)
        {
            WriteObjectToSharedMemory(NetworkSystemClockContextOffset, 4, context);
        }

        private unsafe T ReadObjectFromSharedMemory<T>(ulong offset, ulong padding) where T : unmanaged
{
    // 添加平台检查（在循环外只检查一次）
    if (!OperatingSystem.IsAndroid() && 
        !OperatingSystem.IsWindows() && 
        !OperatingSystem.IsLinux() && 
        !OperatingSystem.IsMacOS())
    {
        throw new PlatformNotSupportedException("Memory operations are only supported on Android, Windows, Linux, and macOS.");
    }

    T result;
    uint index;
    uint possiblyNewIndex;

    do
    {
        // 读取索引
        index = _timeSharedMemoryStorage.GetRef<uint>(offset);

        // 计算对象偏移量
        ulong objectOffset = offset + 4 + padding + (ulong)((index & 1) * Unsafe.SizeOf<T>());

        // 直接通过指针读取对象
        byte* ptr = (byte*)_timeSharedMemoryStorage.GetPointer(objectOffset).ToPointer();
        result = Unsafe.Read<T>(ptr);

        // 替换 MemoryBlock.Read 为指针操作
        byte* indexPtr = (byte*)_device.Memory.GetPointer(offset).ToPointer();
        possiblyNewIndex = Unsafe.Read<uint>(indexPtr);
    } while (index != possiblyNewIndex);

    return result;
} // 确保此处有闭合大括号

        private void WriteObjectToSharedMemory<T>(ulong offset, ulong padding, T value) where T : unmanaged
{
    // 使用原子操作更新索引
    uint newIndex = AtomicIncrement(ref _timeSharedMemoryStorage.GetRef<uint>(offset));

    ulong objectOffset = offset + 4 + padding + (ulong)((newIndex & 1) * Unsafe.SizeOf<T>());

    // 直接写入内存
    unsafe
    {
        byte* ptr = (byte*)_timeSharedMemoryStorage.GetPointer(objectOffset).ToPointer();
        Unsafe.Write(ptr, value);
    }
}

// 原子递增辅助方法
private uint AtomicIncrement(ref uint location)
{
    uint original, newValue;
    do
    {
        original = Volatile.Read(ref location);
        newValue = original + 1;
    } while (Interlocked.CompareExchange(ref location, newValue, original) != original);
    return newValue;
}
