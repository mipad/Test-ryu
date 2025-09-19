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
        
        // 初始化后应用控制器设置
        applyControllerSettings()
    }

    // 安全的控制器设置应用方法
    fun applyControllerSettings() {
        if (!_isInit) {
            return
        }

        try {
            val quickSettings = QuickSettings(this)
            
            // 只设置虚拟控制器（设备ID 0）- 玩家1
            val player1Setting = quickSettings.getPlayerSetting(1)
            if (player1Setting != null && player1Setting.isConnected) {
                // 确保控制器类型值在有效范围内 (0-4)
                val controllerType = player1Setting.controllerType.coerceIn(0, 4)
                // 将控制器类型索引转换为位掩码值
                val controllerTypeBitmask = controllerTypeIndexToBitmask(controllerType)
                RyujinxNative.jnaInstance.setControllerType(0, controllerTypeBitmask)
                android.util.Log.d("MainActivity", "Controller type set for device 0: $controllerTypeBitmask")
            }
            
            // 设置其他玩家的控制器类型（设备ID 1-7）
            for (i in 2..8) {
                val playerSetting = quickSettings.getPlayerSetting(i)
                if (playerSetting != null && playerSetting.isConnected) {
                    val deviceId = i - 1 // 设备ID从1开始
                    // 确保控制器类型值在有效范围内 (0-4)
                    val controllerType = playerSetting.controllerType.coerceIn(0, 4)
                    // 将控制器类型索引转换为位掩码值
                    val controllerTypeBitmask = controllerTypeIndexToBitmask(controllerType)
                    RyujinxNative.jnaInstance.setControllerType(deviceId, controllerTypeBitmask)
                    android.util.Log.d("MainActivity", "Controller type set for device $deviceId: $controllerTypeBitmask")
                }
            }
        } catch (e: Exception) {
            android.util.Log.e("MainActivity", "Failed to apply controller settings", e)
        }
    }

    // 新增方法：将控制器类型索引转换为位掩码值
    private fun controllerTypeIndexToBitmask(controllerTypeIndex: Int): Int {
        return when (controllerTypeIndex) {
            0 -> 1  // ProController = 1 << 0
            1 -> 8  // JoyconLeft = 1 << 3
            2 -> 16 // JoyconRight = 1 << 4
            3 -> 4  // JoyconPair = 1 << 2
            4 -> 2  // Handheld = 1 << 1
            else -> 1 // 默认返回ProController
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
            
            // 恢复时重新应用控制器设置
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
