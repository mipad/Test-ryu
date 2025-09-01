package org.ryujinx.android.viewmodels

import android.app.Activity
import android.content.SharedPreferences
import androidx.preference.PreferenceManager

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
    var aspectRatio: Int // 0: Stretched, 1: 16:9, 2: 4:3, 3: 21:9, 4: 32:9, 5: 16:10, 6: 32:10
    var isGrid: Boolean
    var useSwitchLayout: Boolean
    var enableMotion: Boolean
    var enablePerformanceMode: Boolean
    var controllerStickSensitivity: Float
    var skipMemoryBarriers: Boolean // 新增：跳过内存屏障

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
        aspectRatio = sharedPref.getInt("aspect_ratio", 0) // 默认使用拉伸
        useVirtualController = sharedPref.getBoolean("useVirtualController", true)
        isGrid = sharedPref.getBoolean("isGrid", true)
        useSwitchLayout = sharedPref.getBoolean("useSwitchLayout", true)
        enableMotion = sharedPref.getBoolean("enableMotion", true)
        enablePerformanceMode = sharedPref.getBoolean("enablePerformanceMode", true)
        controllerStickSensitivity = sharedPref.getFloat("controllerStickSensitivity", 1.0f)
        skipMemoryBarriers = sharedPref.getBoolean("skipMemoryBarriers", false) // 初始化

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
