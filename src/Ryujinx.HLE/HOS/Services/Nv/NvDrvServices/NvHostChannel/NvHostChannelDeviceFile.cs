using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.Types;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel
{
    class NvHostChannelDeviceFile : NvDeviceFile
    {
        private static readonly ConcurrentDictionary<ulong, Host1xContext> _host1xContextRegistry = new();

        private const uint MaxModuleSyncpoint = 16;

#pragma warning disable IDE0052 // Remove unread private member
        private uint _timeout;
        private uint _submitTimeout;
        private uint _timeslice;
#pragma warning restore IDE0052

        private readonly Switch _device;

        private readonly IVirtualMemoryManager _memory;
        private readonly Host1xContext _host1xContext;
        private readonly long _contextId;

        public GpuChannel Channel { get; }

        public enum ResourcePolicy
        {
            Device,
            Channel,
        }

        protected static uint[] DeviceSyncpoints = new uint[MaxModuleSyncpoint];

        protected uint[] ChannelSyncpoints;

        protected static ResourcePolicy ChannelResourcePolicy = ResourcePolicy.Device;

        private NvFence _channelSyncpoint;

        // 添加性能监控
        private readonly Stopwatch _perfStopwatch = new();
        private long _totalSubmitTime = 0;
        private int _submitCount = 0;
        private long _maxSubmitTime = 0;

        public NvHostChannelDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner)
        {
            _device = context.Device;
            _memory = memory;
            _timeout = 3000;
            _submitTimeout = 0;
            _timeslice = 0;
            _host1xContext = GetHost1XContext(context.Device.Gpu, owner);
            _contextId = _host1xContext.Host1x.CreateContext();
            Channel = _device.Gpu.CreateChannel();

            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"NvHostChannelDeviceFile created: owner=0x{owner:X}, contextId={_contextId}, channel={Channel.GetHashCode()}");

            ChannelInitialization.InitializeState(Channel);

            ChannelSyncpoints = new uint[MaxModuleSyncpoint];

            _channelSyncpoint.Id = _device.System.HostSyncpoint.AllocateSyncpoint(false);
            _channelSyncpoint.UpdateValue(_device.System.HostSyncpoint);
            
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"Allocated syncpoint for channel: id={_channelSyncpoint.Id}, initial value={_channelSyncpoint.Value}");

            // 启动性能监控线程
            StartPerformanceMonitoring();
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"NvHostChannelDeviceFile.Ioctl: type=0x{command.Type:X2}, num=0x{command.Number:X2}, " +
                $"dir={command.DirectionValue}, size={command.Size}, owner=0x{Owner:X}");

            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvHostCustomMagic)
            {
                switch (command.Number)
                {
                    case 0x01:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing Submit command (0x01)");
                        result = Submit(arguments);
                        break;
                    case 0x02:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing GetSyncpoint command (0x02)");
                        result = CallIoctlMethod<GetParameterArguments>(GetSyncpoint, arguments);
                        break;
                    case 0x03:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing GetWaitBase command (0x03)");
                        result = CallIoctlMethod<GetParameterArguments>(GetWaitBase, arguments);
                        break;
                    case 0x07:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing SetSubmitTimeout command (0x07)");
                        result = CallIoctlMethod<uint>(SetSubmitTimeout, arguments);
                        break;
                    case 0x09:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing MapCommandBuffer command (0x09)");
                        result = MapCommandBuffer(arguments);
                        break;
                    case 0x0a:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing UnmapCommandBuffer command (0x0a)");
                        result = UnmapCommandBuffer(arguments);
                        break;
                }
            }
            else if (command.Type == NvIoctl.NvHostMagic)
            {
                switch (command.Number)
                {
                    case 0x01:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing SetNvMapFd command (0x01)");
                        result = CallIoctlMethod<int>(SetNvMapFd, arguments);
                        break;
                    case 0x03:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing SetTimeout command (0x03)");
                        result = CallIoctlMethod<uint>(SetTimeout, arguments);
                        break;
                    case 0x08:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing SubmitGpfifo command (0x08)");
                        result = SubmitGpfifo(arguments);
                        break;
                    case 0x09:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing AllocObjCtx command (0x09)");
                        result = CallIoctlMethod<AllocObjCtxArguments>(AllocObjCtx, arguments);
                        break;
                    case 0x0b:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing ZcullBind command (0x0b)");
                        result = CallIoctlMethod<ZcullBindArguments>(ZcullBind, arguments);
                        break;
                    case 0x0c:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing SetErrorNotifier command (0x0c)");
                        result = CallIoctlMethod<SetErrorNotifierArguments>(SetErrorNotifier, arguments);
                        break;
                    case 0x0d:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing SetPriority command (0x0d)");
                        result = CallIoctlMethod<NvChannelPriority>(SetPriority, arguments);
                        break;
                    case 0x18:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing AllocGpfifoEx command (0x18)");
                        result = CallIoctlMethod<AllocGpfifoExArguments>(AllocGpfifoEx, arguments);
                        break;
                    case 0x1a:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing AllocGpfifoEx2 command (0x1a)");
                        result = CallIoctlMethod<AllocGpfifoExArguments>(AllocGpfifoEx2, arguments);
                        break;
                    case 0x1d:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing SetTimeslice command (0x1d)");
                        result = CallIoctlMethod<uint>(SetTimeslice, arguments);
                        break;
                }
            }
            else if (command.Type == NvIoctl.NvGpuMagic)
            {
                switch (command.Number)
                {
                    case 0x14:
                        Logger.Debug?.Print(LogClass.ServiceNv, "Processing SetUserData command (0x14)");
                        result = CallIoctlMethod<ulong>(SetUserData, arguments);
                        break;
                }
            }

            if (result != NvInternalResult.Success && result != NvInternalResult.NotImplemented)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, 
                    $"NvHostChannelDeviceFile.Ioctl failed: type=0x{command.Type:X2}, num=0x{command.Number:X2}, result={result}");
            }

            return result;
        }

        private NvInternalResult Submit(Span<byte> arguments)
        {
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                SubmitArguments submitHeader = GetSpanAndSkip<SubmitArguments>(ref arguments, 1)[0];
                Span<CommandBuffer> commandBuffers = GetSpanAndSkip<CommandBuffer>(ref arguments, submitHeader.CmdBufsCount);
#pragma warning disable IDE0059 // Remove unnecessary value assignment
                Span<Reloc> relocs = GetSpanAndSkip<Reloc>(ref arguments, submitHeader.RelocsCount);
                Span<uint> relocShifts = GetSpanAndSkip<uint>(ref arguments, submitHeader.RelocsCount);
#pragma warning restore IDE0059
                Span<SyncptIncr> syncptIncrs = GetSpanAndSkip<SyncptIncr>(ref arguments, submitHeader.SyncptIncrsCount);
                Span<uint> fenceThresholds = GetSpanAndSkip<uint>(ref arguments, submitHeader.FencesCount);

                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"Submit: cmdBufs={submitHeader.CmdBufsCount}, relocs={submitHeader.RelocsCount}, " +
                    $"syncptIncrs={submitHeader.SyncptIncrsCount}, fences={submitHeader.FencesCount}");

                lock (_device)
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, "Submit: Entered device lock");
                    
                    // 记录同步点递增操作
                    for (int i = 0; i < syncptIncrs.Length; i++)
                    {
                        SyncptIncr syncptIncr = syncptIncrs[i];
                        uint id = syncptIncr.Id;
                        
                        uint oldMax = _device.System.HostSyncpoint.ReadSyncpointMaxValue(id);
                        fenceThresholds[i] = Context.Device.System.HostSyncpoint.IncrementSyncpointMax(id, syncptIncr.Incrs);
                        uint newMax = fenceThresholds[i];
                        
                        Logger.Debug?.Print(LogClass.ServiceNv, 
                            $"Submit: Syncpoint incremented - id={id}, incrs={syncptIncr.Incrs}, " +
                            $"oldMax={oldMax}, newMax={newMax}");
                    }

                    Logger.Debug?.Print(LogClass.ServiceNv, $"Submit: Processing {commandBuffers.Length} command buffers");
                    
                    // 记录每个命令缓冲区的提交
                    foreach (CommandBuffer commandBuffer in commandBuffers)
                    {
                        NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(Owner, commandBuffer.Mem);

                        if (map == null)
                        {
                            Logger.Warning?.Print(LogClass.ServiceNv, 
                                $"Submit: Invalid map handle 0x{commandBuffer.Mem:X8} for command buffer");
                            continue;
                        }

                        var data = _memory.GetSpan(map.Address + commandBuffer.Offset, commandBuffer.WordsCount * 4);

                        Logger.Debug?.Print(LogClass.ServiceNv, 
                            $"Submit: CommandBuffer - mem=0x{commandBuffer.Mem:X8}, offset=0x{commandBuffer.Offset:X}, " +
                            $"words={commandBuffer.WordsCount}, size={data.Length} bytes");

                        _host1xContext.Host1x.Submit(MemoryMarshal.Cast<byte, int>(data), _contextId);
                    }
                    
                    Logger.Debug?.Print(LogClass.ServiceNv, "Submit: Exit device lock");
                }

                return NvInternalResult.Success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceNv, $"Submit error: {ex.Message}");
                return NvInternalResult.InvalidInput;
            }
            finally
            {
                stopwatch.Stop();
                long elapsedMs = stopwatch.ElapsedMilliseconds;
                
                // 更新性能统计
                lock (_perfStopwatch)
                {
                    _totalSubmitTime += elapsedMs;
                    _submitCount++;
                    _maxSubmitTime = Math.Max(_maxSubmitTime, elapsedMs);
                }
                
                if (elapsedMs > 50) // 超过50ms记录警告
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, 
                        $"Submit took {elapsedMs}ms - this may cause GPU sync timeouts");
                }
                else if (elapsedMs > 10) // 超过10ms记录调试信息
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"Submit took {elapsedMs}ms");
                }
            }
        }

        private Span<T> GetSpanAndSkip<T>(ref Span<byte> arguments, int count) where T : unmanaged
        {
            Span<T> output = MemoryMarshal.Cast<byte, T>(arguments)[..count];

            arguments = arguments[(Unsafe.SizeOf<T>() * count)..];

            return output;
        }

        private NvInternalResult GetSyncpoint(ref GetParameterArguments arguments)
        {
            if (arguments.Parameter >= MaxModuleSyncpoint)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, 
                    $"GetSyncpoint: Invalid parameter {arguments.Parameter}, max={MaxModuleSyncpoint}");
                return NvInternalResult.InvalidInput;
            }

            if (ChannelResourcePolicy == ResourcePolicy.Device)
            {
                arguments.Value = GetSyncpointDevice(_device.System.HostSyncpoint, arguments.Parameter, false);
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"GetSyncpoint[Device]: param={arguments.Parameter}, value={arguments.Value}");
            }
            else
            {
                arguments.Value = GetSyncpointChannel(arguments.Parameter, false);
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"GetSyncpoint[Channel]: param={arguments.Parameter}, value={arguments.Value}");
            }

            if (arguments.Value == 0)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"GetSyncpoint: TryAgain for parameter {arguments.Parameter}");
                return NvInternalResult.TryAgain;
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult GetWaitBase(ref GetParameterArguments arguments)
        {
            arguments.Value = 0;

            Logger.Debug?.Print(LogClass.ServiceNv, $"GetWaitBase: returning 0 (stub)");

            return NvInternalResult.Success;
        }

        private NvInternalResult SetSubmitTimeout(ref uint submitTimeout)
        {
            _submitTimeout = submitTimeout;

            Logger.Debug?.Print(LogClass.ServiceNv, $"SetSubmitTimeout: {submitTimeout}ms");

            return NvInternalResult.Success;
        }

        private NvInternalResult MapCommandBuffer(Span<byte> arguments)
        {
            try
            {
                int headerSize = Unsafe.SizeOf<MapCommandBufferArguments>();
                MapCommandBufferArguments commandBufferHeader = MemoryMarshal.Cast<byte, MapCommandBufferArguments>(arguments)[0];
                Span<CommandBufferHandle> commandBufferEntries = MemoryMarshal.Cast<byte, CommandBufferHandle>(arguments[headerSize..])[..commandBufferHeader.NumEntries];

                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"MapCommandBuffer: numEntries={commandBufferHeader.NumEntries}");

                foreach (ref CommandBufferHandle commandBufferEntry in commandBufferEntries)
                {
                    NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(Owner, commandBufferEntry.MapHandle);

                    if (map == null)
                    {
                        Logger.Warning?.Print(LogClass.ServiceNv, 
                            $"MapCommandBuffer: Invalid handle 0x{commandBufferEntry.MapHandle:x8}!");
                        return NvInternalResult.InvalidInput;
                    }

                    lock (map)
                    {
                        if (map.DmaMapAddress == 0)
                        {
                            ulong va = _host1xContext.MemoryAllocator.GetFreeAddress(map.Size, out ulong freeAddressStartPosition, 1, MemoryManager.PageSize);

                            if (va != NvMemoryAllocator.PteUnmapped && va <= uint.MaxValue && (va + map.Size) <= uint.MaxValue)
                            {
                                _host1xContext.MemoryAllocator.AllocateRange(va, map.Size, freeAddressStartPosition);
                                _host1xContext.Smmu.Map(map.Address, va, map.Size);
                                map.DmaMapAddress = va;
                                
                                Logger.Debug?.Print(LogClass.ServiceNv, 
                                    $"MapCommandBuffer: Mapped handle 0x{commandBufferEntry.MapHandle:x8} to DMA address 0x{va:X}");
                            }
                            else
                            {
                                map.DmaMapAddress = NvMemoryAllocator.PteUnmapped;
                                Logger.Warning?.Print(LogClass.ServiceNv, 
                                    $"MapCommandBuffer: Failed to map handle 0x{commandBufferEntry.MapHandle:x8}, size={map.Size}");
                            }
                        }

                        commandBufferEntry.MapAddress = (int)map.DmaMapAddress;
                    }
                }

                return NvInternalResult.Success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceNv, $"MapCommandBuffer error: {ex.Message}");
                return NvInternalResult.InvalidInput;
            }
        }

        private NvInternalResult UnmapCommandBuffer(Span<byte> arguments)
        {
            try
            {
                int headerSize = Unsafe.SizeOf<MapCommandBufferArguments>();
                MapCommandBufferArguments commandBufferHeader = MemoryMarshal.Cast<byte, MapCommandBufferArguments>(arguments)[0];
                Span<CommandBufferHandle> commandBufferEntries = MemoryMarshal.Cast<byte, CommandBufferHandle>(arguments[headerSize..])[..commandBufferHeader.NumEntries];

                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"UnmapCommandBuffer: numEntries={commandBufferHeader.NumEntries}");

                foreach (ref CommandBufferHandle commandBufferEntry in commandBufferEntries)
                {
                    NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(Owner, commandBufferEntry.MapHandle);

                    if (map == null)
                    {
                        Logger.Warning?.Print(LogClass.ServiceNv, 
                            $"UnmapCommandBuffer: Invalid handle 0x{commandBufferEntry.MapHandle:x8}!");
                        return NvInternalResult.InvalidInput;
                    }

                    lock (map)
                    {
                        if (map.DmaMapAddress != 0)
                        {
                            Logger.Debug?.Print(LogClass.ServiceNv, 
                                $"UnmapCommandBuffer: Would unmap handle 0x{commandBufferEntry.MapHandle:x8} from DMA address 0x{map.DmaMapAddress:X}");
                            // FIXME: 当前无法正确取消映射，原因如注释所述
                        }
                    }
                }

                return NvInternalResult.Success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceNv, $"UnmapCommandBuffer error: {ex.Message}");
                return NvInternalResult.InvalidInput;
            }
        }

        private NvInternalResult SetNvMapFd(ref int nvMapFd)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"SetNvMapFd: {nvMapFd}");

            return NvInternalResult.Success;
        }

        private NvInternalResult SetTimeout(ref uint timeout)
        {
            _timeout = timeout;

            Logger.Debug?.Print(LogClass.ServiceNv, $"SetTimeout: {timeout}ms");

            return NvInternalResult.Success;
        }

        private NvInternalResult SubmitGpfifo(Span<byte> arguments)
        {
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                int headerSize = Unsafe.SizeOf<SubmitGpfifoArguments>();
                SubmitGpfifoArguments gpfifoSubmissionHeader = MemoryMarshal.Cast<byte, SubmitGpfifoArguments>(arguments)[0];
                Span<ulong> gpfifoEntries = MemoryMarshal.Cast<byte, ulong>(arguments[headerSize..])[..gpfifoSubmissionHeader.NumEntries];

                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"SubmitGpfifo: numEntries={gpfifoSubmissionHeader.NumEntries}, " +
                    $"flags={gpfifoSubmissionHeader.Flags}, fence=({gpfifoSubmissionHeader.Fence.Id}:{gpfifoSubmissionHeader.Fence.Value})");

                return SubmitGpfifo(ref gpfifoSubmissionHeader, gpfifoEntries);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceNv, $"SubmitGpfifo error: {ex.Message}");
                return NvInternalResult.InvalidInput;
            }
            finally
            {
                stopwatch.Stop();
                long elapsedMs = stopwatch.ElapsedMilliseconds;
                
                if (elapsedMs > 100)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, 
                        $"SubmitGpfifo took {elapsedMs}ms - HIGH LATENCY, may cause syncpoint timeout!");
                }
                else if (elapsedMs > 50)
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"SubmitGpfifo took {elapsedMs}ms");
                }
            }
        }

        private NvInternalResult AllocObjCtx(ref AllocObjCtxArguments arguments)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"AllocObjCtx: stub implementation");

            return NvInternalResult.Success;
        }

        private NvInternalResult ZcullBind(ref ZcullBindArguments arguments)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"ZcullBind: stub implementation");

            return NvInternalResult.Success;
        }

        private NvInternalResult SetErrorNotifier(ref SetErrorNotifierArguments arguments)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"SetErrorNotifier: stub implementation");

            return NvInternalResult.Success;
        }

        private NvInternalResult SetPriority(ref NvChannelPriority priority)
        {
            switch (priority)
            {
                case NvChannelPriority.Low:
                    _timeslice = 1300; // 微秒
                    break;
                case NvChannelPriority.Medium:
                    _timeslice = 2600; // 微秒
                    break;
                case NvChannelPriority.High:
                    _timeslice = 5200; // 微秒
                    break;
                default:
                    Logger.Warning?.Print(LogClass.ServiceNv, $"SetPriority: Invalid priority {priority}");
                    return NvInternalResult.InvalidInput;
            }

            Logger.Debug?.Print(LogClass.ServiceNv, $"SetPriority: {priority} -> timeslice={_timeslice}μs");

            // TODO: 实现GPU调度器后启用通道抢占

            return NvInternalResult.Success;
        }

        private NvInternalResult AllocGpfifoEx(ref AllocGpfifoExArguments arguments)
        {
            _channelSyncpoint.UpdateValue(_device.System.HostSyncpoint);

            arguments.Fence = _channelSyncpoint;

            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"AllocGpfifoEx: returning fence=({_channelSyncpoint.Id}:{_channelSyncpoint.Value})");

            return NvInternalResult.Success;
        }

        private NvInternalResult AllocGpfifoEx2(ref AllocGpfifoExArguments arguments)
        {
            _channelSyncpoint.UpdateValue(_device.System.HostSyncpoint);

            arguments.Fence = _channelSyncpoint;

            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"AllocGpfifoEx2: returning fence=({_channelSyncpoint.Id}:{_channelSyncpoint.Value})");

            return NvInternalResult.Success;
        }

        private NvInternalResult SetTimeslice(ref uint timeslice)
        {
            if (timeslice < 1000 || timeslice > 50000)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, 
                    $"SetTimeslice: Invalid timeslice {timeslice}μs (must be 1000-50000)");
                return NvInternalResult.InvalidInput;
            }

            _timeslice = timeslice; // 微秒

            Logger.Debug?.Print(LogClass.ServiceNv, $"SetTimeslice: {timeslice}μs");

            // TODO: 实现GPU调度器后启用通道抢占

            return NvInternalResult.Success;
        }

        private NvInternalResult SetUserData(ref ulong userData)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"SetUserData: 0x{userData:X}");

            return NvInternalResult.Success;
        }

        protected NvInternalResult SubmitGpfifo(ref SubmitGpfifoArguments header, Span<ulong> entries)
        {
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"SubmitGpfifo internal: entries={entries.Length}, flags={header.Flags}, " +
                    $"fence=({header.Fence.Id}:{header.Fence.Value})");

                if (header.Flags.HasFlag(SubmitGpfifoFlags.FenceWait) && header.Flags.HasFlag(SubmitGpfifoFlags.IncrementWithValue))
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, 
                        "SubmitGpfifo: Both FenceWait and IncrementWithValue flags are set - invalid combination");
                    return NvInternalResult.InvalidInput;
                }

                // 检查是否需要等待同步点
                if (header.Flags.HasFlag(SubmitGpfifoFlags.FenceWait))
                {
                    bool isExpired = _device.System.HostSyncpoint.IsSyncpointExpired(header.Fence.Id, header.Fence.Value);
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"SubmitGpfifo: FenceWait check - fence=({header.Fence.Id}:{header.Fence.Value}), expired={isExpired}");
                    
                    if (!isExpired)
                    {
                        Logger.Debug?.Print(LogClass.ServiceNv, 
                            $"SubmitGpfifo: Creating wait command buffer for fence=({header.Fence.Id}:{header.Fence.Value})");
                        Channel.PushHostCommandBuffer(CreateWaitCommandBuffer(header.Fence));
                    }
                }

                // 记录原始fence信息
                uint originalFenceId = header.Fence.Id;
                uint originalFenceValue = header.Fence.Value;

                // 设置通道同步点
                header.Fence.Id = _channelSyncpoint.Id;

                // 处理同步点递增
                if (header.Flags.HasFlag(SubmitGpfifoFlags.FenceIncrement) || header.Flags.HasFlag(SubmitGpfifoFlags.IncrementWithValue))
                {
                    uint incrementCount = header.Flags.HasFlag(SubmitGpfifoFlags.FenceIncrement) ? 2u : 0u;

                    if (header.Flags.HasFlag(SubmitGpfifoFlags.IncrementWithValue))
                    {
                        incrementCount += header.Fence.Value;
                    }

                    uint oldMax = _device.System.HostSyncpoint.ReadSyncpointMaxValue(header.Fence.Id);
                    header.Fence.Value = _device.System.HostSyncpoint.IncrementSyncpointMaxExt(header.Fence.Id, (int)incrementCount);
                    uint newMax = header.Fence.Value;
                    
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"SubmitGpfifo: Incremented syncpoint {header.Fence.Id} by {incrementCount}, " +
                        $"oldMax={oldMax}, newMax={newMax}");
                }
                else
                {
                    header.Fence.Value = _device.System.HostSyncpoint.ReadSyncpointMaxValue(header.Fence.Id);
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"SubmitGpfifo: Using current syncpoint max value: {header.Fence.Value}");
                }

                // 推送GPU命令
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"SubmitGpfifo: Pushing {entries.Length} entries to GPU channel");
                Channel.PushEntries(entries);

                // 如果需要递增，创建递增命令缓冲区
                if (header.Flags.HasFlag(SubmitGpfifoFlags.FenceIncrement))
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"SubmitGpfifo: Creating increment command buffer for fence=({header.Fence.Id}:{header.Fence.Value})");
                    Channel.PushHostCommandBuffer(CreateIncrementCommandBuffer(ref header.Fence, header.Flags));
                }

                header.Flags = SubmitGpfifoFlags.None;

                // 通知GPU有新条目
                _device.Gpu.GPFifo.SignalNewEntries();
                
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"SubmitGpfifo completed: original fence=({originalFenceId}:{originalFenceValue}), " +
                    $"final fence=({header.Fence.Id}:{header.Fence.Value})");

                return NvInternalResult.Success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ServiceNv, $"SubmitGpfifo internal error: {ex}");
                return NvInternalResult.InvalidInput;
            }
            finally
            {
                stopwatch.Stop();
                long elapsedMs = stopwatch.ElapsedMilliseconds;
                
                if (elapsedMs > 100)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, 
                        $"SubmitGpfifo internal took {elapsedMs}ms - HIGH LATENCY, entries={entries.Length}");
                }
            }
        }

        public uint GetSyncpointChannel(uint index, bool isClientManaged)
        {
            if (ChannelSyncpoints[index] != 0)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"GetSyncpointChannel: index={index}, returning existing syncpoint {ChannelSyncpoints[index]}");
                return ChannelSyncpoints[index];
            }

            ChannelSyncpoints[index] = _device.System.HostSyncpoint.AllocateSyncpoint(isClientManaged);
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"GetSyncpointChannel: index={index}, allocated new syncpoint {ChannelSyncpoints[index]}");

            return ChannelSyncpoints[index];
        }

        public uint GetSyncpointDevice(NvHostSyncpt syncpointManager, uint index, bool isClientManaged)
        {
            if (DeviceSyncpoints[index] != 0)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"GetSyncpointDevice: index={index}, returning existing syncpoint {DeviceSyncpoints[index]}");
                return DeviceSyncpoints[index];
            }

            DeviceSyncpoints[index] = syncpointManager.AllocateSyncpoint(isClientManaged);
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"GetSyncpointDevice: index={index}, allocated new syncpoint {DeviceSyncpoints[index]}");

            return DeviceSyncpoints[index];
        }

        private static int[] CreateWaitCommandBuffer(NvFence fence)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"CreateWaitCommandBuffer: fence=({fence.Id}:{fence.Value})");
                
            int[] commandBuffer = new int[4];

            // SyncpointValue = fence.Value;
            commandBuffer[0] = 0x2001001C;
            commandBuffer[1] = (int)fence.Value;

            // SyncpointAction(fence.id, increment: false, switch_en: true);
            commandBuffer[2] = 0x2001001D;
            commandBuffer[3] = (((int)fence.Id << 8) | (0 << 0) | (1 << 4));

            return commandBuffer;
        }

        private int[] CreateIncrementCommandBuffer(ref NvFence fence, SubmitGpfifoFlags flags)
        {
            bool hasWfi = !flags.HasFlag(SubmitGpfifoFlags.SuppressWfi);

            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"CreateIncrementCommandBuffer: fence=({fence.Id}:{fence.Value}), hasWfi={hasWfi}, flags={flags}");

            int[] commandBuffer;

            int offset = 0;

            if (hasWfi)
            {
                commandBuffer = new int[8];

                // WaitForInterrupt(handle)
                commandBuffer[offset++] = 0x2001001E;
                commandBuffer[offset++] = 0x0;
            }
            else
            {
                commandBuffer = new int[6];
            }

            // SyncpointValue = 0x0;
            commandBuffer[offset++] = 0x2001001C;
            commandBuffer[offset++] = 0x0;

            // Increment the syncpoint 2 times. (mitigate a hardware bug)

            // SyncpointAction(fence.id, increment: true, switch_en: false);
            commandBuffer[offset++] = 0x2001001D;
            commandBuffer[offset++] = (((int)fence.Id << 8) | (1 << 0) | (0 << 4));

            // SyncpointAction(fence.id, increment: true, switch_en: false);
            commandBuffer[offset++] = 0x2001001D;
            commandBuffer[offset++] = (((int)fence.Id << 8) | (1 << 0) | (0 << 4));

            return commandBuffer;
        }

        public override void Close()
        {
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"NvHostChannelDeviceFile.Close: owner=0x{Owner:X}, contextId={_contextId}");

            _host1xContext.Host1x.DestroyContext(_contextId);
            Channel.Dispose();

            // 释放通道同步点
            for (int i = 0; i < MaxModuleSyncpoint; i++)
            {
                if (ChannelSyncpoints[i] != 0)
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"Releasing channel syncpoint {ChannelSyncpoints[i]}");
                    _device.System.HostSyncpoint.ReleaseSyncpoint(ChannelSyncpoints[i]);
                    ChannelSyncpoints[i] = 0;
                }
            }

            // 释放主通道同步点
            if (_channelSyncpoint.Id != 0)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"Releasing main channel syncpoint {_channelSyncpoint.Id}");
                _device.System.HostSyncpoint.ReleaseSyncpoint(_channelSyncpoint.Id);
                _channelSyncpoint.Id = 0;
            }
            
            // 打印性能统计
            LogPerformanceStats();
        }

        private static Host1xContext GetHost1XContext(GpuContext gpu, ulong pid)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"GetHost1XContext: pid=0x{pid:X}");
                
            return _host1xContextRegistry.GetOrAdd(pid, (ulong key) => 
            {
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"Creating new Host1xContext for pid=0x{key:X}");
                return new Host1xContext(gpu, key);
            });
        }

        public static void Destroy()
        {
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"NvHostChannelDeviceFile.Destroy: cleaning up {_host1xContextRegistry.Count} contexts");

            foreach (Host1xContext host1xContext in _host1xContextRegistry.Values)
            {
                host1xContext.Dispose();
            }

            _host1xContextRegistry.Clear();
        }

        // 性能监控相关方法
        private void StartPerformanceMonitoring()
        {
            // 可以在这里启动一个后台线程定期报告性能统计
            // 由于这可能增加复杂性，暂时注释掉
            /*
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(10000); // 每10秒报告一次
                    LogPerformanceStats();
                }
            });
            */
        }

        private void LogPerformanceStats()
        {
            lock (_perfStopwatch)
            {
                if (_submitCount > 0)
                {
                    double avgSubmitTime = (double)_totalSubmitTime / _submitCount;
                    
                    Logger.Info?.Print(LogClass.ServiceNv, 
                        $"Performance Stats - Submits: {_submitCount}, " +
                        $"Avg: {avgSubmitTime:F2}ms, Max: {_maxSubmitTime}ms, " +
                        $"Total: {_totalSubmitTime}ms");
                }
            }
        }
    }
}
