package org.ryujinx.android.viewmodels

import android.content.SharedPreferences
import androidx.compose.runtime.MutableState
import androidx.documentfile.provider.DocumentFile
import androidx.navigation.NavHostController
import androidx.preference.PreferenceManager
import com.anggrayudi.storage.callback.FileCallback
import com.anggrayudi.storage.file.FileFullPath
import com.anggrayudi.storage.file.copyFileTo
import com.anggrayudi.storage.file.extension
import com.anggrayudi.storage.file.getAbsolutePath
import org.ryujinx.android.BackendThreading
import org.ryujinx.android.LogLevel
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RegionCode
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.SystemLanguage
import java.io.File
import java.util.Calendar
import kotlin.concurrent.thread

class SettingsViewModel(var navController: NavHostController, val activity: MainActivity) {
    var selectedFirmwareVersion: String = ""
    private var previousFileCallback: ((requestCode: Int, files: List<DocumentFile>) -> Unit)?
    private var previousFolderCallback: ((requestCode: Int, folder: DocumentFile) -> Unit)?
    private var sharedPref: SharedPreferences
    var selectedFirmwareFile: DocumentFile? = null

    init {
        sharedPref = getPreferences()
        previousFolderCallback = activity.storageHelper!!.onFolderSelected
        previousFileCallback = activity.storageHelper!!.onFileSelected
        activity.storageHelper!!.onFolderSelected = { _, folder ->
            run {
                val p = folder.getAbsolutePath(activity)
                val editor = sharedPref.edit()
                editor?.putString("gameFolder", p)
                editor?.apply()
            }
        }
    }

    private fun getPreferences(): SharedPreferences {
        return PreferenceManager.getDefaultSharedPreferences(activity)
    }

    fun initializeState(
        memoryManagerMode: MutableState<Int>,  
        useNce: MutableState<Boolean>,
        enableVsync: MutableState<Boolean>,
        enableDocked: MutableState<Boolean>,
        enablePtc: MutableState<Boolean>,
        enableLowPowerPptc: MutableState<Boolean>,
        enableJitCacheEviction: MutableState<Boolean>,
        enableFsIntegrityChecks: MutableState<Boolean>,
        ignoreMissingServices: MutableState<Boolean>,
        enableShaderCache: MutableState<Boolean>,
        enableTextureRecompression: MutableState<Boolean>,
        resScale: MutableState<Float>,
        aspectRatio: MutableState<Int>,
        useVirtualController: MutableState<Boolean>,
        isGrid: MutableState<Boolean>,
        useSwitchLayout: MutableState<Boolean>,
        enableMotion: MutableState<Boolean>,
        enablePerformanceMode: MutableState<Boolean>,
        controllerStickSensitivity: MutableState<Float>,
        enableDebugLogs: MutableState<Boolean>,
        enableStubLogs: MutableState<Boolean>,
        enableInfoLogs: MutableState<Boolean>,
        enableWarningLogs: MutableState<Boolean>,
        enableErrorLogs: MutableState<Boolean>,
        enableGuestLogs: MutableState<Boolean>,
        enableAccessLogs: MutableState<Boolean>,
        enableTraceLogs: MutableState<Boolean>,
        enableGraphicsLogs: MutableState<Boolean>,
        skipMemoryBarriers: MutableState<Boolean>,
        regionCode: MutableState<Int>,
        systemLanguage: MutableState<Int>,
        audioEngineType: MutableState<Int>,
        scalingFilter: MutableState<Int>,
        scalingFilterLevel: MutableState<Int>,
        antiAliasing: MutableState<Int>,
        memoryConfiguration: MutableState<Int>,
        systemTimeOffset: MutableState<Long>,
        customTimeEnabled: MutableState<Boolean>,
        customTimeYear: MutableState<Int>,
        customTimeMonth: MutableState<Int>,
        customTimeDay: MutableState<Int>,
        customTimeHour: MutableState<Int>,
        customTimeMinute: MutableState<Int>,
        customTimeSecond: MutableState<Int>,
        // 新增：表面格式相关参数
        customSurfaceFormatEnabled: MutableState<Boolean>,
        surfaceFormat: MutableState<Int>,
        surfaceColorSpace: MutableState<Int>,
        // 新增：Enable Color Space Passthrough 参数
        enableColorSpacePassthrough: MutableState<Boolean>,
        // 新增：BackendThreading 参数
        backendThreading: MutableState<Int>
    ) {

        memoryManagerMode.value = sharedPref.getInt("memoryManagerMode", 2)  // 默认使用HostMappedUnsafe
        useNce.value = sharedPref.getBoolean("useNce", true)
        enableVsync.value = sharedPref.getBoolean("enableVsync", true)
        enableDocked.value = sharedPref.getBoolean("enableDocked", true)
        enablePtc.value = sharedPref.getBoolean("enablePtc", true)
        enableLowPowerPptc.value = sharedPref.getBoolean("enableLowPowerPptc", false)
        enableJitCacheEviction.value = sharedPref.getBoolean("enableJitCacheEviction", false)
        enableFsIntegrityChecks.value = sharedPref.getBoolean("enableFsIntegrityChecks", false)
        ignoreMissingServices.value = sharedPref.getBoolean("ignoreMissingServices", false)
        enableShaderCache.value = sharedPref.getBoolean("enableShaderCache", true)
        enableTextureRecompression.value =
            sharedPref.getBoolean("enableTextureRecompression", false)
        resScale.value = sharedPref.getFloat("resScale", 1f)
        aspectRatio.value = sharedPref.getInt("aspect_ratio", 1)
        useVirtualController.value = sharedPref.getBoolean("useVirtualController", true)
        isGrid.value = sharedPref.getBoolean("isGrid", true)
        useSwitchLayout.value = sharedPref.getBoolean("useSwitchLayout", true)
        enableMotion.value = sharedPref.getBoolean("enableMotion", true)
        enablePerformanceMode.value = sharedPref.getBoolean("enablePerformanceMode", false)
        controllerStickSensitivity.value = sharedPref.getFloat("controllerStickSensitivity", 1.0f)
        skipMemoryBarriers.value = sharedPref.getBoolean("skipMemoryBarriers", false)
        regionCode.value = sharedPref.getInt("regionCode", RegionCode.USA.ordinal)
        systemLanguage.value = sharedPref.getInt("systemLanguage", SystemLanguage.AmericanEnglish.ordinal)
        audioEngineType.value = sharedPref.getInt("audioEngineType", 1)
        scalingFilter.value = sharedPref.getInt("scalingFilter", 0)
        scalingFilterLevel.value = sharedPref.getInt("scalingFilterLevel", 80)
        antiAliasing.value = sharedPref.getInt("antiAliasing", 0)
        memoryConfiguration.value = sharedPref.getInt("memoryConfiguration", 0)
        systemTimeOffset.value = sharedPref.getLong("systemTimeOffset", 0)
        
        // 初始化自定义时间设置
        customTimeEnabled.value = sharedPref.getBoolean("customTimeEnabled", false)
        customTimeYear.value = sharedPref.getInt("customTimeYear", 2023)
        customTimeMonth.value = sharedPref.getInt("customTimeMonth", 9)
        customTimeDay.value = sharedPref.getInt("customTimeDay", 12)
        customTimeHour.value = sharedPref.getInt("customTimeHour", 10)
        customTimeMinute.value = sharedPref.getInt("customTimeMinute", 27)
        customTimeSecond.value = sharedPref.getInt("customTimeSecond", 0)

        // 初始化表面格式设置
        customSurfaceFormatEnabled.value = sharedPref.getBoolean("customSurfaceFormatEnabled", false)
        surfaceFormat.value = sharedPref.getInt("surfaceFormat", -1)
        surfaceColorSpace.value = sharedPref.getInt("surfaceColorSpace", -1)
        
        // 初始化 Enable Color Space Passthrough
        enableColorSpacePassthrough.value = sharedPref.getBoolean("enableColorSpacePassthrough", false)
        
        // 初始化 BackendThreading
        backendThreading.value = sharedPref.getInt("backendThreading", BackendThreading.Auto.ordinal)
        
        // 如果之前保存了自定义表面格式，则恢复设置
        if (customSurfaceFormatEnabled.value && surfaceFormat.value != -1 && surfaceColorSpace.value != -1) {
            RyujinxNative.setCustomSurfaceFormat(surfaceFormat.value, surfaceColorSpace.value)
        } else {
            RyujinxNative.clearCustomSurfaceFormat()
        }

        // 设置色彩空间直通
        RyujinxNative.setColorSpacePassthrough(enableColorSpacePassthrough.value)

        enableDebugLogs.value = sharedPref.getBoolean("enableDebugLogs", false)
        enableStubLogs.value = sharedPref.getBoolean("enableStubLogs", false)
        enableInfoLogs.value = sharedPref.getBoolean("enableInfoLogs", true)
        enableWarningLogs.value = sharedPref.getBoolean("enableWarningLogs", true)
        enableErrorLogs.value = sharedPref.getBoolean("enableErrorLogs", true)
        enableGuestLogs.value = sharedPref.getBoolean("enableGuestLogs", true)
        enableAccessLogs.value = sharedPref.getBoolean("enableAccessLogs", false)
        enableTraceLogs.value = sharedPref.getBoolean("enableStubLogs", false)
        enableGraphicsLogs.value = sharedPref.getBoolean("enableGraphicsLogs", false)
    }

    fun save(
        memoryManagerMode: MutableState<Int>,  // 新增：内存管理器模式
        useNce: MutableState<Boolean>,
        enableVsync: MutableState<Boolean>,
        enableDocked: MutableState<Boolean>,
        enablePtc: MutableState<Boolean>,
        enableLowPowerPptc: MutableState<Boolean>,
        enableJitCacheEviction: MutableState<Boolean>,
        enableFsIntegrityChecks: MutableState<Boolean>,
        ignoreMissingServices: MutableState<Boolean>,
        enableShaderCache: MutableState<Boolean>,
        enableTextureRecompression: MutableState<Boolean>,
        resScale: MutableState<Float>,
        aspectRatio: MutableState<Int>,
        useVirtualController: MutableState<Boolean>,
        isGrid: MutableState<Boolean>,
        useSwitchLayout: MutableState<Boolean>,
        enableMotion: MutableState<Boolean>,
        enablePerformanceMode: MutableState<Boolean>,
        controllerStickSensitivity: MutableState<Float>,
        enableDebugLogs: MutableState<Boolean>,
        enableStubLogs: MutableState<Boolean>,
        enableInfoLogs: MutableState<Boolean>,
        enableWarningLogs: MutableState<Boolean>,
        enableErrorLogs: MutableState<Boolean>,
        enableGuestLogs: MutableState<Boolean>,
        enableAccessLogs: MutableState<Boolean>,
        enableTraceLogs: MutableState<Boolean>,
        enableGraphicsLogs: MutableState<Boolean>,
        skipMemoryBarriers: MutableState<Boolean>,
        regionCode: MutableState<Int>,
        systemLanguage: MutableState<Int>,
        audioEngineType: MutableState<Int>,
        scalingFilter: MutableState<Int>,
        scalingFilterLevel: MutableState<Int>,
        antiAliasing: MutableState<Int>,
        memoryConfiguration: MutableState<Int>,
        systemTimeOffset: MutableState<Long>,
        customTimeEnabled: MutableState<Boolean>,
        customTimeYear: MutableState<Int>,
        customTimeMonth: MutableState<Int>,
        customTimeDay: MutableState<Int>,
        customTimeHour: MutableState<Int>,
        customTimeMinute: MutableState<Int>,
        customTimeSecond: MutableState<Int>,
        // 新增：表面格式相关参数
        customSurfaceFormatEnabled: MutableState<Boolean>,
        surfaceFormat: MutableState<Int>,
        surfaceColorSpace: MutableState<Int>,
        // 新增：Enable Color Space Passthrough 参数
        enableColorSpacePassthrough: MutableState<Boolean>,
        // 新增：BackendThreading 参数
        backendThreading: MutableState<Int>
    ) {
        val editor = sharedPref.edit()

        editor.putInt("memoryManagerMode", memoryManagerMode.value)  // 保存内存管理器模式
        editor.putBoolean("useNce", useNce.value)
        editor.putBoolean("enableVsync", enableVsync.value)
        editor.putBoolean("enableDocked", enableDocked.value)
        editor.putBoolean("enablePtc", enablePtc.value)
        editor.putBoolean("enableLowPowerPptc", enableLowPowerPptc.value)
        editor.putBoolean("enableJitCacheEviction", enableJitCacheEviction.value)
        editor.putBoolean("enableFsIntegrityChecks", enableFsIntegrityChecks.value)
        editor.putBoolean("ignoreMissingServices", ignoreMissingServices.value)
        editor.putBoolean("enableShaderCache", enableShaderCache.value)
        editor.putBoolean("enableTextureRecompression", enableTextureRecompression.value)
        editor.putFloat("resScale", resScale.value)
        editor.putInt("aspect_ratio", aspectRatio.value)
        editor.putBoolean("useVirtualController", useVirtualController.value)
        editor.putBoolean("isGrid", isGrid.value)
        editor.putBoolean("useSwitchLayout", useSwitchLayout.value)
        editor.putBoolean("enableMotion", enableMotion.value)
        editor.putBoolean("enablePerformanceMode", enablePerformanceMode.value)
        editor.putFloat("controllerStickSensitivity", controllerStickSensitivity.value)
        editor.putBoolean("skipMemoryBarriers", skipMemoryBarriers.value)
        editor.putInt("regionCode", regionCode.value)
        editor.putInt("systemLanguage", systemLanguage.value)
        editor.putInt("audioEngineType", audioEngineType.value)
        editor.putInt("scalingFilter", scalingFilter.value)
        editor.putInt("scalingFilterLevel", scalingFilterLevel.value)
        editor.putInt("antiAliasing", antiAliasing.value)
        editor.putInt("memoryConfiguration", memoryConfiguration.value)
        
        // 保存自定义时间设置
        editor.putBoolean("customTimeEnabled", customTimeEnabled.value)
        editor.putInt("customTimeYear", customTimeYear.value)
        editor.putInt("customTimeMonth", customTimeMonth.value)
        editor.putInt("customTimeDay", customTimeDay.value)
        editor.putInt("customTimeHour", customTimeHour.value)
        editor.putInt("customTimeMinute", customTimeMinute.value)
        editor.putInt("customTimeSecond", customTimeSecond.value)

        // 保存表面格式设置
        editor.putBoolean("customSurfaceFormatEnabled", customSurfaceFormatEnabled.value)
        editor.putInt("surfaceFormat", surfaceFormat.value)
        editor.putInt("surfaceColorSpace", surfaceColorSpace.value)

        // 保存 Enable Color Space Passthrough
        editor.putBoolean("enableColorSpacePassthrough", enableColorSpacePassthrough.value)

        // 保存 BackendThreading
        editor.putInt("backendThreading", backendThreading.value)

        editor.putBoolean("enableDebugLogs", enableDebugLogs.value)
        editor.putBoolean("enableStubLogs", enableStubLogs.value)
        editor.putBoolean("enableInfoLogs", enableInfoLogs.value)
        editor.putBoolean("enableWarningLogs", enableWarningLogs.value)
        editor.putBoolean("enableErrorLogs", enableErrorLogs.value)
        editor.putBoolean("enableGuestLogs", enableGuestLogs.value)
        editor.putBoolean("enableAccessLogs", enableAccessLogs.value)
        editor.putBoolean("enableTraceLogs", enableTraceLogs.value)
        editor.putBoolean("enableGraphicsLogs", enableGraphicsLogs.value)

        // 计算并设置系统时间偏移
        val calculatedTimeOffset = if (customTimeEnabled.value) {
            // 创建Calendar实例并设置自定义时间
            val calendar = Calendar.getInstance()
            calendar.set(customTimeYear.value, customTimeMonth.value - 1, customTimeDay.value, 
                        customTimeHour.value, customTimeMinute.value, customTimeSecond.value)
            
            // 计算自定义时间与当前时间的偏移量（秒）
            val customTimeMillis = calendar.timeInMillis
            val currentTimeMillis = System.currentTimeMillis()
            val timeOffset = (customTimeMillis - currentTimeMillis) / 1000
            
            // 更新状态值
            systemTimeOffset.value = timeOffset
            
            timeOffset
        } else {
            // 如果不使用自定义时间，则重置为0
            0L
        }
        
        // 保存计算出的时间偏移
        editor.putLong("systemTimeOffset", calculatedTimeOffset)
        
        // 应用所有设置
        editor.apply()

        activity.storageHelper!!.onFolderSelected = previousFolderCallback

        // 设置跳过内存屏障
        RyujinxNative.jnaInstance.setSkipMemoryBarriers(skipMemoryBarriers.value)

        // 设置画面比例
        RyujinxNative.jnaInstance.setAspectRatio(aspectRatio.value)

        // 设置缩放过滤器和级别
        RyujinxNative.jnaInstance.setScalingFilter(scalingFilter.value)
        RyujinxNative.jnaInstance.setScalingFilterLevel(scalingFilterLevel.value)

        // 设置抗锯齿
        RyujinxNative.jnaInstance.setAntiAliasing(antiAliasing.value)

        // 设置内存配置
        RyujinxNative.jnaInstance.setMemoryConfiguration(memoryConfiguration.value)

        // 设置系统时间偏移
        RyujinxNative.jnaInstance.setSystemTimeOffset(calculatedTimeOffset)

        // 设置表面格式
        if (customSurfaceFormatEnabled.value && surfaceFormat.value != -1 && surfaceColorSpace.value != -1) {
            RyujinxNative.setCustomSurfaceFormat(surfaceFormat.value, surfaceColorSpace.value)
        } else {
            RyujinxNative.clearCustomSurfaceFormat()
        }

        // 设置色彩空间直通
        RyujinxNative.setColorSpacePassthrough(enableColorSpacePassthrough.value)

        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Debug.ordinal, enableDebugLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Info.ordinal, enableInfoLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Stub.ordinal, enableStubLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Warning.ordinal,
            enableWarningLogs.value
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Error.ordinal, enableErrorLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.AccessLog.ordinal,
            enableAccessLogs.value
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Guest.ordinal, enableGuestLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Trace.ordinal, enableTraceLogs.value)
        RyujinxNative.jnaInstance.loggingEnabledGraphicsLog(enableGraphicsLogs.value)
    }

    fun openGameFolder() {
        val path = sharedPref.getString("gameFolder", "") ?: ""

        if (path.isEmpty())
            activity.storageHelper?.storage?.openFolderPicker()
        else
            activity.storageHelper?.storage?.openFolderPicker(
                activity.storageHelper!!.storage.requestCodeFolderPicker,
                FileFullPath(activity, path)
            )
    }

    fun importProdKeys() {
        activity.storageHelper!!.onFileSelected = { _, files ->
            run {
                activity.storageHelper!!.onFileSelected = previousFileCallback
                val file = files.firstOrNull()
                file?.apply {
                    if (name == "prod.keys") {
                        val outputFile = File(MainActivity.AppPath + "/system")
                        outputFile.delete()

                        thread {
                            file.copyFileTo(
                                activity,
                                outputFile,
                                callback = object : FileCallback() {
                                })
                        }
                    }
                }
            }
        }
        activity.storageHelper?.storage?.openFilePicker()
    }

    fun selectFirmware(installState: MutableState<FirmwareInstallState>) {
        if (installState.value != FirmwareInstallState.None)
            return
        activity.storageHelper!!.onFileSelected = { _, files ->
            run {
                activity.storageHelper!!.onFileSelected = previousFileCallback
                val file = files.firstOrNull()
                file?.apply {
                    if (extension == "xci" || extension == "zip") {
                        installState.value = FirmwareInstallState.Verifying
                        thread {
                            val descriptor =
                                activity.contentResolver.openFileDescriptor(file.uri, "rw")
                            descriptor?.use { d ->
                                selectedFirmwareVersion =
                                    RyujinxNative.jnaInstance.deviceVerifyFirmware(
                                        d.fd,
                                        extension == "xci"
                                    )
                                selectedFirmwareFile = file
                                if (!selectedFirmwareVersion.isEmpty()) {
                                    installState.value = FirmwareInstallState.Query
                                } else {
                                    installState.value = FirmwareInstallState.Cancelled
                                }
                            }
                        }
                    } else {
                        installState.value = FirmwareInstallState.Cancelled
                    }
                }
            }
        }
        activity.storageHelper?.storage?.openFilePicker()
    }

    fun installFirmware(installState: MutableState<FirmwareInstallState>) {
        if (installState.value != FirmwareInstallState.Query)
            return
        if (selectedFirmwareFile == null) {
            installState.value = FirmwareInstallState.None
            return
        }
        selectedFirmwareFile?.apply {
            val descriptor = activity.contentResolver.openFileDescriptor(uri, "rw")

            if(descriptor != null)
            {
                installState.value = FirmwareInstallState.Install
                thread {
                    Thread.sleep(1000)

                    try {
                        RyujinxNative.jnaInstance.deviceInstallFirmware(
                            descriptor.fd,
                            extension == "xci"
                        )
                    } finally {
                        MainActivity.mainViewModel?.refreshFirmwareVersion()
                        installState.value = FirmwareInstallState.Done
                    }
                }
            }
        }
    }

    fun clearFirmwareSelection(installState: MutableState<FirmwareInstallState>) {
        selectedFirmwareFile = null
        selectedFirmwareVersion = ""
        installState.value = FirmwareInstallState.None
    }
}

enum class FirmwareInstallState {
    None,
    Cancelled,
    Verifying,
    Query,
    Install,
    Done
}