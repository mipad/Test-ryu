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

    // 玩家设置列表（使用 0-based 索引，0-7为普通玩家，8为掌机模式）
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

    // 加载玩家设置（包括掌机模式）
    private fun loadPlayerSettings() {
        val json = sharedPref.getString("player_settings", null)
        val handheldJson = sharedPref.getString("handheld_setting", null)
        
        if (json != null || handheldJson != null) {
            try {
                // 先初始化默认设置
                initDefaultPlayerSettings()
                
                // 如果有保存的设置，则加载
                if (json != null) {
                    val savedSettings = Json.decodeFromString<MutableList<PlayerSetting>>(json)
                    
                    // 更新普通玩家设置 (0-7)
                    savedSettings.forEach { savedSetting ->
                        val index = playerSettings.indexOfFirst { it.playerIndex == savedSetting.playerIndex }
                        if (index != -1 && savedSetting.playerIndex in 0..7) {
                            playerSettings[index] = savedSetting
                        }
                    }
                }
                
                // 加载掌机模式设置
                if (handheldJson != null) {
                    val handheldSetting = Json.decodeFromString<PlayerSetting>(handheldJson)
                    val index = playerSettings.indexOfFirst { it.playerIndex == 8 }
                    if (index != -1) {
                        playerSettings[index] = handheldSetting
                    } else {
                        // 如果列表中没有掌机模式设置，添加它
                        playerSettings.add(handheldSetting)
                    }
                }
                
                // 确保所有控制器类型值有效
                playerSettings.forEach { setting ->
                    if (!setting.controllerType.isValidControllerType()) {
                        setting.controllerType = if (setting.playerIndex == 8) 4 else 0 // 掌机模式默认Handheld，其他默认Pro
                    }
                }
            } catch (e: Exception) {
                // 如果解析失败，使用默认设置
                android.util.Log.e("SettingsViewModel", "Failed to parse player settings, using defaults", e)
                initDefaultPlayerSettings()
            }
        } else {
            initDefaultPlayerSettings()
        }
    }

    // 初始化默认玩家设置（使用 0-based 索引，包括掌机模式）
    private fun initDefaultPlayerSettings() {
        playerSettings = mutableListOf(
            PlayerSetting(0, true, 0), // Player 1 (索引0) 默认开启，Pro Controller
            PlayerSetting(1, false, 0), // Player 2 (索引1) 默认关闭
            PlayerSetting(2, false, 0), // Player 3 (索引2) 默认关闭
            PlayerSetting(3, false, 0), // Player 4 (索引3) 默认关闭
            PlayerSetting(4, false, 0), // Player 5 (索引4) 默认关闭
            PlayerSetting(5, false, 0), // Player 6 (索引5) 默认关闭
            PlayerSetting(6, false, 0), // Player 7 (索引6) 默认关闭
            PlayerSetting(7, false, 0), // Player 8 (索引7) 默认关闭
            PlayerSetting(8, false, 4)  // Handheld (索引8) 默认关闭，控制器类型为Handheld
        )
    }

    // 保存玩家设置（包括掌机模式）
    private fun savePlayerSettings() {
        try {
            // 分离普通玩家设置和掌机模式设置
            val regularPlayers = playerSettings.filter { it.playerIndex in 0..7 }
            val handheldSetting = playerSettings.find { it.playerIndex == 8 } ?: PlayerSetting(8, false, 4)
            
            val regularJson = Json.encodeToString(regularPlayers)
            val handheldJson = Json.encodeToString(handheldSetting)
            
            sharedPref.edit()
                .putString("player_settings", regularJson)
                .putString("handheld_setting", handheldJson)
                .apply()
        } catch (e: Exception) {
            android.util.Log.e("SettingsViewModel", "Failed to save player settings", e)
        }
    }

    // 获取指定玩家的设置（使用 0-based 索引，0-8）
    fun getPlayerSetting(playerIndex: Int): PlayerSetting? {
        return playerSettings.find { it.playerIndex == playerIndex }
    }

    // 更新玩家设置（包括掌机模式）
    fun updatePlayerSetting(playerSetting: PlayerSetting) {
        val index = playerSettings.indexOfFirst { it.playerIndex == playerSetting.playerIndex }
        if (index != -1) {
            // 确保控制器类型值有效
            if (!playerSetting.controllerType.isValidControllerType()) {
                playerSetting.controllerType = if (playerSetting.playerIndex == 8) 4 else 0
            }
            
            playerSettings[index] = playerSetting
            savePlayerSettings()
            
            // 如果是已连接的玩家，立即应用设置
            if (playerSetting.isConnected) {
                applyPlayerSettingToNative(playerSetting)
            }
            
            // 如果是玩家1（索引0）或掌机模式（索引8），更新ControllerManager中的控制器类型
            if (playerSetting.playerIndex == 0 || playerSetting.playerIndex == 8) {
                updateControllerTypeInManager(playerSetting.controllerType, playerSetting.playerIndex)
            }
        }
    }
    
    // 应用玩家设置到Native层
    private fun applyPlayerSettingToNative(playerSetting: PlayerSetting) {
        try {
            // 将控制器类型索引转换为位掩码值
            val controllerTypeBitmask = controllerTypeIndexToBitmask(playerSetting.controllerType)
            
            // 对于掌机模式，使用玩家索引8，其他使用正常索引
            val targetPlayerIndex = if (playerSetting.playerIndex == 8) {
                8 // 掌机模式使用索引8
            } else {
                playerSetting.playerIndex // 其他玩家使用正常索引
            }
            
            RyujinxNative.jnaInstance.setControllerType(targetPlayerIndex, controllerTypeBitmask)
            
            android.util.Log.d("SettingsViewModel", "Applied controller setting: playerIndex=$targetPlayerIndex, controllerType=${playerSetting.controllerType}, bitmask=$controllerTypeBitmask")
        } catch (e: Exception) {
            android.util.Log.e("SettingsViewModel", "Failed to apply player setting to native", e)
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

        // 设置所有已连接玩家的控制器类型，使用 0-based 玩家索引 (0-8)
        for (playerIndex in 0..8) {
            val playerSetting = getPlayerSetting(playerIndex)
            if (playerSetting != null && playerSetting.isConnected) {
                applyPlayerSettingToNative(playerSetting)
                android.util.Log.d("SettingsViewModel", "Controller type set for player index $playerIndex")
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
    private fun updateControllerTypeInManager(controllerTypeIndex: Int, playerIndex: Int) {
        try {
            val controllerTypeEnum = when (controllerTypeIndex) {
                0 -> ControllerType.PRO_CONTROLLER
                1 -> ControllerType.JOYCON_LEFT
                2 -> ControllerType.JOYCON_RIGHT
                3 -> ControllerType.JOYCON_PAIR
                4 -> ControllerType.HANDHELD
                else -> ControllerType.PRO_CONTROLLER
            }
            
            // 使用 physicalControllerManager 来更新控制器类型（针对玩家0）
            if (playerIndex == 0) {
                MainActivity.mainViewModel?.physicalControllerManager?.updateControllerType(controllerTypeEnum)
            }
            
            // 如果是掌机模式，确保控制器类型正确
            if (playerIndex == 8 && controllerTypeIndex != 4) {
                android.util.Log.w("SettingsViewModel", "Handheld mode should use Handheld controller type, but got: $controllerTypeIndex")
            }
        } catch (e: Exception) {
            android.util.Log.e("SettingsViewModel", "Failed to update controller type in manager", e)
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
    
    // 新增方法：获取玩家显示名称（用于UI显示）
    fun getPlayerDisplayName(playerIndex: Int): String {
        return when (playerIndex) {
            in 0..7 -> "Player ${playerIndex + 1}"
            8 -> "Handheld" // 掌机模式
            else -> "Unknown Player"
        }
    }
    
    // 新增方法：检查玩家索引是否有效（包括掌机模式）
    fun isValidPlayerIndex(playerIndex: Int): Boolean {
        return playerIndex in 0..8 // 现在包括掌机模式索引8
    }
    
    // 新增方法：连接玩家（包括掌机模式）
    fun connectPlayer(playerIndex: Int) {
        if (isValidPlayerIndex(playerIndex)) {
            val setting = getPlayerSetting(playerIndex)
            if (setting != null && !setting.isConnected) {
                setting.isConnected = true
                updatePlayerSetting(setting)
                android.util.Log.d("SettingsViewModel", "Connected player index: $playerIndex")
            }
        }
    }
    
    // 新增方法：断开玩家连接（包括掌机模式）
    fun disconnectPlayer(playerIndex: Int) {
        if (isValidPlayerIndex(playerIndex)) {
            val setting = getPlayerSetting(playerIndex)
            if (setting != null && setting.isConnected) {
                setting.isConnected = false
                updatePlayerSetting(setting)
                android.util.Log.d("SettingsViewModel", "Disconnected player index: $playerIndex")
            }
        }
    }
    
    // 新增方法：连接掌机模式
    fun connectHandheld() {
        val handheldSetting = getPlayerSetting(8)
        if (handheldSetting != null && !handheldSetting.isConnected) {
            handheldSetting.isConnected = true
            updatePlayerSetting(handheldSetting)
            android.util.Log.d("SettingsViewModel", "Connected handheld mode")
        }
    }
    
    // 新增方法：断开掌机模式连接
    fun disconnectHandheld() {
        val handheldSetting = getPlayerSetting(8)
        if (handheldSetting != null && handheldSetting.isConnected) {
            handheldSetting.isConnected = false
            updatePlayerSetting(handheldSetting)
            android.util.Log.d("SettingsViewModel", "Disconnected handheld mode")
        }
    }
    
    // 新增方法：检查掌机模式是否连接
    fun isHandheldConnected(): Boolean {
        val handheldSetting = getPlayerSetting(8)
        return handheldSetting?.isConnected ?: false
    }
    
    // 新增方法：获取所有已连接的玩家索引（包括掌机模式）
    fun getConnectedPlayerIndices(): List<Int> {
        return playerSettings.filter { it.isConnected }.map { it.playerIndex }
    }
    
    // 新增方法：切换掌机模式状态
    fun toggleHandheldMode() {
        if (isHandheldConnected()) {
            disconnectHandheld()
        } else {
            connectHandheld()
        }
    }
    
    // 新增方法：设置掌机模式控制器类型为Handheld（确保正确）
    fun ensureHandheldControllerType() {
        val handheldSetting = getPlayerSetting(8)
        if (handheldSetting != null && handheldSetting.controllerType != 4) {
            handheldSetting.controllerType = 4
            updatePlayerSetting(handheldSetting)
        }
    }
    
    // 新增方法：获取掌机模式设置
    fun getHandheldSetting(): PlayerSetting? {
        return getPlayerSetting(8)
    }
    
    // 新增方法：更新掌机模式设置
    fun updateHandheldSetting(setting: PlayerSetting) {
        if (setting.playerIndex == 8) {
            updatePlayerSetting(setting)
        }
    }
    
    // 新增方法：检查是否为掌机模式索引
    fun isHandheldIndex(playerIndex: Int): Boolean {
        return playerIndex == 8
    }
    
    // 新增方法：获取掌机模式索引
    fun getHandheldIndex(): Int {
        return 8
    }
    
    // 新增方法：获取普通玩家设置（不包括掌机模式）
    fun getRegularPlayerSettings(): List<PlayerSetting> {
        return playerSettings.filter { it.playerIndex in 0..7 }
    }
    
    // 新增方法：获取所有玩家设置（包括掌机模式）
    fun getAllPlayerSettings(): List<PlayerSetting> {
        return playerSettings.toList()
    }
    
    // 新增方法：应用所有已连接玩家的设置到Native层
    fun applyAllConnectedPlayerSettings() {
        playerSettings.forEach { setting ->
            if (setting.isConnected) {
                applyPlayerSettingToNative(setting)
            }
        }
    }
    
    // 扩展函数：检查控制器类型是否有效
    private fun Int.isValidControllerType(): Boolean {
        return this in 0..4
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
    
    // 新增方法：获取控制器类型枚举
    fun getControllerTypeEnum(controllerTypeIndex: Int): ControllerType {
        return when (controllerTypeIndex) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
    }
    
    // 新增方法：从控制器类型枚举获取索引
    fun getControllerTypeIndex(controllerType: ControllerType): Int {
        return when (controllerType) {
            ControllerType.PRO_CONTROLLER -> 0
            ControllerType.JOYCON_LEFT -> 1
            ControllerType.JOYCON_RIGHT -> 2
            ControllerType.JOYCON_PAIR -> 3
            ControllerType.HANDHELD -> 4
        }
    }
    
    // 新增方法：重新加载玩家设置
    fun reloadPlayerSettings() {
        loadPlayerSettings()
    }
    
    // 新增方法：重置玩家设置为默认值
    fun resetPlayerSettingsToDefault() {
        initDefaultPlayerSettings()
        savePlayerSettings()
    }
    
    // 新增方法：检查设置是否已更改
    fun hasPlayerSettingsChanged(originalSettings: List<PlayerSetting>): Boolean {
        if (playerSettings.size != originalSettings.size) return true
        
        return playerSettings.any { currentSetting ->
            val originalSetting = originalSettings.find { it.playerIndex == currentSetting.playerIndex }
            originalSetting == null || 
            originalSetting.isConnected != currentSetting.isConnected || 
            originalSetting.controllerType != currentSetting.controllerType
        }
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
