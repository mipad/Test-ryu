package org.ryujinx.android.viewmodels

import android.app.Activity
import android.content.SharedPreferences
import androidx.preference.PreferenceManager
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.ryujinx.android.ControllerType
import org.ryujinx.android.RegionCode
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.SystemLanguage
import org.ryujinx.android.views.PlayerSetting

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
    
    // 玩家设置列表（使用 0-based 索引，0-7为普通玩家，8为掌机模式）
    var playerSettings: MutableList<PlayerSetting> = mutableListOf()
    
    // 掌机模式设置（单独存储，因为掌机模式是特殊的玩家索引8）
    var handheldSetting: PlayerSetting = PlayerSetting(8, false, 4) // 索引8，默认关闭，控制器类型为Handheld

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
        
        // 加载玩家设置
        val json = sharedPref.getString("player_settings", null)
        if (json != null) {
            try {
                playerSettings = Json.decodeFromString<MutableList<PlayerSetting>>(json).toMutableList()
                
                // 确保所有控制器类型值有效
                playerSettings.forEach { setting ->
                    if (!setting.controllerType.isValidControllerType()) {
                        setting.controllerType = 0 // 重置为默认值
                    }
                }
            } catch (e: Exception) {
                // 如果解析失败，使用默认设置
                android.util.Log.e("QuickSettings", "Failed to parse player settings, using defaults", e)
                initDefaultPlayerSettings()
            }
        } else {
            initDefaultPlayerSettings()
        }
        
        // 加载掌机模式设置
        val handheldJson = sharedPref.getString("handheld_setting", null)
        if (handheldJson != null) {
            try {
                handheldSetting = Json.decodeFromString<PlayerSetting>(handheldJson)
                // 确保控制器类型值有效
                if (!handheldSetting.controllerType.isValidControllerType()) {
                    handheldSetting.controllerType = 4 // 重置为Handheld
                }
            } catch (e: Exception) {
                android.util.Log.e("QuickSettings", "Failed to parse handheld setting, using default", e)
                handheldSetting = PlayerSetting(8, false, 4) // 默认掌机模式设置
            }
        }

        enableDebugLogs = sharedPref.getBoolean("enableDebugLogs", false)
        enableStubLogs = sharedPref.getBoolean("enableStubLogs", false)
        enableInfoLogs = sharedPref.getBoolean("enableInfoLogs", true)
        enableWarningLogs = sharedPref.getBoolean("enableWarningLogs", true)
        enableErrorLogs = sharedPref.getBoolean("enableErrorLogs", true)
        enableGuestLogs = sharedPref.getBoolean("enableGuestLogs", true)
        enableAccessLogs = sharedPref.getBoolean("enableAccessLogs", false)
        enableTraceLogs = sharedPref.getBoolean("enableStubLogs", false)
        enableGraphicsLogs = sharedPref.getBoolean("enableGraphicsLogs", false)
        
        // 初始化时立即应用控制器设置
        applyControllerSettings()
    }

    // 初始化默认玩家设置（使用 0-based 索引）
    private fun initDefaultPlayerSettings() {
        playerSettings = mutableListOf(
            PlayerSetting(0, true, 0), // Player 1 (索引0) 默认开启，Pro Controller
            PlayerSetting(1, false, 0), // Player 2 (索引1) 默认关闭
            PlayerSetting(2, false, 0), // Player 3 (索引2) 默认关闭
            PlayerSetting(3, false, 0), // Player 4 (索引3) 默认关闭
            PlayerSetting(4, false, 0), // Player 5 (索引4) 默认关闭
            PlayerSetting(5, false, 0), // Player 6 (索引5) 默认关闭
            PlayerSetting(6, false, 0), // Player 7 (索引6) 默认关闭
            PlayerSetting(7, false, 0)  // Player 8 (索引7) 默认关闭
        )
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
        
        // 保存玩家设置
        val json = Json.encodeToString(playerSettings)
        editor.putString("player_settings", json)
        
        // 保存掌机模式设置
        val handheldJson = Json.encodeToString(handheldSetting)
        editor.putString("handheld_setting", handheldJson)

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
    
    // 获取指定玩家的设置（使用 0-based 索引）
    fun getPlayerSetting(playerIndex: Int): PlayerSetting? {
        return if (playerIndex == 8) {
            // 掌机模式
            handheldSetting
        } else {
            playerSettings.find { it.playerIndex == playerIndex }
        }
    }
    
    // 更新玩家设置
    fun updatePlayerSetting(playerSetting: PlayerSetting) {
        if (playerSetting.playerIndex == 8) {
            // 更新掌机模式设置
            handheldSetting = playerSetting
        } else {
            val index = playerSettings.indexOfFirst { it.playerIndex == playerSetting.playerIndex }
            if (index != -1) {
                // 确保控制器类型值有效
                if (!playerSetting.controllerType.isValidControllerType()) {
                    playerSetting.controllerType = 0 // 重置为默认值
                }
                
                playerSettings[index] = playerSetting
            }
        }
        
        // 立即应用设置
        applyControllerSettings()
    }
    
    // 应用控制器设置到Native层 - 使用 0-based 玩家索引
    fun applyControllerSettings() {
        try {
            // 设置所有玩家的控制器类型，使用 0-based 玩家索引 (0-7)
            for (playerSetting in playerSettings) {
                if (playerSetting.isConnected) {
                    // 确保控制器类型值有效
                    val controllerType = playerSetting.controllerType.coerceIn(0, 4)
                    
                    // 将控制器类型索引转换为位掩码值
                    val controllerTypeBitmask = controllerTypeIndexToBitmask(controllerType)
                    
                    // 使用 0-based 玩家索引
                    RyujinxNative.jnaInstance.setControllerType(playerSetting.playerIndex, controllerTypeBitmask)
                    
                    // 记录设置信息
                    android.util.Log.d("QuickSettings", "Controller type set to: ${getControllerTypeName(controllerType)} (bitmask: $controllerTypeBitmask) for player index ${playerSetting.playerIndex}")
                }
            }
            
            // 单独设置掌机模式（玩家索引8）
            if (handheldSetting.isConnected) {
                // 确保控制器类型值有效（掌机模式应该为Handheld）
                val controllerType = if (handheldSetting.controllerType.isValidControllerType()) {
                    handheldSetting.controllerType
                } else {
                    4 // 强制设置为Handheld
                }
                
                // 将控制器类型索引转换为位掩码值
                val controllerTypeBitmask = controllerTypeIndexToBitmask(controllerType)
                
                // 使用掌机模式索引8
                RyujinxNative.jnaInstance.setControllerType(8, controllerTypeBitmask)
                
                // 记录设置信息
                android.util.Log.d("QuickSettings", "Handheld controller type set to: ${getControllerTypeName(controllerType)} (bitmask: $controllerTypeBitmask) for player index 8")
            }
        } catch (e: Exception) {
            android.util.Log.e("QuickSettings", "Failed to apply controller settings", e)
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
    
    // 获取控制器类型名称
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
    
    // 获取当前控制器类型的枚举值（玩家1，索引0）
    fun getCurrentControllerType(): ControllerType {
        val player1Setting = getPlayerSetting(0)
        val type = player1Setting?.controllerType ?: 0
        
        return when (type.coerceIn(0, 4)) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
    }
    
    // 设置控制器类型通过枚举值（玩家1，索引0）
    fun setControllerType(controllerType: ControllerType) {
        val player1Setting = getPlayerSetting(0)
        if (player1Setting != null) {
            val typeValue = when (controllerType) {
                ControllerType.PRO_CONTROLLER -> 0
                ControllerType.JOYCON_LEFT -> 1
                ControllerType.JOYCON_RIGHT -> 2
                ControllerType.JOYCON_PAIR -> 3
                ControllerType.HANDHELD -> 4
            }
            
            player1Setting.controllerType = typeValue
            updatePlayerSetting(player1Setting)
        }
    }
    
    // 设置掌机模式控制器类型
    fun setHandheldControllerType(controllerType: ControllerType) {
        val typeValue = when (controllerType) {
            ControllerType.PRO_CONTROLLER -> 0
            ControllerType.JOYCON_LEFT -> 1
            ControllerType.JOYCON_RIGHT -> 2
            ControllerType.JOYCON_PAIR -> 3
            ControllerType.HANDHELD -> 4
        }
        
        handheldSetting.controllerType = typeValue
        updatePlayerSetting(handheldSetting)
    }
    
    // 扩展函数：检查控制器类型是否有效
    private fun Int.isValidControllerType(): Boolean {
        return this in 0..4
    }
    
    // 新增方法：获取玩家显示名称（用于UI显示）
    fun getPlayerDisplayName(playerIndex: Int): String {
        return when (playerIndex) {
            in 0..7 -> "Player ${playerIndex + 1}"
            8 -> "Handheld" // 掌机模式
            else -> "Unknown Player"
        }
    }
    
    // 新增方法：检查玩家索引是否有效
    fun isValidPlayerIndex(playerIndex: Int): Boolean {
        return playerIndex in 0..8 // 现在包括掌机模式索引8
    }
    
    // 新增方法：获取所有已连接的玩家索引（包括掌机模式）
    fun getConnectedPlayerIndices(): List<Int> {
        val connectedPlayers = playerSettings.filter { it.isConnected }.map { it.playerIndex }.toMutableList()
        if (handheldSetting.isConnected) {
            connectedPlayers.add(8) // 添加掌机模式
        }
        return connectedPlayers
    }
    
    // 新增方法：连接玩家
    fun connectPlayer(playerIndex: Int) {
        if (isValidPlayerIndex(playerIndex)) {
            val setting = getPlayerSetting(playerIndex)
            if (setting != null && !setting.isConnected) {
                setting.isConnected = true
                updatePlayerSetting(setting)
                android.util.Log.d("QuickSettings", "Connected player index: $playerIndex")
            }
        }
    }
    
    // 新增方法：断开玩家连接
    fun disconnectPlayer(playerIndex: Int) {
        if (isValidPlayerIndex(playerIndex)) {
            val setting = getPlayerSetting(playerIndex)
            if (setting != null && setting.isConnected) {
                setting.isConnected = false
                updatePlayerSetting(setting)
                android.util.Log.d("QuickSettings", "Disconnected player index: $playerIndex")
            }
        }
    }
    
    // 新增方法：连接掌机模式
    fun connectHandheld() {
        if (!handheldSetting.isConnected) {
            handheldSetting.isConnected = true
            updatePlayerSetting(handheldSetting)
            android.util.Log.d("QuickSettings", "Connected handheld mode")
        }
    }
    
    // 新增方法：断开掌机模式连接
    fun disconnectHandheld() {
        if (handheldSetting.isConnected) {
            handheldSetting.isConnected = false
            updatePlayerSetting(handheldSetting)
            android.util.Log.d("QuickSettings", "Disconnected handheld mode")
        }
    }
    
    // 新增方法：检查掌机模式是否连接
    fun isHandheldConnected(): Boolean {
        return handheldSetting.isConnected
    }
    
    // 新增方法：获取掌机模式设置
    fun getHandheldSetting(): PlayerSetting {
        return handheldSetting
    }
    
    // 新增方法：更新掌机模式设置
    fun updateHandheldSetting(setting: PlayerSetting) {
        if (setting.playerIndex == 8) {
            handheldSetting = setting
            updatePlayerSetting(handheldSetting)
        }
    }
    
    // 新增方法：获取所有玩家设置（包括掌机模式）
    fun getAllPlayerSettings(): List<PlayerSetting> {
        val allSettings = playerSettings.toMutableList()
        allSettings.add(handheldSetting)
        return allSettings
    }
    
    // 新增方法：获取普通玩家设置（不包括掌机模式）
    fun getRegularPlayerSettings(): List<PlayerSetting> {
        return playerSettings.toList()
    }
    
    // 新增方法：检查是否为掌机模式索引
    fun isHandheldIndex(playerIndex: Int): Boolean {
        return playerIndex == 8
    }
    
    // 新增方法：获取掌机模式索引
    fun getHandheldIndex(): Int {
        return 8
    }
    
    // 新增方法：切换掌机模式状态
    fun toggleHandheldMode() {
        if (handheldSetting.isConnected) {
            disconnectHandheld()
        } else {
            connectHandheld()
        }
    }
    
    // 新增方法：设置掌机模式控制器类型为Handheld（确保正确）
    fun ensureHandheldControllerType() {
        if (handheldSetting.controllerType != 4) {
            handheldSetting.controllerType = 4
            updatePlayerSetting(handheldSetting)
        }
    }
}
