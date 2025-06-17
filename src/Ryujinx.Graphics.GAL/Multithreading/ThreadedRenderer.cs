using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL.Multithreading.Commands;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Buffer;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;
using System;
using System.Buffers;
using System.Diagnostics;
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
        // 将队列大小改为2的幂以启用位掩码优化
        private const int QueueCount = 16384; // 2^14
        private const int QueueMask = QueueCount - 1;
        
        private const int SpanPoolBytes = 4 * 1024 * 1024;
        private const int MaxRefsPerCommand = 2;

        private readonly int _elementSize;
        private readonly IRenderer _baseRenderer;
        private Thread _gpuThread;
        private Thread _backendThread;
        private volatile bool _running;
        private volatile bool _disposing; // 新增：标记正在释放状态

        private readonly AutoResetEvent _frameComplete = new(true);

        private readonly ManualResetEventSlim _galWorkAvailable;
        private readonly CircularSpanPool _spanPool;

        private readonly ManualResetEventSlim _invokeRun;
        private readonly AutoResetEvent _interruptRun;

        private bool _lastSampleCounterClear = true;

        private readonly byte[] _commandQueue;
        private readonly object[] _refQueue;

        private int _consumerPtr;
        private volatile int _commandCount;

        private int _producerPtr;
        private int _lastProducedPtr;
        private int _invokePtr;

        private int _refProducerPtr;
        private int _refConsumerPtr;

        private Action _interruptAction;
        private readonly object _interruptLock = new();

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
            _refQueue = ArrayPool<object>.Shared.Rent(MaxRefsPerCommand * QueueCount);
        }

        public void RunLoop(ThreadStart gpuLoop)
        {
            _running = true;

            _backendThread = Thread.CurrentThread;

            _gpuThread = new Thread(gpuLoop)
            {
                Name = "GPU.MainThread",
                IsBackground = true // 设为后台线程
            };

            _gpuThread.Start();

            RenderLoop();
        }

        public void RenderLoop()
        {
            // Power through the render queue until the Gpu thread work is done.
            SpinWait spinWait = new();

            while (_running)
            {
                // 使用混合等待策略
                while (Volatile.Read(ref _commandCount) == 0 && 
                       Volatile.Read(ref _interruptAction) == null)
                {
                    if (!_running) return;
                    spinWait.SpinOnce();
                    
                    // 每自旋100次检查一次事件
                    if (spinWait.NextSpinWillYield)
                    {
                        _galWorkAvailable.Wait();
                        break;
                    }
                }

                // 处理中断
                if (Volatile.Read(ref _interruptAction) != null)
                {
                    _interruptAction();
                    _interruptRun.Set();
                    Interlocked.Exchange(ref _interruptAction, null);
                }

                // 批量处理命令 (最多32个)
                int batchSize = 0;
                while (batchSize < 32 && Volatile.Read(ref _commandCount) > 0 && 
                       Volatile.Read(ref _interruptAction) == null)
                {
                    int commandPtr = _consumerPtr;

                    Span<byte> command = new(_commandQueue, commandPtr * _elementSize, _elementSize);

                    // 运行命令并处理异常
                    try
                    {
                        CommandHelper.RunCommand(command, this, _baseRenderer);
                    }
                    catch (Exception ex)
                    {
                        // 在实际应用中应使用日志记录
                        Debug.WriteLine($"Command execution failed: {ex.Message}");
                    }

                    // 处理同步调用
                    if (Interlocked.CompareExchange(ref _invokePtr, -1, commandPtr) == commandPtr)
                    {
                        _invokeRun.Set();
                    }

                    // 使用位掩码优化指针计算
                    _consumerPtr = (_consumerPtr + 1) & QueueMask;
                    Interlocked.Decrement(ref _commandCount);
                    batchSize++;
                }
            }
        }

        internal SpanRef<T> CopySpan<T>(ReadOnlySpan<T> data) where T : unmanaged
        {
            return _spanPool.Insert(data);
        }

        private TableRef<T> Ref<T>(T reference)
        {
            return new TableRef<T>(this, reference);
        }

        internal ref T New<T>() where T : struct
        {
            // 双重检查防止释放后访问
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            SpinWait spinWait = new();
            while (_producerPtr == (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
            {
                if (!_running || _disposing)
                {
                    throw new ObjectDisposedException("ThreadedRenderer has been disposed");
                }
                spinWait.SpinOnce();
            }

            int taken = _producerPtr;
            _lastProducedPtr = taken;

            _producerPtr = (_producerPtr + 1) & QueueMask; // 使用位掩码

            Span<byte> memory = new(_commandQueue, taken * _elementSize, _elementSize);
            ref T result = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(memory));

            memory[^1] = (byte)((IGALCommand)result).CommandType;

            return ref result;
        }

        internal int AddTableRef(object obj)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

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

        internal void QueueCommand()
        {
            // 双重检查防止释放后访问
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            int result = Interlocked.Increment(ref _commandCount);

            if (result == 1)
            {
                _galWorkAvailable.Set();
            }
        }

        internal void InvokeCommand()
        {
            if (!_running || _disposing) return;
            
            _invokeRun.Reset();
            _invokePtr = _lastProducedPtr;

            QueueCommand();

            // 等待命令完成
            _invokeRun.Wait();
        }

        internal void WaitForFrame()
        {
            if (!_running || _disposing) return;
            _frameComplete.WaitOne();
        }

        internal void SignalFrame()
        {
            if (!_running || _disposing) return;
            _frameComplete.Set();
        }

        internal bool IsGpuThread()
        {
            // 添加null检查
            return _gpuThread != null && Thread.CurrentThread == _gpuThread;
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            if (IsGpuThread() && !alwaysBackground)
            {
                // 添加状态检查
                if (!_running || _disposing) return;
                
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
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferAccessCommand>().Set(handle, size, access);
            QueueCommand();

            return handle;
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateHostBufferCommand>().Set(handle, pointer, size);
            QueueCommand();

            return handle;
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferSparseCommand>().Set(handle, CopySpan(storageBuffers));
            QueueCommand();

            return handle;
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            var imageArray = new ThreadedImageArray(this);
            New<CreateImageArrayCommand>().Set(Ref(imageArray), size, isBuffer);
            QueueCommand();

            return imageArray;
        }

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            var program = new ThreadedProgram(this);

            SourceProgramRequest request = new(program, shaders, info);

            Programs.Add(request);

            New<CreateProgramCommand>().Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            var sampler = new ThreadedSampler(this);
            New<CreateSamplerCommand>().Set(Ref(sampler), info);
            QueueCommand();

            return sampler;
        }

        public void CreateSync(ulong id, bool strict)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            Sync.CreateSyncHandle(id);
            New<CreateSyncCommand>().Set(id, strict);
            QueueCommand();
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            // 添加状态检查
            if (!_running || _disposing) 
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

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
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            var textureArray = new ThreadedTextureArray(this);
            New<CreateTextureArrayCommand>().Set(Ref(textureArray), size, isBuffer);
            QueueCommand();

            return textureArray;
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            New<BufferDisposeCommand>().Set(buffer);
            QueueCommand();
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

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
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            ResultBox<Capabilities> box = new();
            New<GetCapabilitiesCommand>().Set(Ref(box));
            InvokeCommand();

            return box.Result;
        }

        public ulong GetCurrentSync()
        {
            // 直接调用基础渲染器，无需检查状态
            return _baseRenderer.GetCurrentSync();
        }

        public HardwareInfo GetHardwareInfo()
        {
            // 直接调用基础渲染器，无需检查状态
            return _baseRenderer.GetHardwareInfo();
        }

        /// <summary>
        /// Initialize the base renderer. Must be called on the render thread.
        /// </summary>
        /// <param name="logLevel">Log level to use</param>
        public void Initialize(GraphicsDebugLevel logLevel)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            _baseRenderer.Initialize(logLevel);
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            var program = new ThreadedProgram(this);

            BinaryProgramRequest request = new(program, programBinary, hasFragmentShader, info);
            Programs.Add(request);

            New<CreateProgramCommand>().Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public void PreFrame()
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            New<PreFrameCommand>();
            QueueCommand();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

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
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            New<ResetCounterCommand>().Set(type);
            QueueCommand();
            _lastSampleCounterClear = true;
        }

        public void Screenshot()
        {
            // 直接调用基础渲染器，无需检查状态
            _baseRenderer.Screenshot();
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            New<BufferSetDataCommand>().Set(buffer, offset, CopySpan(data));
            QueueCommand();
        }

        public void UpdateCounters()
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            New<UpdateCountersCommand>();
            QueueCommand();
        }

        public void WaitSync(ulong id)
        {
            // 添加状态检查
            if (!_running || _disposing)
            {
                throw new ObjectDisposedException("ThreadedRenderer has been disposed");
            }

            Sync.WaitSyncAvailability(id);
            _baseRenderer.WaitSync(id);
        }

        private void Interrupt(Action action)
        {
            // 添加状态检查
            if (!_running || _disposing) return;

            if (Thread.CurrentThread == _backendThread)
            {
                action();
            }
            else
            {
                // 使用更高效的中断处理
                var previousAction = Interlocked.Exchange(ref _interruptAction, action);
                if (previousAction != null)
                {
                    // 处理未完成的中断
                    previousAction();
                    _interruptRun.Set();
                }

                _galWorkAvailable.Set();
                _interruptRun.WaitOne();
            }
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            // Threaded renderer ignores given interrupt action, as it provides its own to the child renderer.
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            // 直接调用基础渲染器，无需检查状态
            return _baseRenderer.PrepareHostMapping(address, size);
        }

        public void FlushThreadedCommands()
        {
            // 添加状态检查
            if (!_running || _disposing) return;

            SpinWait wait = new();

            while (Volatile.Read(ref _commandCount) > 0)
            {
                wait.SpinOnce();
            }
        }

        public void Dispose()
        {
            if (!_running || _disposing) return;
            _disposing = true; // 标记正在释放状态

            GC.SuppressFinalize(this);
            _running = false;

            // 刷新剩余命令
            FlushThreadedCommands();

            // 唤醒所有可能等待的线程
            _galWorkAvailable.Set();
            _frameComplete.Set();
            _interruptRun.Set();
            _invokeRun.Set();

            // 等待GPU线程退出
            if (_gpuThread != null && _gpuThread.IsAlive)
            {
                // 更安全地等待线程结束
                if (!_gpuThread.Join(100))
                {
                    // 如果超时，尝试优雅终止
                    _gpuThread.Interrupt();
                    
                    // 再等待50ms
                    if (!_gpuThread.Join(50))
                    {
                        Debug.WriteLine("Warning: GPU thread did not exit gracefully");
                    }
                }
            }

            // 释放基础渲染器
            _baseRenderer.Dispose();

            // 释放其他资源
            Sync?.Dispose();

            // 归还ArrayPool资源
            ArrayPool<object>.Shared.Return(_refQueue);

            // 释放事件对象
            _frameComplete.Dispose();
            _galWorkAvailable.Dispose();
            _invokeRun.Dispose();
            _interruptRun.Dispose();
        }
    }
}
