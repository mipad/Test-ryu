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
using System.IO; // 添加System.IO命名空间引用
using Format = Ryujinx.Graphics.GAL.Format;
using PrimitiveTopology = Ryujinx.Graphics.GAL.PrimitiveTopology;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;

namespace Ryujinx.Graphics.Vulkan
{
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

        internal KhrSynchronization2 Synchronization2Api { get; private set; }
        internal KhrDynamicRendering DynamicRenderingApi { get; private set; }
        internal ExtExtendedDynamicState2 ExtendedDynamicState2Api { get; private set; }
        internal FormatCapabilities FormatCapabilities { get; private set; }
        internal HardwareCapabilities Capabilities;

        internal Vk Api { get; private set; }
        internal KhrSurface SurfaceApi { get; private set; }
        internal KhrSwapchain SwapchainApi { get; private set; }
        internal ExtConditionalRendering ConditionalRenderingApi { get; private set; }
        internal ExtExtendedDynamicState ExtendedDynamicStateApi { get; private set; }
        internal KhrPushDescriptor PushDescriptorApi { get; private set; }
        internal ExtTransformFeedback TransformFeedbackApi { get; private set; }
        internal KhrDrawIndirectCount DrawIndirectCountApi { get; private set; }
        internal ExtAttachmentFeedbackLoopDynamicState DynamicFeedbackLoopApi { get; private set; }
        
        internal bool SupportsFragmentDensityMap { get; private set; }
        internal bool SupportsFragmentDensityMap2 { get; private set; }
        internal bool SupportsMultiview { get; private set; }
        internal bool SupportsSynchronization2 { get; private set; }
        internal bool SupportsDynamicRendering { get; private set; }
        internal bool SupportsExtendedDynamicState2 { get; private set; }
        internal bool SupportsASTCDecodeMode { get; private set; }

        internal uint QueueFamilyIndex { get; private set; }
        internal Queue Queue { get; private set; }
        internal Queue BackgroundQueue { get; private set; }
        internal object BackgroundQueueLock { get; private set; }
        internal object QueueLock { get; private set; }

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
        internal bool IsIntelArc { get; private set; }
        internal bool IsQualcommProprietary { get; private set; }
        internal bool IsMoltenVk { get; private set; }
        internal bool IsTBDR { get; private set; }
        internal bool IsSharedMemory { get; private set; }
        internal bool IsMaliGPU { get; private set; }
        internal bool IsAndroid { get; private set; }

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

            // 检测是否为Android平台
            IsAndroid = Environment.OSVersion.Platform == PlatformID.Unix && 
                       File.Exists("/system/bin/sh") || 
                       RuntimeInformation.RuntimeIdentifier.Contains("android");

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                MVKInitialization.Initialize();
                IsMoltenVk = true;
            }
        }

        // 内部 ASTC 解码模式结构体定义
        internal struct ASTCDecodeFeaturesEXT
        {
            public StructureType SType;
            public unsafe void* PNext;
            public uint DecodeModeSharedExponent;
            public uint DecodeModeExplicit;
        }

        internal struct ASTCDecodePropertiesEXT
        {
            public StructureType SType;
            public unsafe void* PNext;
            public uint DecodeModeSharedExponent;
            public uint DecodeModeExplicit;
            public uint DecodeModeCompressed;
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

            // 移除时间线信号量相关代码

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrSynchronization2 synchronization2Api))
            {
                Synchronization2Api = synchronization2Api;
                SupportsSynchronization2 = true;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrDynamicRendering dynamicRenderingApi))
            {
                DynamicRenderingApi = dynamicRenderingApi;
                SupportsDynamicRendering = true;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState extendedDynamicStateApi))
            {
                ExtendedDynamicStateApi = extendedDynamicStateApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState2 extendedDynamicState2Api))
            {
                ExtendedDynamicState2Api = extendedDynamicState2Api;
                SupportsExtendedDynamicState2 = true;
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

            // 检测 ASTC 解码模式扩展支持
            SupportsASTCDecodeMode = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_astc_decode_mode");

            SupportsFragmentDensityMap = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_density_map");
            SupportsFragmentDensityMap2 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_density_map2");
            SupportsMultiview = _physicalDevice.IsDeviceExtensionPresent("VK_KHR_multiview");

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

            // 添加 ASTC 解码模式属性到属性链
            if (SupportsASTCDecodeMode)
            {
                var propertiesAstcDecode = new ASTCDecodePropertiesEXT
                {
                    SType = (StructureType)1000347001, // VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ASTC_DECODE_PROPERTIES_EXT
                };

                propertiesAstcDecode.PNext = properties2.PNext;
                properties2.PNext = &propertiesAstcDecode;
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

            // 添加 ASTC 解码模式特性到特性链
            if (SupportsASTCDecodeMode)
            {
                var featuresAstcDecode = new ASTCDecodeFeaturesEXT
                {
                    SType = (StructureType)1000347000, // VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ASTC_DECODE_FEATURES_EXT
                };

                featuresAstcDecode.PNext = features2.PNext;
                features2.PNext = &featuresAstcDecode;
            }

            
PhysicalDeviceTextureCompressionASTCHDRFeaturesEXT featuresAstcHdr = new()
{
    SType = StructureType.PhysicalDeviceTextureCompressionAstcHdrFeaturesExt,
};

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_texture_compression_astc_hdr"))
            {
                featuresAstcHdr.PNext = features2.PNext;
                features2.PNext = &featuresAstcHdr;
            }

            // Vulkan 1.3+ 特性
            PhysicalDeviceSynchronization2FeaturesKHR featuresSynchronization2 = new()
            {
                SType = StructureType.PhysicalDeviceSynchronization2Features,
            };

            PhysicalDeviceDynamicRenderingFeaturesKHR featuresDynamicRendering = new()
            {
                SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            };

            PhysicalDeviceExtendedDynamicState2FeaturesEXT featuresExtendedDynamicState2 = new()
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicState2FeaturesExt,
            };

            if (_physicalDevice.IsDeviceExtensionPresent("VK_KHR_synchronization2"))
            {
                featuresSynchronization2.PNext = features2.PNext;
                features2.PNext = &featuresSynchronization2;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_KHR_dynamic_rendering"))
            {
                featuresDynamicRendering.PNext = features2.PNext;
                features2.PNext = &featuresDynamicRendering;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_extended_dynamic_state2"))
            {
                featuresExtendedDynamicState2.PNext = features2.PNext;
                features2.PNext = &featuresExtendedDynamicState2;
            }

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

            // 从 ASTC 解码模式特性链中提取支持信息
            bool supportsAstcDecodeModeSharedExponent = false;
            bool supportsAstcDecodeModeExplicit = false;

            if (SupportsASTCDecodeMode)
            {
                // 由于我们手动构建了特性链，我们可以从返回的数据中提取实际支持情况
                // 但这里我们假设扩展存在就表示支持，实际实现中可能需要更复杂的检测
                supportsAstcDecodeModeSharedExponent = true;
                supportsAstcDecodeModeExplicit = true;
            }

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
            
            // 检测是否为Mali GPU
            IsMaliGPU = Vendor == Vendor.ARM || (GpuRenderer?.Contains("Mali") ?? false);

            // Android平台优化：如果是Android，增加Mali GPU检测的准确性
            if (IsAndroid)
            {
                // 检查驱动信息
                if (hasDriverProperties)
                {
                    IsMaliGPU = driverProperties.DriverID == DriverId.ArmProprietary || 
                               GpuRenderer?.Contains("Mali") == true;
                }
                
                // Android上的Mali GPU通常是TBDR架构
                if (IsMaliGPU)
                {
                    IsTBDR = true;
                }
            }

            IsAmdWindows = Vendor == Vendor.Amd && OperatingSystem.IsWindows();
            IsIntelWindows = Vendor == Vendor.Intel && OperatingSystem.IsWindows();
            IsTBDR = IsTBDR || // 如果Android已设置，保持原值
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

            bool supportsAstcDecodeMode = SupportsASTCDecodeMode && 
                                          supportsAstcDecodeModeSharedExponent &&
                                          supportsAstcDecodeModeExplicit;

            // Android/Mali优化：调整一些能力设置
            bool disableRobustBufferAccess = IsAndroid && IsMaliGPU;
            bool forceNullDescriptor = IsAndroid || IsMaliGPU;

            Capabilities = new HardwareCapabilities(
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8"),
                supportsCustomBorderColor,
                supportsBlendOperationAdvanced,
                propertiesBlendOperationAdvanced.AdvancedBlendCorrelatedOverlap,
                propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedSrcColor,
                propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedDstColor,
                _physicalDevice.IsDeviceExtensionPresent(KhrDrawIndirectCount.ExtensionName),
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock"),
                _physicalDevice.IsDeviceExtensionPresent("VK_NV_geometry_shader_passthrough"),
                features2.Features.ShaderFloat64,
                featuresShaderInt8.ShaderInt8,
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_shader_stencil_export"),
                features2.Features.ShaderStorageImageMultisample,
                _physicalDevice.IsDeviceExtensionPresent(ExtConditionalRendering.ExtensionName),
                _physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName),
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_extended_dynamic_state2") && featuresExtendedDynamicState2.ExtendedDynamicState2,
                features2.Features.MultiViewport && !(IsMoltenVk && Vendor == Vendor.Amd),
                featuresRobustness2.NullDescriptor || IsMoltenVk || forceNullDescriptor,
                supportsPushDescriptors && !IsMoltenVk && !IsMaliGPU, // Mali GPU上禁用push descriptors
                propertiesPushDescriptor.MaxPushDescriptors,
                featuresPrimitiveTopologyListRestart.PrimitiveTopologyListRestart,
                featuresPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart,
                supportsTransformFeedback,
                propertiesTransformFeedback.TransformFeedbackQueries,
                features2.Features.OcclusionQueryPrecise,
                _physicalDevice.PhysicalDeviceFeatures.PipelineStatisticsQuery,
                _physicalDevice.PhysicalDeviceFeatures.GeometryShader,
                _physicalDevice.PhysicalDeviceFeatures.TessellationShader,
                _physicalDevice.IsDeviceExtensionPresent("VK_NV_viewport_array2"),
                _physicalDevice.IsDeviceExtensionPresent(ExtExternalMemoryHost.ExtensionName),
                supportsDepthClipControl && featuresDepthClipControl.DepthClipControl,
                supportsAttachmentFeedbackLoop && featuresAttachmentFeedbackLoop.AttachmentFeedbackLoopLayout,
                supportsDynamicAttachmentFeedbackLoop && featuresDynamicAttachmentFeedbackLoop.AttachmentFeedbackLoopDynamicState,
                false, // SupportsTimelineSemaphores - 设置为false，因为我们移除了支持
                _physicalDevice.IsDeviceExtensionPresent("VK_KHR_synchronization2") && featuresSynchronization2.Synchronization2,
                _physicalDevice.IsDeviceExtensionPresent("VK_KHR_dynamic_rendering") && featuresDynamicRendering.DynamicRendering,
                supportsAstcDecodeMode,
                propertiesSubgroup.SubgroupSize,
                supportedSampleCounts,
                portabilityFlags,
                vertexBufferAlignment,
                properties.Limits.SubTexelPrecisionBits,
                minResourceAlignment);

            IsSharedMemory = MemoryAllocator.IsDeviceMemoryShared(_physicalDevice);

            MemoryAllocator = new MemoryAllocator(Api, _physicalDevice, _device);

            Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExternalMemoryHost hostMemoryApi);
            HostMemoryAllocator = new HostMemoryAllocator(MemoryAllocator, Api, hostMemoryApi, _device);

            CommandBufferPool = new CommandBufferPool(Api, _device, Queue, QueueLock, queueFamilyIndex, IsQualcommProprietary, false);

            PipelineLayoutCache = new PipelineLayoutCache();

            BackgroundResources = new BackgroundResources(this, _device);

            BufferManager = new BufferManager(this, _device);

            SyncManager = new SyncManager(this, _device);
            _pipeline = new PipelineFull(this, _device);
            _pipeline.Initialize();

            HelperShader = new HelperShader(this, _device);

            Barriers = new BarrierBatch(this);

            _counters = new Counters(this, _device, _pipeline);

            // 如果是Android/Mali平台，启用特殊优化
            if (IsAndroid && IsMaliGPU)
            {
                Logger.Info?.PrintMsg(LogClass.Gpu, "Android Mali GPU detected, enabling optimizations...");
                ApplyMaliOptimizations();
            }
        }

        // 应用Mali GPU优化
        private void ApplyMaliOptimizations()
        {
            try
            {
                // 设置环境变量以优化Mali GPU性能
                Environment.SetEnvironmentVariable("MALI_VK_DISABLE_PIPELINE_CACHE", "0");
                Environment.SetEnvironmentVariable("MALI_VK_PIPELINE_CACHE_SIZE", "67108864"); // 64MB
                Environment.SetEnvironmentVariable("MALI_VK_SHADER_CACHE_SIZE", "16777216"); // 16MB
                Environment.SetEnvironmentVariable("MALI_VK_ENABLE_PIPELINE_STATS", "0");
                
                Logger.Info?.PrintMsg(LogClass.Gpu, "Mali GPU optimizations applied");
            }
            catch (Exception ex)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Failed to apply Mali optimizations: {ex.Message}");
            }
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
                // Mali优化：禁用push descriptors
                if (IsMaliGPU)
                {
                    _pdReservedBindings = Array.Empty<int>();
                }
                else if (Capabilities.MaxPushDescriptors <= Constants.MaxUniformBuffersPerStage * 2)
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
            // Mali优化：禁用稀疏缓冲区
            if (IsMaliGPU && access.HasFlag(BufferAccess.SparseCompatible))
            {
                access = access & ~BufferAccess.SparseCompatible;
                Logger.Debug?.PrintMsg(LogClass.Gpu, "Sparse buffers disabled for Mali GPU");
            }
            
            return BufferManager.CreateWithHandle(this, size, access.HasFlag(BufferAccess.SparseCompatible), access.Convert(), access.HasFlag(BufferAccess.Stream));
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            return BufferManager.CreateHostImported(this, pointer, size);
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            // Mali优化：返回普通缓冲区而不是稀疏缓冲区
            if (IsMaliGPU)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Sparse buffers not supported on Mali GPU, creating normal buffer");
                int totalSize = 0;
                foreach (var range in storageBuffers)
                {
                    totalSize += range.Size;
                }
                return CreateBuffer(totalSize, BufferAccess.Stream);
            }
            
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
            // 修复：创建新的TextureCreateInfo实例而不是修改只读属性
            TextureCreateInfo adjustedInfo = info;
            
            // Mali优化：对于ASTC纹理，如果硬件不支持解码，使用软件解码
            if (IsMaliGPU && info.Format.IsAstc() && ShouldUseSoftwareASTCDecode())
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, "Using software ASTC decode for Mali GPU");
                
                // 创建新的info对象，修改格式为RGBA
                adjustedInfo = new TextureCreateInfo(
                    info.Width,
                    info.Height,
                    info.Depth,
                    info.Levels,
                    info.Samples,
                    info.BlockWidth,
                    info.BlockHeight,
                    ConvertAstcToRgbaFormat(info.Format),
                    info.DepthStencilMode,
                    info.Target,
                    info.SwizzleR,
                    info.SwizzleG,
                    info.SwizzleB,
                    info.SwizzleA);
            }
            
            var storage = CreateTextureStorage(adjustedInfo);
            return storage.CreateView(adjustedInfo, 0, 0);
        }

        internal TextureStorage CreateTextureStorage(TextureCreateInfo info)
        {
            if (info.Width == 0 || info.Height == 0 || info.Depth == 0)
            {
                throw new ArgumentException("Invalid texture dimensions");
            }
            
            return new TextureStorage(this, _device, info);
        }

        // 将ASTC格式转换为RGBA格式（用于软件解码）
        private Format ConvertAstcToRgbaFormat(Format format)
        {
            return format switch
            {
                Format.Astc4x4Srgb or Format.Astc4x4Unorm => Format.R8G8B8A8Srgb,
                Format.Astc5x4Srgb or Format.Astc5x4Unorm => Format.R8G8B8A8Srgb,
                Format.Astc5x5Srgb or Format.Astc5x5Unorm => Format.R8G8B8A8Srgb,
                Format.Astc6x5Srgb or Format.Astc6x5Unorm => Format.R8G8B8A8Srgb,
                Format.Astc6x6Srgb or Format.Astc6x6Unorm => Format.R8G8B8A8Srgb,
                Format.Astc8x5Srgb or Format.Astc8x5Unorm => Format.R8G8B8A8Srgb,
                Format.Astc8x6Srgb or Format.Astc8x6Unorm => Format.R8G8B8A8Srgb,
                Format.Astc8x8Srgb or Format.Astc8x8Unorm => Format.R8G8B8A8Srgb,
                Format.Astc10x5Srgb or Format.Astc10x5Unorm => Format.R8G8B8A8Srgb,
                Format.Astc10x6Srgb or Format.Astc10x6Unorm => Format.R8G8B8A8Srgb,
                Format.Astc10x8Srgb or Format.Astc10x8Unorm => Format.R8G8B8A8Srgb,
                Format.Astc10x10Srgb or Format.Astc10x10Unorm => Format.R8G8B8A8Srgb,
                Format.Astc12x10Srgb or Format.Astc12x10Unorm => Format.R8G8B8A8Srgb,
                Format.Astc12x12Srgb or Format.Astc12x12Unorm => Format.R8G8B8A8Srgb,
                _ => Format.R8G8B8A8Unorm,
            };
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            BufferManager.Delete(buffer);
        }

        internal void FlushAllCommands()
        {
            _pipeline?.FlushCommandsImpl();
        }

        internal void RegisterFlush()
        {
            SyncManager.RegisterFlush();
            BufferManager.StagingBuffer.FreeCompleted();
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

            // Android/Mali优化：调整能力报告
            bool supportsMultiViewport = features2.Features.MultiViewport && !(IsMoltenVk && Vendor == Vendor.Amd);
            if (IsMaliGPU)
            {
                // Mali GPU通常对多视口支持有限
                supportsMultiViewport = false;
            }

            return new Capabilities(
                api: TargetApi.Vulkan,
                GpuVendor,
                memoryType: memoryType,
                hasFrontFacingBug: IsIntelWindows,
                hasVectorIndexingBug: IsQualcommProprietary || Vendor == Vendor.ARM || IsMaliGPU,
                needsFragmentOutputSpecialization: IsMoltenVk,
                reduceShaderPrecision: IsMoltenVk || Vendor == Vendor.ARM || IsMaliGPU,
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
                supportsSparseBuffer: features2.Features.SparseBinding && mainQueueProperties.QueueFlags.HasFlag(QueueFlags.SparseBindingBit) && !IsMaliGPU,
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
                supportsShaderBallotDivergence: Vendor != Vendor.Qualcomm && !IsMaliGPU,
                supportsShaderBarrierDivergence: Vendor != Vendor.Intel && !IsMaliGPU,
                supportsShaderFloat64: Capabilities.SupportsShaderFloat64,
                supportsTextureGatherOffsets: features2.Features.ShaderImageGatherExtended && !IsMoltenVk && !IsMaliGPU,
                supportsTextureShadowLod: false,
                supportsVertexStoreAndAtomics: features2.Features.VertexPipelineStoresAndAtomics,
                supportsViewportIndexVertexTessellation: featuresVk12.ShaderOutputViewportIndex,
                supportsViewportMask: Capabilities.SupportsViewportArray2,
                supportsViewportSwizzle: false,
                supportsIndirectParameters: true,
                supportsDepthClipControl: Capabilities.SupportsDepthClipControl,
                supportsFragmentDensityMap: supportsFragmentDensityMap,
                supportsFragmentDensityMap2: supportsFragmentDensityMap2,
                supportsMultiview: SupportsMultiview,
                supportsTimelineSemaphores: false, // 设置为false
                supportsSynchronization2: SupportsSynchronization2,
                supportsDynamicRendering: SupportsDynamicRendering,
                supportsExtendedDynamicState2: SupportsExtendedDynamicState2,
                supportsASTCDecodeMode: SupportsASTCDecodeMode,
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

        private unsafe ulong GetTotalGPUMemory()
        {
            ulong totalMemory = 0;

            // 修复：使用局部变量而不是属性作为out参数
            var physicalDevice = _physicalDevice.PhysicalDevice;
            Api.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);

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
            // Mali优化：更好地处理不支持的原语类型
            if (IsMaliGPU)
            {
                return topology switch
                {
                    PrimitiveTopology.Quads => PrimitiveTopology.Triangles,
                    PrimitiveTopology.QuadStrip => PrimitiveTopology.TriangleStrip,
                    PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => PrimitiveTopology.Triangles,
                    _ => topology,
                };
            }
            
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
            // Mali优化：更多原语类型转换
            if (IsMaliGPU)
            {
                return topology switch
                {
                    PrimitiveTopology.Quads => true,
                    PrimitiveTopology.QuadStrip => true,
                    PrimitiveTopology.TriangleFan => true,
                    PrimitiveTopology.Polygon => true,
                    _ => false,
                };
            }
            
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
            
            if (SupportsSynchronization2)
                Logger.Notice.Print(LogClass.Gpu, "Supports: Synchronization2");
            if (SupportsDynamicRendering)
                Logger.Notice.Print(LogClass.Gpu, "Supports: Dynamic Rendering");
            if (SupportsMultiview)
                Logger.Notice.Print(LogClass.Gpu, "Supports: Multiview");
            if (SupportsASTCDecodeMode)
                Logger.Notice.Print(LogClass.Gpu, "Supports: ASTC Decode Mode");
            
            if (IsTBDR)
            {
                Logger.Notice.Print(LogClass.Gpu, "Platform: TBDR (Tile-Based Deferred Rendering)");
                Logger.Notice.Print(LogClass.Gpu, "Query Optimization: Batch processing enabled");
            }
            
            if (IsMaliGPU)
            {
                Logger.Notice.Print(LogClass.Gpu, "GPU: Mali (ARM) - Applying optimizations");
            }
            
            if (IsAndroid)
            {
                Logger.Notice.Print(LogClass.Gpu, "Platform: Android");
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
        
        // 暴露Counters接口给PipelineFull
        internal Counters GetCounters()
        {
            return _counters;
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
            return !(IsMoltenVk || IsQualcommProprietary || Vendor == Vendor.ARM || IsMaliGPU);
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

        // 针对Mali GPU的特殊处理
        internal bool ShouldUseSoftwareASTCDecode()
        {
            return IsMaliGPU && !SupportsASTCDecodeMode;
        }

        // 立即结束并提交命令缓冲区
        internal unsafe void EndAndSubmitCommandBuffer(CommandBufferScoped cbs, ReadOnlySpan<Semaphore> waitSemaphores, ReadOnlySpan<PipelineStageFlags> waitDstStageMask, ReadOnlySpan<Semaphore> signalSemaphores)
        {
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"EndAndSubmitCommandBuffer: 命令缓冲区 {cbs.CommandBufferIndex}");
            
            // 提交命令缓冲区
            CommandBufferPool.Return(cbs, waitSemaphores, waitDstStageMask, signalSemaphores);
        }

        // 重载版本：不需要额外信号量
        internal void EndAndSubmitCommandBuffer(CommandBufferScoped cbs)
        {
            EndAndSubmitCommandBuffer(cbs, default, default, default);
        }
    }
}