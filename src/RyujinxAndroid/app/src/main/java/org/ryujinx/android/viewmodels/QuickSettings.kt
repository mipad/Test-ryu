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
    var isHostMapped: Boolean
    var enableShaderCache: Boolean
    var enableTextureRecompression: Boolean
    var resScale: Float
    var aspectRatio: Int
    var isGrid: Boolean
    var useSwitchLayout: Boolean
    var enableMotion: Boolean
    var enablePerformanceMode: Boolean
    var controllerStickSensitivity: Float
    var skipMemoryBarriers: Boolean
    var regionCode: Int
    var systemLanguage: Int
    var audioEngineType: Int
    var scalingFilter: Int
    var scalingFilterLevel: Int
    var antiAliasing: Int
    var memoryConfiguration: Int
    var systemTimeOffset: Long
    
    var customTimeEnabled: Boolean
    var customTimeYear: Int
    var customTimeMonth: Int
    var customTimeDay: Int
    var customTimeHour: Int
    var customTimeMinute: Int
    var customTimeSecond: Int

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
        aspectRatio = sharedPref.getInt("aspect_ratio", 1)
        useVirtualController = sharedPref.getBoolean("useVirtualController", true)
        isGrid = sharedPref.getBoolean("isGrid", true)
        useSwitchLayout = sharedPref.getBoolean("useSwitchLayout", true)
        enableMotion = sharedPref.getBoolean("enableMotion", true)
        enablePerformanceMode = sharedPref.getBoolean("enablePerformanceMode", true)
        controllerStickSensitivity = sharedPref.getFloat("controllerStickSensitivity", 1.0f)
        skipMemoryBarriers = sharedPref.getBoolean("skipMemoryBarriers", false)
        regionCode = sharedPref.getInt("regionCode", RegionCode.USA.ordinal)
        systemLanguage = sharedPref.getInt("systemLanguage", SystemLanguage.AmericanEnglish.ordinal)
        audioEngineType = sharedPref.getInt("audioEngineType", 1)
        scalingFilter = sharedPref.getInt("scalingFilter", 0)
        scalingFilterLevel = sharedPref.getInt("scalingFilterLevel", 80)
        antiAliasing = sharedPref.getInt("antiAliasing", 0)
        memoryConfiguration = sharedPref.getInt("memoryConfiguration", 0)
        systemTimeOffset = sharedPref.getLong("systemTimeOffset", 0)
        
        customTimeEnabled = sharedPref.getBoolean("customTimeEnabled", false)
        customTimeYear = sharedPref.getInt("customTimeYear", 2023)
        customTimeMonth = sharedPref.getInt("customTimeMonth", 9)
        customTimeDay = sharedPref.getInt("customTimeDay", 12)
        customTimeHour = sharedPref.getInt("customTimeHour", 10)
        customTimeMinute = sharedPref.getInt("customTimeMinute", 27)
        customTimeSecond = sharedPref.getInt("customTimeSecond", 0)

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
        editor.putBoolean("skipMemoryBarriers", skipMemoryBarriers)
        editor.putInt("regionCode", regionCode)
        editor.putInt("systemLanguage", systemLanguage)
        editor.putInt("audioEngineType", audioEngineType)
        editor.putInt("scalingFilter", scalingFilter)
        editor.putInt("scalingFilterLevel", scalingFilterLevel)
        editor.putInt("antiAliasing", antiAliasing)
        editor.putInt("memoryConfiguration", memoryConfiguration)
        editor.putLong("systemTimeOffset", systemTimeOffset)
        
        editor.putBoolean("customTimeEnabled", customTimeEnabled)
        editor.putInt("customTimeYear", customTimeYear)
        editor.putInt("customTimeMonth", customTimeMonth)
        editor.putInt("customTimeDay", customTimeDay)
        editor.putInt("customTimeHour", customTimeHour)
        editor.putInt("customTimeMinute", customTimeMinute)
        editor.putInt("customTimeSecond", customTimeSecond)

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
