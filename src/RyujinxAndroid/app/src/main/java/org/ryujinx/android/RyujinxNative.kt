package org.ryujinx.android

import com.sun.jna.JNIEnv
import com.sun.jna.Library
import com.sun.jna.Native
import org.ryujinx.android.viewmodels.GameInfo
import java.util.Collections

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
        memoryConfiguration: Int,  // 新增内存配置参数
        systemTimeOffset: Long  // 新增系统时间偏移参数
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
    // 添加设置音频引擎的方法
    fun setAudioBackend(audioBackend: Int)
    // 添加设置缩放过滤器的方法
    fun setScalingFilter(filter: Int)
    fun setScalingFilterLevel(level: Int)
    // 添加设置抗锯齿的方法
    fun setAntiAliasing(mode: Int)
    // 添加设置内存配置的方法
    fun setMemoryConfiguration(memoryConfiguration: Int)
    // 添加设置系统时间偏移的方法
    fun setSystemTimeOffset(offset: Long)
    // 添加获取系统时间偏移的方法
    fun getSystemTimeOffset(): Long
    
    // 金手指相关方法
    fun cheatGetCheats(titleId: String, gamePath: String): Array<String>
    fun cheatGetEnabledCheats(titleId: String): Array<String>
    fun cheatSetEnabled(titleId: String, cheatId: String, enabled: Boolean)
    fun cheatSave(titleId: String)
    
    // 存档管理相关方法
    fun saveDataExport(titleId: String, outputPath: String): Boolean
    fun saveDataImport(titleId: String, zipFilePath: String): Boolean
    fun saveDataDelete(titleId: String): Boolean
    fun saveDataGetSaveId(titleId: String): String
    fun saveDataExists(titleId: String): Boolean
    
    // 新增：删除存档文件的方法（只删除0和1文件夹中的文件）
    fun saveDataDeleteFiles(titleId: String): Boolean
    
    // 新增：刷新存档数据的方法
    fun saveDataRefresh()
    
    // 新增：调试存档数据的方法
    fun saveDataDebug()
    
    // 新增：获取所有存档列表的方法
    fun saveDataGetAll(): Array<String>
}

class RyujinxNative {

    companion object {
        val jnaInstance: RyujinxNativeJna = Native.load(
            "ryujinx",
            RyujinxNativeJna::class.java,
            Collections.singletonMap(Library.OPTION_ALLOW_OBJECTS, true)
        )

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
        
        // 添加设置系统时间偏移的静态方法
        @JvmStatic
        fun setSystemTimeOffset(offset: Long) {
            jnaInstance.setSystemTimeOffset(offset)
        }
        
        // 添加获取系统时间偏移的静态方法
        @JvmStatic
        fun getSystemTimeOffset(): Long {
            return jnaInstance.getSystemTimeOffset()
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
        
        // 存档管理相关静态方法
        @JvmStatic
        fun exportSaveData(titleId: String, outputPath: String): Boolean {
            return jnaInstance.saveDataExport(titleId, outputPath)
        }

        @JvmStatic
        fun importSaveData(titleId: String, zipFilePath: String): Boolean {
            return jnaInstance.saveDataImport(titleId, zipFilePath)
        }

        @JvmStatic
        fun deleteSaveData(titleId: String): Boolean {
            return jnaInstance.saveDataDelete(titleId)
        }

        @JvmStatic
        fun deleteSaveFiles(titleId: String): Boolean {
            return jnaInstance.saveDataDeleteFiles(titleId)
        }

        @JvmStatic
        fun getSaveIdByTitleId(titleId: String): String {
            return jnaInstance.saveDataGetSaveId(titleId)
        }

        @JvmStatic
        fun saveDataExists(titleId: String): Boolean {
            return jnaInstance.saveDataExists(titleId)
        }
        
        // 新增：刷新存档数据的静态方法
        @JvmStatic
        fun refreshSaveData() {
            jnaInstance.saveDataRefresh()
        }
        
        // 新增：调试存档数据的静态方法
        @JvmStatic
        fun debugSaveData() {
            jnaInstance.saveDataDebug()
        }
        
        // 新增：获取所有存档列表的静态方法
        @JvmStatic
        fun getAllSaveData(): Array<String> {
            return jnaInstance.saveDataGetAll()
        }
    }
}
