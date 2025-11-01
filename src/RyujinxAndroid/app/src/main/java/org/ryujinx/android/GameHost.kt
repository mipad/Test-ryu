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
    private var _isClosed: Boolean = false
    private var _renderingThreadWatcher: Thread? = null
    private var _height: Int = 0
    private var _width: Int = 0
    private var _updateThread: Thread? = null
    private var _guestThread: Thread? = null
    private var _isInit: Boolean = false
    private var _isStarted: Boolean = false
    private val _nativeWindow: NativeWindow

    // 前台服务绑定相关变量
    private var emuBound = false
    private var emuBinder: EmulationService.LocalBinder? = null
    private var _startedViaService = false
    private var _inputInitialized: Boolean = false

    private val mainHandler = Handler(Looper.getMainLooper())

    // 稳定器状态
    private var stabilizerActive = false

    // 最后已知的 Android 旋转 (0,1,2,3)
    private var lastRotation: Int? = null

    // 防抖动的重置触发
    private var lastKickAt = 0L

    val currentSurface: Long
        get() = _currentWindow

    val currentWindowhandle: Long
        get() = _nativeWindow.nativePointer

    // 服务连接器
    private val emuConn = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, service: IBinder) {
            emuBinder = service as EmulationService.LocalBinder
            emuBound = true
            ghLog("EmulationService bound")

            // 如果启动已准备且没有循环运行 → 现在在服务中启动
            if (_isStarted && !_startedViaService && _guestThread == null) {
                startRunLoopInService()
            }
        }

        override fun onServiceDisconnected(name: ComponentName) {
            ghLog("EmulationService unbound")
            emuBound = false
            emuBinder = null
            _startedViaService = false
        }
    }

    init {
        holder.addCallback(this)
        _nativeWindow = NativeWindow(this)
        mainViewModel.gameHost = this
    }

    private fun ghLog(msg: String) {
        val enabled = BuildConfig.DEBUG && org.ryujinx.android.viewmodels.QuickSettings(mainViewModel.activity).enableDebugLogs
        if (enabled) Log.d("GameHost", msg)
    }

    /**
     * (重新)绑定当前 ANativeWindow 到渲染器
     * 强制获取新的原生指针并传递给 C#
     */
    fun rebindNativeWindow(force: Boolean = false) {
        if (_isClosed) return
        try {
            _currentWindow = _nativeWindow.requeryWindowHandle()
            _nativeWindow.swapInterval = 0
            RyujinxNative.jnaInstance.deviceSetWindowHandle(currentWindowhandle)

            val w = if (holder.surfaceFrame.width() > 0) holder.surfaceFrame.width() else width
            val h = if (holder.surfaceFrame.height() > 0) holder.surfaceFrame.height() else height
            if (w > 0 && h > 0) {
                if (MainActivity.mainViewModel?.rendererReady == true) {
                    try { RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h) } catch (_: Throwable) {}
                }
                if (_inputInitialized) {
                    try { RyujinxNative.jnaInstance.inputSetClientSize(w, h) } catch (_: Throwable) {}
                }
            }
        } catch (_: Throwable) { }
    }

    /**
     * 在成功重新附着后安全地"唤醒"交换链/视口：
     *  - 设置旋转
     *  - 两次连续的重置大小触发
     */
    fun postReattachKicks(rotation: Int?) {
        if (_isClosed) return
        try {
            // 注意：这里需要根据您的实际实现调整旋转设置方法
            // RyujinxNative.jnaInstance.setSurfaceRotationByAndroidRotation(rotation ?: 0)
            val w = if (holder.surfaceFrame.width() > 0) holder.surfaceFrame.width() else width
            val h = if (holder.surfaceFrame.height() > 0) holder.surfaceFrame.height() else height
            if (w > 0 && h > 0 &&
                MainActivity.mainViewModel?.rendererReady == true &&
                _isStarted && _inputInitialized
            ) {
                try { 
                    RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h)
                    RyujinxNative.jnaInstance.inputSetClientSize(w, h)
                } catch (_: Throwable) {}
                mainHandler.postDelayed({
                    try { 
                        RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h)
                        RyujinxNative.jnaInstance.inputSetClientSize(w, h)
                    } catch (_: Throwable) {}
                }, 32)
            }
        } catch (_: Throwable) { }
    }

    // -------- Surface 生命周期 --------

    override fun surfaceCreated(holder: SurfaceHolder) {
        ghLog("surfaceCreated")
        // 提前绑定，确保服务在启动前就绪
        ensureServiceStartedAndBound()
        rebindNativeWindow(force = true)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        ghLog("surfaceChanged ${width}x$height")
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
        ghLog("surfaceDestroyed → shutdownBinding()")
        // 总是解除绑定（防止任务滑动时的泄漏）
        shutdownBinding()
        // 实际的模拟器关闭通过 close() / 退出游戏处理
    }

    override fun onWindowVisibilityChanged(visibility: Int) {
        super.onWindowVisibilityChanged(visibility)
        if (visibility != android.view.View.VISIBLE) {
            ghLog("window not visible → shutdownBinding()")
            shutdownBinding()
        }
    }

    // -------- UI 进度 --------

    fun setProgress(info: String, progressVal: Float) {
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
    }

    fun hideProgressIndicator() {
        isProgressHidden = true
        showLoading?.apply {
            if (value == isProgressHidden) value = !isProgressHidden
        }
    }

    // -------- 启动/停止模拟 --------

    private fun start(surfaceHolder: SurfaceHolder) {
        if (_isStarted) return

        // 不立即设置 _isStarted = true → 先准备一切
        rebindNativeWindow(force = true)

        game = if (mainViewModel.isMiiEditorLaunched) null else mainViewModel.gameModel

        // 初始化输入
        RyujinxNative.jnaInstance.inputInitialize(width, height)
        _inputInitialized = true

        val id = mainViewModel.physicalControllerManager?.connect()
        mainViewModel.motionSensorManager?.setControllerId(id ?: -1)

        val currentRot = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            mainViewModel.activity.display?.rotation
        } else null
        lastRotation = currentRot

        try {
            // 注意：这里需要根据您的实际实现调整旋转设置方法
            // RyujinxNative.jnaInstance.setSurfaceRotationByAndroidRotation(currentRot ?: 0)
            try { RyujinxNative.jnaInstance.deviceSetWindowHandle(currentWindowhandle) } catch (_: Throwable) {}

            // 只有当渲染器 READY 且输入已初始化时才进行温和的触发
            if (width > 0 && height > 0 &&
                MainActivity.mainViewModel?.rendererReady == true &&
                _inputInitialized
            ) {
                try { 
                    RyujinxNative.jnaInstance.graphicsRendererSetSize(width, height)
                    RyujinxNative.jnaInstance.inputSetClientSize(width, height)
                } catch (_: Throwable) {}
            }
        } catch (_: Throwable) {}

        val qs = org.ryujinx.android.viewmodels.QuickSettings(mainViewModel.activity)
        // 注意：这里需要根据您的实际实现调整全屏拉伸设置
        // try { RyujinxNative.jnaInstance.graphicsSetFullscreenStretch(qs.stretchToFullscreen) } catch (_: Throwable) {}

        // Host 现在被视为"已启动"
        _isStarted = true

        // 总是优先在服务中启动；如果绑定尚未完成 → 短暂等待，然后回退
        if (emuBound) {
            startRunLoopInService()
        } else {
            ghLog("Service not yet bound → delayed runloop start")
            mainHandler.postDelayed({
                if (!_isStarted) return@postDelayed
                if (emuBound) {
                    startRunLoopInService()
                } else {
                    // 回退：本地线程（应该很少发生）
                    ghLog("Fallback: starting RunLoop in local thread")
                    _guestThread = thread(start = true, name = "RyujinxGuest") { runGame() }
                }
            }, 150)
        }

        _updateThread = thread(start = true, name = "RyujinxInput/Stats") {
            var c = 0
            while (_isStarted) {
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
            }
        }
    }

    private fun runGame() {
        RyujinxNative.jnaInstance.graphicsRendererRunLoop()
        game?.close()
    }

    fun close() {
        ghLog("close()")
        _isClosed = true
        _isInit = false
        _isStarted = false
        _inputInitialized = false

        RyujinxNative.jnaInstance.uiHandlerSetResponse(false, "")

        // 停止服务中的模拟（如果在那里启动）
        try {
            if (emuBound && _startedViaService) {
                emuBinder?.stopEmulation {
                    try { RyujinxNative.jnaInstance.deviceCloseEmulation() } catch (_: Throwable) {}
                }
            }
        } catch (_: Throwable) { }

        // 回退：停止本地线程
        try { _updateThread?.join(200) } catch (_: Throwable) {}
        try { _renderingThreadWatcher?.join(200) } catch (_: Throwable) {}

        // 解除绑定
        shutdownBinding()

        // 显式停止服务（如果仍在运行）
        try {
            mainViewModel.activity.stopService(Intent(mainViewModel.activity, EmulationService::class.java))
        } catch (_: Throwable) { }
    }

    // -------- 方向/尺寸调整 --------

    /**
     * 安全设置渲染器/输入尺寸
     */
    @Synchronized
    private fun safeSetSize(w: Int, h: Int) {
        if (_isClosed || w <= 0 || h <= 0) return
        try {
            ghLog("safeSetSize: ${w}x$h (started=$_isStarted, inputInit=$_inputInitialized)")
            RyujinxNative.jnaInstance.graphicsRendererSetSize(w, h)
            if (_isStarted && _inputInitialized) {
                RyujinxNative.jnaInstance.inputSetClientSize(w, h)
            }
        } catch (t: Throwable) {
            Log.e("GameHost", "safeSetSize failed: ${t.message}", t)
        }
    }

    /**
     * 当旋转/布局变化时由 Activity 调用
     * 检测 90°↔270° 并强制（防抖动）重新查询/调整大小
     */
    fun onOrientationOrSizeChanged(rotation: Int? = null) {
        if (_isClosed) return

        val old = lastRotation
        lastRotation = rotation

        val isSideFlip = (old == 1 && rotation == 3) || (old == 3 && rotation == 1)

        if (isSideFlip) {
            // 注意：这里需要根据您的实际实现调整旋转设置方法
            // try { RyujinxNative.jnaInstance.setSurfaceRotationByAndroidRotation(rotation ?: 0) } catch (_: Throwable) {}
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
        if (_isClosed) return

        // 如果已激活则重新启动
        if (stabilizerActive) {
            stabilizerActive = false
        }
        stabilizerActive = true

        var attempts = 0
        var stableCount = 0
        var lastW = -1
        var lastH = -1

        val task = object : Runnable {
            override fun run() {
                if (!_isStarted || _isClosed) {
                    stabilizerActive = false
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
                    ghLog("resize stabilized after $attempts ticks → ${w}x$h")
                    safeSetSize(w, h)
                    stabilizerActive = false
                    return
                }

                if (stabilizerActive) {
                    mainHandler.postDelayed(this, 16)
                }
            }
        }

        mainHandler.post(task)
    }

    // ===== 服务辅助方法 =====

    /** 由 Activity/Surface-Lifecycle 调用，确保 FGS 安全 */
    fun ensureServiceStartedAndBound() {
        val act = mainViewModel.activity
        val intent = Intent(act, EmulationService::class.java)
        try {
            if (Build.VERSION.SDK_INT >= 26) {
                act.startForegroundService(intent)
            } else {
                @Suppress("DEPRECATION")
                act.startService(intent)
            }
        } catch (_: Throwable) { }

        try {
            if (!emuBound) {
                act.bindService(intent, emuConn, Context.BIND_AUTO_CREATE)
            }
        } catch (_: Throwable) { }
    }

    /** 由 Activity 在 onPause/onStop/onDestroy 中调用（以及在 surfaceDestroyed/onWindowVisibilityChanged 内部调用） */
    fun shutdownBinding() {
        if (emuBound) {
            try {
                mainViewModel.activity.unbindService(emuConn)
            } catch (_: Throwable) { }
            emuBound = false
            emuBinder = null
            _startedViaService = false
            ghLog("shutdownBinding() → unbound")
        }
    }

    private fun startRunLoopInService() {
        if (!emuBound) return
        if (_startedViaService) return
        _startedViaService = true

        emuBinder?.startEmulation {
            try {
                RyujinxNative.jnaInstance.graphicsRendererRunLoop()
            } catch (t: Throwable) {
                Log.e("GameHost", "RunLoop crash in service", t)
            } finally {
                _startedViaService = false
            }
        }
        ghLog("RunLoop started in EmulationService")
    }
}
