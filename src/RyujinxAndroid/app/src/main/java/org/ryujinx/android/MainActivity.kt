package org.ryujinx.android

import android.annotation.SuppressLint
import android.content.pm.ActivityInfo
import android.os.Bundle
import android.os.Environment
import android.os.Handler
import android.os.Looper
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
import org.ryujinx.android.ui.theme.RyujinxAndroidTheme
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings
import org.ryujinx.android.views.MainView


class MainActivity : BaseActivity() {
    private var physicalControllerManager: PhysicalControllerManager =
        PhysicalControllerManager(this)
    private lateinit var motionSensorManager: MotionSensorManager
    private var _isInit: Boolean = false
    var isGameRunning = false
    var isActive = false
    var storageHelper: SimpleStorageHelper? = null
    lateinit var uiHandler: UiHandler
    private val handler = Handler(Looper.getMainLooper())

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

    private fun initialize() {
        if (_isInit)
            return

        val appPath: String = AppPath

        var quickSettings = QuickSettings(this)
        
        // 设置跳过内存屏障
        RyujinxNative.jnaInstance.setSkipMemoryBarriers(quickSettings.skipMemoryBarriers)
        
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
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Trace.ordinal,
            quickSettings.enableTraceLogs
        )
        RyujinxNative.jnaInstance.loggingEnabledGraphicsLog(
            quickSettings.enableTraceLogs
        )
        val success =
            RyujinxNative.jnaInstance.javaInitialize(appPath, JNIEnv.CURRENT)

        uiHandler = UiHandler()
        _isInit = success
        
        // Native初始化成功后应用控制器设置
        if (success) {
            applyControllerSettings()
        }
    }

    // 安全的控制器设置应用方法
    fun applyControllerSettings() {
        if (!_isInit) {
            // Native层未初始化，延迟100ms重试
            handler.postDelayed({ applyControllerSettings() }, 100)
            return
        }

        try {
            val quickSettings = QuickSettings(this)
            
            // 设置虚拟控制器的类型（设备ID 0）
            val controllerTypeValue = when (quickSettings.controllerType) {
                0 -> 0 // Pro Controller
                1 -> 1 // Joy-Con Left
                2 -> 2 // Joy-Con Right
                3 -> 3 // Joy-Con Pair
                4 -> 4 // Handheld
                else -> 0 // Default to Pro Controller
            }
            
            RyujinxNative.jnaInstance.setControllerType(0, controllerTypeValue)
            
            // 如果有物理控制器，也设置它们的类型
            val connectedControllers = ControllerManager.connectedControllers.value ?: emptyList()
            connectedControllers.forEachIndexed { index, controller ->
                if (!controller.isVirtual) {
                    // 物理控制器从设备ID 1开始
                    val deviceId = index + 1
                    val physicalControllerTypeValue = when (controller.controllerType) {
                        ControllerType.PRO_CONTROLLER -> 0
                        ControllerType.JOYCON_LEFT -> 1
                        ControllerType.JOYCON_RIGHT -> 2
                        ControllerType.JOYCON_PAIR -> 3
                        ControllerType.HANDHELD -> 4
                    }
                    RyujinxNative.jnaInstance.setControllerType(deviceId, physicalControllerTypeValue)
                }
            }
        } catch (e: Exception) {
            // 捕获任何异常，防止崩溃
            e.printStackTrace()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        motionSensorManager = MotionSensorManager(this)
        Thread.setDefaultUncaughtExceptionHandler(crashHandler)

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

        mainViewModel = MainViewModel(this)
        mainViewModel!!.physicalControllerManager = physicalControllerManager
        mainViewModel!!.motionSensorManager = motionSensorManager

        mainViewModel!!.refreshFirmwareVersion()

        mainViewModel?.apply {
            setContent {
                RyujinxAndroidTheme {
                    // A surface container using the 'background' color from the theme
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

    override fun onStop() {
        super.onStop()
        isActive = false

        if (isGameRunning) {
            mainViewModel?.performanceManager?.setTurboMode(false)
        }
    }

    override fun onResume() {
        super.onResume()
        isActive = true

        if (isGameRunning) {
            setFullScreen(true)
            if (QuickSettings(this).enableMotion)
                motionSensorManager.register()
            
            // 应用恢复时重新应用控制器设置
            applyControllerSettings()
        }
    }

    override fun onPause() {
        super.onPause()
        isActive = true

        if (isGameRunning) {
            mainViewModel?.performanceManager?.setTurboMode(false)
        }

        motionSensorManager.unregister()
    }
}
