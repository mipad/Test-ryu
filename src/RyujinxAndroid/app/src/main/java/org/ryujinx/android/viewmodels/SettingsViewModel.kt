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
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.ryujinx.android.ControllerType
import org.ryujinx.android.LogLevel
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RegionCode
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.SystemLanguage
import org.ryujinx.android.views.PlayerSetting
import java.io.File
import kotlin.concurrent.thread

class SettingsViewModel(var navController: NavHostController, val activity: MainActivity) {
    var selectedFirmwareVersion: String = ""
    private var previousFileCallback: ((requestCode: Int, files: List<DocumentFile>) -> Unit)?
    private var previousFolderCallback: ((requestCode: Int, folder: DocumentFile) -> Unit)?
    private var sharedPref: SharedPreferences
    var selectedFirmwareFile: DocumentFile? = null

    // 玩家设置列表
    var playerSettings: MutableList<PlayerSetting> = mutableListOf()

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
        
        // 初始化玩家设置
        loadPlayerSettings()
    }

    private fun getPreferences(): SharedPreferences {
        return PreferenceManager.getDefaultSharedPreferences(activity)
    }

    // 加载玩家设置
    private fun loadPlayerSettings() {
        val json = sharedPref.getString("player_settings", null)
        if (json != null) {
            try {
                playerSettings = Json.decodeFromString<MutableList<PlayerSetting>>(json).toMutableList()
            } catch (e: Exception) {
                // 如果解析失败，使用默认设置
                initDefaultPlayerSettings()
            }
        } else {
            initDefaultPlayerSettings()
        }
    }

    // 初始化默认玩家设置
    private fun initDefaultPlayerSettings() {
        playerSettings = mutableListOf(
            PlayerSetting(1, true, 0), // Player 1 默认开启，Pro Controller
            PlayerSetting(2, false, 0), // Player 2-8 默认关闭
            PlayerSetting(3, false, 0),
            PlayerSetting(4, false, 0),
            PlayerSetting(5, false, 0),
            PlayerSetting(6, false, 0),
            PlayerSetting(7, false, 0),
            PlayerSetting(8, false, 0)
        )
    }

    // 保存玩家设置
    private fun savePlayerSettings() {
        val json = Json.encodeToString(playerSettings)
        sharedPref.edit().putString("player_settings", json).apply()
    }

    // 获取指定玩家的设置
    fun getPlayerSetting(playerNumber: Int): PlayerSetting? {
        return playerSettings.find { it.playerNumber == playerNumber }
    }

    // 更新玩家设置
    fun updatePlayerSetting(playerSetting: PlayerSetting) {
        val index = playerSettings.indexOfFirst { it.playerNumber == playerSetting.playerNumber }
        if (index != -1) {
            playerSettings[index] = playerSetting
            savePlayerSettings()
            
            // 如果是玩家1，立即应用设置
            if (playerSetting.playerNumber == 1 && playerSetting.isConnected) {
                updateControllerTypeInManager(playerSetting.controllerType)
            }
        }
    }

    fun initializeState(
        isHostMapped: MutableState<Boolean>,
        useNce: MutableState<Boolean>,
        enableVsync: MutableState<Boolean>,
        enableDocked: MutableState<Boolean>,
        enablePtc: MutableState<Boolean>,
        enableJitCacheEviction: MutableState<Boolean>,
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
        memoryConfiguration: MutableState<Int>
    ) {

        isHostMapped.value = sharedPref.getBoolean("isHostMapped", true)
        useNce.value = sharedPref.getBoolean("useNce", true)
        enableVsync.value = sharedPref.getBoolean("enableVsync", true)
        enableDocked.value = sharedPref.getBoolean("enableDocked", true)
        enablePtc.value = sharedPref.getBoolean("enablePtc", true)
        enableJitCacheEviction.value = sharedPref.getBoolean("enableJitCacheEviction", false)
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
        scalingFilterLevel.value = sharedPref.getInt("scalingFilterLevel", 0)
        antiAliasing.value = sharedPref.getInt("antiAliasing", 0)
        memoryConfiguration.value = sharedPref.getInt("memoryConfiguration", 0)

        enableDebugLogs.value = sharedPref.getBoolean("enableDebugLogs", false)
        enableStubLogs.value = sharedPref.getBoolean("enableStubLogs", false)
        enableInfoLogs.value = sharedPref.getBoolean("enableInfoLogs", true)
        enableWarningLogs.value = sharedPref.getBoolean("enableWarningLogs", true)
        enableErrorLogs.value = sharedPref.getBoolean("enableErrorLogs", true)
        enableGuestLogs.value = sharedPref.getBoolean("enableGuestLogs", true)
        enableAccessLogs.value = sharedPref.getBoolean("enableAccessLogs", false)
        enableTraceLogs.value = sharedPref.getBoolean("enableStubLogs", false)
        enableGraphicsLogs.value = sharedPref.getBoolean("enableGraphicsLogs", false)
        
        // 玩家设置已经在init中加载，不需要再次初始化
    }

    fun save(
        isHostMapped: MutableState<Boolean>,
        useNce: MutableState<Boolean>,
        enableVsync: MutableState<Boolean>,
        enableDocked: MutableState<Boolean>,
        enablePtc: MutableState<Boolean>,
        enableJitCacheEviction: MutableState<Boolean>,
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
        memoryConfiguration: MutableState<Int>
    ) {
        val editor = sharedPref.edit()

        editor.putBoolean("isHostMapped", isHostMapped.value)
        editor.putBoolean("useNce", useNce.value)
        editor.putBoolean("enableVsync", enableVsync.value)
        editor.putBoolean("enableDocked", enableDocked.value)
        editor.putBoolean("enablePtc", enablePtc.value)
        editor.putBoolean("enableJitCacheEviction", enableJitCacheEviction.value)
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

        editor.putBoolean("enableDebugLogs", enableDebugLogs.value)
        editor.putBoolean("enableStubLogs", enableStubLogs.value)
        editor.putBoolean("enableInfoLogs", enableInfoLogs.value)
        editor.putBoolean("enableWarningLogs", enableWarningLogs.value)
        editor.putBoolean("enableErrorLogs", enableErrorLogs.value)
        editor.putBoolean("enableGuestLogs", enableGuestLogs.value)
        editor.putBoolean("enableAccessLogs", enableAccessLogs.value)
        editor.putBoolean("enableTraceLogs", enableTraceLogs.value)
        editor.putBoolean("enableGraphicsLogs", enableGraphicsLogs.value)

        editor.apply()
        activity.storageHelper!!.onFolderSelected = previousFolderCallback

        // 保存玩家设置
        savePlayerSettings()

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

        // 设置所有已连接玩家的控制器类型，使用玩家编号而不是设备ID
        for (playerNumber in 1..8) {
            val playerSetting = getPlayerSetting(playerNumber)
            if (playerSetting != null && playerSetting.isConnected) {
                // 将控制器类型索引转换为位掩码值
                val controllerTypeBitmask = controllerTypeIndexToBitmask(playerSetting.controllerType)
                // 使用玩家编号而不是设备ID
                RyujinxNative.jnaInstance.setControllerType(playerNumber, controllerTypeBitmask)
                
                // 如果是玩家1，同时更新ControllerManager中的控制器类型
                if (playerNumber == 1) {
                    updateControllerTypeInManager(playerSetting.controllerType)
                }
                
                android.util.Log.d("SettingsViewModel", "Controller type set for player $playerNumber: $controllerTypeBitmask")
            }
        }

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

    // 新增方法：更新ControllerManager中的控制器类型
    private fun updateControllerTypeInManager(controllerTypeIndex: Int) {
        try {
            val controllerTypeEnum = when (controllerTypeIndex) {
                0 -> ControllerType.PRO_CONTROLLER
                1 -> ControllerType.JOYCON_LEFT
                2 -> ControllerType.JOYCON_RIGHT
                3 -> ControllerType.JOYCON_PAIR
                4 -> ControllerType.HANDHELD
                else -> ControllerType.PRO_CONTROLLER
            }
            
            // 使用 physicalControllerManager 来更新控制器类型
            MainActivity.mainViewModel?.physicalControllerManager?.updateControllerType(controllerTypeEnum)
            
            // 同时更新ControllerManager中的控制器类型
            org.ryujinx.android.ControllerManager.updateControllerType(
                activity,
                "virtual_controller_1",
                controllerTypeEnum
            )
        } catch (e: Exception) {
            e.printStackTrace()
        }
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
