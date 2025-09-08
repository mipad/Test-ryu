package org.ryujinx.android

import android.annotation.SuppressLint
import android.content.pm.ActivityInfo
import android.os.Bundle
import android.os.Environment
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

        // 初始化日志系统
        LogToFile.initialize(this)
        LogToFile.log("MainActivity", "Initializing application")

        val appPath: String = AppPath
        LogToFile.log("MainActivity", "App path: $appPath")

        var quickSettings = QuickSettings(this)
        LogToFile.log("MainActivity", "Quick settings loaded")
        
        // 设置跳过内存屏障
        RyujinxNative.jnaInstance.setSkipMemoryBarriers(quickSettings.skipMemoryBarriers)
        LogToFile.log("MainActivity", "Skip memory barriers: ${quickSettings.skipMemoryBarriers}")
        
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
        LogToFile.log("MainActivity", "Logging settings applied")
        
        val success =
            RyujinxNative.jnaInstance.javaInitialize(appPath, JNIEnv.CURRENT)

        LogToFile.log("MainActivity", "Java initialization result: $success")

        // 初始化 Oboe 音频
        if (success) {
            LogToFile.log("MainActivity", "Initializing Oboe audio")
            NativeHelpers.instance.initOboeAudio()
            LogToFile.log("MainActivity", "Oboe audio initialization called")
        } else {
            LogToFile.log("MainActivity", "Skipping Oboe audio initialization due to failed Java init")
        }

        uiHandler = UiHandler()
        _isInit = success
        LogToFile.log("MainActivity", "Initialization complete: $_isInit")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        LogToFile.log("MainActivity", "onCreate called")

        motionSensorManager = MotionSensorManager(this)
        Thread.setDefaultUncaughtExceptionHandler(crashHandler)

        if (
            !Environment.isExternalStorageManager()
        ) {
            LogToFile.log("MainActivity", "Requesting full storage access")
            storageHelper?.storage?.requestFullStorageAccess()
        }

        AppPath = this.getExternalFilesDir(null)!!.absolutePath
        LogToFile.log("MainActivity", "App path set to: $AppPath")

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
        LogToFile.log("MainActivity", "UI setup complete")
    }

    override fun onSaveInstanceState(outState: Bundle) {
        LogToFile.log("MainActivity", "onSaveInstanceState called")
        storageHelper?.onSaveInstanceState(outState)
        super.onSaveInstanceState(outState)
    }

    override fun onRestoreInstanceState(savedInstanceState: Bundle) {
        super.onRestoreInstanceState(savedInstanceState)
        LogToFile.log("MainActivity", "onRestoreInstanceState called")
        storageHelper?.onRestoreInstanceState(savedInstanceState)
    }

    fun setFullScreen(fullscreen: Boolean) {
        LogToFile.log("MainActivity", "Setting fullscreen: $fullscreen")
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
        LogToFile.log("MainActivity", "onStop called")
        isActive = false

        if (isGameRunning) {
            LogToFile.log("MainActivity", "Disabling turbo mode and shutting down Oboe audio")
            mainViewModel?.performanceManager?.setTurboMode(false)
            // 关闭 Oboe 音频
            NativeHelpers.instance.shutdownOboeAudio()
        }
    }

    override fun onResume() {
        super.onResume()
        LogToFile.log("MainActivity", "onResume called")
        isActive = true

        if (isGameRunning) {
            LogToFile.log("MainActivity", "Game is running, setting fullscreen and initializing Oboe audio")
            setFullScreen(true)
            if (QuickSettings(this).enableMotion)
                motionSensorManager.register()
            // 重新初始化 Oboe 音频
            NativeHelpers.instance.initOboeAudio()
        }
    }

    override fun onPause() {
        super.onPause()
        LogToFile.log("MainActivity", "onPause called")
        isActive = true

        if (isGameRunning) {
            LogToFile.log("MainActivity", "Game is running, disabling turbo mode and shutting down Oboe audio")
            mainViewModel?.performanceManager?.setTurboMode(false)
            // 关闭 Oboe 音频
            NativeHelpers.instance.shutdownOboeAudio()
        }

        motionSensorManager.unregister()
    }

    override fun onDestroy() {
        super.onDestroy()
        LogToFile.log("MainActivity", "onDestroy called")
        LogToFile.close()
    }
}
