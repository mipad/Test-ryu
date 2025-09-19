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
    fun inputSetButtonPressed(button: Int, id: Int)
    fun inputSetButtonReleased(button: Int, id: Int)
    fun inputConnectGamepad(index: Int): Int
    fun inputSetStickAxis(stick: Int, x: Float, y: Float, id: Int)
    fun inputSetAccelerometerData(x: Float, y: Float, z: Float, id: Int)
    fun inputSetGyroData(x: Float, y: Float, z: Float, id: Int)
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
    // 添加设置控制器类型的方法
    fun setControllerType(deviceId: Int, controllerType: Int)
    
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
        
        // 设备ID管理
        private val connectedDeviceIds = mutableSetOf<Int>()
        private val nextDeviceId = AtomicInteger(0)
        
        /**
         * 获取下一个可用的设备ID
         * @return 可用的设备ID，如果没有可用ID则返回-1
         */
        @JvmStatic
        fun getNextAvailableDeviceId(): Int {
            synchronized(connectedDeviceIds) {
                // 尝试从0到7的ID
                for (i in 0..7) {
                    if (!connectedDeviceIds.contains(i)) {
                        connectedDeviceIds.add(i)
                        return i
                    }
                }
                return -1 // 没有可用ID
            }
        }
        
        /**
         * 释放设备ID
         * @param deviceId 要释放的设备ID
         */
        @JvmStatic
        fun releaseDeviceId(deviceId: Int) {
            synchronized(connectedDeviceIds) {
                connectedDeviceIds.remove(deviceId)
            }
        }
        
        /**
         * 连接游戏手柄并返回设备ID
         * @return 设备ID，如果连接失败返回-1
         */
        @JvmStatic
        fun connectGamepad(): Int {
            val deviceId = getNextAvailableDeviceId()
            if (deviceId != -1) {
                // 调用原生方法连接游戏手柄
                val result = jnaInstance.inputConnectGamepad(deviceId)
                if (result == -1) {
                    // 连接失败，释放设备ID
                    releaseDeviceId(deviceId)
                    return -1
                }
            }
            return deviceId
        }
        
        /**
         * 断开游戏手柄连接
         * @param deviceId 要断开的设备ID
         */
        @JvmStatic
        fun disconnectGamepad(deviceId: Int) {
            if (deviceId != -1) {
                // 释放设备ID
                releaseDeviceId(deviceId)
                // 注意：原生层可能没有显式的断开连接方法
                // 如果需要，可以在这里调用相应的原生方法
            }
        }

        @JvmStatic
        fun test()
        {
            val i = 0
        }

        @JvmStatic
        fun frameEnded()
        {
            MainActivity.frameEnded()
        }

        @JvmStatic
        fun getSurfacePtr() : Long
        {
            return MainActivity.mainViewModel?.gameHost?.currentSurface ?: -1
        }

        @JvmStatic
        fun getWindowHandle() : Long
        {
            return MainActivity.mainViewModel?.gameHost?.currentWindowhandle ?: -1
        }

        @JvmStatic
        fun updateProgress(infoPtr : Long, progress: Float)
        {
            val info = NativeHelpers.instance.getStringJava(infoPtr);
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
        )
        {
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
                    newInitialText);
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
        
        // 添加设置控制器类型的静态方法
        @JvmStatic
        fun setControllerType(deviceId: Int, controllerType: Int) {
            jnaInstance.setControllerType(deviceId, controllerType)
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
    }
}
