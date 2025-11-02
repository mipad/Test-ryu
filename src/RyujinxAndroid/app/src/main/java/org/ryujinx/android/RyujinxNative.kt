package org.ryujinx.android

import com.sun.jna.JNIEnv
import com.sun.jna.Library
import com.sun.jna.Native
import org.ryujinx.android.viewmodels.GameInfo
import java.util.Collections

interface RyujinxNativeJna : Library {
    fun deviceInitialize(
        memoryManagerMode: Int,  // 修改：使用MemoryManagerMode枚举值
        useNce: Boolean,
        systemLanguage: Int,
        regionCode: Int,
        enableVsync: Boolean,
        enableDockedMode: Boolean,
        enablePtc: Boolean,
        enableJitCacheEviction: Boolean,
        enableInternetAccess: Boolean,
        timeZone: String,
        ignoreMissingServices: Boolean,
        audioEngineType: Int,
        memoryConfiguration: Int,
        systemTimeOffset: Long
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
        aspectRatio: Int = 0
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
    
    // ==================== Mod 管理相关方法 ====================
    fun modsGetMods(titleId: String): String
    fun modsSetEnabled(titleId: String, modPath: String, enabled: Boolean): Boolean
    fun modsDeleteMod(titleId: String, modPath: String): Boolean
    fun modsDeleteAll(titleId: String): Boolean
    fun modsEnableAll(titleId: String): Boolean
    fun modsDisableAll(titleId: String): Boolean
    fun modsAddMod(titleId: String, sourcePath: String, modName: String): Boolean

    // ==================== 表面格式管理相关方法 ====================
    // 获取设备支持的表面格式列表
    fun surfaceGetAvailableFormats(): Array<String>
    
    // 设置自定义表面格式
    fun surfaceSetCustomFormat(format: Int, colorSpace: Int)
    
    // 清除自定义表面格式设置
    fun surfaceClearCustomFormat()
    
    // 检查自定义表面格式是否有效
    fun surfaceIsCustomFormatValid(): Boolean
    
    // 获取当前表面格式信息
    fun surfaceGetCurrentFormatInfo(): String
    
    // 新增：游戏启动时触发表面格式保存的方法
    fun surfaceOnGameStarted()

    // ==================== 表面管理相关方法（使用现有的方法） ====================
    fun graphicsSetPresentEnabled(enabled: Boolean)
    fun ReleaseRendererSurface()
    fun TryReattachSurface(): Boolean

    // ==================== 窗口管理相关方法 ====================
    fun deviceSetWindowHandle(handle: Long)
    fun deviceRecreateSwapchain()
    fun deviceWaitForGpuDone(timeoutMs: Int)
    
    // ==================== 新增：暂停和恢复模拟器的方法 ====================
    fun devicePauseEmulation()
    fun deviceResumeEmulation()
    fun deviceIsEmulationPaused(): Boolean
}

class RyujinxNative {

    companion object {
        val jnaInstance: RyujinxNativeJna = Native.load(
            "ryujinx",
            RyujinxNativeJna::class.java,
            Collections.singletonMap(Library.OPTION_ALLOW_OBJECTS, true)
        )

        @JvmStatic
        fun frameEnded()
        {
            MainActivity.frameEnded()
        }

        @JvmStatic
        fun test() {
            // no-op
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
        fun detachWindow() {
            try { 
                graphicsSetPresentEnabled(false) 
                deviceWaitForGpuDone(100)
                ReleaseRendererSurface()
                deviceSetWindowHandle(0)
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun reattachWindowIfReady(): Boolean {
            return try {
                val handle = getWindowHandle()
                if (handle <= 0) return false
                deviceSetWindowHandle(handle)
                deviceRecreateSwapchain()
                true
            } catch (_: Throwable) {
                false
            }
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
        
        // ==================== 设备管理相关静态方法 ====================
        
        @JvmStatic
        fun deviceCloseEmulation() {
            try {
                jnaInstance.deviceCloseEmulation()
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun deviceSignalEmulationClose() {
            try {
                jnaInstance.deviceSignalEmulationClose()
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun deviceSetWindowHandle(handle: Long) {
            try {
                jnaInstance.deviceSetWindowHandle(handle)
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun deviceRecreateSwapchain() {
            try {
                jnaInstance.deviceRecreateSwapchain()
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun deviceWaitForGpuDone(timeoutMs: Int) {
            try {
                jnaInstance.deviceWaitForGpuDone(timeoutMs)
            } catch (_: Throwable) {}
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
        
        // ==================== Mod 管理相关静态方法 ====================
        @JvmStatic
        fun getMods(titleId: String): String {
            return jnaInstance.modsGetMods(titleId)
        }

        @JvmStatic
        fun setModEnabled(titleId: String, modPath: String, enabled: Boolean): Boolean {
            return jnaInstance.modsSetEnabled(titleId, modPath, enabled)
        }

        @JvmStatic
        fun deleteMod(titleId: String, modPath: String): Boolean {
            return jnaInstance.modsDeleteMod(titleId, modPath)
        }

        @JvmStatic
        fun deleteAllMods(titleId: String): Boolean {
            return jnaInstance.modsDeleteAll(titleId)
        }

        @JvmStatic
        fun enableAllMods(titleId: String): Boolean {
            return jnaInstance.modsEnableAll(titleId)
        }

        @JvmStatic
        fun disableAllMods(titleId: String): Boolean {
            return jnaInstance.modsDisableAll(titleId)
        }

        @JvmStatic
        fun addMod(titleId: String, sourcePath: String, modName: String): Boolean {
            return jnaInstance.modsAddMod(titleId, sourcePath, modName)
        }

        // ==================== 表面格式管理相关静态方法 ====================
        
        /**
         * 获取设备支持的表面格式列表
         * 返回格式：Array<String>，每个字符串格式为 "format:colorSpace:displayName"
         * 例如：["44:0:BGRA8 (SpaceSrgbNonlinearKhr)", "50:1:RGBA8 (SpacePassThroughExt)"]
         */
        @JvmStatic
        fun getAvailableSurfaceFormats(): Array<String> {
            return jnaInstance.surfaceGetAvailableFormats()
        }
        
        /**
         * 设置自定义表面格式
         * @param format Vulkan格式枚举值
         * @param colorSpace 颜色空间枚举值
         */
        @JvmStatic
        fun setCustomSurfaceFormat(format: Int, colorSpace: Int) {
            jnaInstance.surfaceSetCustomFormat(format, colorSpace)
        }
        
        /**
         * 清除自定义表面格式设置，恢复为自动选择
         */
        @JvmStatic
        fun clearCustomSurfaceFormat() {
            jnaInstance.surfaceClearCustomFormat()
        }
        
        /**
         * 检查自定义表面格式是否有效
         * @return true表示自定义格式有效且正在使用，false表示使用自动选择
         */
        @JvmStatic
        fun isCustomSurfaceFormatValid(): Boolean {
            return jnaInstance.surfaceIsCustomFormatValid()
        }
        
        /**
         * 获取当前表面格式信息
         * @return 当前使用的表面格式信息字符串
         */
        @JvmStatic
        fun getCurrentSurfaceFormatInfo(): String {
            return jnaInstance.surfaceGetCurrentFormatInfo()
        }
        
        /**
         * 游戏启动时触发表面格式保存
         * 这个方法应该在游戏成功启动后调用，延迟保存表面格式列表到文件
         */
        @JvmStatic
        fun onGameStarted() {
            jnaInstance.surfaceOnGameStarted()
        }

        // ==================== 表面管理相关静态方法 ====================
        
        @JvmStatic
        fun graphicsSetPresentEnabled(enabled: Boolean) {
            try {
                jnaInstance.graphicsSetPresentEnabled(enabled)
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun ReleaseRendererSurface() {
            try {
                jnaInstance.ReleaseRendererSurface()
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun TryReattachSurface(): Boolean {
            return try {
                jnaInstance.TryReattachSurface()
            } catch (_: Throwable) {
                false
            }
        }
        
        // ==================== 新增：暂停和恢复模拟器的静态方法 ====================
        
        @JvmStatic
        fun pauseEmulation() {
            try {
                jnaInstance.devicePauseEmulation()
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun resumeEmulation() {
            try {
                jnaInstance.deviceResumeEmulation()
            } catch (_: Throwable) {}
        }

        @JvmStatic
        fun isEmulationPaused(): Boolean {
            return try {
                jnaInstance.deviceIsEmulationPaused()
            } catch (_: Throwable) {
                false
            }
        }
    }
}

// 表面格式相关的数据类
data class SurfaceFormatInfo(
    val format: Int,
    val colorSpace: Int,
    val displayName: String
) {
    companion object {
        /**
         * 从字符串解析表面格式信息
         * 字符串格式："format:colorSpace:displayName"
         */
        fun fromString(formatString: String): SurfaceFormatInfo? {
            return try {
                val parts = formatString.split(":")
                if (parts.size >= 3) {
                    SurfaceFormatInfo(
                        format = parts[0].toInt(),
                        colorSpace = parts[1].toInt(),
                        displayName = parts[2]
                    )
                } else {
                    null
                }
            } catch (e: Exception) {
                null
            }
        }
    }
    
    override fun toString(): String {
        return displayName
    }
}

// 常用的Vulkan格式常量（可以根据需要扩展）
object VulkanFormats {
    // 常用格式
    const val FORMAT_B8G8R8A8_UNORM = 44  // VK_FORMAT_B8G8R8A8_UNORM
    const val FORMAT_R8G8B8A8_UNORM = 37  // VK_FORMAT_R8G8B8A8_UNORM
    const val FORMAT_R8G8B8A8_SRGB = 43   // VK_FORMAT_R8G8B8A8_SRGB
    const val FORMAT_B8G8R8A8_SRGB = 50   // VK_FORMAT_B8G8R8A8_SRGB
    
    // 常用颜色空间
    const val COLOR_SPACE_SRGB_NONLINEAR = 0      // VK_COLOR_SPACE_SRGB_NONLINEAR_KHR
    const val COLOR_SPACE_PASSTHROUGH_EXT = 1000021004  // VK_COLOR_SPACE_PASS_THROUGH_EXT
    const val COLOR_SPACE_DISPLAY_P3_NONLINEAR = 1000104001  // VK_COLOR_SPACE_DISPLAY_P3_NONLINEAR_EXT
    
    // 获取格式的友好名称
    fun getFormatName(format: Int): String {
        return when (format) {
            FORMAT_B8G8R8A8_UNORM -> "BGRA8"
            FORMAT_R8G8B8A8_UNORM -> "RGBA8"
            FORMAT_R8G8B8A8_SRGB -> "RGBA8 SRGB"
            FORMAT_B8G8R8A8_SRGB -> "BGRA8 SRGB"
            else -> "Format $format"
        }
    }
    
    // 获取颜色空间的友好名称
    fun getColorSpaceName(colorSpace: Int): String {
        return when (colorSpace) {
            COLOR_SPACE_SRGB_NONLINEAR -> "sRGB"
            COLOR_SPACE_PASSTHROUGH_EXT -> "PassThrough"
            COLOR_SPACE_DISPLAY_P3_NONLINEAR -> "Display P3"
            else -> "ColorSpace $colorSpace"
        }
    }
}
