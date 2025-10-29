package org.ryujinx.android.viewmodels

import android.app.Activity
import android.content.SharedPreferences
import androidx.preference.PreferenceManager
import org.ryujinx.android.RegionCode
import org.ryujinx.android.SystemLanguage

class QuickSettings(val activity: Activity) {
    var ignoreMissingServices: Boolean
    var enablePtc: Boolean
    var enableJitCacheEviction: Boolean
    var enableDocked: Boolean
    var enableVsync: Boolean
    var useNce: Boolean
    var useVirtualController: Boolean
    var memoryManagerMode: Int // 新增：内存管理器模式 0=SoftwarePageTable, 1=HostMapped, 2=HostMappedUnsafe
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
    var systemTimeOffset: Long // 新增：系统时间偏移（秒）
    
    // 新增：自定义时间相关字段
    var customTimeEnabled: Boolean // 新增：自定义时间开关
    var customTimeYear: Int // 新增：自定义时间-年
    var customTimeMonth: Int // 新增：自定义时间-月
    var customTimeDay: Int // 新增：自定义时间-日
    var customTimeHour: Int // 新增：自定义时间-时
    var customTimeMinute: Int // 新增：自定义时间-分
    var customTimeSecond: Int // 新增：自定义时间-秒

    // 新增：表面格式相关字段
    var customSurfaceFormatEnabled: Boolean // 是否启用自定义表面格式
    var surfaceFormat: Int // 表面格式值
    var surfaceColorSpace: Int // 颜色空间值

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
        memoryManagerMode = sharedPref.getInt("memoryManagerMode", 2) // 默认使用HostMappedUnsafe
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
        systemTimeOffset = sharedPref.getLong("systemTimeOffset", 0) // 默认0秒偏移
        
        // 初始化自定义时间字段
        customTimeEnabled = sharedPref.getBoolean("customTimeEnabled", false)
        customTimeYear = sharedPref.getInt("customTimeYear", 2023)
        customTimeMonth = sharedPref.getInt("customTimeMonth", 9)
        customTimeDay = sharedPref.getInt("customTimeDay", 12)
        customTimeHour = sharedPref.getInt("customTimeHour", 10)
        customTimeMinute = sharedPref.getInt("customTimeMinute", 27)
        customTimeSecond = sharedPref.getInt("customTimeSecond", 0)

        // 初始化表面格式字段
        customSurfaceFormatEnabled = sharedPref.getBoolean("customSurfaceFormatEnabled", false)
        surfaceFormat = sharedPref.getInt("surfaceFormat", -1)
        surfaceColorSpace = sharedPref.getInt("surfaceColorSpace", -1)

        enableDebugLogs = sharedPref.getBoolean("enableDebugLogs", false)
        enableStubLogs = sharedPref.getBoolean("enableStubLogs", false)
        enableInfoLogs = sharedPref.getBoolean("enableInfoLogs", true)
        enableWarningLogs = sharedPref.getBoolean("enableWarningLogs", true)
        enableErrorLogs = sharedPref.getBoolean("enableErrorLogs", true)
        enableGuestLogs = sharedPref.getBoolean("enableGuestLogs", true)
        enableAccessLogs = sharedPref.getBoolean("enableAccessLogs", false)
        enableTraceLogs = sharedPref.getBoolean("enableStubLogs", false)
        enableGraphicsLogs = sharedPref.getBoolean("enableGraphicsLogs", false)
    }

    fun save() {
        val editor = sharedPref.edit()

        editor.putInt("memoryManagerMode", memoryManagerMode)  // 保存内存管理器模式
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
        editor.putLong("systemTimeOffset", systemTimeOffset) // 保存系统时间偏移
        
        // 保存自定义时间字段
        editor.putBoolean("customTimeEnabled", customTimeEnabled)
        editor.putInt("customTimeYear", customTimeYear)
        editor.putInt("customTimeMonth", customTimeMonth)
        editor.putInt("customTimeDay", customTimeDay)
        editor.putInt("customTimeHour", customTimeHour)
        editor.putInt("customTimeMinute", customTimeMinute)
        editor.putInt("customTimeSecond", customTimeSecond)

        // 保存表面格式字段
        editor.putBoolean("customSurfaceFormatEnabled", customSurfaceFormatEnabled)
        editor.putInt("surfaceFormat", surfaceFormat)
        editor.putInt("surfaceColorSpace", surfaceColorSpace)

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
    }
}