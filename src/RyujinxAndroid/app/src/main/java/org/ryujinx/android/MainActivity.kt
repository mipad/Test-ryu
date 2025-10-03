package org.ryujinx.android

import android.annotation.SuppressLint
import android.app.PictureInPictureParams
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.graphics.Rect
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.util.Rational
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
    
    // 画中画相关变量
    private var isInPictureInPictureMode = false
    private val pipParamsBuilder by lazy { PictureInPictureParams.Builder() }

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

    // 进入画中画模式
    fun enterPictureInPictureMode() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && isGameRunning) {
            try {
                val rational = Rational(width, height)
                pipParamsBuilder.setAspectRatio(rational)
                
                val params = pipParamsBuilder.build()
                setPictureInPictureParams(params)
                enterPictureInPictureMode(params)
                isInPictureInPictureMode = true
                
                // 隐藏不必要的UI元素
                setFullScreen(true)
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
    }

    // 退出画中画模式
    fun exitPictureInPictureMode() {
        isInPictureInPictureMode = false
        // 恢复UI状态
        setFullScreen(true)
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // 当用户点击Home键或切换到其他应用时，自动进入画中画模式
        if (isGameRunning && !isInPictureInPictureMode) {
            enterPictureInPictureMode()
        }
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        this.isInPictureInPictureMode = isInPictureInPictureMode
        
        if (isInPictureInPictureMode) {
            // 进入画中画模式时的处理
            motionSensorManager.unregister() // 在画中画模式下禁用运动传感器
            mainViewModel?.performanceManager?.setTurboMode(false) // 降低性能消耗
        } else {
            // 退出画中画模式时的处理
            if (isActive && QuickSettings(this).enableMotion) {
                motionSensorManager.register()
            }
            if (isGameRunning) {
                mainViewModel?.performanceManager?.setTurboMode(true)
            }
            setFullScreen(true)
        }
    }

    override fun onStop() {
        super.onStop()
        isActive = false

        if (isGameRunning && !isInPictureInPictureMode) {
            mainViewModel?.performanceManager?.setTurboMode(false)
        }
    }

    override fun onResume() {
        super.onResume()
        isActive = true

        if (isGameRunning && !isInPictureInPictureMode) {
            setFullScreen(true)
            if (QuickSettings(this).enableMotion)
                motionSensorManager.register()
        }
    }

    override fun onPause() {
        super.onPause()
        isActive = false

        if (isGameRunning && !isInPictureInPictureMode) {
            mainViewModel?.performanceManager?.setTurboMode(false)
        }

        if (!isInPictureInPictureMode) {
            motionSensorManager.unregister()
        }
    }

    @SuppressLint("RestrictedApi")
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        // 在画中画模式下禁用部分输入
        if (isInPictureInPictureMode) {
            when (event.keyCode) {
                KeyEvent.KEYCODE_BACK -> {
                    if (event.action == KeyEvent.ACTION_UP) {
                        // 在画中画模式下点击返回键可以退出画中画
                        exitPictureInPictureMode()
                    }
                    return true
                }
            }
        }
        
        event.apply {
            if (physicalControllerManager.onKeyEvent(this))
                return true
        }
        return super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(ev: MotionEvent?): Boolean {
        // 在画中画模式下禁用运动事件
        if (!isInPictureInPictureMode) {
            ev?.apply {
                physicalControllerManager.onMotionEvent(this)
            }
        }
        return super.dispatchGenericMotionEvent(ev)
    }
}
