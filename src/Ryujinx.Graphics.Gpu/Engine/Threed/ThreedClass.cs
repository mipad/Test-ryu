using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Engine.InlineToMemory;
using Ryujinx.Graphics.Gpu.Engine.Threed.Blender;
using Ryujinx.Graphics.Gpu.Engine.Types;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.Memory.Range;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    /// <summary>
    /// 表示3D引擎类，负责处理3D图形渲染命令
    /// Represents a 3D engine class.
    /// </summary>
    class ThreedClass : IDeviceState, IDisposable
    {
        private readonly GpuContext _context;
        private readonly GPFifoClass _fifoClass;
        private readonly DeviceStateWithShadow<ThreedClassState> _state;
        private readonly object _inlineDataLock = new(); // [优化] 线程安全锁，用于保护内联数据加载

        private readonly InlineToMemoryClass _i2mClass;
        private readonly AdvancedBlendManager _blendManager;
        private readonly DrawManager _drawManager;
        private readonly SemaphoreUpdater _semaphoreUpdater;
        private readonly ConstantBufferUpdater _cbUpdater;
        private readonly StateUpdater _stateUpdater;

        private SetMmeShadowRamControlMode ShadowMode => _state.State.SetMmeShadowRamControlMode;

        // [优化] 延迟状态更新
        private readonly HashSet<int> _pendingDirtyOffsets = new();
        private const int DirtyOffsetThreshold = 32; // 脏状态阈值，超过此值触发批量更新

        /// <summary>
        /// 创建3D引擎类的新实例
        /// Creates a new instance of the 3D engine class.
        /// </summary>
        /// <param name="context">GPU上下文</param>
        /// <param name="channel">GPU通道</param>
        /// <param name="fifoClass">GPFifo类</param>
        public ThreedClass(GpuContext context, GpuChannel channel, GPFifoClass fifoClass)
        {
            _context = context;
            _fifoClass = fifoClass;
            _state = new DeviceStateWithShadow<ThreedClassState>(new Dictionary<string, RwCallback>
            {
                { nameof(ThreedClassState.LaunchDma), new RwCallback(LaunchDma, null) },
                { nameof(ThreedClassState.LoadInlineData), new RwCallback(LoadInlineData, null) },
                { nameof(ThreedClassState.SyncpointAction), new RwCallback(IncrementSyncpoint, null) },
                { nameof(ThreedClassState.InvalidateSamplerCacheNoWfi), new RwCallback(InvalidateSamplerCacheNoWfi, null) },
                { nameof(ThreedClassState.InvalidateTextureHeaderCacheNoWfi), new RwCallback(InvalidateTextureHeaderCacheNoWfi, null) },
                { nameof(ThreedClassState.TextureBarrier), new RwCallback(TextureBarrier, null) },
                { nameof(ThreedClassState.LoadBlendUcodeStart), new RwCallback(LoadBlendUcodeStart, null) },
                { nameof(ThreedClassState.LoadBlendUcodeInstruction), new RwCallback(LoadBlendUcodeInstruction, null) },
                { nameof(ThreedClassState.TextureBarrierTiled), new RwCallback(TextureBarrierTiled, null) },
                { nameof(ThreedClassState.DrawTextureSrcY), new RwCallback(DrawTexture, null) },
                { nameof(ThreedClassState.DrawVertexArrayBeginEndInstanceFirst), new RwCallback(DrawVertexArrayBeginEndInstanceFirst, null) },
                { nameof(ThreedClassState.DrawVertexArrayBeginEndInstanceSubsequent), new RwCallback(DrawVertexArrayBeginEndInstanceSubsequent, null) },
                { nameof(ThreedClassState.VbElementU8), new RwCallback(VbElementU8, null) },
                { nameof(ThreedClassState.VbElementU16), new RwCallback(VbElementU16, null) },
                { nameof(ThreedClassState.VbElementU32), new RwCallback(VbElementU32, null) },
                { nameof(ThreedClassState.ResetCounter), new RwCallback(ResetCounter, null) },
                { nameof(ThreedClassState.RenderEnableCondition), new RwCallback(null, Zero) },
                { nameof(ThreedClassState.DrawEnd), new RwCallback(DrawEnd, null) },
                { nameof(ThreedClassState.DrawBegin), new RwCallback(DrawBegin, null) },
                { nameof(ThreedClassState.DrawIndexBuffer32BeginEndInstanceFirst), new RwCallback(DrawIndexBuffer32BeginEndInstanceFirst, null) },
                { nameof(ThreedClassState.DrawIndexBuffer16BeginEndInstanceFirst), new RwCallback(DrawIndexBuffer16BeginEndInstanceFirst, null) },
                { nameof(ThreedClassState.DrawIndexBuffer8BeginEndInstanceFirst), new RwCallback(DrawIndexBuffer8BeginEndInstanceFirst, null) },
                { nameof(ThreedClassState.DrawIndexBuffer32BeginEndInstanceSubsequent), new RwCallback(DrawIndexBuffer32BeginEndInstanceSubsequent, null) },
                { nameof(ThreedClassState.DrawIndexBuffer16BeginEndInstanceSubsequent), new RwCallback(DrawIndexBuffer16BeginEndInstanceSubsequent, null) },
                { nameof(ThreedClassState.DrawIndexBuffer8BeginEndInstanceSubsequent), new RwCallback(DrawIndexBuffer8BeginEndInstanceSubsequent, null) },
                { nameof(ThreedClassState.IndexBufferCount), new RwCallback(SetIndexBufferCount, null) },
                { nameof(ThreedClassState.Clear), new RwCallback(Clear, null) },
                { nameof(ThreedClassState.SemaphoreControl), new RwCallback(Report, null) },
                { nameof(ThreedClassState.SetFalcon04), new RwCallback(SetFalcon04, null) },
                { nameof(ThreedClassState.UniformBufferUpdateData), new RwCallback(ConstantBufferUpdate, null) },
                { nameof(ThreedClassState.UniformBufferBindVertex), new RwCallback(ConstantBufferBindVertex, null) },
                { nameof(ThreedClassState.UniformBufferBindTessControl), new RwCallback(ConstantBufferBindTessControl, null) },
                { nameof(ThreedClassState.UniformBufferBindTessEvaluation), new RwCallback(ConstantBufferBindTessEvaluation, null) },
                { nameof(ThreedClassState.UniformBufferBindGeometry), new RwCallback(ConstantBufferBindGeometry, null) },
                { nameof(ThreedClassState.UniformBufferBindFragment), new RwCallback(ConstantBufferBindFragment, null) },
            });

            _i2mClass = new InlineToMemoryClass(context, channel, initializeState: false);

            var spec = new SpecializationStateUpdater(context);
            var drawState = new DrawState();

            _drawManager = new DrawManager(context, channel, _state, drawState, spec);
            _blendManager = new AdvancedBlendManager(_state);
            _semaphoreUpdater = new SemaphoreUpdater(context, channel, _state);
            _cbUpdater = new ConstantBufferUpdater(channel, _state);
            _stateUpdater = new StateUpdater(context, channel, _state, drawState, _blendManager, spec);

            // 默认设置为"always"，即使没有任何寄存器写入
            _state.State.RenderEnableCondition = Condition.Always;
        }

        /// <summary>
        /// 从类寄存器读取数据
        /// Reads data from the class registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(int offset) => _state.Read(offset);

        /// <summary>
        /// 使用延迟状态更新将数据写入类寄存器
        /// Writes data to the class registers with deferred state updates.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int offset, int data)
        {
            _state.WriteWithRedundancyCheck(offset, data, out bool valueChanged);

            if (valueChanged)
            {
                _pendingDirtyOffsets.Add(offset);
                
                // [优化] 批量处理脏状态
                if (_pendingDirtyOffsets.Count > DirtyOffsetThreshold)
                {
                    UpdatePendingState();
                }
            }
        }

        /// <summary>
        /// 更新待处理状态
        /// Update pending state changes
        /// </summary>
        private void UpdatePendingState()
        {
            foreach (int offset in _pendingDirtyOffsets)
            {
                _stateUpdater.SetDirty(offset);
            }
            _pendingDirtyOffsets.Clear();
            _stateUpdater.Update();
        }

        /// <summary>
        /// 设置所有子通道的阴影RAM控制值
        /// Sets the shadow ram control value of all sub-channels.
        /// </summary>
        public void SetShadowRamControl(int control)
        {
            _state.State.SetMmeShadowRamControl = (uint)control;
        }

        /// <summary>
        /// 更新自上次调用此方法以来修改的所有寄存器的当前主机状态
        /// Updates current host state for all registers modified since the last call to this method.
        /// </summary>
        public void UpdateState()
        {
            _fifoClass.CreatePendingSyncs();
            _cbUpdater.FlushUboDirty();
            UpdatePendingState();
        }

        /// <summary>
        /// 更新自上次调用此方法以来修改的所有寄存器的当前主机状态
        /// Updates current host state for all registers modified since the last call to this method.
        /// </summary>
        /// <param name="mask">掩码，每个设置的位表示应检查相应的状态组索引</param>
        public void UpdateState(ulong mask)
        {
            _stateUpdater.Update(mask);
        }

        /// <summary>
        /// 基于当前渲染目标状态更新渲染目标（颜色和深度-模板缓冲区）
        /// Updates render targets (color and depth-stencil buffers) based on current render target state.
        /// </summary>
        public void UpdateRenderTargetState(RenderTargetUpdateFlags updateFlags, int singleUse = -1)
        {
            _stateUpdater.UpdateRenderTargetState(updateFlags, singleUse);
        }

        /// <summary>
        /// 基于当前渲染目标状态更新剪刀
        /// Updates scissor based on current render target state.
        /// </summary>
        public void UpdateScissorState()
        {
            _stateUpdater.UpdateScissorState();
        }

        /// <summary>
        /// 将整个状态标记为脏，强制在下一次绘制之前进行完整的主机状态更新
        /// Marks the entire state as dirty, forcing a full host state update before the next draw.
        /// </summary>
        public void ForceStateDirty()
        {
            _drawManager.ForceStateDirty();
            _stateUpdater.SetAllDirty();
        }

        /// <summary>
        /// 将指定的寄存器偏移标记为脏，强制在下次绘制时更新关联状态
        /// Marks the specified register offset as dirty, forcing the associated state to update on the next draw.
        /// </summary>
        public void ForceStateDirty(int offset)
        {
            _stateUpdater.SetDirty(offset);
        }

        /// <summary>
        /// 将组索引的指定寄存器范围标记为脏，强制在下次绘制时更新关联状态
        /// Marks the specified register range for a group index as dirty, forcing the associated state to update on the next draw.
        /// </summary>
        public void ForceStateDirtyByIndex(int groupIndex)
        {
            _stateUpdater.ForceDirty(groupIndex);
        }

        /// <summary>
        /// 强制在下一次绘制时重新绑定着色器
        /// Forces the shaders to be rebound on the next draw.
        /// </summary>
        public void ForceShaderUpdate()
        {
            _stateUpdater.ForceShaderUpdate();
        }

        /// <summary>
        /// 从WaitForIdle命令创建当前待处理的任何同步
        /// Create any syncs from WaitForIdle command that are currently pending.
        /// </summary>
        public void CreatePendingSyncs()
        {
            _fifoClass.CreatePendingSyncs();
        }

        /// <summary>
        /// 刷新任何排队的UBO更新
        /// Flushes any queued UBO updates.
        /// </summary>
        public void FlushUboDirty()
        {
            _cbUpdater.FlushUboDirty();
        }

        /// <summary>
        /// 执行任何延迟的绘制
        /// Perform any deferred draws.
        /// </summary>
        public void PerformDeferredDraws()
        {
            _drawManager.PerformDeferredDraws(this);
        }

        /// <summary>
        /// 使用SIMD加速更新当前绑定的常量缓冲区
        /// Updates the currently bound constant buffer with SIMD optimization.
        /// </summary>
        public void ConstantBufferUpdate(ReadOnlySpan<int> data)
        {
            // [优化] 使用SIMD加速大数据更新
            if (Vector256.IsHardwareAccelerated && data.Length >= 8)
            {
                ref var srcVec = ref Unsafe.As<int, Vector256<int>>(ref MemoryMarshal.GetReference(data));
                _cbUpdater.UpdateVectorized(ref srcVec, data.Length);
            }
            else
            {
                _cbUpdater.Update(data);
            }
        }

        /// <summary>
        /// 测试两个32字节结构是否相等
        /// Test if two 32 byte structs are equal. 
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool UnsafeEquals32Byte<T>(ref T lhs, ref T rhs) where T : unmanaged
        {
            if (Vector256.IsHardwareAccelerated)
            {
                return Vector256.EqualsAll(
                    Unsafe.As<T, Vector256<uint>>(ref lhs),
                    Unsafe.As<T, Vector256<uint>>(ref rhs)
                );
            }
            else
            {
                ref var lhsVec = ref Unsafe.As<T, Vector128<uint>>(ref lhs);
                ref var rhsVec = ref Unsafe.As<T, Vector128<uint>>(ref rhs);

                return Vector128.EqualsAll(lhsVec, rhsVec) &&
                    Vector128.EqualsAll(Unsafe.Add(ref lhsVec, 1), Unsafe.Add(ref rhsVec, 1));
            }
        }

        /// <summary>
        /// 线程安全的内联数据加载
        /// Thread-safe inline data loading.
        /// </summary>
        public void LoadInlineData(ReadOnlySpan<int> data)
        {
            lock (_inlineDataLock)
            {
                // [优化] 使用内存池减少分配
                int[] rentedArray = ArrayPool<int>.Shared.Rent(data.Length);
                try
                {
                    data.CopyTo(rentedArray);
                    _i2mClass.LoadInlineData(rentedArray.AsSpan(0, data.Length));
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(rentedArray);
                }
            }
        }

        /// <summary>
        /// 更新混合启用。尊重当前阴影模式
        /// Updates blend enable. Respects current shadow mode.
        /// </summary>
        public void UpdateBlendEnable(ref Array8<Boolean32> enable)
        {
            var shadow = ShadowMode;
            ref var state = ref _state.State.BlendEnable;

            if (shadow.IsReplay())
            {
                enable = _state.ShadowState.BlendEnable;
            }

            if (!UnsafeEquals32Byte(ref enable, ref state))
            {
                state = enable;

                _stateUpdater.ForceDirty(StateUpdater.BlendStateIndex);
            }

            if (shadow.IsTrack())
            {
                _state.ShadowState.BlendEnable = enable;
            }
        }

        /// <summary>
        /// 更新颜色掩码。尊重当前阴影模式
        /// Updates color masks. Respects current shadow mode.
        /// </summary>
        public void UpdateColorMasks(ref Array8<RtColorMask> masks)
        {
            var shadow = ShadowMode;
            ref var state = ref _state.State.RtColorMask;

            if (shadow.IsReplay())
            {
                masks = _state.ShadowState.RtColorMask;
            }

            if (!UnsafeEquals32Byte(ref masks, ref state))
            {
                state = masks;

                _stateUpdater.ForceDirty(StateUpdater.RtColorMaskIndex);
            }

            if (shadow.IsTrack())
            {
                _state.ShadowState.RtColorMask = masks;
            }
        }

        /// <summary>
        /// 更新索引缓冲区的状态以进行索引绘制。尊重当前阴影模式
        /// Updates index buffer state for an indexed draw. Respects current shadow mode.
        /// </summary>
        public void UpdateIndexBuffer(uint addrHigh, uint addrLow, IndexType type)
        {
            var shadow = ShadowMode;
            ref var state = ref _state.State.IndexBufferState;

            if (shadow.IsReplay())
            {
                ref var shadowState = ref _state.ShadowState.IndexBufferState;
                addrHigh = shadowState.Address.High;
                addrLow = shadowState.Address.Low;
                type = shadowState.Type;
            }

            if (state.Address.High != addrHigh || state.Address.Low != addrLow || state.Type != type)
            {
                state.Address.High = addrHigh;
                state.Address.Low = addrLow;
                state.Type = type;

                _stateUpdater.ForceDirty(StateUpdater.IndexBufferStateIndex);
            }

            if (shadow.IsTrack())
            {
                ref var shadowState = ref _state.ShadowState.IndexBufferState;
                shadowState.Address.High = addrHigh;
                shadowState.Address.Low = addrLow;
                shadowState.Type = type;
            }
        }

        /// <summary>
        /// 更新统一缓冲区状态以进行更新或绑定。尊重当前阴影模式
        /// Updates uniform buffer state for update or bind. Respects current shadow mode.
        /// </summary>
        public void UpdateUniformBufferState(int size, uint addrHigh, uint addrLow)
        {
            var shadow = ShadowMode;
            ref var state = ref _state.State.UniformBufferState;

            if (shadow.IsReplay())
            {
                ref var shadowState = ref _state.ShadowState.UniformBufferState;
                size = shadowState.Size;
                addrHigh = shadowState.Address.High;
                addrLow = shadowState.Address.Low;
            }

            state.Size = size;
            state.Address.High = addrHigh;
            state.Address.Low = addrLow;

            if (shadow.IsTrack())
            {
                ref var shadowState = ref _state.ShadowState.UniformBufferState;
                shadowState.Size = size;
                shadowState.Address.High = addrHigh;
                shadowState.Address.Low = addrLow;
            }
        }

        /// <summary>
        /// 更新着色器偏移。尊重当前阴影模式
        /// Updates a shader offset. Respects current shadow mode.
        /// </summary>
        public void SetShaderOffset(int index, uint offset)
        {
            var shadow = ShadowMode;
            ref var shaderState = ref _state.State.ShaderState[index];

            if (shadow.IsReplay())
            {
                offset = _state.ShadowState.ShaderState[index].Offset;
            }

            if (shaderState.Offset != offset)
            {
                shaderState.Offset = offset;

                _stateUpdater.ForceDirty(StateUpdater.ShaderStateIndex);
            }

            if (shadow.IsTrack())
            {
                _state.ShadowState.ShaderState[index].Offset = offset;
            }
        }

        /// <summary>
        /// 更新统一缓冲区状态以进行更新。尊重当前阴影模式
        /// Updates uniform buffer state for update. Respects current shadow mode.
        /// </summary>
        public void UpdateUniformBufferState(UniformBufferState ubState)
        {
            var shadow = ShadowMode;
            ref var state = ref _state.State.UniformBufferState;

            if (shadow.IsReplay())
            {
                ubState = _state.ShadowState.UniformBufferState;
            }

            state = ubState;

            if (shadow.IsTrack())
            {
                _state.ShadowState.UniformBufferState = ubState;
            }
        }

        /// <summary>
        /// 启动内联到内存的DMA复制操作
        /// Launches the Inline-to-Memory DMA copy operation.
        /// </summary>
        private void LaunchDma(int argument)
        {
            _i2mClass.LaunchDma(ref Unsafe.As<ThreedClassState, InlineToMemoryClassState>(ref _state.State), argument);
        }

        /// <summary>
        /// 将一个字的数据推送到内联到内存引擎
        /// Pushes a word of data to the Inline-to-Memory engine.
        /// </summary>
        private void LoadInlineData(int argument)
        {
            _i2mClass.LoadInlineData(argument);
        }

        /// <summary>
        /// 在同步点上执行增量
        /// Performs an incrementation on a syncpoint.
        /// </summary>
        public void IncrementSyncpoint(int argument)
        {
            uint syncpointId = (uint)argument & 0xFFFF;

            _context.AdvanceSequence();
            _context.CreateHostSyncIfNeeded(HostSyncFlags.StrictSyncpoint);
            _context.Renderer.UpdateCounters(); // 轮询查询计数器，游戏可能需要更新结果
            _context.Synchronization.IncrementSyncpoint(syncpointId);
        }

        /// <summary>
        /// 使用来自采样器池的采样器描述符使缓存无效
        /// Invalidates the cache with the sampler descriptors from the sampler pool.
        /// </summary>
        private void InvalidateSamplerCacheNoWfi(int argument)
        {
            _context.AdvanceSequence();
        }

        /// <summary>
        /// 使用来自纹理池的纹理描述符使缓存无效
        /// Invalidates the cache with the texture descriptors from the texture pool.
        /// </summary>
        private void InvalidateTextureHeaderCacheNoWfi(int argument)
        {
            _context.AdvanceSequence();
        }

        /// <summary>
        /// 发出纹理屏障
        /// Issues a texture barrier.
        /// </summary>
        private void TextureBarrier(int argument)
        {
            _context.Renderer.Pipeline.TextureBarrier();
        }

        /// <summary>
        /// 设置内存中混合微码的起始偏移量
        /// Sets the start offset of the blend microcode in memory.
        /// </summary>
        private void LoadBlendUcodeStart(int argument)
        {
            _blendManager.LoadBlendUcodeStart(argument);
        }

        /// <summary>
        /// 推送一个字混合微码
        /// Pushes one word of blend microcode.
        /// </summary>
        private void LoadBlendUcodeInstruction(int argument)
        {
            _blendManager.LoadBlendUcodeInstruction(argument);
        }

        /// <summary>
        /// 发出平铺纹理屏障
        /// Issues a texture barrier.
        /// </summary>
        private void TextureBarrierTiled(int argument)
        {
            _context.Renderer.Pipeline.TextureBarrierTiled();
        }

        /// <summary>
        /// 绘制纹理，无需指定着色器程序
        /// Draws a texture, without needing to specify shader programs.
        /// </summary>
        private void DrawTexture(int argument)
        {
            _drawManager.DrawTexture(this, argument);
        }

        /// <summary>
        /// 使用指定的拓扑、索引和计数执行非索引绘制
        /// Performs a non-indexed draw with the specified topology, index and count.
        /// </summary>
        private void DrawVertexArrayBeginEndInstanceFirst(int argument)
        {
            _drawManager.DrawVertexArrayBeginEndInstanceFirst(this, argument);
        }

        /// <summary>
        /// 使用指定的拓扑、索引和计数执行非索引绘制，同时增加当前实例
        /// Performs a non-indexed draw with the specified topology, index and count,
        /// while incrementing the current instance.
        /// </summary>
        private void DrawVertexArrayBeginEndInstanceSubsequent(int argument)
        {
            _drawManager.DrawVertexArrayBeginEndInstanceSubsequent(this, argument);
        }

        /// <summary>
        /// 推送四个8位索引缓冲区元素
        /// Pushes four 8-bit index buffer elements.
        /// </summary>
        private void VbElementU8(int argument)
        {
            _drawManager.VbElementU8(argument);
        }

        /// <summary>
        /// 推送两个16位索引缓冲区元素
        /// Pushes two 16-bit index buffer elements.
        /// </summary>
        private void VbElementU16(int argument)
        {
            _drawManager.VbElementU16(argument);
        }

        /// <summary>
        /// 推送一个32位索引缓冲区元素
        /// Pushes one 32-bit index buffer element.
        /// </summary>
        private void VbElementU32(int argument)
        {
            _drawManager.VbElementU32(argument);
        }

        /// <summary>
        /// 将内部GPU计数器的值重置为零
        /// Resets the value of an internal GPU counter back to zero.
        /// </summary>
        private void ResetCounter(int argument)
        {
            _semaphoreUpdater.ResetCounter(argument);
        }

        /// <summary>
        /// 完成绘制调用
        /// Finishes the draw call.
        /// </summary>
        private void DrawEnd(int argument)
        {
            _drawManager.DrawEnd(this, argument);
        }

        /// <summary>
        /// 开始绘制
        /// Starts draw.
        /// </summary>
        private void DrawBegin(int argument)
        {
            _drawManager.DrawBegin(this, argument);
        }

        /// <summary>
        /// 设置索引缓冲区计数
        /// Sets the index buffer count.
        /// </summary>
        private void SetIndexBufferCount(int argument)
        {
            _drawManager.SetIndexBufferCount(argument);
        }

        /// <summary>
        /// 使用8位索引缓冲区元素执行索引绘制
        /// Performs a indexed draw with 8-bit index buffer elements.
        /// </summary>
        private void DrawIndexBuffer8BeginEndInstanceFirst(int argument)
        {
            _drawManager.DrawIndexBuffer8BeginEndInstanceFirst(this, argument);
        }

        /// <summary>
        /// 使用16位索引缓冲区元素执行索引绘制
        /// Performs a indexed draw with 16-bit index buffer elements.
        /// </summary>
        private void DrawIndexBuffer16BeginEndInstanceFirst(int argument)
        {
            _drawManager.DrawIndexBuffer16BeginEndInstanceFirst(this, argument);
        }

        /// <summary>
        /// 优化的32位索引绘制调用，支持批处理
        /// Optimized draw call with batching support.
        /// </summary>
        private void DrawIndexBuffer32BeginEndInstanceFirst(int argument)
        {
            // [优化] 尝试合并绘制调用
            if (_drawManager.TryMergeDraw(argument, IndexType.UInt))
            {
                return;
            }
            
            _drawManager.DrawIndexBuffer32BeginEndInstanceFirst(this, argument);
        }

        /// <summary>
        /// 使用8位索引缓冲区元素执行索引绘制，同时预增加当前实例值
        /// Performs a indexed draw with 8-bit index buffer elements,
        /// while also pre-incrementing the current instance value.
        /// </summary>
        private void DrawIndexBuffer8BeginEndInstanceSubsequent(int argument)
        {
            _drawManager.DrawIndexBuffer8BeginEndInstanceSubsequent(this, argument);
        }

        /// <summary>
        /// 使用16位索引缓冲区元素执行索引绘制，同时预增加当前实例值
        /// Performs a indexed draw with 16-bit index buffer elements,
        /// while also pre-incrementing the current instance value.
        /// </summary>
        private void DrawIndexBuffer16BeginEndInstanceSubsequent(int argument)
        {
            _drawManager.DrawIndexBuffer16BeginEndInstanceSubsequent(this, argument);
        }

        /// <summary>
        /// 使用32位索引缓冲区元素执行索引绘制，同时预增加当前实例值
        /// Performs a indexed draw with 32-bit index buffer elements,
        /// while also pre-incrementing the current instance value.
        /// </summary>
        private void DrawIndexBuffer32BeginEndInstanceSubsequent(int argument)
        {
            _drawManager.DrawIndexBuffer32BeginEndInstanceSubsequent(this, argument);
        }

        /// <summary>
        /// 清除当前颜色和深度-模板缓冲区
        /// Clears the current color and depth-stencil buffers.
        /// </summary>
        private void Clear(int argument)
        {
            _drawManager.Clear(this, argument);
        }

        /// <summary>
        /// 将GPU计数器写入访客内存
        /// Writes a GPU counter to guest memory.
        /// </summary>
        private void Report(int argument)
        {
            _semaphoreUpdater.Report(argument);
        }

        /// <summary>
        /// 执行Falcon微码函数号"4"的高级仿真
        /// Performs high-level emulation of Falcon microcode function number "4".
        /// </summary>
        private void SetFalcon04(int argument)
        {
            _state.State.SetMmeShadowScratch[0] = 1;
        }

        /// <summary>
        /// 使用内联数据更新统一缓冲区数据
        /// Updates the uniform buffer data with inline data.
        /// </summary>
        private void ConstantBufferUpdate(int argument)
        {
            _cbUpdater.Update(argument);
        }

        /// <summary>
        /// 为顶点着色器阶段绑定统一缓冲区
        /// Binds a uniform buffer for the vertex shader stage.
        /// </summary>
        private void ConstantBufferBindVertex(int argument)
        {
            _cbUpdater.BindVertex(argument);
        }

        /// <summary>
        /// 为曲面细分控制着色器阶段绑定统一缓冲区
        /// Binds a uniform buffer for the tessellation control shader stage.
        /// </summary>
        private void ConstantBufferBindTessControl(int argument)
        {
            _cbUpdater.BindTessControl(argument);
        }

        /// <summary>
        /// 为曲面细分评估着色器阶段绑定统一缓冲区
        /// Binds a uniform buffer for the tessellation evaluation shader stage.
        /// </summary>
        private void ConstantBufferBindTessEvaluation(int argument)
        {
            _cbUpdater.BindTessEvaluation(argument);
        }

        /// <summary>
        /// 为几何着色器阶段绑定统一缓冲区
        /// Binds a uniform buffer for the geometry shader stage.
        /// </summary>
        private void ConstantBufferBindGeometry(int argument)
        {
            _cbUpdater.BindGeometry(argument);
        }

        /// <summary>
        /// 为片段着色器阶段绑定统一缓冲区
        /// Binds a uniform buffer for the fragment shader stage.
        /// </summary>
        private void ConstantBufferBindFragment(int argument)
        {
            _cbUpdater.BindFragment(argument);
        }

        /// <summary>
        /// 通用寄存器读取函数，仅返回0
        /// Generic register read function that just returns 0.
        /// </summary>
        private static int Zero()
        {
            return 0;
        }

        /// <summary>
        /// 执行索引或非索引绘制
        /// Performs a indexed or non-indexed draw.
        /// </summary>
        public void Draw(
            PrimitiveTopology topology,
            int count,
            int instanceCount,
            int firstIndex,
            int firstVertex,
            int firstInstance,
            bool indexed)
        {
            _drawManager.Draw(this, topology, count, instanceCount, firstIndex, firstVertex, firstInstance, indexed);
        }

        /// <summary>
        /// 使用来自GPU缓冲区的参数执行间接绘制
        /// Performs a indirect draw, with parameters from a GPU buffer.
        /// </summary>
        public void DrawIndirect(
            PrimitiveTopology topology,
            MultiRange indirectBufferRange,
            MultiRange parameterBufferRange,
            int maxDrawCount,
            int stride,
            int indexCount,
            IndirectDrawType drawType)
        {
            _drawManager.DrawIndirect(this, topology, indirectBufferRange, parameterBufferRange, maxDrawCount, stride, indexCount, drawType);
        }

        /// <summary>
        /// 清除当前颜色和深度-模板缓冲区
        /// Clears the current color and depth-stencil buffers.
        /// </summary>
        public void Clear(int argument, int layerCount)
        {
            _drawManager.Clear(this, argument, layerCount);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pendingDirtyOffsets.Clear(); // [优化] 清理资源
                _drawManager.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
