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
#pragma warning disable IDE0052
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
                index = _timeSharedMemoryStorage.GetRef<uint>(offset);
                ulong objectOffset = offset + 4 + padding + (ulong)((index & 1) * Unsafe.SizeOf<T>());
                
                // 修正：通过 MemoryBlock 获取指针，并传入 size 参数
                byte* ptr = (byte*)_timeSharedMemoryStorage.Block.GetPointer(objectOffset, (ulong)Unsafe.SizeOf<T>()).ToPointer();
                result = Unsafe.Read<T>(ptr);

                // 修正：补全 GetPointer 的 size 参数
                byte* indexPtr = (byte*)_device.Memory.GetPointer(offset, (ulong)Unsafe.SizeOf<uint>()).ToPointer();
                possiblyNewIndex = Unsafe.Read<uint>(indexPtr);
            } while (index != possiblyNewIndex);

            return result;
        }

        private void WriteObjectToSharedMemory<T>(ulong offset, ulong padding, T value) where T : unmanaged
        {
            uint newIndex = AtomicIncrement(ref _timeSharedMemoryStorage.GetRef<uint>(offset));
            ulong objectOffset = offset + 4 + padding + (ulong)((newIndex & 1) * Unsafe.SizeOf<T>());

            unsafe
            {
                // 修正：通过 MemoryBlock 获取指针，并传入 size 参数
                byte* ptr = (byte*)_timeSharedMemoryStorage.Block.GetPointer(objectOffset, (ulong)Unsafe.SizeOf<T>()).ToPointer();
                Unsafe.Write(ptr, value);
            }
        }

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
    }
}
