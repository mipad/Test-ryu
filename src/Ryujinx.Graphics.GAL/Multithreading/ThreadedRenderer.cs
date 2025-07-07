using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL.Multithreading.Commands;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Buffer;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;
using Ryujinx.Graphics.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// The ThreadedRenderer is a layer that can be put in front of any Renderer backend to make
    /// its processing happen on a separate thread, rather than intertwined with the GPU emulation.
    /// A new thread is created to handle the GPU command processing, separate from the renderer thread.
    /// Calls to the renderer, pipeline and resources are queued to happen on the renderer thread.
    /// </summary>
    public class ThreadedRenderer : IRenderer
    {
        private const int SpanPoolBytes = 4 * 1024 * 1024;
        private const int MaxRefsPerCommand = 2;
        private const int QueueCount = 10000;

        private readonly int _elementSize;
        private readonly IRenderer _baseRenderer;
        private Thread _gpuThread;
        private Thread _backendThread;
        private bool _running;

        private readonly AutoResetEvent _frameComplete = new(true);

        private readonly ManualResetEventSlim _galWorkAvailable;
        private readonly CircularSpanPool _spanPool;

        private readonly ManualResetEventSlim _invokeRun;
        private readonly AutoResetEvent _interruptRun;

        private bool _lastSampleCounterClear = true;

        private readonly byte[] _commandQueue;
        private readonly object[] _refQueue;

        private int _consumerPtr;
        private int _commandCount;

        private int _producerPtr;
        private int _lastProducedPtr;
        private int _invokePtr;

        private int _refProducerPtr;
        private int _refConsumerPtr;

        private Action _interruptAction;
        private readonly object _interruptLock = new();

        // Load monitoring fields
        private readonly Stopwatch _frameTimer = new();
        private long _lastFrameTimeTicks;
        private readonly ConcurrentQueue<long> _frameTimeHistory = new();
        private const int FrameTimeHistorySize = 60;
        private const long TargetFrameTimeTicks = TimeSpan.FromMilliseconds(16).Ticks;

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        internal BufferMap Buffers { get; }
        internal SyncMap Sync { get; }
        internal CircularSpanPool SpanPool { get; }
        internal ProgramQueue Programs { get; }

        public IPipeline Pipeline { get; }
        public IWindow Window { get; }

        public IRenderer BaseRenderer => _baseRenderer;

        public bool PreferThreading => _baseRenderer.PreferThreading;

        public ThreadedRenderer(IRenderer renderer)
        {
            _baseRenderer = renderer;

            renderer.ScreenCaptured += (sender, info) => ScreenCaptured?.Invoke(this, info);
            renderer.SetInterruptAction(Interrupt);

            Pipeline = new ThreadedPipeline(this);
            Window = new ThreadedWindow(this, renderer);
            Buffers = new BufferMap();
            Sync = new SyncMap();
            Programs = new ProgramQueue(renderer);

            _galWorkAvailable = new ManualResetEventSlim(false);
            _invokeRun = new ManualResetEventSlim();
            _interruptRun = new AutoResetEvent(false);
            _spanPool = new CircularSpanPool(this, SpanPoolBytes);
            SpanPool = _spanPool;

            _elementSize = BitUtils.AlignUp(CommandHelper.GetMaxCommandSize(), 4);

            _commandQueue = new byte[_elementSize * QueueCount];
            _refQueue = new object[MaxRefsPerCommand * QueueCount];
        }

        public void RunLoop(ThreadStart gpuLoop)
        {
            _running = true;

            _backendThread = Thread.CurrentThread;

            _gpuThread = new Thread(gpuLoop)
            {
                Name = "GPU.MainThread",
            };

            _gpuThread.Start();

            RenderLoop();
        }

        public void RenderLoop()
        {
            _frameTimer.Start();
            
            while (_running)
            {
                _galWorkAvailable.Wait();
                _galWorkAvailable.Reset();

                // Frame time measurement
                long currentFrameTime = _frameTimer.ElapsedTicks - _lastFrameTimeTicks;
                _lastFrameTimeTicks = _frameTimer.ElapsedTicks;
                
                // Maintain frame time history
                _frameTimeHistory.Enqueue(currentFrameTime);
                while (_frameTimeHistory.Count > FrameTimeHistorySize)
                {
                    _frameTimeHistory.TryDequeue(out _);
                }

                // Dynamic load detection
                if (ShouldThrottleCommands())
                {
                    Thread.Sleep(CalculateThrottleTime());
                }

                if (Volatile.Read(ref _interruptAction) != null)
                {
                    _interruptAction?.Invoke();
                    _interruptRun.Set();

                    Interlocked.Exchange(ref _interruptAction, null);
                }

                while (Volatile.Read(ref _commandCount) > 0 && Volatile.Read(ref _interruptAction) == null)
                {
                    int commandPtr = _consumerPtr;

                    Span<byte> command = new(_commandQueue, commandPtr * _elementSize, _elementSize);

                    CommandHelper.RunCommand(command, this, _baseRenderer);

                    if (Interlocked.CompareExchange(ref _invokePtr, -1, commandPtr) == commandPtr)
                    {
                        _invokeRun.Set();
                    }

                    _consumerPtr = (_consumerPtr + 1) % QueueCount;

                    Interlocked.Decrement(ref _commandCount);
                }
            }
            
            _frameTimer.Stop();
        }

        private bool ShouldThrottleCommands()
        {
            if (_frameTimeHistory.Count < FrameTimeHistorySize / 2)
                return false;

            long averageTicks = (long)_frameTimeHistory.Average();
            return averageTicks > TargetFrameTimeTicks * 1.2;
        }

        private int CalculateThrottleTime()
        {
            if (_frameTimeHistory.Count < 10)
                return 1;

            long maxFrameTime = _frameTimeHistory.Max();
            double overloadRatio = (double)maxFrameTime / TargetFrameTimeTicks;
            
            return Math.Clamp((int)(overloadRatio * 2), 1, 5);
        }

        internal SpanRef<T> CopySpan<T>(ReadOnlySpan<T> data) where T : unmanaged
        {
            return _spanPool.Insert(data);
        }

        private TableRef<T> Ref<T>(T reference)
        {
            return new TableRef<T>(this, reference);
        }

        internal ref T New<T>(CommandPriority priority = CommandPriority.Normal) where T : struct
        {
            while (_producerPtr == (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
            {
                Thread.Sleep(1);
            }

            int taken = _producerPtr;
            _lastProducedPtr = taken;

            _producerPtr = (_producerPtr + 1) % QueueCount;

            Span<byte> memory = new(_commandQueue, taken * _elementSize, _elementSize);
            ref T result = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(memory));

            if (_elementSize >= 2)
            {
                memory[^1] = (byte)((IGALCommand)result).CommandType;
                memory[^2] = (byte)priority;
            }
            else
            {
                memory[0] = (byte)((IGALCommand)result).CommandType;
            }
            
            return ref result;
        }

        internal int AddTableRef(object obj)
        {
            int index = _refProducerPtr;

            _refQueue[index] = obj;

            _refProducerPtr = (_refProducerPtr + 1) % _refQueue.Length;

            return index;
        }

        internal object RemoveTableRef(int index)
        {
            Debug.Assert(index == _refConsumerPtr);

            object result = _refQueue[_refConsumerPtr];
            _refQueue[_refConsumerPtr] = null;

            _refConsumerPtr = (_refConsumerPtr + 1) % _refQueue.Length;

            return result;
        }

        internal void QueueCommand(CommandPriority priority = CommandPriority.Normal)
        {
            if (priority == CommandPriority.High)
            {
                _galWorkAvailable.Set();
            }

            int result = Interlocked.Increment(ref _commandCount);

            if (result == 1 || priority == CommandPriority.High)
            {
                _galWorkAvailable.Set();
            }
        }

        internal void InvokeCommand()
        {
            _invokeRun.Reset();
            _invokePtr = _lastProducedPtr;

            QueueCommand();
            _invokeRun.Wait();
        }

        internal void WaitForFrame()
        {
            _frameComplete.WaitOne();
        }

        internal void SignalFrame()
        {
            _frameComplete.Set();
        }

        internal bool IsGpuThread()
        {
            return Thread.CurrentThread == _gpuThread;
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            if (IsGpuThread() && !alwaysBackground)
            {
                New<ActionCommand>().Set(Ref(action));
                InvokeCommand();
            }
            else
            {
                _baseRenderer.BackgroundContextAction(action, true);
            }
        }

        public BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferAccessCommand>().Set(handle, size, access);
            QueueCommand();

            return handle;
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateHostBufferCommand>().Set(handle, pointer, size);
            QueueCommand();

            return handle;
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferSparseCommand>().Set(handle, CopySpan(storageBuffers));
            QueueCommand();

            return handle;
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            var imageArray = new ThreadedImageArray(this);
            New<CreateImageArrayCommand>().Set(Ref(imageArray), size, isBuffer);
            QueueCommand();

            return imageArray;
        }

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            var program = new ThreadedProgram(this);

            SourceProgramRequest request = new(program, shaders, info);

            Programs.Add(request);

            New<CreateProgramCommand>().Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            var sampler = new ThreadedSampler(this);
            New<CreateSamplerCommand>().Set(Ref(sampler), info);
            QueueCommand();

            return sampler;
        }

        public void CreateSync(ulong id, bool strict)
        {
            Sync.CreateSyncHandle(id);
            New<CreateSyncCommand>().Set(id, strict);
            QueueCommand();
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            if (IsGpuThread())
            {
                var texture = new ThreadedTexture(this, info);
                New<CreateTextureCommand>().Set(Ref(texture), info);
                QueueCommand();

                return texture;
            }
            else
            {
                var texture = new ThreadedTexture(this, info)
                {
                    Base = _baseRenderer.CreateTexture(info),
                };

                return texture;
            }
        }

        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            var textureArray = new ThreadedTextureArray(this);
            New<CreateTextureArrayCommand>().Set(Ref(textureArray), size, isBuffer);
            QueueCommand();

            return textureArray;
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            New<BufferDisposeCommand>().Set(buffer);
            QueueCommand();
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            if (IsGpuThread())
            {
                ResultBox<PinnedSpan<byte>> box = new();
                New<BufferGetDataCommand>().Set(buffer, offset, size, Ref(box));
                InvokeCommand();

                return box.Result;
            }
            else
            {
                return _baseRenderer.GetBufferData(Buffers.MapBufferBlocking(buffer), offset, size);
            }
        }

        public Capabilities GetCapabilities()
        {
            ResultBox<Capabilities> box = new();
            New<GetCapabilitiesCommand>().Set(Ref(box));
            InvokeCommand();

            return box.Result;
        }

        public ulong GetCurrentSync()
        {
            return _baseRenderer.GetCurrentSync();
        }

        public HardwareInfo GetHardwareInfo()
        {
            return _baseRenderer.GetHardwareInfo();
        }

        public void Initialize(GraphicsDebugLevel logLevel)
        {
            _baseRenderer.Initialize(logLevel);
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            var program = new ThreadedProgram(this);

            BinaryProgramRequest request = new(program, programBinary, hasFragmentShader, info);
            Programs.Add(request);

            New<CreateProgramCommand>().Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public void PreFrame()
        {
            New<PreFrameCommand>();
            QueueCommand();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            ThreadedCounterEvent evt = new(this, type, _lastSampleCounterClear);
            New<ReportCounterCommand>().Set(Ref(evt), type, Ref(resultHandler), divisor, hostReserved);
            QueueCommand();

            if (type == CounterType.SamplesPassed)
            {
                _lastSampleCounterClear = false;
            }

            return evt;
        }

        public void ResetCounter(CounterType type)
        {
            New<ResetCounterCommand>().Set(type);
            QueueCommand();
            _lastSampleCounterClear = true;
        }

        public void Screenshot()
        {
            _baseRenderer.Screenshot();
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            New<BufferSetDataCommand>().Set(buffer, offset, CopySpan(data));
            QueueCommand();
        }

        public void UpdateCounters()
        {
            New<UpdateCountersCommand>();
            QueueCommand();
        }

        public void WaitSync(ulong id)
        {
            Sync.WaitSyncAvailability(id);
            _baseRenderer.WaitSync(id);
        }

        private void Interrupt(Action action)
        {
            if (Thread.CurrentThread == _backendThread)
            {
                action();
            }
            else
            {
                lock (_interruptLock)
                {
                    while (Interlocked.CompareExchange(ref _interruptAction, action, null) != null)
                    {
                    }

                    _galWorkAvailable.Set();
                    _interruptRun.WaitOne();
                }
            }
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            // Threaded renderer ignores given interrupt action
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return _baseRenderer.PrepareHostMapping(address, size);
        }

        public void FlushThreadedCommands()
        {
            SpinWait wait = new();

            while (Volatile.Read(ref _commandCount) > 0)
            {
                wait.SpinOnce();
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            // Clean monitoring data
            _frameTimeHistory.Clear();
            _frameTimer.Reset();

            // Stop GPU thread
            _running = false;
            _galWorkAvailable.Set();

            if (_gpuThread != null && _gpuThread.IsAlive)
            {
                _gpuThread.Join();
            }

            // Dispose renderer
            _baseRenderer.Dispose();

            // Dispose events
            _frameComplete.Dispose();
            _galWorkAvailable.Dispose();
            _invokeRun.Dispose();
            _interruptRun.Dispose();

            Sync.Dispose();
        }

        public enum CommandPriority
        {
            Low,
            Normal,
            High
        }
    }
}
