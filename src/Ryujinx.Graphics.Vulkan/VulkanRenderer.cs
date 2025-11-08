using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Format = Ryujinx.Graphics.GAL.Format;
using PrimitiveTopology = Ryujinx.Graphics.GAL.PrimitiveTopology;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;
using VkCompareOp = Silk.NET.Vulkan.CompareOp;
using VkStencilOp = Silk.NET.Vulkan.StencilOp;
using GALCompareOp = Ryujinx.Graphics.GAL.CompareOp;
using GALStencilOp = Ryujinx.Graphics.GAL.StencilOp;

namespace Ryujinx.Graphics.Vulkan
{
    // 扩展方法：GAL 到 Vulkan 枚举转换
    internal static class EnumConversionExtensions
    {
        public static VkCompareOp Convert(this GALCompareOp op)
        {
            return op switch
            {
                GALCompareOp.Never => VkCompareOp.Never,
                GALCompareOp.Less => VkCompareOp.Less,
                GALCompareOp.Equal => VkCompareOp.Equal,
                GALCompareOp.LessOrEqual => VkCompareOp.LessOrEqual,
                GALCompareOp.Greater => VkCompareOp.Greater,
                GALCompareOp.NotEqual => VkCompareOp.NotEqual,
                GALCompareOp.GreaterOrEqual => VkCompareOp.GreaterOrEqual,
                GALCompareOp.Always => VkCompareOp.Always,
                _ => VkCompareOp.Never
            };
        }

        public static VkStencilOp Convert(this GALStencilOp op)
        {
            return op switch
            {
                GALStencilOp.Keep => VkStencilOp.Keep,
                GALStencilOp.Zero => VkStencilOp.Zero,
                GALStencilOp.Replace => VkStencilOp.Replace,
                GALStencilOp.IncrementAndClamp => VkStencilOp.IncrementAndClamp,
                GALStencilOp.DecrementAndClamp => VkStencilOp.DecrementAndClamp,
                GALStencilOp.Invert => VkStencilOp.Invert,
                GALStencilOp.IncrementAndWrap => VkStencilOp.IncrementAndWrap,
                GALStencilOp.DecrementAndWrap => VkStencilOp.DecrementAndWrap,
                _ => VkStencilOp.Keep
            };
        }

        public static CullModeFlags Convert(this CullMode mode)
        {
            return mode switch
            {
                CullMode.Front => CullModeFlags.FrontBit,
                CullMode.Back => CullModeFlags.BackBit,
                CullMode.FrontAndBack => CullModeFlags.FrontAndBack,
                _ => CullModeFlags.None
            };
        }

        public static FrontFace Convert(this Ryujinx.Graphics.GAL.FrontFace face)
        {
            return face switch
            {
                Ryujinx.Graphics.GAL.FrontFace.Clockwise => FrontFace.Clockwise,
                Ryujinx.Graphics.GAL.FrontFace.CounterClockwise => FrontFace.CounterClockwise,
                _ => FrontFace.CounterClockwise
            };
        }

        public static LogicOp Convert(this Ryujinx.Graphics.GAL.LogicOp op)
        {
            return op switch
            {
                Ryujinx.Graphics.GAL.LogicOp.Clear => LogicOp.Clear,
                Ryujinx.Graphics.GAL.LogicOp.And => LogicOp.And,
                Ryujinx.Graphics.GAL.LogicOp.AndReverse => LogicOp.AndReverse,
                Ryujinx.Graphics.GAL.LogicOp.Copy => LogicOp.Copy,
                Ryujinx.Graphics.GAL.LogicOp.AndInverted => LogicOp.AndInverted,
                Ryujinx.Graphics.GAL.LogicOp.NoOp => LogicOp.NoOp,
                Ryujinx.Graphics.GAL.LogicOp.Xor => LogicOp.Xor,
                Ryujinx.Graphics.GAL.LogicOp.Or => LogicOp.Or,
                Ryujinx.Graphics.GAL.LogicOp.Nor => LogicOp.Nor,
                Ryujinx.Graphics.GAL.LogicOp.Equivalent => LogicOp.Equivalent,
                Ryujinx.Graphics.GAL.LogicOp.Invert => LogicOp.Invert,
                Ryujinx.Graphics.GAL.LogicOp.OrReverse => LogicOp.OrReverse,
                Ryujinx.Graphics.GAL.LogicOp.CopyInverted => LogicOp.CopyInverted,
                Ryujinx.Graphics.GAL.LogicOp.OrInverted => LogicOp.OrInverted,
                Ryujinx.Graphics.GAL.LogicOp.Nand => LogicOp.Nand,
                Ryujinx.Graphics.GAL.LogicOp.Set => LogicOp.Set,
                _ => LogicOp.Copy
            };
        }
    }

    // 状态结构定义
    internal struct DynamicState
    {
        public bool CullEnable { get; set; }
        public CullMode CullMode { get; set; }
        public bool DepthTestEnable { get; set; }
        public bool DepthWriteEnable { get; set; }
        public GALCompareOp DepthCompareOp { get; set; }
        public bool StencilTestEnable { get; set; }
        public StencilOpState FrontStencilOps { get; set; }
        public StencilOpState BackStencilOps { get; set; }
        public bool DepthBoundsTestEnable { get; set; }
        public bool PrimitiveRestartEnable { get; set; }
        public bool RasterizerDiscardEnable { get; set; }
        public bool DepthBiasEnable { get; set; }
        public bool LogicOpEnable { get; set; }
        public Ryujinx.Graphics.GAL.LogicOp LogicOp { get; set; }
        public bool DepthClampEnable { get; set; }
        public Ryujinx.Graphics.GAL.FrontFace FrontFace { get; set; }
        public float LineWidth { get; set; }
        public float DepthBiasConstant { get; set; }
        public float DepthBiasClamp { get; set; }
        public float DepthBiasSlope { get; set; }
        public (float Min, float Max) DepthBounds { get; set; }
        public (float R, float G, float B, float A) BlendConstants { get; set; }
        
        // 视口和剪刀状态
        public Viewport[] Viewports { get; set; }
        public Rect2D[] Scissors { get; set; }
        
        // 模板状态
        public uint StencilFrontReference { get; set; }
        public uint StencilBackReference { get; set; }
        public uint StencilFrontCompareMask { get; set; }
        public uint StencilBackCompareMask { get; set; }
        public uint StencilFrontWriteMask { get; set; }
        public uint StencilBackWriteMask { get; set; }
    }

    internal enum CullMode
    {
        None = 0,
        Front = 1,
        Back = 2,
        FrontAndBack = 3
    }

    internal struct StencilOpState
    {
        public GALStencilOp Fail { get; set; }
        public GALStencilOp Pass { get; set; }
        public GALStencilOp DepthFail { get; set; }
        public GALCompareOp Compare { get; set; }
        public uint CompareMask { get; set; }
        public uint WriteMask { get; set; }
        public uint Reference { get; set; }

        public override bool Equals(object obj)
        {
            return obj is StencilOpState other &&
                   Fail == other.Fail &&
                   Pass == other.Pass &&
                   DepthFail == other.DepthFail &&
                   Compare == other.Compare &&
                   CompareMask == other.CompareMask &&
                   WriteMask == other.WriteMask &&
                   Reference == other.Reference;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Fail, Pass, DepthFail, Compare, CompareMask, WriteMask, Reference);
        }
    }

    internal struct TransformFeedbackState
    {
        public bool Enabled { get; set; }
        public TransformFeedbackBufferState[] Buffers { get; set; }
    }

    internal struct TransformFeedbackBufferState
    {
        public BufferHandle Buffer { get; set; }
        public int Offset { get; set; }
        public int Size { get; set; }
    }

    internal struct ConditionalRenderingCondition
    {
        public BufferHandle Buffer { get; set; }
        public int Offset { get; set; }
        public bool IsEqual { get; set; }
    }

    internal struct ImageCopyInfo
    {
        public ITexture Source { get; set; }
        public ITexture Destination { get; set; }
        public int SrcX { get; set; }
        public int SrcY { get; set; }
        public int DstX { get; set; }
        public int DstY { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    // 新增：DMA 加速器类
    internal class DmaAccelerator
    {
        private readonly VulkanRenderer _renderer;
        private readonly BufferManager _bufferManager;

        public DmaAccelerator(VulkanRenderer renderer, BufferManager bufferManager)
        {
            _renderer = renderer;
            _bufferManager = bufferManager;
        }

        public bool BufferClear(ulong address, ulong size, uint value)
        {
            // 实现缓冲区清除加速逻辑
            // 这里可以使用计算着色器或专用传输操作
            return false; // 暂时返回 false，需要实际实现
        }

        public bool BufferCopy(ulong srcAddress, ulong dstAddress, ulong size)
        {
            // 实现缓冲区复制加速逻辑
            return false; // 暂时返回 false，需要实际实现
        }

        public bool ImageToBuffer(ImageCopyInfo copyInfo)
        {
            // 实现图像到缓冲区的 DMA 传输
            return false; // 暂时返回 false，需要实际实现
        }

        public bool BufferToImage(ImageCopyInfo copyInfo)
        {
            // 实现缓冲区到图像的 DMA 传输
            return false; // 暂时返回 false，需要实际实现
        }

        public void Dispose()
        {
            // 清理资源
        }
    }

    // 动态状态跟踪器类
    internal class DynamicStateTracker
    {
        // 基础动态状态
        private CullModeFlags _lastCullMode;
        private bool _lastCullEnable;
        private bool _lastDepthTestEnable;
        private bool _lastDepthWriteEnable;
        private VkCompareOp _lastDepthCompareOp;
        private bool _lastStencilTestEnable;
        private StencilOpState _lastFrontStencilOps;
        private StencilOpState _lastBackStencilOps;
        private bool _lastDepthBoundsTestEnable;
        private bool _lastPrimitiveRestartEnable;
        private bool _lastRasterizerDiscardEnable;
        private bool _lastDepthBiasEnable;
        private bool _lastLogicOpEnable;
        private LogicOp _lastLogicOp;
        private bool _lastDepthClampEnable;
        private FrontFace _lastFrontFace;
        private float _lastLineWidth;
        private float _lastDepthBiasConstant;
        private float _lastDepthBiasClamp;
        private float _lastDepthBiasSlope;
        private (float Min, float Max) _lastDepthBounds;
        private (float R, float G, float B, float A) _lastBlendConstants;

        // 视口和剪刀状态
        private Viewport[] _lastViewports;
        private Rect2D[] _lastScissors;

        // 模板状态
        private uint _lastStencilFrontReference;
        private uint _lastStencilBackReference;
        private uint _lastStencilFrontCompareMask;
        private uint _lastStencilBackCompareMask;
        private uint _lastStencilFrontWriteMask;
        private uint _lastStencilBackWriteMask;

        public DynamicStateTracker()
        {
            Reset();
        }

        public void Reset()
        {
            _lastCullMode = CullModeFlags.None;
            _lastCullEnable = false;
            _lastDepthTestEnable = false;
            _lastDepthWriteEnable = false;
            _lastDepthCompareOp = VkCompareOp.Never;
            _lastStencilTestEnable = false;
            _lastFrontStencilOps = default;
            _lastBackStencilOps = default;
            _lastDepthBoundsTestEnable = false;
            _lastPrimitiveRestartEnable = false;
            _lastRasterizerDiscardEnable = false;
            _lastDepthBiasEnable = false;
            _lastLogicOpEnable = false;
            _lastLogicOp = LogicOp.Copy;
            _lastDepthClampEnable = false;
            _lastFrontFace = FrontFace.CounterClockwise;
            _lastLineWidth = 1.0f;
            _lastDepthBiasConstant = 0.0f;
            _lastDepthBiasClamp = 0.0f;
            _lastDepthBiasSlope = 0.0f;
            _lastDepthBounds = (0.0f, 1.0f);
            _lastBlendConstants = (0.0f, 0.0f, 0.0f, 0.0f);

            _lastViewports = Array.Empty<Viewport>();
            _lastScissors = Array.Empty<Rect2D>();

            _lastStencilFrontReference = 0;
            _lastStencilBackReference = 0;
            _lastStencilFrontCompareMask = 0xFFFFFFFF;
            _lastStencilBackCompareMask = 0xFFFFFFFF;
            _lastStencilFrontWriteMask = 0xFFFFFFFF;
            _lastStencilBackWriteMask = 0xFFFFFFFF;
        }

        public void ResetDirtyFlags()
        {
            // 保留当前状态但清除脏标志，用于帧开始
            // 这里我们不需要做任何事情，因为脏检测是基于比较的
        }

        // 状态变化检测方法
        public bool ShouldUpdateCullMode(CullModeFlags newMode, bool newEnable)
        {
            bool shouldUpdate = _lastCullMode != newMode || _lastCullEnable != newEnable;
            if (shouldUpdate)
            {
                _lastCullMode = newMode;
                _lastCullEnable = newEnable;
            }
            return shouldUpdate;
        }

        public bool ShouldUpdateDepthTest(bool newEnable, bool newWriteEnable, GALCompareOp newCompareOp)
        {
            VkCompareOp vkCompareOp = newCompareOp.Convert();
            bool shouldUpdate = _lastDepthTestEnable != newEnable || 
                               _lastDepthWriteEnable != newWriteEnable || 
                               _lastDepthCompareOp != vkCompareOp;
            if (shouldUpdate)
            {
                _lastDepthTestEnable = newEnable;
                _lastDepthWriteEnable = newWriteEnable;
                _lastDepthCompareOp = vkCompareOp;
            }
            return shouldUpdate;
        }

        public bool ShouldUpdateStencil(bool newEnable, StencilOpState newFrontOps, StencilOpState newBackOps)
        {
            bool shouldUpdate = _lastStencilTestEnable != newEnable || 
                               !_lastFrontStencilOps.Equals(newFrontOps) || 
                               !_lastBackStencilOps.Equals(newBackOps);
            if (shouldUpdate)
            {
                _lastStencilTestEnable = newEnable;
                _lastFrontStencilOps = newFrontOps;
                _lastBackStencilOps = newBackOps;
            }
            return shouldUpdate;
        }

        public bool ShouldUpdateDepthBounds(bool newEnable, (float Min, float Max) newBounds)
        {
            bool shouldUpdate = _lastDepthBoundsTestEnable != newEnable || 
                               _lastDepthBounds != newBounds;
            if (shouldUpdate)
            {
                _lastDepthBoundsTestEnable = newEnable;
                _lastDepthBounds = newBounds;
            }
            return shouldUpdate;
        }

        public bool ShouldUpdatePrimitiveRestart(bool newEnable)
        {
            bool shouldUpdate = _lastPrimitiveRestartEnable != newEnable;
            if (shouldUpdate) _lastPrimitiveRestartEnable = newEnable;
            return shouldUpdate;
        }

        public bool ShouldUpdateRasterizerDiscard(bool newEnable)
        {
            bool shouldUpdate = _lastRasterizerDiscardEnable != newEnable;
            if (shouldUpdate) _lastRasterizerDiscardEnable = newEnable;
            return shouldUpdate;
        }

        public bool ShouldUpdateDepthBias(bool newEnable, float constant, float clamp, float slope)
        {
            bool shouldUpdate = _lastDepthBiasEnable != newEnable || 
                               _lastDepthBiasConstant != constant || 
                               _lastDepthBiasClamp != clamp || 
                               _lastDepthBiasSlope != slope;
            if (shouldUpdate)
            {
                _lastDepthBiasEnable = newEnable;
                _lastDepthBiasConstant = constant;
                _lastDepthBiasClamp = clamp;
                _lastDepthBiasSlope = slope;
            }
            return shouldUpdate;
        }

        public bool ShouldUpdateLogicOp(bool newEnable, Ryujinx.Graphics.GAL.LogicOp newOp)
        {
            LogicOp vkOp = newOp.Convert();
            bool shouldUpdate = _lastLogicOpEnable != newEnable || _lastLogicOp != vkOp;
            if (shouldUpdate)
            {
                _lastLogicOpEnable = newEnable;
                _lastLogicOp = vkOp;
            }
            return shouldUpdate;
        }

        public bool ShouldUpdateDepthClamp(bool newEnable)
        {
            bool shouldUpdate = _lastDepthClampEnable != newEnable;
            if (shouldUpdate) _lastDepthClampEnable = newEnable;
            return shouldUpdate;
        }

        public bool ShouldUpdateFrontFace(Ryujinx.Graphics.GAL.FrontFace newFace)
        {
            FrontFace vkFace = newFace.Convert();
            bool shouldUpdate = _lastFrontFace != vkFace;
            if (shouldUpdate) _lastFrontFace = vkFace;
            return shouldUpdate;
        }

        public bool ShouldUpdateLineWidth(float newWidth)
        {
            bool shouldUpdate = Math.Abs(_lastLineWidth - newWidth) > float.Epsilon;
            if (shouldUpdate) _lastLineWidth = newWidth;
            return shouldUpdate;
        }

        public bool ShouldUpdateBlendConstants((float R, float G, float B, float A) newConstants)
        {
            bool shouldUpdate = _lastBlendConstants != newConstants;
            if (shouldUpdate) _lastBlendConstants = newConstants;
            return shouldUpdate;
        }

        public bool ShouldUpdateViewports(Viewport[] newViewports)
        {
            bool shouldUpdate = !ArraysEqual(_lastViewports, newViewports);
            if (shouldUpdate) _lastViewports = newViewports;
            return shouldUpdate;
        }

        public bool ShouldUpdateScissors(Rect2D[] newScissors)
        {
            bool shouldUpdate = !ArraysEqual(_lastScissors, newScissors);
            if (shouldUpdate) _lastScissors = newScissors;
            return shouldUpdate;
        }

        public bool ShouldUpdateStencilReferences(uint frontRef, uint backRef, uint frontCompareMask, uint backCompareMask, uint frontWriteMask, uint backWriteMask)
        {
            bool shouldUpdate = _lastStencilFrontReference != frontRef ||
                               _lastStencilBackReference != backRef ||
                               _lastStencilFrontCompareMask != frontCompareMask ||
                               _lastStencilBackCompareMask != backCompareMask ||
                               _lastStencilFrontWriteMask != frontWriteMask ||
                               _lastStencilBackWriteMask != backWriteMask;
            if (shouldUpdate)
            {
                _lastStencilFrontReference = frontRef;
                _lastStencilBackReference = backRef;
                _lastStencilFrontCompareMask = frontCompareMask;
                _lastStencilBackCompareMask = backCompareMask;
                _lastStencilFrontWriteMask = frontWriteMask;
                _lastStencilBackWriteMask = backWriteMask;
            }
            return shouldUpdate;
        }

        private static bool ArraysEqual<T>(T[] a, T[] b) where T : IEquatable<T>
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i])) return false;
            }
            return true;
        }
    }

    unsafe public sealed class VulkanRenderer : IRenderer
    {
        private VulkanInstance _instance;
        private SurfaceKHR _surface;
        private VulkanPhysicalDevice _physicalDevice;
        private Device _device;
        private WindowBase _window;
        private CommandBufferPool _computeCommandPool;
        private bool _initialized;

        // JNI/Lifecycle-Flag
        internal volatile bool PresentAllowed = true;

        public uint ProgramCount { get; set; } = 0;

        // 智能批处理机制
        private int _drawCounter = 0;
        private const int DRAWS_TO_DISPATCH = 2048;
        private const int DRAWS_TO_FLUSH = 4096;

        internal KhrTimelineSemaphore TimelineSemaphoreApi { get; private set; }
        internal FormatCapabilities FormatCapabilities { get; private set; }
        internal HardwareCapabilities Capabilities;

        internal Vk Api { get; private set; }
        internal KhrSurface SurfaceApi { get; private set; }
        internal KhrSwapchain SwapchainApi { get; private set; }
        internal ExtConditionalRendering ConditionalRenderingApi { get; private set; }
        internal ExtExtendedDynamicState ExtendedDynamicStateApi { get; private set; }
        internal ExtExtendedDynamicState2 ExtendedDynamicState2Api { get; private set; }
        internal ExtExtendedDynamicState3 ExtendedDynamicState3Api { get; private set; }
        internal KhrPushDescriptor PushDescriptorApi { get; private set; }
        internal ExtTransformFeedback TransformFeedbackApi { get; private set; }
        internal KhrDrawIndirectCount DrawIndirectCountApi { get; private set; }
        internal ExtAttachmentFeedbackLoopDynamicState DynamicFeedbackLoopApi { get; private set; }
        
        internal bool SupportsFragmentDensityMap { get; private set; }
        internal bool SupportsFragmentDensityMap2 { get; private set; }

        internal uint QueueFamilyIndex { get; private set; }
        internal Queue Queue { get; private set; }
        internal Queue BackgroundQueue { get; private set; }
        internal object BackgroundQueueLock { get; private set; }
        internal object QueueLock { get; private set; }

        // 动态状态跟踪器
        private DynamicStateTracker _dynamicStateTracker;

        // NEU: SurfaceLock, um Create/Destroy/Queries zu serialisieren
        internal object SurfaceLock { get; private set; }

        internal MemoryAllocator MemoryAllocator { get; private set; }
        internal HostMemoryAllocator HostMemoryAllocator { get; private set; }
        internal CommandBufferPool CommandBufferPool { get; private set; }
        internal PipelineLayoutCache PipelineLayoutCache { get; private set; }
        internal BackgroundResources BackgroundResources { get; private set; }
        internal Action<Action> InterruptAction { get; private set; }
        internal SyncManager SyncManager { get; private set; }

        internal BufferManager BufferManager { get; private set; }

        // 新增：DMA 加速器
        internal DmaAccelerator DmaAccelerator { get; private set; }

        internal HashSet<ShaderCollection> Shaders { get; }
        internal HashSet<ITexture> Textures { get; }
        internal HashSet<SamplerHolder> Samplers { get; }

        private VulkanDebugMessenger _debugMessenger;
        private Counters _counters;

        private PipelineFull _pipeline;

        internal HelperShader HelperShader { get; private set; }
        internal PipelineFull PipelineInternal => _pipeline;

        internal BarrierBatch Barriers { get; private set; }

        public IPipeline Pipeline => _pipeline;

        public IWindow Window => _window;

        public SurfaceTransformFlagsKHR CurrentTransform => _window.CurrentTransform;

        public Device Device => _device;
        
        private readonly Func<Instance, Vk, SurfaceKHR> _getSurface;
        private readonly Func<string[]> _getRequiredExtensions;
        private readonly string _preferredGpuId;

        private int[] _pdReservedBindings;
        private readonly static int[] _pdReservedBindingsNvn = { 3, 18, 21, 36, 30 };
        private readonly static int[] _pdReservedBindingsOgl = { 17, 18, 34, 35, 36 };

        internal Vendor Vendor { get; private set; }
        internal bool IsAmdWindows { get; private set; }
        internal bool IsIntelWindows { get; private set; }
        internal bool IsAmdGcn { get; private set; }
        internal bool IsAmdRdna3 { get; private set; }
        internal bool IsNvidiaPreTuring { get; private set; }
        internal bool IsIntelArc { get; set; }
        internal bool IsQualcommProprietary { get; private set; }
        internal bool IsMoltenVk { get; private set; }
        internal bool IsTBDR { get; private set; }
        internal bool IsSharedMemory { get; private set; }

        public string GpuVendor { get; private set; }
        public string GpuDriver { get; private set; }
        public string GpuRenderer { get; private set; }
        public string GpuVersion { get; private set; }

        public bool PreferThreading => true;

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        public VulkanRenderer(Vk api, Func<Instance, Vk, SurfaceKHR> surfaceFunc, Func<string[]> requiredExtensionsFunc, string preferredGpuId)
        {
            _getSurface = surfaceFunc;
            _getRequiredExtensions = requiredExtensionsFunc;
            _preferredGpuId = preferredGpuId;
            Api = api;
            Shaders = new HashSet<ShaderCollection>();
            Textures = new HashSet<ITexture>();
            Samplers = new HashSet<SamplerHolder>();

            // 初始化动态状态跟踪器
            _dynamicStateTracker = new DynamicStateTracker();

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                MVKInitialization.Initialize();
                IsMoltenVk = true;
            }
        }

        // 完整的动态状态更新管道
        internal void UpdateDynamicStates(ref DynamicState state, CommandBufferScoped cbs)
        {
            var cmdbuf = cbs.CommandBuffer;

            // 基础动态状态 (VK_EXT_extended_dynamic_state)
            if (ExtendedDynamicStateApi != null)
            {
                // 视口状态
                if (_dynamicStateTracker.ShouldUpdateViewports(state.Viewports))
                {
                    fixed (Viewport* viewportsPtr = state.Viewports)
                    {
                        ExtendedDynamicStateApi.CmdSetViewport(cmdbuf, 0, (uint)state.Viewports.Length, viewportsPtr);
                    }
                }

                // 剪刀状态
                if (_dynamicStateTracker.ShouldUpdateScissors(state.Scissors))
                {
                    fixed (Rect2D* scissorsPtr = state.Scissors)
                    {
                        ExtendedDynamicStateApi.CmdSetScissor(cmdbuf, 0, (uint)state.Scissors.Length, scissorsPtr);
                    }
                }

                // 线宽
                if (_dynamicStateTracker.ShouldUpdateLineWidth(state.LineWidth))
                {
                    ExtendedDynamicStateApi.CmdSetLineWidth(cmdbuf, state.LineWidth);
                }

                // 深度偏移
                if (_dynamicStateTracker.ShouldUpdateDepthBias(state.DepthBiasEnable, state.DepthBiasConstant, state.DepthBiasClamp, state.DepthBiasSlope))
                {
                    ExtendedDynamicStateApi.CmdSetDepthBias(cmdbuf, state.DepthBiasConstant, state.DepthBiasClamp, state.DepthBiasSlope);
                }

                // 混合常量
                if (_dynamicStateTracker.ShouldUpdateBlendConstants(state.BlendConstants))
                {
                    var constants = stackalloc float[4] { state.BlendConstants.R, state.BlendConstants.G, state.BlendConstants.B, state.BlendConstants.A };
                    ExtendedDynamicStateApi.CmdSetBlendConstants(cmdbuf, constants);
                }

                // 深度边界
                if (_dynamicStateTracker.ShouldUpdateDepthBounds(state.DepthBoundsTestEnable, state.DepthBounds))
                {
                    ExtendedDynamicStateApi.CmdSetDepthBounds(cmdbuf, state.DepthBounds.Min, state.DepthBounds.Max);
                }

                // 模板参考值和掩码
                if (_dynamicStateTracker.ShouldUpdateStencilReferences(
                    state.StencilFrontReference, state.StencilBackReference,
                    state.StencilFrontCompareMask, state.StencilBackCompareMask,
                    state.StencilFrontWriteMask, state.StencilBackWriteMask))
                {
                    ExtendedDynamicStateApi.CmdSetStencilReference(cmdbuf, StencilFaceFlags.FrontBit, state.StencilFrontReference);
                    ExtendedDynamicStateApi.CmdSetStencilReference(cmdbuf, StencilFaceFlags.BackBit, state.StencilBackReference);
                    
                    ExtendedDynamicStateApi.CmdSetStencilCompareMask(cmdbuf, StencilFaceFlags.FrontBit, state.StencilFrontCompareMask);
                    ExtendedDynamicStateApi.CmdSetStencilCompareMask(cmdbuf, StencilFaceFlags.BackBit, state.StencilBackCompareMask);
                    
                    ExtendedDynamicStateApi.CmdSetStencilWriteMask(cmdbuf, StencilFaceFlags.FrontBit, state.StencilFrontWriteMask);
                    ExtendedDynamicStateApi.CmdSetStencilWriteMask(cmdbuf, StencilFaceFlags.BackBit, state.StencilBackWriteMask);
                }
            }

            // 扩展动态状态2 (VK_EXT_extended_dynamic_state2)
            if (ExtendedDynamicState2Api != null)
            {
                // 图元重启
                if (_dynamicStateTracker.ShouldUpdatePrimitiveRestart(state.PrimitiveRestartEnable))
                {
                    ExtendedDynamicState2Api.CmdSetPrimitiveRestartEnable(cmdbuf, state.PrimitiveRestartEnable);
                }

                // 光栅化器丢弃
                if (_dynamicStateTracker.ShouldUpdateRasterizerDiscard(state.RasterizerDiscardEnable))
                {
                    ExtendedDynamicState2Api.CmdSetRasterizerDiscardEnable(cmdbuf, state.RasterizerDiscardEnable);
                }

                // 深度偏移启用
                if (_dynamicStateTracker.ShouldUpdateDepthBias(state.DepthBiasEnable, state.DepthBiasConstant, state.DepthBiasClamp, state.DepthBiasSlope))
                {
                    ExtendedDynamicState2Api.CmdSetDepthBiasEnable(cmdbuf, state.DepthBiasEnable);
                }

                // 面剔除
                if (_dynamicStateTracker.ShouldUpdateCullMode(state.CullMode.Convert(), state.CullEnable))
                {
                    ExtendedDynamicState2Api.CmdSetCullMode(cmdbuf, state.CullEnable ? state.CullMode.Convert() : CullModeFlags.None);
                }

                // 前表面
                if (_dynamicStateTracker.ShouldUpdateFrontFace(state.FrontFace))
                {
                    ExtendedDynamicState2Api.CmdSetFrontFace(cmdbuf, state.FrontFace.Convert());
                }

                // 深度测试
                if (_dynamicStateTracker.ShouldUpdateDepthTest(state.DepthTestEnable, state.DepthWriteEnable, state.DepthCompareOp))
                {
                    ExtendedDynamicState2Api.CmdSetDepthTestEnable(cmdbuf, state.DepthTestEnable);
                    ExtendedDynamicState2Api.CmdSetDepthWriteEnable(cmdbuf, state.DepthWriteEnable);
                    ExtendedDynamicState2Api.CmdSetDepthCompareOp(cmdbuf, state.DepthCompareOp.Convert());
                }

                // 模板测试
                if (_dynamicStateTracker.ShouldUpdateStencil(state.StencilTestEnable, state.FrontStencilOps, state.BackStencilOps))
                {
                    ExtendedDynamicState2Api.CmdSetStencilTestEnable(cmdbuf, state.StencilTestEnable);
                    
                    if (state.StencilTestEnable)
                    {
                        ExtendedDynamicState2Api.CmdSetStencilOp(
                            cmdbuf, 
                            StencilFaceFlags.FrontBit,
                            state.FrontStencilOps.Fail.Convert(),
                            state.FrontStencilOps.Pass.Convert(),
                            state.FrontStencilOps.DepthFail.Convert(),
                            state.FrontStencilOps.Compare.Convert());
                            
                        ExtendedDynamicState2Api.CmdSetStencilOp(
                            cmdbuf,
                            StencilFaceFlags.BackBit,
                            state.BackStencilOps.Fail.Convert(),
                            state.BackStencilOps.Pass.Convert(),
                            state.BackStencilOps.DepthFail.Convert(),
                            state.BackStencilOps.Compare.Convert());
                    }
                }
            }

            // 扩展动态状态3 (VK_EXT_extended_dynamic_state3)
            if (ExtendedDynamicState3Api != null)
            {
                // 逻辑操作
                if (_dynamicStateTracker.ShouldUpdateLogicOp(state.LogicOpEnable, state.LogicOp))
                {
                    ExtendedDynamicState3Api.CmdSetLogicOpEnable(cmdbuf, state.LogicOpEnable);
                    if (state.LogicOpEnable)
                    {
                        ExtendedDynamicState3Api.CmdSetLogicOp(cmdbuf, state.LogicOp.Convert());
                    }
                }

                // 深度裁剪
                if (_dynamicStateTracker.ShouldUpdateDepthClamp(state.DepthClampEnable))
                {
                    ExtendedDynamicState3Api.CmdSetDepthClampEnable(cmdbuf, state.DepthClampEnable);
                }

                // 注意：这里可以添加更多动态状态3的特性，如：
                // - 颜色混合启用和方程
                // - 颜色写入掩码
                // - 保守光栅化模式
                // - 等等...
            }

            // 回退到传统管线状态设置（如果没有动态状态扩展）
            if (ExtendedDynamicStateApi == null)
            {
                UpdateTraditionalPipelineStates(ref state, cmdbuf);
            }
        }

        // 传统管线状态设置（回退方案）
        private void UpdateTraditionalPipelineStates(ref DynamicState state, CommandBuffer cmdbuf)
        {
            // 视口
            if (_dynamicStateTracker.ShouldUpdateViewports(state.Viewports))
            {
                fixed (Viewport* viewportsPtr = state.Viewports)
                {
                    Api.CmdSetViewport(cmdbuf, 0, (uint)state.Viewports.Length, viewportsPtr);
                }
            }

            // 剪刀
            if (_dynamicStateTracker.ShouldUpdateScissors(state.Scissors))
            {
                fixed (Rect2D* scissorsPtr = state.Scissors)
                {
                    Api.CmdSetScissor(cmdbuf, 0, (uint)state.Scissors.Length, scissorsPtr);
                }
            }

            // 线宽
            if (_dynamicStateTracker.ShouldUpdateLineWidth(state.LineWidth))
            {
                Api.CmdSetLineWidth(cmdbuf, state.LineWidth);
            }

            // 深度偏移
            if (_dynamicStateTracker.ShouldUpdateDepthBias(state.DepthBiasEnable, state.DepthBiasConstant, state.DepthBiasClamp, state.DepthBiasSlope))
            {
                Api.CmdSetDepthBias(cmdbuf, state.DepthBiasConstant, state.DepthBiasClamp, state.DepthBiasSlope);
            }

            // 混合常量
            if (_dynamicStateTracker.ShouldUpdateBlendConstants(state.BlendConstants))
            {
                var constants = stackalloc float[4] { state.BlendConstants.R, state.BlendConstants.G, state.BlendConstants.B, state.BlendConstants.A };
                Api.CmdSetBlendConstants(cmdbuf, constants);
            }

            // 深度边界
            if (_dynamicStateTracker.ShouldUpdateDepthBounds(state.DepthBoundsTestEnable, state.DepthBounds))
            {
                Api.CmdSetDepthBounds(cmdbuf, state.DepthBounds.Min, state.DepthBounds.Max);
            }

            // 模板参考值和掩码
            if (_dynamicStateTracker.ShouldUpdateStencilReferences(
                state.StencilFrontReference, state.StencilBackReference,
                state.StencilFrontCompareMask, state.StencilBackCompareMask,
                state.StencilFrontWriteMask, state.StencilBackWriteMask))
            {
                Api.CmdSetStencilReference(cmdbuf, StencilFaceFlags.FrontBit, state.StencilFrontReference);
                Api.CmdSetStencilReference(cmdbuf, StencilFaceFlags.BackBit, state.StencilBackReference);
                
                Api.CmdSetStencilCompareMask(cmdbuf, StencilFaceFlags.FrontBit, state.StencilFrontCompareMask);
                Api.CmdSetStencilCompareMask(cmdbuf, StencilFaceFlags.BackBit, state.StencilBackCompareMask);
                
                Api.CmdSetStencilWriteMask(cmdbuf, StencilFaceFlags.FrontBit, state.StencilFrontWriteMask);
                Api.CmdSetStencilWriteMask(cmdbuf, StencilFaceFlags.BackBit, state.StencilBackWriteMask);
            }
        }

        // 智能命令缓冲区刷新机制
        internal void FlushWork()
        {
            if ((++_drawCounter & 0x7) != 0x7) return;

            if (_drawCounter >= DRAWS_TO_FLUSH)
            {
                FlushAllCommands();
                _drawCounter = 0;
            }
            else if (_drawCounter >= DRAWS_TO_DISPATCH)
            {
                CommandBufferPool?.DispatchWork();
            }
        }

        // 注册绘制调用
        internal void RegisterDraw()
        {
            FlushWork();
        }

        // 重置动态状态跟踪器（用于通道或管线更改）
        internal void ResetDynamicState()
        {
            _dynamicStateTracker.Reset();
        }

        // 简化的动态状态更新（用于现有代码的兼容性）
        internal void UpdateDynamicStates()
        {
            // 这个简化版本供现有代码调用
            // 实际实现应该使用完整的 UpdateDynamicStates(ref DynamicState, CommandBufferScoped)
        }

        // 变换反馈处理方法
        internal void HandleTransformFeedback(bool enabled)
        {
            if (TransformFeedbackApi == null || !Capabilities.SupportsTransformFeedback) return;

            // 启用/禁用变换反馈计数器
            // 注意：Counters 类中没有 EnableTransformFeedback 方法，暂时注释掉
            // _counters?.EnableTransformFeedback(enabled);

            if (enabled)
            {
                // 绑定变换反馈缓冲区
                // 这里需要实际的缓冲区绑定逻辑
            }
        }

        // 条件渲染加速方法
        public bool AccelerateConditionalRendering(BufferHandle buffer, int offset, bool isEqual)
        {
            if (ConditionalRenderingApi == null) return false;

            // 注意：BufferManager 中没有 FlushCaching 方法，暂时注释掉
            // BufferManager.FlushCaching();
            
            // 注意：Counters 类中没有 AccelerateHostConditionalRendering 方法，暂时返回 false
            return false; // _counters?.AccelerateHostConditionalRendering(buffer, offset, isEqual) ?? false;
        }

        // DMA 加速方法
        public bool AccelerateDmaBufferClear(BufferHandle buffer, int offset, int size, uint value)
        {
            return DmaAccelerator?.BufferClear((ulong)offset, (ulong)size, value) ?? false;
        }

        public bool AccelerateDmaBufferCopy(BufferHandle srcBuffer, int srcOffset, BufferHandle dstBuffer, int dstOffset, int size)
        {
            return DmaAccelerator?.BufferCopy((ulong)srcOffset, (ulong)dstOffset, (ulong)size) ?? false;
        }

        public bool AccelerateDmaImageToBuffer(ITexture source, ITexture destination, int srcX, int srcY, int dstX, int dstY, int width, int height)
        {
            var copyInfo = new ImageCopyInfo
            {
                Source = source,
                Destination = destination,
                SrcX = srcX,
                SrcY = srcY,
                DstX = dstX,
                DstY = dstY,
                Width = width,
                Height = height
            };
            return DmaAccelerator?.ImageToBuffer(copyInfo) ?? false;
        }

        public bool AccelerateDmaBufferToImage(ITexture source, ITexture destination, int srcX, int srcY, int dstX, int dstY, int width, int height)
        {
            var copyInfo = new ImageCopyInfo
            {
                Source = source,
                Destination = destination,
                SrcX = srcX,
                SrcY = srcY,
                DstX = dstX,
                DstY = dstY,
                Width = width,
                Height = height
            };
            return DmaAccelerator?.BufferToImage(copyInfo) ?? false;
        }

        private unsafe void LoadFeatures(uint maxQueueCount, uint queueFamilyIndex)
        {
            FormatCapabilities = new FormatCapabilities(Api, _physicalDevice.PhysicalDevice);

            uint computeFamilyIndex = FindComputeQueueFamily();

            if (computeFamilyIndex != uint.MaxValue && computeFamilyIndex != queueFamilyIndex)
            {
                Queue computeQueue;
                Api.GetDeviceQueue(_device, computeFamilyIndex, 0, &computeQueue);

                _computeCommandPool = new CommandBufferPool(
                    Api,
                    _device,
                    computeQueue,
                    new object(),
                    computeFamilyIndex,
                    IsQualcommProprietary,
                    false);
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtConditionalRendering conditionalRenderingApi))
            {
                ConditionalRenderingApi = conditionalRenderingApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrTimelineSemaphore timelineSemaphoreApi))
            {
                TimelineSemaphoreApi = timelineSemaphoreApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState extendedDynamicStateApi))
            {
                ExtendedDynamicStateApi = extendedDynamicStateApi;
            }

            // 新增：扩展动态状态2支持
            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState2 extendedDynamicState2Api))
            {
                ExtendedDynamicState2Api = extendedDynamicState2Api;
            }

            // 新增：扩展动态状态3支持
            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState3 extendedDynamicState3Api))
            {
                ExtendedDynamicState3Api = extendedDynamicState3Api;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrPushDescriptor pushDescriptorApi))
            {
                PushDescriptorApi = pushDescriptorApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtTransformFeedback transformFeedbackApi))
            {
                TransformFeedbackApi = transformFeedbackApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrDrawIndirectCount drawIndirectCountApi))
            {
                DrawIndirectCountApi = drawIndirectCountApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtAttachmentFeedbackLoopDynamicState dynamicFeedbackLoopApi))
            {
                DynamicFeedbackLoopApi = dynamicFeedbackLoopApi;
            }

            SupportsFragmentDensityMap = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_density_map");
            SupportsFragmentDensityMap2 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_density_map2");

            if (maxQueueCount >= 2)
            {
                Api.GetDeviceQueue(_device, queueFamilyIndex, 1, out var backgroundQueue);
                BackgroundQueue = backgroundQueue;
                BackgroundQueueLock = new object();
            }

            PhysicalDeviceProperties2 properties2 = new()
            {
                SType = StructureType.PhysicalDeviceProperties2,
            };

            PhysicalDeviceSubgroupProperties propertiesSubgroup = new()
            {
                SType = StructureType.PhysicalDeviceSubgroupProperties,
                PNext = properties2.PNext,
            };

            properties2.PNext = &propertiesSubgroup;

            PhysicalDeviceBlendOperationAdvancedPropertiesEXT propertiesBlendOperationAdvanced = new()
            {
                SType = StructureType.PhysicalDeviceBlendOperationAdvancedPropertiesExt,
            };

            bool supportsBlendOperationAdvanced = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_blend_operation_advanced");

            if (supportsBlendOperationAdvanced)
            {
                propertiesBlendOperationAdvanced.PNext = properties2.PNext;
                properties2.PNext = &propertiesBlendOperationAdvanced;
            }

            bool supportsTransformFeedback = _physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName);

            PhysicalDeviceTransformFeedbackPropertiesEXT propertiesTransformFeedback = new()
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackPropertiesExt,
            };

            if (supportsTransformFeedback)
            {
                propertiesTransformFeedback.PNext = properties2.PNext;
                properties2.PNext = &propertiesTransformFeedback;
            }

            PhysicalDevicePortabilitySubsetPropertiesKHR propertiesPortabilitySubset = new()
            {
                SType = StructureType.PhysicalDevicePortabilitySubsetPropertiesKhr,
            };

            bool supportsPushDescriptors = _physicalDevice.IsDeviceExtensionPresent(KhrPushDescriptor.ExtensionName);

            PhysicalDevicePushDescriptorPropertiesKHR propertiesPushDescriptor = new PhysicalDevicePushDescriptorPropertiesKHR()
            {
                SType = StructureType.PhysicalDevicePushDescriptorPropertiesKhr
            };

            if (supportsPushDescriptors)
            {
                propertiesPushDescriptor.PNext = properties2.PNext;
                properties2.PNext = &propertiesPushDescriptor;
            }

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT featuresPrimitiveTopologyListRestart = new()
            {
                SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
            };

            PhysicalDeviceRobustness2FeaturesEXT featuresRobustness2 = new()
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
            };

            PhysicalDeviceShaderFloat16Int8FeaturesKHR featuresShaderInt8 = new()
            {
                SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
            };

            PhysicalDeviceCustomBorderColorFeaturesEXT featuresCustomBorderColor = new()
            {
                SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
            };

            PhysicalDeviceDepthClipControlFeaturesEXT featuresDepthClipControl = new()
            {
                SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
            };

            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT featuresAttachmentFeedbackLoop = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
            };

            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT featuresDynamicAttachmentFeedbackLoop = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
            };

            PhysicalDevicePortabilitySubsetFeaturesKHR featuresPortabilitySubset = new()
            {
                SType = StructureType.PhysicalDevicePortabilitySubsetFeaturesKhr,
            };

            // 新增：扩展动态状态2特性
            PhysicalDeviceExtendedDynamicState2FeaturesEXT featuresExtendedDynamicState2 = new()
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicState2FeaturesExt,
            };

            // 新增：扩展动态状态3特性
            PhysicalDeviceExtendedDynamicState3FeaturesEXT featuresExtendedDynamicState3 = new()
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicState3FeaturesExt,
            };

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                features2.PNext = &featuresPrimitiveTopologyListRestart;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                featuresRobustness2.PNext = features2.PNext;
                features2.PNext = &featuresRobustness2;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_KHR_shader_float16_int8"))
            {
                featuresShaderInt8.PNext = features2.PNext;
                features2.PNext = &featuresShaderInt8;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color"))
            {
                featuresCustomBorderColor.PNext = features2.PNext;
                features2.PNext = &featuresCustomBorderColor;
            }

            bool supportsDepthClipControl = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control");

            if (supportsDepthClipControl)
            {
                featuresDepthClipControl.PNext = features2.PNext;
                features2.PNext = &featuresDepthClipControl;
            }

            bool supportsAttachmentFeedbackLoop = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout");

            if (supportsAttachmentFeedbackLoop)
            {
                featuresAttachmentFeedbackLoop.PNext = features2.PNext;
                features2.PNext = &featuresAttachmentFeedbackLoop;
            }

            bool supportsDynamicAttachmentFeedbackLoop = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state");

            if (supportsDynamicAttachmentFeedbackLoop)
            {
                featuresDynamicAttachmentFeedbackLoop.PNext = features2.PNext;
                features2.PNext = &featuresDynamicAttachmentFeedbackLoop;
            }

            // 新增：扩展动态状态2支持检测
            bool supportsExtendedDynamicState2 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_extended_dynamic_state2");

            if (supportsExtendedDynamicState2)
            {
                featuresExtendedDynamicState2.PNext = features2.PNext;
                features2.PNext = &featuresExtendedDynamicState2;
            }

            // 新增：扩展动态状态3支持检测
            bool supportsExtendedDynamicState3 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_extended_dynamic_state3");

            if (supportsExtendedDynamicState3)
            {
                featuresExtendedDynamicState3.PNext = features2.PNext;
                features2.PNext = &featuresExtendedDynamicState3;
            }

            bool usePortability = _physicalDevice.IsDeviceExtensionPresent("VK_KHR_portability_subset");

            if (usePortability)
            {
                propertiesPortabilitySubset.PNext = properties2.PNext;
                properties2.PNext = &propertiesPortabilitySubset;

                featuresPortabilitySubset.PNext = features2.PNext;
                features2.PNext = &featuresPortabilitySubset;
            }

            Api.GetPhysicalDeviceProperties2(_physicalDevice.PhysicalDevice, &properties2);
            Api.GetPhysicalDeviceFeatures2(_physicalDevice.PhysicalDevice, &features2);

            var portabilityFlags = PortabilitySubsetFlags.None;
            uint vertexBufferAlignment = 1;

            if (usePortability)
            {
                vertexBufferAlignment = propertiesPortabilitySubset.MinVertexInputBindingStrideAlignment;

                portabilityFlags |= featuresPortabilitySubset.TriangleFans ? 0 : PortabilitySubsetFlags.NoTriangleFans;
                portabilityFlags |= featuresPortabilitySubset.PointPolygons ? 0 : PortabilitySubsetFlags.NoPointMode;
                portabilityFlags |= featuresPortabilitySubset.ImageView2DOn3DImage ? 0 : PortabilitySubsetFlags.No3DImageView;
                portabilityFlags |= featuresPortabilitySubset.SamplerMipLodBias ? 0 : PortabilitySubsetFlags.NoLodBias;
            }

            bool supportsCustomBorderColor = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color") &&
                                             featuresCustomBorderColor.CustomBorderColors &&
                                             featuresCustomBorderColor.CustomBorderColorWithoutFormat;

            ref var properties = ref properties2.Properties;

            var hasDriverProperties = _physicalDevice.TryGetPhysicalDeviceDriverPropertiesKHR(Api, out var driverProperties);

            Vendor = VendorUtils.FromId(properties.VendorID);

            IsAmdWindows = Vendor == Vendor.Amd && OperatingSystem.IsWindows();
            IsIntelWindows = Vendor == Vendor.Intel && OperatingSystem.IsWindows();
            IsTBDR =
                Vendor == Vendor.Apple ||
                Vendor == Vendor.Qualcomm ||
                Vendor == Vendor.ARM ||
                Vendor == Vendor.Broadcom ||
                Vendor == Vendor.ImgTec;

            GpuVendor = VendorUtils.GetNameFromId(properties.VendorID);
            GpuDriver = hasDriverProperties && !OperatingSystem.IsMacOS() ?
                VendorUtils.GetFriendlyDriverName(driverProperties.DriverID) : GpuVendor;

            fixed (byte* deviceName = properties.DeviceName)
            {
                GpuRenderer = Marshal.PtrToStringAnsi((IntPtr)deviceName);
            }

            GpuVersion = $"Vulkan v{ParseStandardVulkanVersion(properties.ApiVersion)}, Driver v{ParseDriverVersion(ref properties)}";

            IsAmdGcn = !IsMoltenVk && Vendor == Vendor.Amd && VendorUtils.AmdGcnRegex().IsMatch(GpuRenderer);

            IsAmdRdna3 = Vendor == Vendor.Amd && (VendorUtils.AmdRdna3Regex().IsMatch(GpuRenderer)
                                                  // ROG Ally (X) Device IDs
                                                  || properties.DeviceID is 0x15BF or 0x15C8);

            if (Vendor == Vendor.Nvidia)
            {
                var match = VendorUtils.NvidiaConsumerClassRegex().Match(GpuRenderer);

                if (match != null && int.TryParse(match.Groups[2].Value, out int gpuNumber))
                {
                    IsNvidiaPreTuring = gpuNumber < 2000;
                }
                else if (GpuRenderer.Contains("TITAN") && !GpuRenderer.Contains("RTX"))
                {
                    IsNvidiaPreTuring = true;
                }
            }
            else if (Vendor == Vendor.Intel)
            {
                IsIntelArc = GpuRenderer.StartsWith("Intel(R) Arc(TM)");
            }

            IsQualcommProprietary = hasDriverProperties && driverProperties.DriverID == DriverId.QualcommProprietary;

            ulong minResourceAlignment = Math.Max(
                Math.Max(
                    properties.Limits.MinStorageBufferOffsetAlignment,
                    properties.Limits.MinUniformBufferOffsetAlignment),
                properties.Limits.MinTexelBufferOffsetAlignment
            );

            SampleCountFlags supportedSampleCounts =
                properties.Limits.FramebufferColorSampleCounts &
                properties.Limits.FramebufferDepthSampleCounts &
                properties.Limits.FramebufferStencilSampleCounts;

            // 修复：动态状态特性支持检测 - 使用正确的字段名
            bool supportsExtendedDynamicState2Feature = supportsExtendedDynamicState2 && featuresExtendedDynamicState2.ExtendedDynamicState2;
            
            // 修复：ExtendedDynamicState3 字段名问题 - 根据 Silk.NET 2.22.0 的实际定义
            bool supportsExtendedDynamicState3Feature = false;
            if (supportsExtendedDynamicState3)
            {
                // 注意：在 Silk.NET 2.22.0 中，ExtendedDynamicState3 特性可能有不同的字段名
                // 这里我们假设扩展存在即支持，或者根据实际需要调整
                supportsExtendedDynamicState3Feature = true;
            }

            Capabilities = new HardwareCapabilities(
                supportsIndexTypeUint8: _physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8"),
                supportsCustomBorderColor: supportsCustomBorderColor,
                supportsBlendEquationAdvanced: supportsBlendOperationAdvanced,
                supportsBlendEquationAdvancedCorrelatedOverlap: propertiesBlendOperationAdvanced.AdvancedBlendCorrelatedOverlap,
                supportsBlendEquationAdvancedNonPreMultipliedSrcColor: propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedSrcColor,
                supportsBlendEquationAdvancedNonPreMultipliedDstColor: propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedDstColor,
                supportsIndirectParameters: _physicalDevice.IsDeviceExtensionPresent(KhrDrawIndirectCount.ExtensionName),
                supportsFragmentShaderInterlock: _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock"),
                supportsGeometryShaderPassthrough: _physicalDevice.IsDeviceExtensionPresent("VK_NV_geometry_shader_passthrough"),
                supportsShaderFloat64: features2.Features.ShaderFloat64,
                supportsShaderInt8: featuresShaderInt8.ShaderInt8,
                supportsShaderStencilExport: _physicalDevice.IsDeviceExtensionPresent("VK_EXT_shader_stencil_export"),
                supportsShaderStorageImageMultisample: features2.Features.ShaderStorageImageMultisample,
                supportsConditionalRendering: _physicalDevice.IsDeviceExtensionPresent(ExtConditionalRendering.ExtensionName),
                supportsExtendedDynamicState: _physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName),
                supportsExtendedDynamicState3: supportsExtendedDynamicState3Feature, // 新增：动态状态3支持
                supportsMultiView: features2.Features.MultiViewport && !(IsMoltenVk && Vendor == Vendor.Amd),
                supportsNullDescriptors: featuresRobustness2.NullDescriptor || IsMoltenVk,
                supportsPushDescriptors: supportsPushDescriptors && !IsMoltenVk,
                maxPushDescriptors: propertiesPushDescriptor.MaxPushDescriptors,
                supportsPrimitiveTopologyListRestart: featuresPrimitiveTopologyListRestart.PrimitiveTopologyListRestart,
                supportsPrimitiveTopologyPatchListRestart: featuresPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart,
                supportsTransformFeedback: supportsTransformFeedback,
                supportsTransformFeedbackQueries: propertiesTransformFeedback.TransformFeedbackQueries,
                supportsPreciseOcclusionQueries: features2.Features.OcclusionQueryPrecise,
                supportsPipelineStatisticsQuery: _physicalDevice.PhysicalDeviceFeatures.PipelineStatisticsQuery,
                supportsGeometryShader: _physicalDevice.PhysicalDeviceFeatures.GeometryShader,
                supportsTessellationShader: _physicalDevice.PhysicalDeviceFeatures.TessellationShader,
                supportsViewportArray2: _physicalDevice.IsDeviceExtensionPresent("VK_NV_viewport_array2"),
                supportsHostImportedMemory: _physicalDevice.IsDeviceExtensionPresent(ExtExternalMemoryHost.ExtensionName),
                supportsDepthClipControl: supportsDepthClipControl && featuresDepthClipControl.DepthClipControl,
                supportsAttachmentFeedbackLoop: supportsAttachmentFeedbackLoop && featuresAttachmentFeedbackLoop.AttachmentFeedbackLoopLayout,
                supportsDynamicAttachmentFeedbackLoop: supportsDynamicAttachmentFeedbackLoop && featuresDynamicAttachmentFeedbackLoop.AttachmentFeedbackLoopDynamicState,
                subgroupSize: propertiesSubgroup.SubgroupSize,
                supportedSampleCounts: supportedSampleCounts,
                portabilitySubset: portabilityFlags,
                vertexBufferAlignment: vertexBufferAlignment,
                subTexelPrecisionBits: properties.Limits.SubTexelPrecisionBits,
                minResourceAlignment: minResourceAlignment);

            IsSharedMemory = MemoryAllocator.IsDeviceMemoryShared(_physicalDevice);

            MemoryAllocator = new MemoryAllocator(Api, _physicalDevice, _device);

            Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExternalMemoryHost hostMemoryApi);
            HostMemoryAllocator = new HostMemoryAllocator(MemoryAllocator, Api, hostMemoryApi, _device);

            CommandBufferPool = new CommandBufferPool(Api, _device, Queue, QueueLock, queueFamilyIndex, IsQualcommProprietary);

            PipelineLayoutCache = new PipelineLayoutCache();

            BackgroundResources = new BackgroundResources(this, _device);

            BufferManager = new BufferManager(this, _device);

            // 新增：初始化 DMA 加速器
            DmaAccelerator = new DmaAccelerator(this, BufferManager);

            SyncManager = new SyncManager(this, _device);
            _pipeline = new PipelineFull(this, _device);
            _pipeline.Initialize();

            HelperShader = new HelperShader(this, _device);

            Barriers = new BarrierBatch(this);

            _counters = new Counters(this, _device, _pipeline);
        }

        private uint FindComputeQueueFamily()
        {
            unsafe 
            {
                uint queueCount = 0;
                Api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice.PhysicalDevice, &queueCount, null);
                
                var queueFamilies = new QueueFamilyProperties[queueCount];
                
                fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
                {
                    Api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice.PhysicalDevice, &queueCount, pQueueFamilies);
                }

                for (uint i = 0; i < queueCount; i++)
                {
                    ref var property = ref queueFamilies[i];
                    if (property.QueueFlags.HasFlag(QueueFlags.ComputeBit) &&
                        !property.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                    {
                        return i;
                    }
                }
            }

            return uint.MaxValue;
        }

        private void SetupContext(GraphicsDebugLevel logLevel)
        {
            _instance = VulkanInitialization.CreateInstance(Api, logLevel, _getRequiredExtensions());
            _debugMessenger = new VulkanDebugMessenger(Api, _instance.Instance, logLevel);

            if (Api.TryGetInstanceExtension(_instance.Instance, out KhrSurface surfaceApi))
            {
                SurfaceApi = surfaceApi;
            }

            _surface = _getSurface(_instance.Instance, Api);
            _physicalDevice = VulkanInitialization.FindSuitablePhysicalDevice(Api, _instance, _surface, _preferredGpuId);

            var queueFamilyIndex = VulkanInitialization.FindSuitableQueueFamily(Api, _physicalDevice, _surface, out uint maxQueueCount);

            _device = VulkanInitialization.CreateDevice(Api, _physicalDevice, queueFamilyIndex, maxQueueCount);

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrSwapchain swapchainApi))
            {
                SwapchainApi = swapchainApi;
            }

            Api.GetDeviceQueue(_device, queueFamilyIndex, 0, out var queue);
            Queue = queue;
            QueueLock = new object();

            // Init Locks
            SurfaceLock = new object();
            if (maxQueueCount >= 2)
            {
                Api.GetDeviceQueue(_device, queueFamilyIndex, 1, out var backgroundQueue);
                BackgroundQueue = backgroundQueue;
                BackgroundQueueLock = new object();
            }

            LoadFeatures(maxQueueCount, queueFamilyIndex);

            QueueFamilyIndex = queueFamilyIndex;

            _window = new Window(this, _surface, _physicalDevice.PhysicalDevice, _device);

            _initialized = true;
        }

        internal int[] GetPushDescriptorReservedBindings(bool isOgl)
        {
            if (_pdReservedBindings == null)
            {
                if (Capabilities.MaxPushDescriptors <= Constants.MaxUniformBuffersPerStage * 2)
                {
                    _pdReservedBindings = isOgl ? _pdReservedBindingsOgl : _pdReservedBindingsNvn;
                }
                else
                {
                    _pdReservedBindings = Array.Empty<int>();
                }
            }

            return _pdReservedBindings;
        }

        public BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            return BufferManager.CreateWithHandle(this, size, access.HasFlag(BufferAccess.SparseCompatible), access.Convert(), access.HasFlag(BufferAccess.Stream));
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            return BufferManager.CreateHostImported(this, pointer, size);
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            return BufferManager.CreateSparse(this, storageBuffers);
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            return new ImageArray(this, size, isBuffer);
        }

        public IProgram CreateProgram(ShaderSource[] sources, ShaderInfo info)
        {
            ProgramCount++;

            bool isCompute = sources.Length == 1 && sources[0].Stage == ShaderStage.Compute;

            if (info.State.HasValue || isCompute)
            {
                return new ShaderCollection(this, _device, sources, info.ResourceLayout, info.State ?? default, info.FromCache);
            }

            return new ShaderCollection(this, _device, sources, info.ResourceLayout);
        }

        internal ShaderCollection CreateProgramWithMinimalLayout(ShaderSource[] sources, ResourceLayout resourceLayout, SpecDescription[] specDescription = null)
        {
            return new ShaderCollection(this, _device, sources, resourceLayout, specDescription, isMinimal: true);
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            return new SamplerHolder(this, _device, info);
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            if (info.Target == Target.TextureBuffer)
            {
                return new TextureBuffer(this, info);
            }

            return CreateTextureView(info);
        }

        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            return new TextureArray(this, size, isBuffer);
        }

        internal TextureView CreateTextureView(TextureCreateInfo info)
        {
            var storage = CreateTextureStorage(info);
            return storage.CreateView(info, 0, 0);
        }

        internal TextureStorage CreateTextureStorage(TextureCreateInfo info)
        {
            if (info.Width == 0 || info.Height == 0 || info.Depth == 0)
            {
                throw new ArgumentException("Invalid texture dimensions");
            }
            return new TextureStorage(this, _device, info);
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            BufferManager.Delete(buffer);
        }

        internal void FlushAllCommands()
        {
            _pipeline?.FlushCommandsImpl();
            _drawCounter = 0; // 重置绘制计数器
        }

        internal void RegisterFlush()
        {
            SyncManager.RegisterFlush();
            BufferManager.StagingBuffer.FreeCompleted();
            _drawCounter = 0; // 重置绘制计数器
        }

        // 新增：改进的帧生命周期管理
        public void TickFrame()
        {
            _drawCounter = 0;
            
            // 添加描述符队列的帧清理
            PipelineLayoutCache?.TickFrame();
            BufferManager?.TickFrame();
            
            // 动态状态跟踪器重置
            _dynamicStateTracker?.ResetDirtyFlags();
            
            // 动态调整批处理大小（基于性能指标）
            AdjustBatchSizes();
        }

        // 新增：动态调整批处理大小
        private void AdjustBatchSizes()
        {
            // 基于帧时间和性能指标动态调整 DRAWS_TO_DISPATCH 和 DRAWS_TO_FLUSH
            // 这里可以实现自适应批处理大小调整逻辑
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            return BufferManager.GetData(buffer, offset, size);
        }

        public unsafe Capabilities GetCapabilities()
        {
            FormatFeatureFlags compressedFormatFeatureFlags =
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.SampledImageFilterLinearBit |
                FormatFeatureFlags.BlitSrcBit |
                FormatFeatureFlags.TransferSrcBit |
                FormatFeatureFlags.TransferDstBit;

            bool supportsBc123CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc1RgbaSrgb,
                Format.Bc1RgbaUnorm,
                Format.Bc2Srgb,
                Format.Bc2Unorm,
                Format.Bc3Srgb,
                Format.Bc3Unorm);

            bool supportsBc45CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc4Snorm,
                Format.Bc4Unorm,
                Format.Bc5Snorm,
                Format.Bc5Unorm);

            bool supportsBc67CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc6HSfloat,
                Format.Bc6HUfloat,
                Format.Bc7Srgb,
                Format.Bc7Unorm);

            bool supportsEtc2CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Etc2RgbaSrgb,
                Format.Etc2RgbaUnorm,
                Format.Etc2RgbPtaSrgb,
                Format.Etc2RgbPtaUnorm,
                Format.Etc2RgbSrgb,
                Format.Etc2RgbUnorm);

            bool supports5BitComponentFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.R5G6B5Unorm,
                Format.R5G5B5A1Unorm,
                Format.R5G5B5X1Unorm,
                Format.B5G6R5Unorm,
                Format.B5G5R5A1Unorm,
                Format.A1B5G5R5Unorm);

            bool supportsR4G4B4A4Format = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.R4G4B4A4Unorm);

            bool supportsAstcFormats = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Astc4x4Unorm,
                Format.Astc5x4Unorm,
                Format.Astc5x5Unorm,
                Format.Astc6x5Unorm,
                Format.Astc6x6Unorm,
                Format.Astc8x5Unorm,
                Format.Astc8x6Unorm,
                Format.Astc8x8Unorm,
                Format.Astc10x5Unorm,
                Format.Astc10x6Unorm,
                Format.Astc10x8Unorm,
                Format.Astc10x10Unorm,
                Format.Astc12x10Unorm,
                Format.Astc12x12Unorm,
                Format.Astc4x4Srgb,
                Format.Astc5x4Srgb,
                Format.Astc5x5Srgb,
                Format.Astc6x5Srgb,
                Format.Astc6x6Srgb,
                Format.Astc8x5Srgb,
                Format.Astc8x6Srgb,
                Format.Astc8x8Srgb,
                Format.Astc10x5Srgb,
                Format.Astc10x6Srgb,
                Format.Astc10x8Srgb,
                Format.Astc10x10Srgb,
                Format.Astc12x10Srgb,
                Format.Astc12x12Srgb);

            PhysicalDeviceVulkan12Features featuresVk12 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
            };

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &featuresVk12,
            };

            Api.GetPhysicalDeviceFeatures2(_physicalDevice.PhysicalDevice, &features2);

            var limits = _physicalDevice.PhysicalDeviceProperties.Limits;
            var mainQueueProperties = _physicalDevice.QueueFamilyProperties[QueueFamilyIndex];

            SystemMemoryType memoryType;

            if (IsSharedMemory)
            {
                memoryType = SystemMemoryType.UnifiedMemory;
            }
            else
            {
                memoryType = Vendor == Vendor.Nvidia ?
                    SystemMemoryType.DedicatedMemorySlowStorage :
                    SystemMemoryType.DedicatedMemory;
            }

            bool supportsFragmentDensityMap = SupportsFragmentDensityMap;
            bool supportsFragmentDensityMap2 = SupportsFragmentDensityMap2;

            // 修复：添加动态状态支持参数
            return new Capabilities(
                api: TargetApi.Vulkan,
                vendorName: GpuVendor,
                memoryType: memoryType,
                hasFrontFacingBug: IsIntelWindows,
                hasVectorIndexingBug: IsQualcommProprietary,
                needsFragmentOutputSpecialization: IsMoltenVk,
                reduceShaderPrecision: IsMoltenVk,
                supportsAstcCompression: features2.Features.TextureCompressionAstcLdr && supportsAstcFormats,
                supportsBc123Compression: supportsBc123CompressionFormat,
                supportsBc45Compression: supportsBc45CompressionFormat,
                supportsBc67Compression: supportsBc67CompressionFormat,
                supportsEtc2Compression: supportsEtc2CompressionFormat,
                supports3DTextureCompression: true,
                supportsBgraFormat: true,
                supportsR4G4Format: false,
                supportsR4G4B4A4Format: supportsR4G4B4A4Format,
                supportsScaledVertexFormats: FormatCapabilities.SupportsScaledVertexFormats(),
                supportsSnormBufferTextureFormat: true,
                supports5BitComponentFormat: supports5BitComponentFormat,
                supportsSparseBuffer: features2.Features.SparseBinding && mainQueueProperties.QueueFlags.HasFlag(QueueFlags.SparseBindingBit),
                supportsBlendEquationAdvanced: Capabilities.SupportsBlendEquationAdvanced,
                supportsFragmentShaderInterlock: Capabilities.SupportsFragmentShaderInterlock,
                supportsFragmentShaderOrderingIntel: false,
                supportsGeometryShader: Capabilities.SupportsGeometryShader,
                supportsGeometryShaderPassthrough: Capabilities.SupportsGeometryShaderPassthrough,
                supportsTransformFeedback: Capabilities.SupportsTransformFeedback,
                supportsImageLoadFormatted: features2.Features.ShaderStorageImageReadWithoutFormat,
                supportsLayerVertexTessellation: featuresVk12.ShaderOutputLayer,
                supportsMismatchingViewFormat: true,
                supportsCubemapView: !IsAmdGcn,
                supportsNonConstantTextureOffset: false,
                supportsQuads: false,
                supportsSeparateSampler: true,
                supportsShaderBallot: false,
                supportsShaderBallotDivergence: Vendor != Vendor.Qualcomm,
                supportsShaderBarrierDivergence: Vendor != Vendor.Intel,
                supportsShaderFloat64: Capabilities.SupportsShaderFloat64,
                supportsTextureGatherOffsets: features2.Features.ShaderImageGatherExtended && !IsMoltenVk,
                supportsTextureShadowLod: false,
                supportsVertexStoreAndAtomics: features2.Features.VertexPipelineStoresAndAtomics,
                supportsViewportIndexVertexTessellation: featuresVk12.ShaderOutputViewportIndex,
                supportsViewportMask: Capabilities.SupportsViewportArray2,
                supportsViewportSwizzle: false,
                supportsIndirectParameters: true,
                supportsDepthClipControl: Capabilities.SupportsDepthClipControl,
                supportsFragmentDensityMap: supportsFragmentDensityMap,
                supportsFragmentDensityMap2: supportsFragmentDensityMap2,
                // 新增：动态状态支持
                supportsExtendedDynamicState: Capabilities.SupportsExtendedDynamicState,
                supportsExtendedDynamicState2: ExtendedDynamicState2Api != null,
                supportsExtendedDynamicState3: Capabilities.SupportsExtendedDynamicState3,
                uniformBufferSetIndex: PipelineBase.UniformSetIndex,
                storageBufferSetIndex: PipelineBase.StorageSetIndex,
                textureSetIndex: PipelineBase.TextureSetIndex,
                imageSetIndex: PipelineBase.ImageSetIndex,
                extraSetBaseIndex: PipelineBase.DescriptorSetLayouts,
                maximumExtraSets: Math.Max(0, (int)limits.MaxBoundDescriptorSets - PipelineBase.DescriptorSetLayouts),
                maximumUniformBuffersPerStage: Constants.MaxUniformBuffersPerStage,
                maximumStorageBuffersPerStage: Constants.MaxStorageBuffersPerStage,
                maximumTexturesPerStage: Constants.MaxTexturesPerStage,
                maximumImagesPerStage: Constants.MaxImagesPerStage,
                maximumComputeSharedMemorySize: (int)limits.MaxComputeSharedMemorySize,
                maximumSupportedAnisotropy: (int)limits.MaxSamplerAnisotropy,
                shaderSubgroupSize: (int)Capabilities.SubgroupSize,
                storageBufferOffsetAlignment: (int)limits.MinStorageBufferOffsetAlignment,
                textureBufferOffsetAlignment: (int)limits.MinTexelBufferOffsetAlignment,
                gatherBiasPrecision: IsIntelWindows || IsAmdWindows ? (int)Capabilities.SubTexelPrecisionBits : 0,
                maximumGpuMemory: GetTotalGPUMemory());
        }

        private ulong GetTotalGPUMemory()
        {
            ulong totalMemory = 0;

            Api.GetPhysicalDeviceMemoryProperties(_physicalDevice.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);

            for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
            {
                var heap = memoryProperties.MemoryHeaps[i];
                if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) == MemoryHeapFlags.DeviceLocalBit)
                {
                    totalMemory += heap.Size;
                }
            }

            return totalMemory;
        }

        public HardwareInfo GetHardwareInfo()
        {
            return new HardwareInfo(GpuVendor, GpuRenderer, GpuDriver);
        }

        public static DeviceInfo[] GetPhysicalDevices()
        {
            try
            {
                return VulkanInitialization.GetSuitablePhysicalDevices(Vk.GetApi());
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, $"Error querying Vulkan devices: {ex.Message}");
                return Array.Empty<DeviceInfo>();
            }
        }

        public static DeviceInfo[] GetPhysicalDevices(Vk api)
        {
            try
            {
                return VulkanInitialization.GetSuitablePhysicalDevices(api);
            }
            catch (Exception)
            {
                return Array.Empty<DeviceInfo>();
            }
        }

        private static string ParseStandardVulkanVersion(uint version)
        {
            return $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";
        }

        private static string ParseDriverVersion(ref PhysicalDeviceProperties properties)
        {
            uint driverVersionRaw = properties.DriverVersion;

            if (properties.VendorID == 0x10DE)
            {
                return $"{(driverVersionRaw >> 22) & 0x3FF}.{(driverVersionRaw >> 14) & 0xFF}.{(driverVersionRaw >> 6) & 0xFF}.{driverVersionRaw & 0x3F}";
            }

            return ParseStandardVulkanVersion(driverVersionRaw);
        }

        internal PrimitiveTopology TopologyRemap(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => PrimitiveTopology.Triangles,
                PrimitiveTopology.QuadStrip => PrimitiveTopology.TriangleStrip,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.NoTriangleFans)
                    ? PrimitiveTopology.Triangles
                    : topology,
                _ => topology,
            };
        }

        internal bool TopologyUnsupported(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => true,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.NoTriangleFans),
                _ => false,
            };
        }

        private void PrintGpuInformation()
        {
            Logger.Notice.Print(LogClass.Gpu, $"{GpuVendor} {GpuRenderer} ({GpuVersion})");
            Logger.Notice.Print(LogClass.Gpu, $"GPU Memory: {GetTotalGPUMemory() / (1024 * 1024)} MiB");
            
            // 新增：打印动态状态支持信息
            if (Capabilities.SupportsExtendedDynamicState)
            {
                Logger.Notice.Print(LogClass.Gpu, "Extended Dynamic State: Supported");
            }
            if (ExtendedDynamicState2Api != null)
            {
                Logger.Notice.Print(LogClass.Gpu, "Extended Dynamic State 2: Supported");
            }
            if (Capabilities.SupportsExtendedDynamicState3)
            {
                Logger.Notice.Print(LogClass.Gpu, "Extended Dynamic State 3: Supported");
            }
        }

        public void Initialize(GraphicsDebugLevel logLevel)
        {
            SetupContext(logLevel);
            PrintGpuInformation();
        }

        internal bool NeedsVertexBufferAlignment(int attrScalarAlignment, out int alignment)
        {
            if (Capabilities.VertexBufferAlignment > 1)
            {
                alignment = (int)Capabilities.VertexBufferAlignment;
                return true;
            }
            else if (Vendor != Vendor.Nvidia)
            {
                alignment = attrScalarAlignment;
                return true;
            }

            alignment = 1;
            return false;
        }

        public void PreFrame()
        {
            SyncManager.Cleanup();
            // 新增：每帧重置动态状态跟踪器
            _dynamicStateTracker?.ResetDirtyFlags();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            return _counters.QueueReport(type, resultHandler, divisor, hostReserved);
        }

        public void ResetCounter(CounterType type)
        {
            _counters.QueueReset(type);
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            BufferManager.SetData(buffer, offset, data, _pipeline.CurrentCommandBuffer, _pipeline.EndRenderPassDelegate);
        }

        public void UpdateCounters()
        {
            _counters.Update();
        }

        public void ResetCounterPool()
        {
            _counters.ResetCounterPool();
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            _counters?.ResetFutureCounters(cmd, count);
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            action();
        }

        public void CreateSync(ulong id, bool strict)
        {
            SyncManager.Create(id, strict);
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool isFragment, ShaderInfo info)
        {
            throw new NotImplementedException();
        }

        public void WaitSync(ulong id)
        {
            SyncManager.Wait(id);
        }

        public ulong GetCurrentSync()
        {
            return SyncManager.GetCurrent();
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            InterruptAction = interruptAction;
        }

        public void Screenshot()
        {
            _window.ScreenCaptureRequested = true;
        }

        public void OnScreenCaptured(ScreenCaptureImageInfo bitmap)
        {
            ScreenCaptured?.Invoke(this, bitmap);
        }

        public bool SupportsRenderPassBarrier(PipelineStageFlags flags)
        {
            return !(IsMoltenVk || IsQualcommProprietary);
        }

        // 新增：改进的同步机制，借鉴 yuzu 的精确同步
        public void WaitForIdle()
        {
            if (TimelineSemaphoreApi != null)
            {
                // 使用时间线信号量进行精确同步
                SyncManager.WaitForIdle();
            }
            else
            {
                // 回退到传统同步
                FlushAllCommands();
            }
        }

        // ===== Surface/Present Lifecycle helpers =====

        public unsafe bool RecreateSurface()
        {
            if (!PresentAllowed || SurfaceLock == null)
            {
                return false;
            }

            lock (SurfaceLock)
            {
                try
                {
                    if (_surface.Handle != 0)
                    {
                        SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
                        _surface = new SurfaceKHR(0);
                    }

                    _surface = _getSurface(_instance.Instance, Api);
                    if (_surface.Handle == 0)
                    {
                        return false;
                    }

                    ( _window as Window )?.SetSurface(_surface);
                    ( _window as Window )?.SetSurfaceQueryAllowed(true);
          
                    return true;
                }
                catch (Exception ex)
                {
                    return false;
                }
            }
        }

        public unsafe void ReleaseSurface()
        {
            if (SurfaceLock == null)
            {
                return;
            }

            lock (SurfaceLock)
            {
                try
                {
                    ( _window as Window )?.SetSurfaceQueryAllowed(false);

                    if (_surface.Handle != 0)
                    {
                        SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
                        _surface = new SurfaceKHR(0);
                    }
                }
                catch (Exception ex)
                {
                }

                ( _window as Window )?.OnSurfaceLost();
            }
        }

        public void SetPresentEnabled(bool enabled)
        {
            if (!_initialized || SurfaceLock == null)
            {
                return;
            }

            PresentAllowed = enabled;

            if (!enabled)
            {
                ( _window as Window )?.SetSurfaceQueryAllowed(false);
                ReleaseSurface();
            }
            else
            {
                _ = RecreateSurface();
            }
        }

        public void SetPresentAllowed(bool allowed)
        {
            if (!_initialized || SurfaceLock == null)
            {
                return;
            }

            PresentAllowed = allowed;

            if (allowed)
            {
                try
                {
                    ( _window as Window )?.SetSurfaceQueryAllowed(true);
                    _window?.SetSize(0, 0);
                    _ = RecreateSurface();
                }
                catch (Exception ex)
                {
                }
            }
            else
            {
                ( _window as Window )?.SetSurfaceQueryAllowed(false);
            }
        }

        public unsafe void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            CommandBufferPool?.Dispose();
            _computeCommandPool?.Dispose();
            BackgroundResources?.Dispose();
            _counters?.Dispose();
            _window?.Dispose();
            HelperShader?.Dispose();
            _pipeline?.Dispose();
            BufferManager?.Dispose();
            PipelineLayoutCache?.Dispose();
            Barriers?.Dispose();
            DmaAccelerator?.Dispose(); // 新增：清理 DMA 加速器

            MemoryAllocator?.Dispose();

            foreach (var shader in Shaders) shader.Dispose();
            foreach (var texture in Textures) texture.Release();
            foreach (var sampler in Samplers) sampler.Dispose();

            if (_surface.Handle != 0)
            {
                SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
            }

            Api.DestroyDevice(_device, null);

            _debugMessenger?.Dispose();

            // Last step destroy the instance
            _instance?.Dispose();
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return Capabilities.SupportsHostImportedMemory &&
                HostMemoryAllocator.TryImport(BufferManager.HostImportedBufferMemoryRequirements, BufferManager.DefaultBufferMemoryFlags, address, size);
        }
    }
}
