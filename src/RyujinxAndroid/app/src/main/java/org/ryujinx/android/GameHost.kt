package org.ryujinx.android

import android.annotation.SuppressLint
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.compose.runtime.MutableState
import org.ryujinx.android.service.EmulationService
import org.ryujinx.android.viewmodels.GameModel
import org.ryujinx.android.viewmodels.MainViewModel
import kotlin.concurrent.thread

@SuppressLint("ViewConstructor")
class GameHost(context: Context?, private val mainViewModel: MainViewModel) : SurfaceView(context),
    SurfaceHolder.Callback {

    private var _currentWindow: Long = -1
    private var isProgressHidden: Boolean = false
    private var progress: MutableState<String>? = null
    private var progressValue: MutableState<Float>? = null
    private var showLoading: MutableState<Boolean>? = null
    private var game: GameModel? = null
    
    // 使用 volatile 确保多线程可见性
    @Volatile private var _isClosed: Boolean = false
    @Volatile private var _isStarted: Boolean = false
    @Volatile private var _isInit: Boolean = false
    @Volatile private var _inputInitialized: Boolean = false
    
    private var _renderingThreadWatcher: Thread? = null
    private var _height: Int = 0
    private var _width: Int = 0
    private var _updateThread: Thread? = null
    private var _guestThread: Thread? = null
    
    private val _nativeWindow: NativeWindow

    // 前台服务绑定相关变量
    @Volatile private var emuBound = false
    private var emuBinder: EmulationService.LocalBinder? = null
    @Volatile private var _startedViaService = false

    private val mainHandler = Handler(Looper.getMainLooper())

    // 稳定器状态
    @Volatile private var stabilizerActive = false

    // 最后已知的 Android 旋转 (0,1,2,3)
    private var lastRotation: Int? = null

    // 防抖动的重置触发
    private var lastKickAt = 0L

    // 关闭同步锁
    private val closeLock = Any()

    val currentSurface: Long
        get() = _currentWindow

    val currentWindowhandle: Long
        get() = _nativeWindow.nativePointer

    // 服务连接器
    private val emuConn = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, service: IBinder) {
            emuBinder = service as EmulationService.LocalBinder
            emuBound = true
            Log.d("GameHost", "Emulation service connected")

            // 如果启动已准备且没有循环运行 → 现在在服务中启动
            if (_isStarted && !_startedViaService && _guestThread == null) {
                startRunLoopInService()
            }
        }

        override fun onServiceDisconnected(name: ComponentName) {
            Log.d("GameHost", "Emulation service disconnected")
            emuBound = false
            emuBinder = null
            _startedViaService = false
        }
    }

    init {
        holder.addCallback(this)
        _nativeWindow = NativeWindow(this)
        mainViewModel.gameHost = this
        Log.d("GameHost", "GameHost initialized")
    }

    /**
     * (重新)绑定当前 ANativeWindow 到渲染器
     * 强制获取新的原生指针并传递给 C#
     */
    fun rebindNativeWindow(force: Boolean = false) {
        if (_isClosed) {
            Log.d("GameHost", "Cannot rebind, GameHost is closed")
            return
        }
        try {
            _currentWindow = _nativeWindow.requeryWindowHandle()
            _nativeWindow.swapInterval = 0
            RyujinxNative.jnaInstance.deviceSetWindowHandle(currentWindowhandle)
            Log.d("GameHost", "Native window rebound, handle: $currentWindowhandle")

            val w = if (holder.surfaceFrame.width() > 0) holder.surfaceFrame.width() else width
            val h = if (holder.surfaceFrame.height() > 0) holder.surfaceFrame.height() else height
            if (w > 0 && h > 0) {
                if (MainActivity.mainViewModel?.rendererReady == true) {
                    try { 
                        RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h) 
                        Log.d("GameHost", "Renderer size set to: ${w}x${h}")
                    } catch (_: Throwable) {}
                }
                if (_inputInitialized) {
                    try { 
                        RyujinxNative.jnaInstance.inputSetClientSize(w, h) 
                        Log.d("GameHost", "Input client size set to: ${w}x${h}")
                    } catch (_: Throwable) {}
                }
            }
        } catch (e: Throwable) { 
            Log.e("GameHost", "Error rebinding native window", e)
        }
    }

    /**
     * 在成功重新附着后安全地"唤醒"交换链/视口：
     *  - 设置旋转
     *  - 两次连续的重置大小触发
     */
    fun postReattachKicks(rotation: Int?) {
        if (_isClosed) return
        try {
            val w = if (holder.surfaceFrame.width() > 0) holder.surfaceFrame.width() else width
            val h = if (holder.surfaceFrame.height() > 0) holder.surfaceFrame.height() else height
            if (w > 0 && h > 0 &&
                MainActivity.mainViewModel?.rendererReady == true &&
                _isStarted && _inputInitialized
            ) {
                try { 
                    RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h)
                    RyujinxNative.jnaInstance.inputSetClientSize(w, h)
                    Log.d("GameHost", "Post-reattach kicks applied: ${w}x${h}")
                } catch (_: Throwable) {}
                mainHandler.postDelayed({
                    try { 
                        RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h)
                        RyujinxNative.jnaInstance.inputSetClientSize(w, h)
                        Log.d("GameHost", "Delayed post-reattach kicks applied: ${w}x${h}")
                    } catch (_: Throwable) {}
                }, 32)
            }
        } catch (e: Throwable) { 
            Log.e("GameHost", "Error in postReattachKicks", e)
        }
    }

    // -------- Surface 生命周期 --------

    override fun surfaceCreated(holder: SurfaceHolder) {
        Log.d("GameHost", "Surface created")
        // 提前绑定，确保服务在启动前就绪
        ensureServiceStartedAndBound()
        rebindNativeWindow(force = true)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        Log.d("GameHost", "Surface changed: ${width}x${height}, format: $format")
        if (_isClosed) return

        // 总是重新绑定 - 即使尺寸相同
        rebindNativeWindow(force = true)

        val sizeChanged = (_width != width || _height != height)
        _width = width
        _height = height

        // 确保服务就绪并启动渲染
        ensureServiceStartedAndBound()
        start(holder)

        // 启动稳定的尺寸调整（接管合理的最终尺寸设置）
        startStabilizedResize(expectedRotation = lastRotation)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        Log.d("GameHost", "Surface destroyed")
        // 总是解除绑定（防止任务滑动时的泄漏）
        shutdownBinding()
        // 实际的模拟器关闭通过 close() / 退出游戏处理
    }

    override fun onWindowVisibilityChanged(visibility: Int) {
        super.onWindowVisibilityChanged(visibility)
        Log.d("GameHost", "Window visibility changed: $visibility")
        if (visibility != android.view.View.VISIBLE) {
            shutdownBinding()
        }
    }

    // -------- UI 进度 --------

    fun setProgress(info: String, progressVal: Float) {
        Log.d("GameHost", "Progress update: $info - $progressVal")
        showLoading?.apply {
            progressValue?.apply { this.value = progressVal }
            progress?.apply { this.value = info }
        }
    }

    fun setProgressStates(
        showLoading: MutableState<Boolean>?,
        progressValue: MutableState<Float>?,
        progress: MutableState<String>?
    ) {
        this.showLoading = showLoading
        this.progressValue = progressValue
        this.progress = progress
        showLoading?.apply { value = !isProgressHidden }
        Log.d("GameHost", "Progress states set")
    }

    fun hideProgressIndicator() {
        isProgressHidden = true
        showLoading?.apply {
            if (value == isProgressHidden) value = !isProgressHidden
        }
        Log.d("GameHost", "Progress indicator hidden")
    }

    // -------- 启动/停止模拟 --------

    private fun start(surfaceHolder: SurfaceHolder) {
        if (_isStarted) {
            Log.d("GameHost", "GameHost already started, skipping")
            return
        }

        Log.d("GameHost", "Starting GameHost...")

        // 不立即设置 _isStarted = true → 先准备一切
        rebindNativeWindow(force = true)

        game = if (mainViewModel.isMiiEditorLaunched) null else mainViewModel.gameModel

        // 初始化输入
        RyujinxNative.jnaInstance.inputInitialize(width, height)
        _inputInitialized = true
        Log.d("GameHost", "Input initialized: ${width}x${height}")

        val id = mainViewModel.physicalControllerManager?.connect()
        mainViewModel.motionSensorManager?.setControllerId(id ?: -1)
        Log.d("GameHost", "Controller connected, ID: $id")

        val currentRot = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            mainViewModel.activity.display?.rotation
        } else null
        lastRotation = currentRot
        Log.d("GameHost", "Current rotation: $currentRot")

        try {
            try { 
                RyujinxNative.jnaInstance.deviceSetWindowHandle(currentWindowhandle) 
                Log.d("GameHost", "Window handle set: $currentWindowhandle")
            } catch (_: Throwable) {}

            // 只有当渲染器 READY 且输入已初始化时才进行温和的触发
            if (width > 0 && height > 0 &&
                MainActivity.mainViewModel?.rendererReady == true &&
                _inputInitialized
            ) {
                try { 
                    RyujinxNative.jnaInstance.graphicsRendererSetSize(width, height)
                    RyujinxNative.jnaInstance.inputSetClientSize(width, height)
                    Log.d("GameHost", "Initial size set: ${width}x${height}")
                } catch (_: Throwable) {}
            }
        } catch (e: Throwable) {
            Log.e("GameHost", "Error in start preparation", e)
        }

        val qs = org.ryujinx.android.viewmodels.QuickSettings(mainViewModel.activity)

        // Host 现在被视为"已启动"
        _isStarted = true
        Log.d("GameHost", "GameHost marked as started")

        // 总是优先在服务中启动；如果绑定尚未完成 → 短暂等待，然后回退
        if (emuBound) {
            Log.d("GameHost", "Starting run loop in service (immediate)")
            startRunLoopInService()
        } else {
            Log.d("GameHost", "Service not bound, delaying start")
            mainHandler.postDelayed({
                if (!_isStarted) {
                    Log.d("GameHost", "GameHost no longer started, skipping delayed start")
                    return@postDelayed
                }
                if (emuBound) {
                    Log.d("GameHost", "Starting run loop in service (delayed)")
                    startRunLoopInService()
                } else {
                    // 回退：本地线程（应该很少发生）
                    Log.d("GameHost", "Starting run loop in local thread")
                    _guestThread = thread(start = true, name = "RyujinxGuest") { runGame() }
                }
            }, 150)
        }

        _updateThread = thread(start = true, name = "RyujinxInput/Stats") {
            var c = 0
            Log.d("GameHost", "Update thread started")
            while (_isStarted && !_isClosed) {
                try {
                    RyujinxNative.jnaInstance.inputUpdate()
                    Thread.sleep(1)
                    if (++c >= 1000) {
                        if (progressValue?.value == -1f) {
                            progress?.apply {
                                this.value = "Loading ${if (mainViewModel.isMiiEditorLaunched) "Mii Editor" else game?.titleName ?: ""}"
                            }
                        }
                        c = 0
                        mainViewModel.updateStats(
                            RyujinxNative.jnaInstance.deviceGetGameFifo(),
                            RyujinxNative.jnaInstance.deviceGetGameFrameRate(),
                            RyujinxNative.jnaInstance.deviceGetGameFrameTime()
                        )
                    }
                } catch (e: InterruptedException) {
                    Log.d("GameHost", "Update thread interrupted")
                    break
                } catch (e: Exception) {
                    if (_isStarted && !_isClosed) {
                        Log.e("GameHost", "Error in update thread", e)
                    }
                }
            }
            Log.d("GameHost", "Update thread finished")
        }
    }

    private fun runGame() {
        Log.d("GameHost", "Game thread started")
        try {
            RyujinxNative.jnaInstance.graphicsRendererRunLoop()
            Log.d("GameHost", "Game thread finished normally")
        } catch (e: Exception) {
            Log.e("GameHost", "Error in game thread", e)
        } finally {
            game?.close()
            Log.d("GameHost", "Game resources closed")
        }
    }

    /**
     * 安全关闭游戏主机
     */
    fun close() {
        synchronized(closeLock) {
            if (_isClosed) {
                Log.d("GameHost", "GameHost already closed, skipping")
                return
            }
            _isClosed = true
        }

        Log.d("GameHost", "Closing GameHost...")

        // 第一步：停止所有标志位
        _isStarted = false
        _isInit = false
        _inputInitialized = false
        Log.d("GameHost", "Flags reset")

        // 第二步：停止 UI 处理器
        RyujinxNative.jnaInstance.uiHandlerSetResponse(false, "")
        Log.d("GameHost", "UI handler stopped")

        // 第三步：停止服务中的模拟（如果在那里启动）
        try {
            if (emuBound && _startedViaService) {
                Log.d("GameHost", "Stopping emulation in service")
                emuBinder?.stopEmulation {
                    try { 
                        RyujinxNative.jnaInstance.deviceSignalEmulationClose()
                        Log.d("GameHost", "Emulation close signaled")
                    } catch (e: Exception) {
                        Log.e("GameHost", "Error signaling emulation close", e)
                    }
                }
            } else {
                Log.d("GameHost", "Not stopping service emulation (not bound or not started via service)")
            }
        } catch (e: Exception) {
            Log.e("GameHost", "Error stopping service emulation", e)
        }

        // 第四步：停止所有线程（使用更长的等待时间）
        Log.d("GameHost", "Stopping threads...")
        try {
            _updateThread?.interrupt()
            _updateThread?.join(1000)
            Log.d("GameHost", "Update thread stopped")
        } catch (e: Exception) {
            Log.e("GameHost", "Error stopping update thread", e)
        }
        
        try {
            _renderingThreadWatcher?.interrupt()
            _renderingThreadWatcher?.join(1000)
            Log.d("GameHost", "Rendering thread stopped")
        } catch (e: Exception) {
            Log.e("GameHost", "Error stopping rendering thread", e)
        }
        
        try {
            _guestThread?.interrupt()
            _guestThread?.join(1000)
            Log.d("GameHost", "Guest thread stopped")
        } catch (e: Exception) {
            Log.e("GameHost", "Error stopping guest thread", e)
        }

        // 第五步：解除服务绑定
        shutdownBinding()
        Log.d("GameHost", "Service binding shut down")

        // 第六步：停止前台服务
        try {
            mainViewModel.activity.stopService(Intent(mainViewModel.activity, EmulationService::class.java))
            Log.d("GameHost", "Foreground service stopped")
        } catch (e: Exception) {
            Log.e("GameHost", "Error stopping foreground service", e)
        }

        // 第七步：释放原生窗口（如果 NativeWindow 有 release 方法）
        try {
            if (::_nativeWindow.isInitialized) {
                _nativeWindow.release()
                Log.d("GameHost", "Native window released")
            }
        } catch (e: Exception) {
            Log.e("GameHost", "Error releasing native window", e)
        }

        // 第八步：重置窗口句柄
        _currentWindow = -1
        Log.d("GameHost", "Window handle reset")

        Log.d("GameHost", "GameHost closed successfully")
    }

    // -------- 方向/尺寸调整 --------

    /**
     * 安全设置渲染器/输入尺寸
     */
    @Synchronized
    private fun safeSetSize(w: Int, h: Int) {
        if (_isClosed || w <= 0 || h <= 0) {
            Log.d("GameHost", "Cannot set size, closed or invalid dimensions: ${w}x${h}")
            return
        }
        try {
            RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h)
            if (_isStarted && _inputInitialized) {
                RyujinxNative.jnaInstance.inputSetClientSize(w, h)
            }
            Log.d("GameHost", "Size set safely: ${w}x${h}")
        } catch (t: Throwable) {
            Log.e("GameHost", "safeSetSize failed: ${t.message}", t)
        }
    }

    /**
     * 当旋转/布局变化时由 Activity 调用
     * 检测 90°↔270° 并强制（防抖动）重新查询/调整大小
     */
    fun onOrientationOrSizeChanged(rotation: Int? = null) {
        if (_isClosed) {
            Log.d("GameHost", "Cannot handle orientation change, GameHost is closed")
            return
        }

        val old = lastRotation
        lastRotation = rotation
        Log.d("GameHost", "Orientation changed from $old to $rotation")

        val isSideFlip = (old == 1 && rotation == 3) || (old == 3 && rotation == 1)

        if (isSideFlip) {
            rebindNativeWindow(force = true)
            val now = android.os.SystemClock.uptimeMillis()
            if (now - lastKickAt >= 300L && _inputInitialized && MainActivity.mainViewModel?.rendererReady == true) {
                lastKickAt = now
                val w = if (holder.surfaceFrame.width() > 0) holder.surfaceFrame.width() else width
                val h = if (holder.surfaceFrame.height() > 0) holder.surfaceFrame.height() else height
                if (w > 0 && h > 0) {
                    try { 
                        RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h)
                        RyujinxNative.jnaInstance.inputSetClientSize(w, h)
                        Log.d("GameHost", "Side flip size adjustment: ${w}x${h}")
                    } catch (_: Throwable) {}
                }
            }
        }

        startStabilizedResize(rotation)
    }

    /**
     * 在旋转后等待一段时间直到表面获得最终尺寸，
     * 检查合理性（竖屏/横屏）然后才设置尺寸
     */
    private fun startStabilizedResize(expectedRotation: Int?) {
        if (_isClosed) {
            Log.d("GameHost", "Cannot start stabilized resize, GameHost is closed")
            return
        }

        // 如果已激活则重新启动
        if (stabilizerActive) {
            stabilizerActive = false
            Log.d("GameHost", "Stabilizer was active, restarting")
        }
        stabilizerActive = true
        Log.d("GameHost", "Starting stabilized resize")

        var attempts = 0
        var stableCount = 0
        var lastW = -1
        var lastH = -1

        val task = object : Runnable {
            override fun run() {
                if (!_isStarted || _isClosed) {
                    stabilizerActive = false
                    Log.d("GameHost", "Stabilizer stopped: not started or closed")
                    return
                }

                // 优先使用真实帧尺寸
                var w = holder.surfaceFrame.width()
                var h = holder.surfaceFrame.height()

                // 回退
                if (w <= 0 || h <= 0) {
                    w = width
                    h = height
                }

                // 如果旋转已知：强制合理性（横屏 ↔ 竖屏）
                expectedRotation?.let { rot ->
                    val landscape = (rot == 1 || rot == 3) // ROTATION_90/270
                    if (landscape && h > w) {
                        val t = w; w = h; h = t
                    } else if (!landscape && w > h) {
                        val t = w; w = h; h = t
                    }
                }

                // 稳定性测试
                if (w == lastW && h == lastH && w > 0 && h > 0) {
                    stableCount++
                } else {
                    stableCount = 0
                    lastW = w
                    lastH = h
                }

                attempts++

                // 1 个稳定滴答或最多 12 次尝试
                if ((stableCount >= 1 || attempts >= 12) && w > 0 && h > 0) {
                    safeSetSize(w, h)
                    stabilizerActive = false
                    Log.d("GameHost", "Stabilized resize completed: ${w}x${h} after $attempts attempts")
                    return
                }

                if (stabilizerActive) {
                    mainHandler.postDelayed(this, 16)
                } else {
                    Log.d("GameHost", "Stabilizer stopped before completion")
                }
            }
        }

        mainHandler.post(task)
    }

    // ===== 服务辅助方法 =====

    /** 由 Activity/Surface-Lifecycle 调用，确保 FGS 安全 */
    fun ensureServiceStartedAndBound() {
        if (_isClosed) {
            Log.d("GameHost", "Cannot ensure service, GameHost is closed")
            return
        }
        
        val act = mainViewModel.activity
        val intent = Intent(act, EmulationService::class.java)
        try {
            if (Build.VERSION.SDK_INT >= 26) {
                act.startForegroundService(intent)
                Log.d("GameHost", "Foreground service started")
            } else {
                @Suppress("DEPRECATION")
                act.startService(intent)
                Log.d("GameHost", "Service started")
            }
        } catch (e: Exception) {
            Log.e("GameHost", "Error starting service", e)
        }

        try {
            if (!emuBound) {
                act.bindService(intent, emuConn, Context.BIND_AUTO_CREATE)
                Log.d("GameHost", "Service binding initiated")
            } else {
                Log.d("GameHost", "Service already bound")
            }
        } catch (e: Exception) {
            Log.e("GameHost", "Error binding service", e)
        }
    }

    /** 由 Activity 在 onPause/onStop/onDestroy 中调用（以及在 surfaceDestroyed/onWindowVisibilityChanged 内部调用） */
    fun shutdownBinding() {
        if (emuBound) {
            try {
                mainViewModel.activity.unbindService(emuConn)
                Log.d("GameHost", "Service unbound")
            } catch (e: Exception) {
                Log.e("GameHost", "Error unbinding service", e)
            }
            emuBound = false
            emuBinder = null
            _startedViaService = false
        } else {
            Log.d("GameHost", "Service not bound, skipping unbind")
        }
    }

    private fun startRunLoopInService() {
        if (!emuBound) {
            Log.d("GameHost", "Cannot start run loop, service not bound")
            return
        }
        if (_startedViaService) {
            Log.d("GameHost", "Run loop already started via service")
            return
        }
        _startedViaService = true

        Log.d("GameHost", "Starting run loop in service")
        emuBinder?.startEmulation {
            try {
                RyujinxNative.jnaInstance.graphicsRendererRunLoop()
                Log.d("GameHost", "Run loop in service finished normally")
            } catch (t: Throwable) {
                Log.e("GameHost", "RunLoop crash in service", t)
            } finally {
                _startedViaService = false
                Log.d("GameHost", "Run loop in service marked as finished")
            }
        }
    }
    
    /**
     * 检查是否已关闭
     */
    fun isClosed(): Boolean {
        return _isClosed
    }

    /**
     * 检查是否已启动
     */
    fun isStarted(): Boolean {
        return _isStarted
    }
}
