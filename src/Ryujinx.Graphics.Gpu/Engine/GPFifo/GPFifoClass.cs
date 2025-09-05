using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.MME;
using Ryujinx.Graphics.Gpu.Synchronization;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Represents a GPU General Purpose FIFO class.
    /// </summary>
    class GPFifoClass : IDeviceState
    {
        private readonly GpuContext _context;
        private readonly GPFifoProcessor _parent;
        private readonly DeviceState<GPFifoClassState> _state;

        private bool _createSyncPending;

        private const int MacrosCount = 0x80;

        // Note: The size of the macro memory is unknown, we just make
        // a guess here and use 256kb as the size. Increase if needed.
        private const int MacroCodeSize = 256 * 256;

        private readonly Macro[] _macros;
        private readonly int[] _macroCode;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO class.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="parent">Parent GPU General Purpose FIFO processor</param>
        public GPFifoClass(GpuContext context, GPFifoProcessor parent)
        {
            _context = context;
            _parent = parent;
            _state = new DeviceState<GPFifoClassState>(new Dictionary<string, RwCallback>
            {
                { nameof(GPFifoClassState.Semaphored), new RwCallback(Semaphored, null) },
                { nameof(GPFifoClassState.Syncpointb), new RwCallback(Syncpointb, null) },
                { nameof(GPFifoClassState.WaitForIdle), new RwCallback(WaitForIdle, null) },
                { nameof(GPFifoClassState.SetReference), new RwCallback(SetReference, null) },
                { nameof(GPFifoClassState.LoadMmeInstructionRam), new RwCallback(LoadMmeInstructionRam, null) },
                { nameof(GPFifoClassState.LoadMmeStartAddressRam), new RwCallback(LoadMmeStartAddressRam, null) },
                { nameof(GPFifoClassState.SetMmeShadowRamControl), new RwCallback(SetMmeShadowRamControl, null) },
            });

            _macros = new Macro[MacrosCount];
            _macroCode = new int[MacroCodeSize];
        }

        /// <summary>
        /// Create any syncs from WaitForIdle command that are currently pending.
        /// </summary>
        public void CreatePendingSyncs()
        {
            if (_createSyncPending)
            {
                _createSyncPending = false;
                _context.CreateHostSyncIfNeeded(HostSyncFlags.None);
            }
        }

        /// <summary>
        /// Reads data from the class registers.
        /// </summary>
        /// <param name="offset">Register byte offset</param>
        /// <returns>Data at the specified offset</returns>
        public int Read(int offset) => _state.Read(offset);

        /// <summary>
        /// Writes data to the class registers.
        /// </summary>
        /// <param name="offset">Register byte offset</param>
        /// <param name="data">Data to be written</param>
        public void Write(int offset, int data) => _state.Write(offset, data);

        /// <summary>
        /// Writes a GPU counter to guest memory.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void Semaphored(int argument)
        {
            ulong address = ((ulong)_state.State.SemaphorebOffsetLower << 2) |
                            ((ulong)_state.State.SemaphoreaOffsetUpper << 32);

            int value = _state.State.SemaphorecPayload;

            SemaphoredOperation operation = _state.State.SemaphoredOperation;

            if (_state.State.SemaphoredReleaseSize == SemaphoredReleaseSize.SixteenBytes)
            {
                _parent.MemoryManager.Write(address + 4, 0);
                _parent.MemoryManager.Write(address + 8, _context.GetTimestamp());
            }

            // TODO: Acquire operations (Wait), interrupts for invalid combinations.
            if (operation == SemaphoredOperation.Release)
            {
                _parent.MemoryManager.Write(address, value);
            }
            else if (operation == SemaphoredOperation.Reduction)
            {
                bool signed = _state.State.SemaphoredFormat == SemaphoredFormat.Signed;

                int mem = _parent.MemoryManager.Read<int>(address);

                switch (_state.State.SemaphoredReduction)
                {
                    case SemaphoredReduction.Min:
                        value = signed ? Math.Min(mem, value) : (int)Math.Min((uint)mem, (uint)value);
                        break;
                    case SemaphoredReduction.Max:
                        value = signed ? Math.Max(mem, value) : (int)Math.Max((uint)mem, (uint)value);
                        break;
                    case SemaphoredReduction.Xor:
                        value ^= mem;
                        break;
                    case SemaphoredReduction.And:
                        value &= mem;
                        break;
                    case SemaphoredReduction.Or:
                        value |= mem;
                        break;
                    case SemaphoredReduction.Add:
                        value += mem;
                        break;
                    case SemaphoredReduction.Inc:
                        value = (uint)mem < (uint)value ? mem + 1 : 0;
                        break;
                    case SemaphoredReduction.Dec:
                        value = (uint)mem > 0 && (uint)mem <= (uint)value ? mem - 1 : value;
                        break;
                }

                _parent.MemoryManager.Write(address, value);
            }
        }

        /// <summary>
        /// Apply a fence operation on a syncpoint.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void Syncpointb(int argument)
        {
            SyncpointbOperation operation = _state.State.SyncpointbOperation;

            uint syncpointId = (uint)_state.State.SyncpointbSyncptIndex;
            
            // 添加同步点 ID 验证
            if (syncpointId >= (uint)SynchronizationManager.MaxHardwareSyncpoints)
            {
                // 记录更详细的警告信息
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Invalid syncpoint ID {syncpointId} (max is {SynchronizationManager.MaxHardwareSyncpoints - 1}) in {operation} operation, ignoring command");
                
                // 记录调用堆栈（仅在调试模式下）
                #if DEBUG
                Logger.Debug?.Print(LogClass.Gpu, $"Call stack: {Environment.StackTrace}");
                #endif
                
                _context.AdvanceSequence();
                return;
            }
    
            if (operation == SyncpointbOperation.Wait)
            {
                uint threshold = (uint)_state.State.SyncpointaPayload;

                _context.Synchronization.WaitOnSyncpoint(syncpointId, threshold, Timeout.InfiniteTimeSpan);
            }
            else if (operation == SyncpointbOperation.Incr)
            {
                // "Unbind" render targets since a syncpoint increment might indicate future CPU access for the textures.
                _parent.TextureManager.RefreshModifiedTextures();

                _context.CreateHostSyncIfNeeded(HostSyncFlags.StrictSyncpoint);
                _context.Synchronization.IncrementSyncpoint(syncpointId);
            }

            _context.AdvanceSequence();
        }

        /// <summary>
        /// Waits for the GPU to be idle.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void WaitForIdle(int argument)
        {
            _parent.PerformDeferredDraws();
            _context.Renderer.Pipeline.Barrier();

            _createSyncPending = true;
        }

        /// <summary>
        /// Used as an indirect data barrier on NVN. When used, access to previously written data must be coherent.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void SetReference(int argument)
        {
            _context.Renderer.Pipeline.CommandBufferBarrier();

            _context.CreateHostSyncIfNeeded(HostSyncFlags.Strict);
        }

        /// <summary>
        /// Sends macro code/data to the MME.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void LoadMmeInstructionRam(int argument)
        {
            // 使用 uint 类型，因为 GPFifoClassState 中的字段可能是 uint 类型
            uint pointer = _state.State.LoadMmeInstructionRamPointer;
            
            // 添加边界检查
            if (pointer >= (uint)_macroCode.Length)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"LoadMmeInstructionRamPointer out of range: {pointer} (max is {_macroCode.Length - 1}), resetting to 0");
                pointer = 0;
                _state.State.LoadMmeInstructionRamPointer = pointer;
            }
            
            _macroCode[(int)pointer] = argument; // 显式转换为 int
            _state.State.LoadMmeInstructionRamPointer = pointer + 1;
        }

        /// <summary>
        /// Binds a macro index to a position for the MME
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void LoadMmeStartAddressRam(int argument)
        {
            // 使用 uint 类型，因为 GPFifoClassState 中的字段可能是 uint 类型
            uint pointer = _state.State.LoadMmeStartAddressRamPointer;
            
            // 添加边界检查
            if (pointer >= (uint)_macros.Length)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"LoadMmeStartAddressRamPointer out of range: {pointer} (max is {_macros.Length - 1}), resetting to 0");
                pointer = 0;
                _state.State.LoadMmeStartAddressRamPointer = pointer;
            }
            
            _macros[(int)pointer] = new Macro(argument); // 显式转换为 int
            _state.State.LoadMmeStartAddressRamPointer = pointer + 1;
        }

        /// <summary>
        /// Changes the shadow RAM control.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void SetMmeShadowRamControl(int argument)
        {
            _parent.SetShadowRamControl(argument);
        }

        /// <summary>
        /// Pushes an argument to a macro.
        /// </summary>
        /// <param name="index">Index of the macro</param>
        /// <param name="gpuVa">GPU virtual address where the command word is located</param>
        /// <param name="argument">Argument to be pushed to the macro</param>
        public void MmePushArgument(int index, ulong gpuVa, int argument)
        {
            if (index < 0 || index >= _macros.Length)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Macro index out of range: {index} (max is {_macros.Length - 1}), ignoring push");
                return;
            }
            
            _macros[index].PushArgument(gpuVa, argument);
        }

        /// <summary>
        /// Prepares a macro for execution.
        /// </summary>
        /// <param name="index">Index of the macro</param>
        /// <param name="argument">Initial argument passed to the macro</param>
        public void MmeStart(int index, int argument)
        {
            if (index < 0 || index >= _macros.Length)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Macro index out of range: {index} (max is {_macros.Length - 1}), ignoring start");
                return;
            }
            
            _macros[index].StartExecution(_context, _parent, _macroCode, argument);
        }

        /// <summary>
        /// Executes a macro.
        /// </summary>
        /// <param name="index">Index of the macro</param>
        /// <param name="state">Current GPU state</param>
        public void CallMme(int index, IDeviceState state)
        {
            if (index < 0 || index >= _macros.Length)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Macro index out of range: {index} (max is {_macros.Length - 1}), ignoring call");
                return;
            }
            
            _macros[index].Execute(_macroCode, state);
        }
    }
}
