using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan
{
    public unsafe static class VulkanInitialization
    {
        private const uint InvalidIndex = uint.MaxValue;
        private static readonly uint _minimalVulkanVersion = Vk.Version11.Value;
        private static readonly uint _minimalInstanceVulkanVersion = Vk.Version12.Value;
        private static readonly uint _maximumVulkanVersion = Vk.Version13.Value;
        private const string AppName = "Ryujinx.Graphics.Vulkan";
        private const int QueuesCount = 2;

        private static readonly string[] _desirableExtensions =
        [
            ExtConditionalRendering.ExtensionName,
            ExtExtendedDynamicState.ExtensionName,
            ExtExtendedDynamicState2.ExtensionName,
            ExtTransformFeedback.ExtensionName,
            KhrDrawIndirectCount.ExtensionName,
            KhrPushDescriptor.ExtensionName,
            KhrTimelineSemaphore.ExtensionName,
            KhrSynchronization2.ExtensionName,
            KhrDynamicRendering.ExtensionName,
            "VK_KHR_multiview", // 使用字符串而不是KhrMultiview类
            ExtExternalMemoryHost.ExtensionName,
            "VK_EXT_blend_operation_advanced",
            "VK_EXT_custom_border_color",
            "VK_EXT_descriptor_indexing",
            "VK_EXT_fragment_shader_interlock",
            "VK_EXT_index_type_uint8",
            "VK_EXT_primitive_topology_list_restart",
            "VK_EXT_robustness2",
            "VK_EXT_shader_stencil_export",
            "VK_KHR_shader_float16_int8",
            "VK_EXT_shader_subgroup_ballot",
            "VK_NV_geometry_shader_passthrough",
            "VK_NV_viewport_array2",
            "VK_EXT_depth_clip_control",
            "VK_KHR_portability_subset",
            "VK_EXT_4444_formats",
            "VK_KHR_8bit_storage",
            "VK_KHR_maintenance2",
            "VK_EXT_attachment_feedback_loop_layout",
            "VK_EXT_attachment_feedback_loop_dynamic_state",
            "VK_KHR_maintenance4",
            "VK_EXT_shader_object",
            "VK_EXT_graphics_pipeline_library"
            // 移除 VK_KHR_copy_commands2 相关代码
        ];

        private static readonly string[] _requiredExtensions =
        [
            KhrSwapchain.ExtensionName,
            "VK_KHR_timeline_semaphore"
        ];

        internal static VulkanInstance CreateInstance(Vk api, GraphicsDebugLevel logLevel, string[] requiredExtensions)
        {
            // ... 保持原有代码不变 ...
            var enabledLayers = new List<string>();

            var instanceExtensions = VulkanInstance.GetInstanceExtensions(api);
            var instanceLayers = VulkanInstance.GetInstanceLayers(api);

            void AddAvailableLayer(string layerName)
            {
                if (instanceLayers.Contains(layerName))
                {
                    enabledLayers.Add(layerName);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Missing layer {layerName}");
                }
            }

            if (logLevel != GraphicsDebugLevel.None)
            {
                AddAvailableLayer("VK_LAYER_KHRONOS_validation");
            }

            var enabledExtensions = requiredExtensions;

            if (instanceExtensions.Contains("VK_EXT_debug_utils"))
            {
                enabledExtensions = enabledExtensions.Append(ExtDebugUtils.ExtensionName).ToArray();
            }

            var appName = Marshal.StringToHGlobalAnsi(AppName);

            var applicationInfo = new ApplicationInfo
            {
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)appName,
                EngineVersion = 1,
                ApiVersion = _maximumVulkanVersion,
            };

            IntPtr* ppEnabledExtensions = stackalloc IntPtr[enabledExtensions.Length];
            IntPtr* ppEnabledLayers = stackalloc IntPtr[enabledLayers.Count];

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                ppEnabledExtensions[i] = Marshal.StringToHGlobalAnsi(enabledExtensions[i]);
            }

            for (int i = 0; i < enabledLayers.Count; i++)
            {
                ppEnabledLayers[i] = Marshal.StringToHGlobalAnsi(enabledLayers[i]);
            }

            var instanceCreateInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                PpEnabledExtensionNames = (byte**)ppEnabledExtensions,
                PpEnabledLayerNames = (byte**)ppEnabledLayers,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                EnabledLayerCount = (uint)enabledLayers.Count,
            };

            Result result = VulkanInstance.Create(api, ref instanceCreateInfo, out var instance);

            Marshal.FreeHGlobal(appName);

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                Marshal.FreeHGlobal(ppEnabledExtensions[i]);
            }

            for (int i = 0; i < enabledLayers.Count; i++)
            {
                Marshal.FreeHGlobal(ppEnabledLayers[i]);
            }

            result.ThrowOnError();

            return instance;
        }

        internal static VulkanPhysicalDevice FindSuitablePhysicalDevice(Vk api, VulkanInstance instance, SurfaceKHR surface, string preferredGpuId)
        {
            // ... 保持原有代码不变 ...
            instance.EnumeratePhysicalDevices(out var physicalDevices).ThrowOnError();

            for (int i = 0; i < physicalDevices.Length; i++)
            {
                if (IsPreferredAndSuitableDevice(api, physicalDevices[i], surface, preferredGpuId))
                {
                    return physicalDevices[i];
                }
            }

            for (int i = 0; i < physicalDevices.Length; i++)
            {
                if (IsSuitableDevice(api, physicalDevices[i], surface))
                {
                    return physicalDevices[i];
                }
            }

            throw new VulkanException("Initialization failed, none of the available GPUs meets the minimum requirements.");
        }

        internal static DeviceInfo[] GetSuitablePhysicalDevices(Vk api)
        {
            // ... 保持原有代码不变 ...
            var appName = Marshal.StringToHGlobalAnsi(AppName);

            var applicationInfo = new ApplicationInfo
            {
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)appName,
                EngineVersion = 1,
                ApiVersion = _maximumVulkanVersion,
            };

            var instanceCreateInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                PpEnabledExtensionNames = null,
                PpEnabledLayerNames = null,
                EnabledExtensionCount = 0,
                EnabledLayerCount = 0,
            };

            Result result = VulkanInstance.Create(api, ref instanceCreateInfo, out var rawInstance);

            Marshal.FreeHGlobal(appName);

            result.ThrowOnError();

            using VulkanInstance instance = rawInstance;

            if (instance.InstanceVersion < _minimalInstanceVulkanVersion)
            {
                return [];
            }

            instance.EnumeratePhysicalDevices(out VulkanPhysicalDevice[] physicalDevices).ThrowOnError();

            List<DeviceInfo> deviceInfos = [];

            foreach (VulkanPhysicalDevice physicalDevice in physicalDevices)
            {
                if (physicalDevice.PhysicalDeviceProperties.ApiVersion < _minimalVulkanVersion)
                {
                    continue;
                }

                deviceInfos.Add(physicalDevice.ToDeviceInfo());
            }

            return deviceInfos.ToArray();
        }

        private static bool IsPreferredAndSuitableDevice(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface, string preferredGpuId)
        {
            if (physicalDevice.Id != preferredGpuId)
            {
                return false;
            }

            return IsSuitableDevice(api, physicalDevice, surface);
        }

        private static bool IsSuitableDevice(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface)
        {
            int extensionMatches = 0;

            foreach (string requiredExtension in _requiredExtensions)
            {
                if (physicalDevice.IsDeviceExtensionPresent(requiredExtension))
                {
                    extensionMatches++;
                }
            }

            return extensionMatches == _requiredExtensions.Length && FindSuitableQueueFamily(api, physicalDevice, surface, out _) != InvalidIndex;
        }

        internal static uint FindSuitableQueueFamily(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface, out uint queueCount)
        {
            const QueueFlags RequiredFlags = QueueFlags.GraphicsBit | QueueFlags.ComputeBit;

            var khrSurface = new KhrSurface(api.Context);

            for (uint index = 0; index < physicalDevice.QueueFamilyProperties.Length; index++)
            {
                ref QueueFamilyProperties property = ref physicalDevice.QueueFamilyProperties[index];

                khrSurface.GetPhysicalDeviceSurfaceSupport(physicalDevice.PhysicalDevice, index, surface, out var surfaceSupported).ThrowOnError();

                if (property.QueueFlags.HasFlag(RequiredFlags) && surfaceSupported)
                {
                    queueCount = property.QueueCount;

                    return index;
                }
            }

            queueCount = 0;

            return InvalidIndex;
        }

        internal static Device CreateDevice(Vk api, VulkanPhysicalDevice physicalDevice, uint queueFamilyIndex, uint queueCount)
        {
            if (queueCount > QueuesCount)
            {
                queueCount = QueuesCount;
            }

            float* queuePriorities = stackalloc float[(int)queueCount];

            for (int i = 0; i < queueCount; i++)
            {
                queuePriorities[i] = 1f;
            }

            var queueCreateInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                QueueCount = queueCount,
                PQueuePriorities = queuePriorities,
            };

            bool useRobustBufferAccess = VendorUtils.FromId(physicalDevice.PhysicalDeviceProperties.VendorID) == Vendor.Nvidia;

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            // 时间线信号量特性
            PhysicalDeviceTimelineSemaphoreFeaturesKHR timelineSemaphoreFeatures = new()
            {
                SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
                PNext = features2.PNext,
                TimelineSemaphore = true
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_timeline_semaphore"))
            {
                features2.PNext = &timelineSemaphoreFeatures;
            }

            // 同步2特性
            PhysicalDeviceSynchronization2FeaturesKHR synchronization2Features = new()
            {
                SType = StructureType.PhysicalDeviceSynchronization2Features,
                PNext = features2.PNext,
                Synchronization2 = true
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_synchronization2"))
            {
                features2.PNext = &synchronization2Features;
            }

            // 动态渲染特性
            PhysicalDeviceDynamicRenderingFeaturesKHR dynamicRenderingFeatures = new()
            {
                SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
                PNext = features2.PNext,
                DynamicRendering = true
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_dynamic_rendering"))
            {
                features2.PNext = &dynamicRenderingFeatures;
            }

            // 扩展动态状态2特性
            PhysicalDeviceExtendedDynamicState2FeaturesEXT extendedDynamicState2Features = new()
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicState2FeaturesExt,
                PNext = features2.PNext,
                ExtendedDynamicState2 = true,
                ExtendedDynamicState2LogicOp = true,
                ExtendedDynamicState2PatchControlPoints = true
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_extended_dynamic_state2"))
            {
                features2.PNext = &extendedDynamicState2Features;
            }

            // 多视图特性
            PhysicalDeviceMultiviewFeatures multiviewFeatures = new()
            {
                SType = StructureType.PhysicalDeviceMultiviewFeatures,
                PNext = features2.PNext,
                Multiview = true,
                MultiviewGeometryShader = true,
                MultiviewTessellationShader = true
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_multiview"))
            {
                features2.PNext = &multiviewFeatures;
            }

            // Vulkan 1.1 特性
            PhysicalDeviceVulkan11Features supportedFeaturesVk11 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan11Features,
                PNext = features2.PNext,
            };

            features2.PNext = &supportedFeaturesVk11;

            // Vulkan 1.2 特性
            PhysicalDeviceVulkan12Features supportedPhysicalDeviceVulkan12Features = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = features2.PNext,
            };

            features2.PNext = &supportedPhysicalDeviceVulkan12Features;

            // Vulkan 1.3 特性
            PhysicalDeviceVulkan13Features supportedPhysicalDeviceVulkan13Features = new()
            {
                SType = StructureType.PhysicalDeviceVulkan13Features,
                PNext = features2.PNext,
                Synchronization2 = true,
                DynamicRendering = true,
                Maintenance4 = true
            };

            if (physicalDevice.PhysicalDeviceProperties.ApiVersion >= Vk.Version13.Value)
            {
                features2.PNext = &supportedPhysicalDeviceVulkan13Features;
            }

            // 自定义边框颜色特性
            PhysicalDeviceCustomBorderColorFeaturesEXT supportedFeaturesCustomBorderColor = new()
            {
                SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color"))
            {
                features2.PNext = &supportedFeaturesCustomBorderColor;
            }

            // 图元拓扑列表重启特性
            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT supportedFeaturesPrimitiveTopologyListRestart = new()
            {
                SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                features2.PNext = &supportedFeaturesPrimitiveTopologyListRestart;
            }

            // 变换反馈特性
            PhysicalDeviceTransformFeedbackFeaturesEXT supportedFeaturesTransformFeedback = new()
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName))
            {
                features2.PNext = &supportedFeaturesTransformFeedback;
            }

            // 鲁棒性2特性
            PhysicalDeviceRobustness2FeaturesEXT supportedFeaturesRobustness2 = new()
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                supportedFeaturesRobustness2.PNext = features2.PNext;
                features2.PNext = &supportedFeaturesRobustness2;
            }

            // 深度裁剪控制特性
            PhysicalDeviceDepthClipControlFeaturesEXT supportedFeaturesDepthClipControl = new()
            {
                SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control"))
            {
                features2.PNext = &supportedFeaturesDepthClipControl;
            }

            // 附件反馈循环布局特性
            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT supportedFeaturesAttachmentFeedbackLoopLayout = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout"))
            {
                features2.PNext = &supportedFeaturesAttachmentFeedbackLoopLayout;
            }

            // 动态附件反馈循环特性
            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT supportedFeaturesDynamicAttachmentFeedbackLoopLayout = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state"))
            {
                features2.PNext = &supportedFeaturesDynamicAttachmentFeedbackLoopLayout;
            }

            // 注释掉不支持的CopyCommands2扩展
            // PhysicalDeviceCopyCommands2FeaturesKHR supportedFeaturesCopyCommands2 = new()
            // {
            //     SType = StructureType.PhysicalDeviceCopyCommands2FeaturesKhr,
            //     PNext = features2.PNext,
            // };
            // 
            // if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_copy_commands2"))
            // {
            //     features2.PNext = &supportedFeaturesCopyCommands2;
            // }

            // 获取物理设备特性
            api.GetPhysicalDeviceFeatures2(physicalDevice.PhysicalDevice, &features2);

            var supportedFeatures = features2.Features;

            var features = new PhysicalDeviceFeatures
            {
                DepthBiasClamp = supportedFeatures.DepthBiasClamp,
                DepthClamp = supportedFeatures.DepthClamp,
                DualSrcBlend = supportedFeatures.DualSrcBlend,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                GeometryShader = supportedFeatures.GeometryShader,
                ImageCubeArray = supportedFeatures.ImageCubeArray,
                IndependentBlend = supportedFeatures.IndependentBlend,
                LogicOp = supportedFeatures.LogicOp,
                OcclusionQueryPrecise = supportedFeatures.OcclusionQueryPrecise,
                MultiViewport = supportedFeatures.MultiViewport,
                PipelineStatisticsQuery = supportedFeatures.PipelineStatisticsQuery,
                SamplerAnisotropy = supportedFeatures.SamplerAnisotropy,
                ShaderClipDistance = supportedFeatures.ShaderClipDistance,
                ShaderFloat64 = supportedFeatures.ShaderFloat64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageMultisample = supportedFeatures.ShaderStorageImageMultisample,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                TessellationShader = supportedFeatures.TessellationShader,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                RobustBufferAccess = useRobustBufferAccess,
                SampleRateShading = supportedFeatures.SampleRateShading,
            };

            void* pExtendedFeatures = null;

            // 时间线信号量特性
            PhysicalDeviceTimelineSemaphoreFeaturesKHR featuresTimelineSemaphore;

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_timeline_semaphore"))
            {
                featuresTimelineSemaphore = new PhysicalDeviceTimelineSemaphoreFeaturesKHR
                {
                    SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
                    PNext = pExtendedFeatures,
                    TimelineSemaphore = timelineSemaphoreFeatures.TimelineSemaphore,
                };

                pExtendedFeatures = &featuresTimelineSemaphore;
            }

            // 同步2特性
            PhysicalDeviceSynchronization2FeaturesKHR featuresSynchronization2;

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_synchronization2"))
            {
                featuresSynchronization2 = new PhysicalDeviceSynchronization2FeaturesKHR
                {
                    SType = StructureType.PhysicalDeviceSynchronization2Features,
                    PNext = pExtendedFeatures,
                    Synchronization2 = synchronization2Features.Synchronization2,
                };

                pExtendedFeatures = &featuresSynchronization2;
            }

            // 动态渲染特性
            PhysicalDeviceDynamicRenderingFeaturesKHR featuresDynamicRendering;

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_dynamic_rendering"))
            {
                featuresDynamicRendering = new PhysicalDeviceDynamicRenderingFeaturesKHR
                {
                    SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
                    PNext = pExtendedFeatures,
                    DynamicRendering = dynamicRenderingFeatures.DynamicRendering,
                };

                pExtendedFeatures = &featuresDynamicRendering;
            }

            // 扩展动态状态2特性
            PhysicalDeviceExtendedDynamicState2FeaturesEXT featuresExtendedDynamicState2;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_extended_dynamic_state2"))
            {
                featuresExtendedDynamicState2 = new PhysicalDeviceExtendedDynamicState2FeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceExtendedDynamicState2FeaturesExt,
                    PNext = pExtendedFeatures,
                    ExtendedDynamicState2 = extendedDynamicState2Features.ExtendedDynamicState2,
                    ExtendedDynamicState2LogicOp = extendedDynamicState2Features.ExtendedDynamicState2LogicOp,
                    ExtendedDynamicState2PatchControlPoints = extendedDynamicState2Features.ExtendedDynamicState2PatchControlPoints,
                };

                pExtendedFeatures = &featuresExtendedDynamicState2;
            }

            // 多视图特性
            PhysicalDeviceMultiviewFeatures featuresMultiview;

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_multiview"))
            {
                featuresMultiview = new PhysicalDeviceMultiviewFeatures
                {
                    SType = StructureType.PhysicalDeviceMultiviewFeatures,
                    PNext = pExtendedFeatures,
                    Multiview = multiviewFeatures.Multiview,
                    MultiviewGeometryShader = multiviewFeatures.MultiviewGeometryShader,
                    MultiviewTessellationShader = multiviewFeatures.MultiviewTessellationShader
                };

                pExtendedFeatures = &featuresMultiview;
            }

            // 变换反馈特性
            PhysicalDeviceTransformFeedbackFeaturesEXT featuresTransformFeedback;

            if (physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName))
            {
                featuresTransformFeedback = new PhysicalDeviceTransformFeedbackFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
                    PNext = pExtendedFeatures,
                    TransformFeedback = supportedFeaturesTransformFeedback.TransformFeedback,
                };

                pExtendedFeatures = &featuresTransformFeedback;
            }

            // 图元拓扑列表重启特性
            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT featuresPrimitiveTopologyListRestart;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                featuresPrimitiveTopologyListRestart = new PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT
                {
                    SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
                    PNext = pExtendedFeatures,
                    PrimitiveTopologyListRestart = supportedFeaturesPrimitiveTopologyListRestart.PrimitiveTopologyListRestart,
                    PrimitiveTopologyPatchListRestart = supportedFeaturesPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart,
                };

                pExtendedFeatures = &featuresPrimitiveTopologyListRestart;
            }

            // 鲁棒性2特性
            PhysicalDeviceRobustness2FeaturesEXT featuresRobustness2;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                featuresRobustness2 = new PhysicalDeviceRobustness2FeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                    PNext = pExtendedFeatures,
                    NullDescriptor = supportedFeaturesRobustness2.NullDescriptor,
                };

                pExtendedFeatures = &featuresRobustness2;
            }

            // 扩展动态状态特性
            var featuresExtendedDynamicState = new PhysicalDeviceExtendedDynamicStateFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicStateFeaturesExt,
                PNext = pExtendedFeatures,
                ExtendedDynamicState = physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName),
            };

            pExtendedFeatures = &featuresExtendedDynamicState;

            // Vulkan 1.1 特性
            var featuresVk11 = new PhysicalDeviceVulkan11Features
            {
                SType = StructureType.PhysicalDeviceVulkan11Features,
                PNext = pExtendedFeatures,
                ShaderDrawParameters = supportedFeaturesVk11.ShaderDrawParameters,
            };

            pExtendedFeatures = &featuresVk11;

            // Vulkan 1.2 特性
            var featuresVk12 = new PhysicalDeviceVulkan12Features
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = pExtendedFeatures,
                DescriptorIndexing = supportedPhysicalDeviceVulkan12Features.DescriptorIndexing,
                DrawIndirectCount = supportedPhysicalDeviceVulkan12Features.DrawIndirectCount,
                UniformBufferStandardLayout = supportedPhysicalDeviceVulkan12Features.UniformBufferStandardLayout,
                UniformAndStorageBuffer8BitAccess = supportedPhysicalDeviceVulkan12Features.UniformAndStorageBuffer8BitAccess,
                StorageBuffer8BitAccess = supportedPhysicalDeviceVulkan12Features.StorageBuffer8BitAccess,
            };

            pExtendedFeatures = &featuresVk12;

            // Vulkan 1.3 特性
            PhysicalDeviceVulkan13Features featuresVk13;

            if (physicalDevice.PhysicalDeviceProperties.ApiVersion >= Vk.Version13.Value)
            {
                featuresVk13 = new PhysicalDeviceVulkan13Features
                {
                    SType = StructureType.PhysicalDeviceVulkan13Features,
                    PNext = pExtendedFeatures,
                    Synchronization2 = supportedPhysicalDeviceVulkan13Features.Synchronization2,
                    DynamicRendering = supportedPhysicalDeviceVulkan13Features.DynamicRendering,
                    Maintenance4 = supportedPhysicalDeviceVulkan13Features.Maintenance4,
                };

                pExtendedFeatures = &featuresVk13;
            }

            // 索引类型Uint8特性
            PhysicalDeviceIndexTypeUint8FeaturesEXT featuresIndexU8;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8"))
            {
                featuresIndexU8 = new PhysicalDeviceIndexTypeUint8FeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
                    PNext = pExtendedFeatures,
                    IndexTypeUint8 = true,
                };

                pExtendedFeatures = &featuresIndexU8;
            }

            // 片段着色器互锁特性
            PhysicalDeviceFragmentShaderInterlockFeaturesEXT featuresFragmentShaderInterlock;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock"))
            {
                featuresFragmentShaderInterlock = new PhysicalDeviceFragmentShaderInterlockFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceFragmentShaderInterlockFeaturesExt,
                    PNext = pExtendedFeatures,
                    FragmentShaderPixelInterlock = true,
                };

                pExtendedFeatures = &featuresFragmentShaderInterlock;
            }

            // 自定义边框颜色特性
            PhysicalDeviceCustomBorderColorFeaturesEXT featuresCustomBorderColor;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color") &&
                supportedFeaturesCustomBorderColor.CustomBorderColors &&
                supportedFeaturesCustomBorderColor.CustomBorderColorWithoutFormat)
            {
                featuresCustomBorderColor = new PhysicalDeviceCustomBorderColorFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
                    PNext = pExtendedFeatures,
                    CustomBorderColors = true,
                    CustomBorderColorWithoutFormat = true,
                };

                pExtendedFeatures = &featuresCustomBorderColor;
            }

            // 深度裁剪控制特性
            PhysicalDeviceDepthClipControlFeaturesEXT featuresDepthClipControl;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control") &&
                supportedFeaturesDepthClipControl.DepthClipControl)
            {
                featuresDepthClipControl = new PhysicalDeviceDepthClipControlFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
                    PNext = pExtendedFeatures,
                    DepthClipControl = true,
                };

                pExtendedFeatures = &featuresDepthClipControl;
            }

            // 附件反馈循环布局特性
            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT featuresAttachmentFeedbackLoopLayout;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout") &&
                supportedFeaturesAttachmentFeedbackLoopLayout.AttachmentFeedbackLoopLayout)
            {
                featuresAttachmentFeedbackLoopLayout = new()
                {
                    SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
                    PNext = pExtendedFeatures,
                    AttachmentFeedbackLoopLayout = true,
                };

                pExtendedFeatures = &featuresAttachmentFeedbackLoopLayout;
            }

            // 动态附件反馈循环特性
            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT featuresDynamicAttachmentFeedbackLoopLayout;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state") &&
                supportedFeaturesDynamicAttachmentFeedbackLoopLayout.AttachmentFeedbackLoopDynamicState)
            {
                featuresDynamicAttachmentFeedbackLoopLayout = new()
                {
                    SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
                    PNext = pExtendedFeatures,
                    AttachmentFeedbackLoopDynamicState = true,
                };

                pExtendedFeatures = &featuresDynamicAttachmentFeedbackLoopLayout;
            }

            // 注释掉不支持的CopyCommands2特性
            // PhysicalDeviceCopyCommands2FeaturesKHR featuresCopyCommands2;
            // 
            // if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_copy_commands2"))
            // {
            //     featuresCopyCommands2 = new PhysicalDeviceCopyCommands2FeaturesKHR
            //     {
            //         SType = StructureType.PhysicalDeviceCopyCommands2FeaturesKhr,
            //         PNext = pExtendedFeatures,
            //         CopyCommands2 = supportedFeaturesCopyCommands2.CopyCommands2,
            //     };
            // 
            //     pExtendedFeatures = &featuresCopyCommands2;
            // }

            var enabledExtensions = _requiredExtensions.Union(_desirableExtensions.Intersect(physicalDevice.DeviceExtensions)).ToArray();

            IntPtr* ppEnabledExtensions = stackalloc IntPtr[enabledExtensions.Length];

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                ppEnabledExtensions[i] = Marshal.StringToHGlobalAnsi(enabledExtensions[i]);
            }

            var deviceCreateInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = pExtendedFeatures,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
                PpEnabledExtensionNames = (byte**)ppEnabledExtensions,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                PEnabledFeatures = &features,
            };

            api.CreateDevice(physicalDevice.PhysicalDevice, in deviceCreateInfo, null, out var device).ThrowOnError();

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                Marshal.FreeHGlobal(ppEnabledExtensions[i]);
            }

            return device;
        }
    }
}