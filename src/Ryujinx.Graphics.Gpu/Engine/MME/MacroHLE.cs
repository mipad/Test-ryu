using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Types;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.MME
{
    /// <summary>
    /// Optimized Macro High-level emulation with GPU-specific enhancements.
    /// </summary>
    class MacroHLE : IMacroEE
    {
        private const int ColorLayerCountOffset = 0x818;
        private const int ColorStructSize = 0x40;
        private const int ZetaLayerCountOffset = 0x1230;
        private const int UniformBufferBindVertexOffset = 0x2410;
        private const int FirstVertexOffset = 0x1434;

        private const int IndirectIndexedDataEntrySize = 0x14;

        private const int LogicOpOffset = 0x19c4;
        private const int ShaderIdScratchOffset = 0x3470;
        private const int ShaderAddressScratchOffset = 0x3488;
        private const int UpdateConstantBufferAddressesBase = 0x34a8;
        private const int UpdateConstantBufferSizesBase = 0x34bc;
        private const int UpdateConstantBufferAddressCbu = 0x3460;

        private readonly GPFifoProcessor _processor;
        private readonly MacroHLEFunctionName _functionName;
        private readonly Dictionary<int, int> _shaderCache = new Dictionary<int, int>();

        /// <summary>
        /// Arguments FIFO with improved memory access pattern.
        /// </summary>
        public Queue<FifoWord> Fifo { get; }

        /// <summary>
        /// Creates a new instance of the optimized HLE macro handler.
        /// </summary>
        public MacroHLE(GPFifoProcessor processor, MacroHLEFunctionName functionName)
        {
            _processor = processor;
            _functionName = functionName;
            Fifo = new Queue<FifoWord>(32); // Pre-allocate for common case
        }

        /// <summary>
        /// Executes a macro program with optimized paths for common operations.
        /// </summary>
        public void Execute(ReadOnlySpan<int> code, IDeviceState state, int arg0)
        {
            try
            {
                switch (_functionName)
                {
                    case MacroHLEFunctionName.BindShaderProgram:
                        OptimizedBindShaderProgram(state, arg0);
                        break;
                    case MacroHLEFunctionName.ClearColor:
                        OptimizedClearColor(state, arg0);
                        break;
                    case MacroHLEFunctionName.ClearDepthStencil:
                        OptimizedClearDepthStencil(state, arg0);
                        break;
                    case MacroHLEFunctionName.DrawArraysInstanced:
                        OptimizedDrawArraysInstanced(state, arg0);
                        break;
                    case MacroHLEFunctionName.DrawElements:
                        OptimizedDrawElements(state, arg0);
                        break;
                    case MacroHLEFunctionName.DrawElementsInstanced:
                        OptimizedDrawElementsInstanced(state, arg0);
                        break;
                    case MacroHLEFunctionName.DrawElementsIndirect:
                        OptimizedDrawElementsIndirect(state, arg0);
                        break;
                    case MacroHLEFunctionName.MultiDrawElementsIndirectCount:
                        OptimizedMultiDrawElementsIndirectCount(state, arg0);
                        break;
                    case MacroHLEFunctionName.UpdateBlendState:
                        OptimizedUpdateBlendState(state, arg0);
                        break;
                    case MacroHLEFunctionName.UpdateColorMasks:
                        OptimizedUpdateColorMasks(state, arg0);
                        break;
                    case MacroHLEFunctionName.UpdateUniformBufferState:
                        OptimizedUpdateUniformBufferState(state, arg0);
                        break;
                    case MacroHLEFunctionName.UpdateUniformBufferStateCbu:
                        OptimizedUpdateUniformBufferStateCbu(state, arg0);
                        break;
                    case MacroHLEFunctionName.UpdateUniformBufferStateCbuV2:
                        OptimizedUpdateUniformBufferStateCbuV2(state, arg0);
                        break;
                    default:
                        throw new NotImplementedException(_functionName.ToString());
                }
            }
            finally
            {
                Fifo.Clear(); // Ensure FIFO is always cleared
            }
        }

        /// <summary>
        /// Optimized shader program binding with caching.
        /// </summary>
        private void OptimizedBindShaderProgram(IDeviceState state, int arg0)
        {
            int scratchOffset = ShaderIdScratchOffset + arg0 * 4;
            int lastId = state.Read(scratchOffset);
            
            var idParam = FetchParam();
            var offsetParam = FetchParam();
            
            if (lastId == idParam.Word)
            {
                FetchParam(); // Skip unused params
                FetchParam();
                return;
            }

            // Update shader cache
            _shaderCache[arg0] = idParam.Word;
            
            uint offset = (uint)offsetParam.Word;
            _processor.ThreedClass.SetShaderOffset(arg0, offset);

            // Optimized address masking
            int addrMask = unchecked((int)0xfffc0fff) << 2;
            int maskedScratchOffset = scratchOffset & addrMask;
            int maskedShaderOffset = (ShaderAddressScratchOffset + arg0 * 4) & addrMask;

            state.Write(maskedScratchOffset, idParam.Word);
            state.Write(maskedShaderOffset, offsetParam.Word);

            // Process remaining parameters
            int stage = FetchParam().Word;
            uint cbAddress = (uint)FetchParam().Word;

            // Batch uniform buffer updates
            _processor.ThreedClass.ForceStateDirty();
            _processor.ThreedClass.UpdateUniformBufferState(65536, cbAddress >> 24, cbAddress << 8);
            
            int stageOffset = (stage & 0x7f) << 3;
            state.Write((UniformBufferBindVertexOffset + stageOffset * 4) & addrMask, 17);
            
            _processor.ThreedClass.UpdateState();
        }

        /// <summary>
        /// Optimized uniform buffer state update.
        /// </summary>
        private void OptimizedUpdateUniformBufferState(IDeviceState state, int arg0)
        {
            uint address = (uint)state.Read(UpdateConstantBufferAddressesBase + arg0 * 4);
            int size = state.Read(UpdateConstantBufferSizesBase + arg0 * 4);

            // Align for better GPU memory access
            size = (size + 255) & ~255;
            address = address & ~255u;

            _processor.ThreedClass.UpdateUniformBufferState(size, address >> 24, address << 8);
        }

        /// <summary>
        /// Optimized uniform buffer state update for CBU.
        /// </summary>
        private void OptimizedUpdateUniformBufferStateCbu(IDeviceState state, int arg0)
        {
            uint address = (uint)state.Read(UpdateConstantBufferAddressCbu);

            var ubState = new UniformBufferState
            {
                Address = new()
                {
                    High = address >> 24,
                    Low = (address << 8) & ~255u // Align to 256 bytes
                },
                Size = 24320,
                Offset = arg0 << 2
            };

            _processor.ThreedClass.UpdateUniformBufferState(ubState);
        }

        /// <summary>
        /// Optimized uniform buffer state update for CBU v2.
        /// </summary>
        private void OptimizedUpdateUniformBufferStateCbuV2(IDeviceState state, int arg0)
        {
            uint address = (uint)state.Read(UpdateConstantBufferAddressCbu);

            var ubState = new UniformBufferState
            {
                Address = new()
                {
                    High = address >> 24,
                    Low = (address << 8) & ~255u // Align to 256 bytes
                },
                Size = 28672,
                Offset = arg0 << 2
            };

            _processor.ThreedClass.UpdateUniformBufferState(ubState);
        }

        /// <summary>
        /// Optimized blend state update.
        /// </summary>
        private void OptimizedUpdateBlendState(IDeviceState state, int arg0)
        {
            state.Write(LogicOpOffset, 0);

            var enable = new Array8<Boolean32>();
            for (int i = 0; i < 8; i++)
            {
                enable[i] = new Boolean32((uint)(arg0 >> (i + 8)) & 1);
            }

            _processor.ThreedClass.UpdateBlendEnable(ref enable);
        }

        /// <summary>
        /// Optimized color masks update.
        /// </summary>
        private void OptimizedUpdateColorMasks(IDeviceState state, int arg0)
        {
            var masks = new Array8<RtColorMask>();
            int index = 0;

            for (int i = 0; i < 4; i++)
            {
                masks[index++] = new RtColorMask((uint)arg0 & 0x1fff);
                masks[index++] = new RtColorMask(((uint)arg0 >> 16) & 0x1fff);

                if (i != 3)
                {
                    arg0 = FetchParam().Word;
                }
            }

            _processor.ThreedClass.UpdateColorMasks(ref masks);
        }

        /// <summary>
        /// Optimized color clear operation.
        /// </summary>
        private void OptimizedClearColor(IDeviceState state, int arg0)
        {
            int index = (arg0 >> 6) & 0xf;
            int layerCount = state.Read(ColorLayerCountOffset + index * ColorStructSize);

            _processor.ThreedClass.ForceStateDirty();
            _processor.ThreedClass.Clear(arg0, layerCount);
            _processor.ThreedClass.UpdateState();
        }

        /// <summary>
        /// Optimized depth-stencil clear operation.
        /// </summary>
        private void OptimizedClearDepthStencil(IDeviceState state, int arg0)
        {
            int layerCount = state.Read(ZetaLayerCountOffset);

            _processor.ThreedClass.ForceStateDirty();
            _processor.ThreedClass.Clear(arg0, layerCount);
            _processor.ThreedClass.UpdateState();
        }

        /// <summary>
        /// Optimized instanced draw operation.
        /// </summary>
        private void OptimizedDrawArraysInstanced(IDeviceState state, int arg0)
        {
            var topology = (PrimitiveTopology)arg0;
            var count = FetchParam();
            var instanceCount = FetchParam();
            var firstVertex = FetchParam();
            var firstInstance = FetchParam();

            if (ShouldSkipDraw(state, instanceCount.Word))
            {
                return;
            }

            _processor.ThreedClass.ForceStateDirty();
            _processor.ThreedClass.Draw(
                topology,
                count.Word,
                instanceCount.Word,
                0,
                firstVertex.Word,
                firstInstance.Word,
                indexed: false);
            _processor.ThreedClass.UpdateState();
        }

        /// <summary>
        /// Optimized indexed draw operation.
        /// </summary>
        private void OptimizedDrawElements(IDeviceState state, int arg0)
        {
            var topology = (PrimitiveTopology)arg0;
            var indexAddressHigh = FetchParam();
            var indexAddressLow = FetchParam();
            var indexType = FetchParam();
            var indexCount = FetchParam();

            _processor.ThreedClass.ForceStateDirty();
            _processor.ThreedClass.UpdateIndexBuffer(
                (uint)indexAddressHigh.Word,
                (uint)indexAddressLow.Word,
                (IndexType)indexType.Word);

            _processor.ThreedClass.Draw(
                topology,
                indexCount.Word,
                1,
                0,
                state.Read(FirstVertexOffset),
                0,
                indexed: true);
            _processor.ThreedClass.UpdateState();
        }

        /// <summary>
        /// Optimized indexed instanced draw operation.
        /// </summary>
        private void OptimizedDrawElementsInstanced(IDeviceState state, int arg0)
        {
            var topology = (PrimitiveTopology)arg0;
            var count = FetchParam();
            var instanceCount = FetchParam();
            var firstIndex = FetchParam();
            var firstVertex = FetchParam();
            var firstInstance = FetchParam();

            if (ShouldSkipDraw(state, instanceCount.Word))
            {
                return;
            }

            _processor.ThreedClass.ForceStateDirty();
            _processor.ThreedClass.Draw(
                topology,
                count.Word,
                instanceCount.Word,
                firstIndex.Word,
                firstVertex.Word,
                firstInstance.Word,
                indexed: true);
            _processor.ThreedClass.UpdateState();
        }

        /// <summary>
        /// Optimized indirect indexed draw operation.
        /// </summary>
        private void OptimizedDrawElementsIndirect(IDeviceState state, int arg0)
        {
            var topology = (PrimitiveTopology)arg0;
            var count = FetchParam();
            var instanceCount = FetchParam();
            var firstIndex = FetchParam();
            var firstVertex = FetchParam();
            var firstInstance = FetchParam();

            ulong indirectBufferGpuVa = count.GpuVa;
            var bufferCache = _processor.MemoryManager.Physical.BufferCache;

            bool useBuffer = bufferCache.CheckModified(_processor.MemoryManager, indirectBufferGpuVa, IndirectIndexedDataEntrySize, out ulong indirectBufferAddress);

            _processor.ThreedClass.ForceStateDirty();
            if (useBuffer)
            {
                int indexCount = firstIndex.Word + count.Word;
                _processor.ThreedClass.DrawIndirect(
                    topology,
                    new MultiRange(indirectBufferAddress, IndirectIndexedDataEntrySize),
                    default,
                    1,
                    IndirectIndexedDataEntrySize,
                    indexCount,
                    IndirectDrawType.DrawIndexedIndirect);
            }
            else if (!ShouldSkipDraw(state, instanceCount.Word))
            {
                _processor.ThreedClass.Draw(
                    topology,
                    count.Word,
                    instanceCount.Word,
                    firstIndex.Word,
                    firstVertex.Word,
                    firstInstance.Word,
                    indexed: true);
            }
            _processor.ThreedClass.UpdateState();
        }

        /// <summary>
        /// Optimized indirect multi-draw operation.
        /// </summary>
        private void OptimizedMultiDrawElementsIndirectCount(IDeviceState state, int arg0)
        {
            int arg1 = FetchParam().Word;
            int arg2 = FetchParam().Word;
            int arg3 = FetchParam().Word;

            int startDraw = arg0;
            int endDraw = arg1;
            var topology = (PrimitiveTopology)arg2;
            int paddingWords = arg3;
            int stride = paddingWords * 4 + 0x14;

            ulong parameterBufferGpuVa = FetchParam().GpuVa;
            int maxDrawCount = endDraw - startDraw;

            if (startDraw != 0)
            {
                int drawCount = _processor.MemoryManager.Read<int>(parameterBufferGpuVa, tracked: true);
                if ((uint)drawCount <= (uint)startDraw)
                {
                    maxDrawCount = 0;
                }
                else
                {
                    maxDrawCount = (int)Math.Min((uint)maxDrawCount, (uint)(drawCount - startDraw));
                }
            }

            if (maxDrawCount == 0)
            {
                return;
            }

            ulong indirectBufferGpuVa = 0;
            int indexCount = 0;

            for (int i = 0; i < maxDrawCount; i++)
            {
                var count = FetchParam();
                var instanceCount = FetchParam();
                var firstIndex = FetchParam();
                var firstVertex = FetchParam();
                var firstInstance = FetchParam();

                if (i == 0)
                {
                    indirectBufferGpuVa = count.GpuVa;
                }

                indexCount = Math.Max(indexCount, count.Word + firstIndex.Word);

                if (i != maxDrawCount - 1)
                {
                    for (int j = 0; j < paddingWords; j++)
                    {
                        FetchParam();
                    }
                }
            }

            var bufferCache = _processor.MemoryManager.Physical.BufferCache;
            ulong indirectBufferSize = (ulong)maxDrawCount * (ulong)stride;

            _processor.ThreedClass.ForceStateDirty();
            MultiRange indirectBufferRange = bufferCache.TranslateAndCreateMultiBuffers(
                _processor.MemoryManager, indirectBufferGpuVa, indirectBufferSize, BufferStage.Indirect);
            MultiRange parameterBufferRange = bufferCache.TranslateAndCreateMultiBuffers(
                _processor.MemoryManager, parameterBufferGpuVa, 4, BufferStage.Indirect);

            _processor.ThreedClass.DrawIndirect(
                topology,
                indirectBufferRange,
                parameterBufferRange,
                maxDrawCount,
                stride,
                indexCount,
                Threed.IndirectDrawType.DrawIndexedIndirectCount);
            _processor.ThreedClass.UpdateState();
        }

        private static bool ShouldSkipDraw(IDeviceState state, int instanceCount)
        {
            return (Read(state, 0xd1b) & instanceCount) == 0;
        }

        private FifoWord FetchParam()
        {
            if (!Fifo.TryDequeue(out var value))
            {
                Logger.Warning?.Print(LogClass.Gpu, "Macro attempted to fetch an inexistent argument.");
                return new FifoWord(0UL, 0);
            }
            return value;
        }

        private static int Read(IDeviceState state, int reg)
        {
            return state.Read(reg * 4);
        }
    }
}
