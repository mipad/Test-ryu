using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.OpenGL.Image;
using Ryujinx.Graphics.OpenGL.Queries;
using Ryujinx.Graphics.Shader.Translation;
using System;

namespace Ryujinx.Graphics.OpenGL
{
    public sealed class OpenGLRenderer : IRenderer
    {
        private readonly Pipeline _pipeline;

        public IPipeline Pipeline => _pipeline;

        private readonly Counters _counters;

        private readonly Window _window;

        public IWindow Window => _window;

        private readonly TextureCopy _textureCopy;
        private readonly TextureCopy _backgroundTextureCopy;
        internal TextureCopy TextureCopy => BackgroundContextWorker.InBackground ? _backgroundTextureCopy : _textureCopy;
        internal TextureCopyIncompatible TextureCopyIncompatible { get; }
        internal TextureCopyMS TextureCopyMS { get; }

        private readonly Sync _sync;

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        internal PersistentBuffers PersistentBuffers { get; }

        internal ResourcePool ResourcePool { get; }

        internal int BufferCount { get; private set; }

        public string GpuVendor { get; private set; }
        public string GpuRenderer { get; private set; }
        public string GpuVersion { get; private set; }

        public bool PreferThreading => true;

        public OpenGLRenderer()
        {
            _pipeline = new Pipeline();
            _counters = new Counters();
            _window = new Window(this);
            _textureCopy = new TextureCopy(this);
            _backgroundTextureCopy = new TextureCopy(this);
            TextureCopyIncompatible = new TextureCopyIncompatible(this);
            TextureCopyMS = new TextureCopyMS(this);
            _sync = new Sync();
            PersistentBuffers = new PersistentBuffers();
            ResourcePool = new ResourcePool();
        }

        public BufferHandle CreateBuffer(int size, GAL.BufferAccess access)
        {
            BufferCount++;

            var memType = access & GAL.BufferAccess.MemoryTypeMask;

            if (memType == GAL.BufferAccess.HostMemory)
            {
                BufferHandle handle = Buffer.CreatePersistent(size);

                PersistentBuffers.Map(handle, size);

                return handle;
            }
            else
            {
                return Buffer.Create(size);
            }
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            throw new NotSupportedException();
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            throw new NotSupportedException();
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            return new ImageArray(size);
        }

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            return new Program(shaders, info.FragmentOutputMap);
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            return new Sampler(info);
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            if (info.Target == Target.TextureBuffer)
            {
                return new TextureBuffer(this, info);
            }
            else
            {
                return ResourcePool.GetTextureOrNull(info) ?? new TextureStorage(this, info).CreateDefaultView();
            }
        }

        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            return new TextureArray(size);
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            PersistentBuffers.Unmap(buffer);

            Buffer.Delete(buffer);
        }

        public HardwareInfo GetHardwareInfo()
        {
            return new HardwareInfo(GpuVendor, GpuRenderer, GpuVendor);
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            return Buffer.GetData(this, buffer, offset, size);
        }

        public Capabilities GetCapabilities()
        {
            bool intelWindows = HwCapabilities.Vendor == HwCapabilities.GpuVendor.IntelWindows;
            bool intelUnix = HwCapabilities.Vendor == HwCapabilities.GpuVendor.IntelUnix;
            bool amdWindows = HwCapabilities.Vendor == HwCapabilities.GpuVendor.AmdWindows;

            return new Capabilities(
                api: TargetApi.OpenGL,
                vendorName: GpuVendor,
                memoryType: SystemMemoryType.BackendManaged,
                hasFrontFacingBug: intelWindows,
                hasVectorIndexingBug: amdWindows,
                needsFragmentOutputSpecialization: false,
                reduceShaderPrecision: false,
                supportsAstcCompression: HwCapabilities.SupportsAstcCompression,
                supportsBc123Compression: HwCapabilities.SupportsTextureCompressionS3tc,
                supportsBc45Compression: HwCapabilities.SupportsTextureCompressionRgtc,
                supportsBc67Compression: true,
                supportsEtc2Compression: true,
                supports3DTextureCompression: false,
                supportsBgraFormat: false,
                supportsR4G4Format: false,
                supportsR4G4B4A4Format: true,
                supportsScaledVertexFormats: true,
                supportsSnormBufferTextureFormat: false,
                supports5BitComponentFormat: true,
                supportsSparseBuffer: false,
                supportsBlendEquationAdvanced: HwCapabilities.SupportsBlendEquationAdvanced,
                supportsFragmentShaderInterlock: HwCapabilities.SupportsFragmentShaderInterlock,
                supportsFragmentShaderOrderingIntel: HwCapabilities.SupportsFragmentShaderOrdering,
                supportsGeometryShader: true,
                supportsGeometryShaderPassthrough: HwCapabilities.SupportsGeometryShaderPassthrough,
                supportsTransformFeedback: true,
                supportsImageLoadFormatted: HwCapabilities.SupportsImageLoadFormatted,
                supportsLayerVertexTessellation: HwCapabilities.SupportsShaderViewportLayerArray,
                supportsMismatchingViewFormat: HwCapabilities.SupportsMismatchingViewFormat,
                supportsCubemapView: true,
                supportsNonConstantTextureOffset: HwCapabilities.SupportsNonConstantTextureOffset,
                supportsQuads: HwCapabilities.SupportsQuads,
                supportsSeparateSampler: false,
                supportsShaderBallot: HwCapabilities.SupportsShaderBallot,
                supportsShaderBallotDivergence: true,
                supportsShaderBarrierDivergence: !(intelWindows || intelUnix),
                supportsShaderFloat64: true,
                supportsTextureGatherOffsets: true,
                supportsTextureShadowLod: HwCapabilities.SupportsTextureShadowLod,
                supportsVertexStoreAndAtomics: true,
                supportsViewportIndexVertexTessellation: HwCapabilities.SupportsShaderViewportLayerArray,
                supportsViewportMask: HwCapabilities.SupportsViewportArray2,
                supportsViewportSwizzle: HwCapabilities.SupportsViewportSwizzle,
                supportsIndirectParameters: HwCapabilities.SupportsIndirectParameters,
                supportsDepthClipControl: true,
                supportsFragmentDensityMap: false,
                supportsFragmentDensityMap2: false,
                supportsMultiview: false,
                supportsTimelineSemaphores: false,
                supportsSynchronization2: false,
                supportsDynamicRendering: false,
                supportsExtendedDynamicState2: false,
                supportsASTCDecodeMode: false,
                supportsTimestampQueries: true, // 新增：时间戳查询支持
                uniformBufferSetIndex: 0,
                storageBufferSetIndex: 1,
                textureSetIndex: 2,
                imageSetIndex: 3,
                extraSetBaseIndex: 0,
                maximumExtraSets: 0,
                maximumUniformBuffersPerStage: 13,
                maximumStorageBuffersPerStage: 16,
                maximumTexturesPerStage: 32,
                maximumImagesPerStage: 8,
                maximumComputeSharedMemorySize: HwCapabilities.MaximumComputeSharedMemorySize,
                maximumSupportedAnisotropy: HwCapabilities.MaximumSupportedAnisotropy,
                shaderSubgroupSize: Constants.MaxSubgroupSize,
                storageBufferOffsetAlignment: HwCapabilities.StorageBufferOffsetAlignment,
                textureBufferOffsetAlignment: HwCapabilities.TextureBufferOffsetAlignment,
                gatherBiasPrecision: intelWindows || amdWindows ? 8 : 0,
                maximumGpuMemory: 0);
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            Buffer.SetData(buffer, offset, data);
        }

        public void UpdateCounters()
        {
            _counters.Update();
        }

        public void PreFrame()
        {
            _sync.Cleanup();
            ResourcePool.Tick();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            return _counters.QueueReport(type, resultHandler, divisor, _pipeline.DrawCount, hostReserved);
        }

        public void Initialize(GraphicsDebugLevel glLogLevel)
        {
            Debugger.Initialize(glLogLevel);

            PrintGpuInformation();

            if (HwCapabilities.SupportsParallelShaderCompile)
            {
                GL.Arb.MaxShaderCompilerThreads(Math.Min(Environment.ProcessorCount, 8));
            }

            _counters.Initialize();

            GL.ClampColor(ClampColorTarget.ClampFragmentColor, ClampColorMode.False);
        }

        private void PrintGpuInformation()
        {
            GpuVendor = GL.GetString(StringName.Vendor);
            GpuRenderer = GL.GetString(StringName.Renderer);
            GpuVersion = GL.GetString(StringName.Version);

            Logger.Notice.Print(LogClass.Gpu, $"{GpuVendor} {GpuRenderer} ({GpuVersion})");
        }

        public void ResetCounter(CounterType type)
        {
            _counters.QueueReset(type);
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            if (_window.BackgroundContext.HasContext())
            {
                action();
            }
            else
            {
                _window.BackgroundContext.Invoke(action);
            }
        }

        public void InitializeBackgroundContext(IOpenGLContext baseContext)
        {
            _window.InitializeBackgroundContext(baseContext);
        }

        public void Dispose()
        {
            _textureCopy.Dispose();
            _backgroundTextureCopy.Dispose();
            TextureCopyMS.Dispose();
            PersistentBuffers.Dispose();
            ResourcePool.Dispose();
            _pipeline.Dispose();
            _window.Dispose();
            _counters.Dispose();
            _sync.Dispose();
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            return new Program(programBinary, hasFragmentShader, info.FragmentOutputMap);
        }

        public void CreateSync(ulong id, bool strict)
        {
            _sync.Create(id);
        }

        public void WaitSync(ulong id)
        {
            _sync.Wait(id);
        }

        public ulong GetCurrentSync()
        {
            return _sync.GetCurrent();
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
        }

        public void Screenshot()
        {
            _window.ScreenCaptureRequested = true;
        }

        public void OnScreenCaptured(ScreenCaptureImageInfo bitmap)
        {
            ScreenCaptured?.Invoke(this, bitmap);
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return false;
        }
    }
}