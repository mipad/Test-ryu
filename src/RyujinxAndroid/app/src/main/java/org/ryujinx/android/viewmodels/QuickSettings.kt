package org.ryujinx.android.viewmodels

import android.app.Activity
import android.content.SharedPreferences
import androidx.preference.PreferenceManager
import org.ryujinx.android.ControllerType
import org.ryujinx.android.RegionCode
import org.ryujinx.android.SystemLanguage
import org.ryujinx.android.RyujinxNative

class QuickSettings(val activity: Activity) {
    var ignoreMissingServices: Boolean
    var enablePtc: Boolean
    var enableJitCacheEviction: Boolean
    var enableDocked: Boolean
    var enableVsync: Boolean
    var useNce: Boolean
    var useVirtualController: Boolean
    var isHostMapped: Boolean
    var enableShaderCache: Boolean
    var enableTextureRecompression: Boolean
    var resScale: Float
    var aspectRatio: Int // 0: 4:3, 1: 16:9, 2: 16:10, 3: 21:9, 4: 32:9, 5: Stretched
    var isGrid: Boolean
    var useSwitchLayout: Boolean
    var enableMotion: Boolean
    var enablePerformanceMode: Boolean
    var controllerStickSensitivity: Float
    var skipMemoryBarriers: Boolean // 新增：跳过内存屏障
    var regionCode: Int // 新增：区域代码
    var systemLanguage: Int // 新增：系统语言
    var audioEngineType: Int // 0=禁用，1=OpenAL
    var scalingFilter: Int // 新增：缩放过滤器
    var scalingFilterLevel: Int // 新增：缩放过滤器级别
    var antiAliasing: Int // 新增：抗锯齿模式 0=None, 1=Fxaa, 2=SmaaLow, 3=SmaaMedium, 4=SmaaHigh, 5=SmaaUltra
    var memoryConfiguration: Int // 新增：内存配置 0=4GB, 1=4GB Applet Dev, 2=4GB System Dev, 3=6GB, 4=6GB Applet Dev, 5=8GB
    var controllerType: Int // 新增：控制器类型 0=Pro, 1=JoyConL, 2=JoyConR, 3=Pair, 4=Handheld

    // Logs
    var enableDebugLogs: Boolean
    var enableStubLogs: Boolean
    var enableInfoLogs: Boolean
    var enableWarningLogs: Boolean
    var enableErrorLogs: Boolean
    var enableGuestLogs: Boolean
    var enableAccessLogs: Boolean
    var enableTraceLogs: Boolean
    var enableGraphicsLogs: Boolean

    private var sharedPref: SharedPreferences =
        PreferenceManager.getDefaultSharedPreferences(activity)

    init {
        isHostMapped = sharedPref.getBoolean("isHostMapped", true)
        useNce = sharedPref.getBoolean("useNce", true)
        enableVsync = sharedPref.getBoolean("enableVsync", true)
        enableDocked = sharedPref.getBoolean("enableDocked", true)
        enablePtc = sharedPref.getBoolean("enablePtc", true)
        enableJitCacheEviction = sharedPref.getBoolean("enableJitCacheEviction", true)
        ignoreMissingServices = sharedPref.getBoolean("ignoreMissingServices", false)
        enableShaderCache = sharedPref.getBoolean("enableShaderCache", true)
        enableTextureRecompression = sharedPref.getBoolean("enableTextureRecompression", false)
        resScale = sharedPref.getFloat("resScale", 1f)
        aspectRatio = sharedPref.getInt("aspect_ratio", 1) // 默认使用16:9
        useVirtualController = sharedPref.getBoolean("useVirtualController", true)
        isGrid = sharedPref.getBoolean("isGrid", true)
        useSwitchLayout = sharedPref.getBoolean("useSwitchLayout", true)
        enableMotion = sharedPref.getBoolean("enableMotion", true)
        enablePerformanceMode = sharedPref.getBoolean("enablePerformanceMode", true)
        controllerStickSensitivity = sharedPref.getFloat("controllerStickSensitivity", 1.0f)
        skipMemoryBarriers = sharedPref.getBoolean("skipMemoryBarriers", false) // 初始化
        regionCode = sharedPref.getInt("regionCode", RegionCode.USA.ordinal) // 默认USA
        systemLanguage = sharedPref.getInt("systemLanguage", SystemLanguage.AmericanEnglish.ordinal) // 默认美式英语
        audioEngineType = sharedPref.getInt("audioEngineType", 1) // 默认使用OpenAL
        scalingFilter = sharedPref.getInt("scalingFilter", 0) // 默认：最近邻
        scalingFilterLevel = sharedPref.getInt("scalingFilterLevel", 80) // 默认级别：80
        antiAliasing = sharedPref.getInt("antiAliasing", 0) // 默认关闭
        memoryConfiguration = sharedPref.getInt("memoryConfiguration", 0) // 默认4GB
        controllerType = sharedPref.getInt("controllerType", 0) // 默认Pro控制器

        enableDebugLogs = sharedPref.getBoolean("enableDebugLogs", false)
        enableStubLogs = sharedPref.getBoolean("enableStubLogs", false)
        enableInfoLogs = sharedPref.getBoolean("enableInfoLogs", true)
        enableWarningLogs = sharedPref.getBoolean("enableWarningLogs", true)
        enableErrorLogs = sharedPref.getBoolean("enableErrorLogs", true)
        enableGuestLogs = sharedPref.getBoolean("enableGuestLogs", true)
        enableAccessLogs = sharedPref.getBoolean("enableAccessLogs", false)
        enableTraceLogs = sharedPref.getBoolean("enableStubLogs", false)
        enableGraphicsLogs = sharedPref.getBoolean("enableGraphicsLogs", false)
        
        // 初始化时立即应用控制器类型设置
        applyControllerSettings()
    }

    fun save() {
        val editor = sharedPref.edit()

        editor.putBoolean("isHostMapped", isHostMapped)
        editor.putBoolean("useNce", useNce)
        editor.putBoolean("enableVsync", enableVsync)
        editor.putBoolean("enableDocked", enableDocked)
        editor.putBoolean("enablePtc", enablePtc)
        editor.putBoolean("enableJitCacheEviction", enableJitCacheEviction)
        editor.putBoolean("ignoreMissingServices", ignoreMissingServices)
        editor.putBoolean("enableShaderCache", enableShaderCache)
        editor.putBoolean("enableTextureRecompression", enableTextureRecompression)
        editor.putFloat("resScale", resScale)
        editor.putInt("aspect_ratio", aspectRatio)
        editor.putBoolean("useVirtualController", useVirtualController)
        editor.putBoolean("isGrid", isGrid)
        editor.putBoolean("useSwitchLayout", useSwitchLayout)
        editor.putBoolean("enableMotion", enableMotion)
        editor.putBoolean("enablePerformanceMode", enablePerformanceMode)
        editor.putFloat("controllerStickSensitivity", controllerStickSensitivity)
        editor.putBoolean("skipMemoryBarriers", skipMemoryBarriers) // 保存
        editor.putInt("regionCode", regionCode) // 保存区域代码
        editor.putInt("systemLanguage", systemLanguage) // 保存系统语言
        editor.putInt("audioEngineType", audioEngineType) // 保存音频引擎设置
        editor.putInt("scalingFilter", scalingFilter) // 保存缩放过滤器
        editor.putInt("scalingFilterLevel", scalingFilterLevel) // 保存缩放过滤器级别
        editor.putInt("antiAliasing", antiAliasing) // 保存抗锯齿设置
        editor.putInt("memoryConfiguration", memoryConfiguration) // 保存内存配置
        editor.putInt("controllerType", controllerType) // 保存控制器类型

        editor.putBoolean("enableDebugLogs", enableDebugLogs)
        editor.putBoolean("enableStubLogs", enableStubLogs)
        editor.putBoolean("enableInfoLogs", enableInfoLogs)
        editor.putBoolean("enableWarningLogs", enableWarningLogs)
        editor.putBoolean("enableErrorLogs", enableErrorLogs)
        editor.putBoolean("enableGuestLogs", enableGuestLogs)
        editor.putBoolean("enableAccessLogs", enableAccessLogs)
        editor.putBoolean("enableTraceLogs", enableTraceLogs)
        editor.putBoolean("enableGraphicsLogs", enableGraphicsLogs)

        editor.apply()
        
        // 保存后立即应用控制器设置
        applyControllerSettings()
    }
    
    // 新增方法：应用控制器设置到Native层
    fun applyControllerSettings() {
        try {
            // 设置虚拟控制器的控制器类型（设备ID 0）
            RyujinxNative.jnaInstance.setControllerType(0, controllerType)
            
            // 记录设置信息
            android.util.Log.d("QuickSettings", "Controller type set to: ${getControllerTypeName(controllerType)} for device 0")
        } catch (e: Exception) {
            android.util.Log.e("QuickSettings", "Failed to apply controller settings", e)
        }
    }
    
    // 新增方法：获取控制器类型名称
    fun getControllerTypeName(type: Int): String {
        return when (type) {
            0 -> "Pro Controller"
            1 -> "Joy-Con (L)"
            2 -> "Joy-Con (R)"
            3 -> "Joy-Con Pair"
            4 -> "Handheld"
            else -> "Unknown"
        }
    }
    
    // 新增方法：获取当前控制器类型的枚举值
    fun getCurrentControllerType(): ControllerType {
        return when (controllerType) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
    }
    
    // 新增方法：设置控制器类型并立即应用
    fun setControllerType(newType: Int) {
        controllerType = newType
        applyControllerSettings()
    }
    
    // 新增方法：设置控制器类型通过枚举值
    fun setControllerType(controllerType: ControllerType) {
        this.controllerType = when (controllerType) {
            ControllerType.PRO_CONTROLLER -> 0
            ControllerType.JOYCON_LEFT -> 1
            ControllerType.JOYCON_RIGHT -> 2
            ControllerType.JOYCON_PAIR -> 3
            ControllerType.HANDHELD -> 4
        }
        applyControllerSettings()
    }
}
