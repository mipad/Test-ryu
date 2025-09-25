package org.ryujinx.android

import com.sun.jna.JNIEnv
import com.sun.jna.Library
import com.sun.jna.Native
import org.ryujinx.android.viewmodels.GameInfo
import java.util.Collections
import java.util.concurrent.atomic.AtomicInteger

interface RyujinxNativeJna : Library {
    fun deviceInitialize(
        isHostMapped: Boolean, useNce: Boolean,
        systemLanguage: Int,
        regionCode: Int,
        enableVsync: Boolean,
        enableDockedMode: Boolean,
        enablePtc: Boolean,
        enableJitCacheEviction: Boolean,
        enableInternetAccess: Boolean,
        timeZone: String,
        ignoreMissingServices: Boolean,
        audioEngineType: Int,  // 新增音频引擎参数
        memoryConfiguration: Int  // 新增内存配置参数
    ): Boolean

    fun graphicsInitialize(
        rescale: Float = 1f,
        maxAnisotropy: Float = 1f,
        fastGpuTime: Boolean = true,
        fast2DCopy: Boolean = true,
        enableMacroJit: Boolean = false,
        enableMacroHLE: Boolean = true,
        enableShaderCache: Boolean = true,
        enableTextureRecompression: Boolean = false,
        backendThreading: Int = BackendThreading.Auto.ordinal,
        aspectRatio: Int = 0 // 新增参数：画面比例
    ): Boolean

    fun graphicsInitializeRenderer(
        extensions: Array<String>,
        extensionsLength: Int,
        driver: Long
    ): Boolean

    fun javaInitialize(appPath: String, env: JNIEnv): Boolean
    fun deviceLaunchMiiEditor(): Boolean
    fun deviceGetGameFrameRate(): Double
    fun deviceGetGameFrameTime(): Double
    fun deviceGetGameFifo(): Double
    fun deviceLoadDescriptor(fileDescriptor: Int, gameType: Int, updateDescriptor: Int): Boolean
    fun graphicsRendererSetSize(width: Int, height: Int)
    fun graphicsRendererSetVsync(enabled: Boolean)
    fun graphicsRendererRunLoop()
    fun deviceReloadFilesystem()
    fun inputInitialize(width: Int, height: Int)
    fun inputSetClientSize(width: Int, height: Int)
    fun inputSetTouchPoint(x: Int, y: Int)
    fun inputReleaseTouchPoint()
    fun inputUpdate()
    
    // 修改：使用玩家索引而不是设备ID
    fun inputSetButtonPressed(button: Int, playerIndex: Int)
    
    // 修改：使用玩家索引而不是设备ID
    fun inputSetButtonReleased(button: Int, playerIndex: Int)
    
    // 修改：使用玩家索引而不是设备ID
    fun inputConnectGamepad(playerIndex: Int): Int
    
    // 修改：使用玩家索引而不是设备ID
    fun inputSetStickAxis(stick: Int, x: Float, y: Float, playerIndex: Int)
    
    // 修改：使用玩家索引而不是设备ID
    fun inputSetAccelerometerData(x: Float, y: Float, z: Float, playerIndex: Int)
    
    // 修改：使用玩家索引而不是设备ID
    fun inputSetGyroData(x: Float, y: Float, z: Float, playerIndex: Int)
    
    fun deviceCloseEmulation()
    fun deviceSignalEmulationClose()
    fun userGetOpenedUser(): String
    fun userGetUserPicture(userId: String): String
    fun userSetUserPicture(userId: String, picture: String)
    fun userGetUserName(userId: String): String
    fun userSetUserName(userId: String, userName: String)
    fun userAddUser(username: String, picture: String)
    fun userDeleteUser(userId: String)
    fun userOpenUser(userId: String)
    fun userCloseUser(userId: String)
    fun loggingSetEnabled(logLevel: Int, enabled: Boolean)
    fun deviceVerifyFirmware(fileDescriptor: Int, isXci: Boolean): String
    fun deviceInstallFirmware(fileDescriptor: Int, isXci: Boolean)
    fun deviceGetInstalledFirmwareVersion(): String
    fun uiHandlerSetup()
    fun uiHandlerSetResponse(isOkPressed: Boolean, input: String)
    fun deviceGetDlcTitleId(path: String, ncaPath: String): String
    fun deviceGetGameInfo(fileDescriptor: Int, extension: String, info: GameInfo)
    fun userGetAllUsers(): Array<String>
    fun deviceGetDlcContentList(path: String, titleId: Long): Array<String>
    fun loggingEnabledGraphicsLog(enabled: Boolean)
    
    // 添加跳过内存屏障的方法
    fun setSkipMemoryBarriers(skip: Boolean)
    
    // 添加设置画面比例的方法
    fun setAspectRatio(aspectRatio: Int)
    
    // 在 RyujinxNativeJna 接口中添加这个方法
    fun setAudioBackend(audioBackend: Int)
    
    // 添加设置缩放过滤器的方法
    fun setScalingFilter(filter: Int)
    fun setScalingFilterLevel(level: Int)
    
    // 添加设置抗锯齿的方法
    fun setAntiAliasing(mode: Int)
    
    // 添加设置内存配置的方法
    fun setMemoryConfiguration(memoryConfiguration: Int)
    
    // 修改：设置控制器类型的方法 - 使用玩家索引而不是设备ID
    fun setControllerType(playerIndex: Int, controllerType: Int)
    
    // 金手指相关方法
    fun cheatGetCheats(titleId: String, gamePath: String): Array<String>
    fun cheatGetEnabledCheats(titleId: String): Array<String>
    fun cheatSetEnabled(titleId: String, cheatId: String, enabled: Boolean)
    fun cheatSave(titleId: String)
}

class RyujinxNative {

    companion object {
        val jnaInstance: RyujinxNativeJna = Native.load(
            "ryujinx",
            RyujinxNativeJna::class.java,
            Collections.singletonMap(Library.OPTION_ALLOW_OBJECTS, true)
        )
        
        // 玩家索引管理 (0-7)
        private val connectedPlayerIndices = mutableSetOf<Int>()
        
        /**
         * 获取下一个可用的玩家索引 (0-7)
         * @return 可用的玩家索引，如果没有可用索引则返回-1
         */
        @JvmStatic
        fun getNextAvailablePlayerIndex(): Int {
            synchronized(connectedPlayerIndices) {
                // 尝试从0到7的索引
                for (i in 0..7) {
                    if (!connectedPlayerIndices.contains(i)) {
                        connectedPlayerIndices.add(i)
                        return i
                    }
                }
                return -1 // 没有可用索引
            }
        }
        
        /**
         * 释放玩家索引
         * @param playerIndex 要释放的玩家索引 (0-7)
         */
        @JvmStatic
        fun releasePlayerIndex(playerIndex: Int) {
            synchronized(connectedPlayerIndices) {
                connectedPlayerIndices.remove(playerIndex)
            }
        }
        
        /**
         * 连接游戏手柄并返回玩家索引
         * @return 玩家索引 (0-7)，如果连接失败返回-1
         */
        @JvmStatic
        fun connectGamepad(): Int {
            val playerIndex = getNextAvailablePlayerIndex()
            if (playerIndex != -1) {
                // 调用原生方法连接游戏手柄
                val result = jnaInstance.inputConnectGamepad(playerIndex)
                if (result == -1) {
                    // 连接失败，释放玩家索引
                    releasePlayerIndex(playerIndex)
                    return -1
                }
                android.util.Log.d("RyujinxNative", "Connected gamepad with player index: $playerIndex")
            }
            return playerIndex
        }
        
        /**
         * 断开游戏手柄连接
         * @param playerIndex 要断开的玩家索引 (0-7)
         */
        @JvmStatic
        fun disconnectGamepad(playerIndex: Int) {
            if (playerIndex in 0..7) {
                // 释放玩家索引
                releasePlayerIndex(playerIndex)
                android.util.Log.d("RyujinxNative", "Disconnected gamepad with player index: $playerIndex")
            }
        }
        
        /**
         * 检查玩家索引是否有效且可用
         * @param playerIndex 要检查的玩家索引
         * @return 是否有效且可用
         */
        @JvmStatic
        fun isPlayerIndexAvailable(playerIndex: Int): Boolean {
            return playerIndex in 0..7 && !connectedPlayerIndices.contains(playerIndex)
        }
        
        /**
         * 获取所有已连接的玩家索引
         * @return 已连接的玩家索引列表
         */
        @JvmStatic
        fun getConnectedPlayerIndices(): List<Int> {
            return synchronized(connectedPlayerIndices) {
                connectedPlayerIndices.toList()
            }
        }

        @JvmStatic
        fun test() {
            val i = 0
        }

        @JvmStatic
        fun frameEnded() {
            MainActivity.frameEnded()
        }

        @JvmStatic
        fun getSurfacePtr(): Long {
            return MainActivity.mainViewModel?.gameHost?.currentSurface ?: -1
        }

        @JvmStatic
        fun getWindowHandle(): Long {
            return MainActivity.mainViewModel?.gameHost?.currentWindowhandle ?: -1
        }

        @JvmStatic
        fun updateProgress(infoPtr: Long, progress: Float) {
            val info = NativeHelpers.instance.getStringJava(infoPtr)
            MainActivity.mainViewModel?.gameHost?.setProgress(info, progress)
        }

        @JvmStatic
        fun updateUiHandler(
            newTitlePointer: Long,
            newMessagePointer: Long,
            newWatermarkPointer: Long,
            newType: Int,
            min: Int,
            max: Int,
            nMode: Int,
            newSubtitlePointer: Long,
            newInitialTextPointer: Long
        ) {
            var uiHandler = MainActivity.mainViewModel?.activity?.uiHandler
            uiHandler?.apply {
                val newTitle = NativeHelpers.instance.getStringJava(newTitlePointer)
                val newMessage = NativeHelpers.instance.getStringJava(newMessagePointer)
                val newWatermark = NativeHelpers.instance.getStringJava(newWatermarkPointer)
                val newSubtitle = NativeHelpers.instance.getStringJava(newSubtitlePointer)
                val newInitialText = NativeHelpers.instance.getStringJava(newInitialTextPointer)
                val newMode = KeyboardMode.entries[nMode]
                update(newTitle,
                    newMessage,
                    newWatermark,
                    newType,
                    min,
                    max,
                    newMode,
                    newSubtitle,
                    newInitialText)
            }
        }
        
        // 添加设置抗锯齿的静态方法
        @JvmStatic
        fun setAntiAliasing(mode: Int) {
            jnaInstance.setAntiAliasing(mode)
        }
        
        // 添加设置内存配置的静态方法
        @JvmStatic
        fun setMemoryConfiguration(memoryConfiguration: Int) {
            jnaInstance.setMemoryConfiguration(memoryConfiguration)
        }
        
        // 修改：设置控制器类型的静态方法 - 使用玩家索引而不是设备ID
        @JvmStatic
        fun setControllerType(playerIndex: Int, controllerType: Int) {
            android.util.Log.d("RyujinxNative", "Setting controller type: playerIndex=$playerIndex, controllerType=$controllerType")
            jnaInstance.setControllerType(playerIndex, controllerType)
        }
        
        // 金手指相关静态方法
        @JvmStatic
        fun getCheats(titleId: String, gamePath: String): Array<String> {
            return jnaInstance.cheatGetCheats(titleId, gamePath)
        }

        @JvmStatic
        fun getEnabledCheats(titleId: String): Array<String> {
            return jnaInstance.cheatGetEnabledCheats(titleId)
        }

        @JvmStatic
        fun setCheatEnabled(titleId: String, cheatId: String, enabled: Boolean) {
            jnaInstance.cheatSetEnabled(titleId, cheatId, enabled)
        }

        @JvmStatic
        fun saveCheats(titleId: String) {
            jnaInstance.cheatSave(titleId)
        }
        
        // 新增：设置按钮按下的静态方法 - 使用玩家索引
        @JvmStatic
        fun setButtonPressed(button: Int, playerIndex: Int) {
            jnaInstance.inputSetButtonPressed(button, playerIndex)
        }
        
        // 新增：设置按钮释放的静态方法 - 使用玩家索引
        @JvmStatic
        fun setButtonReleased(button: Int, playerIndex: Int) {
            jnaInstance.inputSetButtonReleased(button, playerIndex)
        }
        
        // 新增：设置摇杆轴的静态方法 - 使用玩家索引
        @JvmStatic
        fun setStickAxis(stick: Int, x: Float, y: Float, playerIndex: Int) {
            jnaInstance.inputSetStickAxis(stick, x, y, playerIndex)
        }
        
        // 新增：设置加速度计数据的静态方法 - 使用玩家索引
        @JvmStatic
        fun setAccelerometerData(x: Float, y: Float, z: Float, playerIndex: Int) {
            jnaInstance.inputSetAccelerometerData(x, y, z, playerIndex)
        }
        
        // 新增：设置陀螺仪数据的静态方法 - 使用玩家索引
        @JvmStatic
        fun setGyroData(x: Float, y: Float, z: Float, playerIndex: Int) {
            jnaInstance.inputSetGyroData(x, y, z, playerIndex)
        }
    }
    
    // 玩家索引相关的枚举和常量
    companion object PlayerIndex {
        const val PLAYER_1 = 0
        const val PLAYER_2 = 1
        const val PLAYER_3 = 2
        const val PLAYER_4 = 3
        const val PLAYER_5 = 4
        const val PLAYER_6 = 5
        const val PLAYER_7 = 6
        const val PLAYER_8 = 7
        
        /**
         * 获取玩家显示名称
         * @param playerIndex 玩家索引 (0-7)
         * @return 显示名称，如 "Player 1"
         */
        @JvmStatic
        fun getPlayerDisplayName(playerIndex: Int): String {
            return if (playerIndex in 0..7) {
                "Player ${playerIndex + 1}"
            } else {
                "Unknown Player"
            }
        }
    }
}
