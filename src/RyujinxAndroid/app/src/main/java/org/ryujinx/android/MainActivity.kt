package org.ryujinx.android

import android.annotation.SuppressLint
import android.content.Intent
import android.content.pm.ActivityInfo
import android.os.Bundle
import android.os.Environment
//import android.util.Log
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.WindowManager
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.ui.Modifier
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.anggrayudi.storage.SimpleStorageHelper
import com.sun.jna.JNIEnv
import org.ryujinx.android.service.EmulationService
import org.ryujinx.android.ui.theme.RyujinxAndroidTheme
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings
import org.ryujinx.android.views.MainView
import android.os.Handler
import android.os.Looper
import androidx.lifecycle.Lifecycle
import android.content.BroadcastReceiver
import android.content.IntentFilter
import android.content.Context
import android.Manifest
import android.content.pm.PackageManager
import androidx.core.content.ContextCompat
import androidx.activity.result.contract.ActivityResultContracts

class MainActivity : BaseActivity() {
    private var physicalControllerManager: PhysicalControllerManager =
        PhysicalControllerManager(this)
    private lateinit var motionSensorManager: MotionSensorManager
    private var _isInit: Boolean = false
    
    // 后台稳定性相关变量
    private val handler = Handler(Looper.getMainLooper())
    private val ENABLE_PRESENT_DELAY_MS = 400L
    private val REATTACH_DELAY_MS = 300L
    private var wantPresentEnabled = false
    private val TAG_FG = "FgPresent"
    
    // 僵尸进程检测
    private val PREFS = "emu_core"
    private val KEY_EMU_RUNNING = "emu_running"
    
    // 后台暂停相关变量
    private var autoPaused = false
    
    var isGameRunning = false
    var isActive = false
    var storageHelper: SimpleStorageHelper? = null
    lateinit var uiHandler: UiHandler

    // 服务停止接收器
    private val serviceStopReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.action == EmulationService.ACTION_STOPPED) {
                handler.removeCallbacks(reattachWindowWhenReady)
                handler.removeCallbacks(enablePresentWhenReady)
                clearEmuRunningFlag()
                hardColdReset("service stopped broadcast")
            }
        }
    }

    // 通知权限请求
    private val requestNotifPerm = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { /* optional: Log/Toast */ }

    companion object {
        var mainViewModel: MainViewModel? = null
        var AppPath: String = ""
        var StorageHelper: SimpleStorageHelper? = null
        val performanceMonitor = PerformanceMonitor()

        @JvmStatic
        fun frameEnded() {
            mainViewModel?.activity?.apply {
                if (isActive && QuickSettings(this).enablePerformanceMode) {
                    mainViewModel?.performanceManager?.setTurboMode(true)
                }
            }
            mainViewModel?.gameHost?.hideProgressIndicator()
        }
    }

    init {
        storageHelper = SimpleStorageHelper(this)
        StorageHelper = storageHelper
        System.loadLibrary("ryujinxjni")
        initVm()
    }

    private external fun initVm()

    // 渲染控制方法
    private fun setPresentEnabled(enabled: Boolean, reason: String) {
        wantPresentEnabled = enabled
        try {
            RyujinxNative.jnaInstance.graphicsSetPresentEnabled(enabled)
            //Log.d(TAG_FG, "present=${if (enabled) "ENABLED" else "DISABLED"} ($reason)")
        } catch (_: Throwable) {
            //Log.d(TAG_FG, "native toggle not available ($reason)")
        }
    }

    private val enablePresentWhenReady = object : Runnable {
        override fun run() {
            val isReallyResumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) && isActive
            val hasFocusNow = hasWindowFocus()
            val rendererReady = MainActivity.mainViewModel?.rendererReady == true

            if (!isReallyResumed || !hasFocusNow || !rendererReady) {
                handler.postDelayed(this, ENABLE_PRESENT_DELAY_MS)
                return
            }
            setPresentEnabled(true, "focus regained + delay")
        }
    }

    private val reattachWindowWhenReady = object : Runnable {
        override fun run() {
            val isReallyResumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) && isActive
            val hasFocusNow = hasWindowFocus()
            if (!isReallyResumed || !hasFocusNow) {
                handler.postDelayed(this, REATTACH_DELAY_MS)
                return
            }

            try { mainViewModel?.gameHost?.rebindNativeWindow(force = true) } catch (_: Throwable) {}

            // 修复：移除不存在的 reattachWindowIfReady 方法调用
            // if (!RyujinxNative.jnaInstance.reattachWindowIfReady()) {
            //     handler.postDelayed(this, REATTACH_DELAY_MS)
            //     return
            // }
            //Log.d(TAG_FG, "window reattached")
        }
    }

    private fun initialize() {
        if (_isInit)
            return

        val appPath: String = AppPath

        var quickSettings = QuickSettings(this)
        
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Debug.ordinal,
            quickSettings.enableDebugLogs
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Info.ordinal,
            quickSettings.enableInfoLogs
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Stub.ordinal,
            quickSettings.enableStubLogs
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Warning.ordinal,
            quickSettings.enableWarningLogs
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Error.ordinal,
            quickSettings.enableErrorLogs
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.AccessLog.ordinal,
            quickSettings.enableAccessLogs
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Guest.ordinal,
            quickSettings.enableGuestLogs
        )
        RyujinxNative.jnaInstance.loggingEnabledGraphicsLog(
            quickSettings.enableTraceLogs
        )
        val success =
            RyujinxNative.jnaInstance.javaInitialize(appPath, JNIEnv.CURRENT)

        uiHandler = UiHandler()
        _isInit = success
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        
        // 确保通知权限
        ensureNotificationPermission()

        motionSensorManager = MotionSensorManager(this)
        Thread.setDefaultUncaughtExceptionHandler(crashHandler)

        // 僵尸进程检测
        coldResetIfZombie("onCreate")

        if (
            !Environment.isExternalStorageManager()
        ) {
            storageHelper?.storage?.requestFullStorageAccess()
        }

        AppPath = this.getExternalFilesDir(null)!!.absolutePath

        initialize()

        window.attributes.layoutInDisplayCutoutMode =
            WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        WindowCompat.setDecorFitsSystemWindows(window, false)
        // 保持屏幕常亮
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        mainViewModel = MainViewModel(this)
        mainViewModel!!.physicalControllerManager = physicalControllerManager
        mainViewModel!!.motionSensorManager = motionSensorManager

        mainViewModel!!.refreshFirmwareVersion()

        mainViewModel?.apply {
            setContent {
                RyujinxAndroidTheme {
                    Surface(
                        modifier = Modifier.fillMaxSize(),
                        color = MaterialTheme.colorScheme.background
                    ) {
                        MainView.Main(mainViewModel = this)
                    }
                }
            }
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        storageHelper?.onSaveInstanceState(outState)
        super.onSaveInstanceState(outState)
    }

    override fun onRestoreInstanceState(savedInstanceState: Bundle) {
        super.onRestoreInstanceState(savedInstanceState)
        storageHelper?.onRestoreInstanceState(savedInstanceState)
    }

    // 增强的生命周期管理
    override fun onStart() {
        super.onStart()
        coldResetIfZombie("onStart")

        if (isGameRunning && MainActivity.mainViewModel?.rendererReady == true) {
            try {
                RyujinxNative.jnaInstance.graphicsSetPresentEnabled(true)
               // Log.d(TAG_FG, "present=ENABLED (onStart)")
            } catch (_: Throwable) {}
        } else {
            //Log.d(TAG_FG, "skip enable present (onStart) — rendererReady=${MainActivity.mainViewModel?.rendererReady}")
            setPresentEnabled(false, "cold reset: onStart (no game)")
        }
    }

    override fun onStop() {
        super.onStop()
        if (isGameRunning) {
            handler.removeCallbacks(reattachWindowWhenReady)
            handler.removeCallbacks(enablePresentWhenReady)
            setPresentEnabled(false, "onStop")
            // 修复：移除不存在的 detachWindow 方法调用
            // try { RyujinxNative.jnaInstance.detachWindow() } catch (_: Throwable) {}
        }
        // 重要：绑定安全解除（防止泄漏）
        try { mainViewModel?.gameHost?.shutdownBinding() } catch (_: Throwable) {}
    }

    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        if (level >= android.content.ComponentCallbacks2.TRIM_MEMORY_UI_HIDDEN && isGameRunning) {
            if (MainActivity.mainViewModel?.rendererReady == true) {
                try {
                    RyujinxNative.jnaInstance.graphicsSetPresentEnabled(false)
                   // Log.d(TAG_FG, "present=DISABLED (onTrimMemory:$level)")
                } catch (_: Throwable) {}
            } else {
               // Log.d(TAG_FG, "skip disable present (onTrimMemory) — rendererReady=${MainActivity.mainViewModel?.rendererReady}")
            }
        }
    }

    override fun onResume() {
        super.onResume()
        isActive = true

        coldResetIfZombie("onResume")

        // 注册服务停止接收器
        try {
            if (android.os.Build.VERSION.SDK_INT >= 33) {
                registerReceiver(
                    serviceStopReceiver,
                    IntentFilter(EmulationService.ACTION_STOPPED),
                    Context.RECEIVER_EXPORTED
                )
            } else {
                @Suppress("DEPRECATION")
                registerReceiver(serviceStopReceiver, IntentFilter(EmulationService.ACTION_STOPPED))
            }
        } catch (_: Throwable) {}

        handler.removeCallbacks(reattachWindowWhenReady)
        handler.removeCallbacks(enablePresentWhenReady)

        try { mainViewModel?.gameHost?.rebindNativeWindow(force = true) } catch (_: Throwable) {}

        if (isGameRunning) {
            setFullScreen(true)
            if (QuickSettings(this).enableMotion)
                motionSensorManager.register()
            
            handler.postDelayed(reattachWindowWhenReady, REATTACH_DELAY_MS)
            if (hasWindowFocus()) {
                handler.postDelayed(enablePresentWhenReady, ENABLE_PRESENT_DELAY_MS)
            }
            
            // 恢复模拟器（如果是自动暂停的）
            if (autoPaused) {
                RyujinxNative.resumeEmulation()
                autoPaused = false
            }
        } else {
            setPresentEnabled(false, "cold reset: onResume (no game)")
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!isGameRunning) return

        handler.removeCallbacks(reattachWindowWhenReady)
        handler.removeCallbacks(enablePresentWhenReady)

        if (hasFocus && isActive) {
            // 首先确保绑定存在
            try { mainViewModel?.gameHost?.ensureServiceStartedAndBound() } catch (_: Throwable) {}

            setPresentEnabled(false, "focus gained → pre-rebind")
            try { mainViewModel?.gameHost?.rebindNativeWindow(force = true) } catch (_: Throwable) {}
            handler.postDelayed(reattachWindowWhenReady, 150L)
            handler.postDelayed(enablePresentWhenReady, 450L)
        } else {
            setPresentEnabled(false, "focus lost")
            // 修复：移除不存在的 detachWindow 方法调用
            // try { RyujinxNative.jnaInstance.detachWindow() } catch (_: Throwable) {}
        }
    }

    override fun onPause() {
        super.onPause()
        isActive = false

        handler.removeCallbacks(reattachWindowWhenReady)
        handler.removeCallbacks(enablePresentWhenReady)

        if (isGameRunning) {
            setPresentEnabled(false, "onPause")
            // 修复：移除不存在的 detachWindow 方法调用
            // try { RyujinxNative.jnaInstance.detachWindow() } catch (_: Throwable) {}
            mainViewModel?.performanceManager?.setTurboMode(false)
            motionSensorManager.unregister()
            
            // 暂停模拟器（如果不是已经暂停的）
            if (!autoPaused && !RyujinxNative.isEmulationPaused()) {
                RyujinxNative.pauseEmulation()
                autoPaused = true
            }
        }

        try { unregisterReceiver(serviceStopReceiver) } catch (_: Throwable) {}

        // 绑定清理（防止任务滑动时的泄漏）
        try { mainViewModel?.gameHost?.shutdownBinding() } catch (_: Throwable) {}
    }

    override fun onDestroy() {
        handler.removeCallbacks(enablePresentWhenReady)
        handler.removeCallbacks(reattachWindowWhenReady)
        // 如果Activity死亡 → 保证解除绑定
        try { mainViewModel?.gameHost?.shutdownBinding() } catch (_: Throwable) {}
        super.onDestroy()
    }

    fun setFullScreen(fullscreen: Boolean) {
        requestedOrientation =
            if (fullscreen) ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE else ActivityInfo.SCREEN_ORIENTATION_FULL_USER

        val insets = WindowCompat.getInsetsController(window, window.decorView)

        insets.apply {
        if (fullscreen) {
            insets.hide(WindowInsetsCompat.Type.statusBars() or WindowInsetsCompat.Type.navigationBars())
            insets.systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        } else {
            insets.show(WindowInsetsCompat.Type.statusBars() or WindowInsetsCompat.Type.navigationBars())
            insets.systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_DEFAULT
        }
        }
    }

    @SuppressLint("RestrictedApi")
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        event.apply {
            if (physicalControllerManager.onKeyEvent(this))
                return true
        }
        return super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(ev: MotionEvent?): Boolean {
        ev?.apply {
            physicalControllerManager.onMotionEvent(this)
        }
        return super.dispatchGenericMotionEvent(ev)
    }

    // 辅助方法
    private fun ensureNotificationPermission() {
        if (android.os.Build.VERSION.SDK_INT >= 33) {
            val granted = ContextCompat.checkSelfPermission(
                this, Manifest.permission.POST_NOTIFICATIONS
            ) == PackageManager.PERMISSION_GRANTED
            if (!granted) {
                requestNotifPerm.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    private fun setEmuRunningFlag(value: Boolean) {
        try {
            getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit()
                .putBoolean(KEY_EMU_RUNNING, value)
                .apply()
        } catch (_: Throwable) { }
    }

    private fun clearEmuRunningFlag() = setEmuRunningFlag(false)

    private fun hardColdReset(reason: String) {
       // Log.d(TAG_FG, "Cold graphics reset ($reason)")
        isGameRunning = false
        mainViewModel?.rendererReady = false
        autoPaused = false

        try { setPresentEnabled(false, "cold reset: $reason") } catch (_: Throwable) {}
        // 修复：移除不存在的 detachWindow 方法调用
        // try { RyujinxNative.jnaInstance.detachWindow() } catch (_: Throwable) {}

        try { stopService(Intent(this, EmulationService::class.java)) } catch (_: Throwable) {}

        // 修复：移除不存在的属性访问
        // try { mainViewModel?.loadGameModel?.value = null } catch (_: Throwable) {}
        // try { mainViewModel?.bootPath?.value = "" } catch (_: Throwable) {}
        // try { mainViewModel?.forceNceAndPptc?.value = false } catch (_: Throwable) {}
    }

    private fun coldResetIfZombie(phase: String) {
        try {
            val zombie = getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getBoolean(KEY_EMU_RUNNING, false)
            if (zombie) {
                clearEmuRunningFlag()
                setPresentEnabled(false, "kill stray: $phase")
                hardColdReset("kill stray: $phase")
            }
        } catch (_: Throwable) { }
    }
}

