package org.ryujinx.android.viewmodels

import android.annotation.SuppressLint
import android.content.Context
import android.content.SharedPreferences
import androidx.compose.runtime.MutableState
import androidx.navigation.NavHostController
import com.anggrayudi.storage.extension.launchOnUiThread
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Semaphore
import org.ryujinx.android.GameController
import org.ryujinx.android.GameHost
import org.ryujinx.android.Logging
import org.ryujinx.android.MainActivity
import org.ryujinx.android.MotionSensorManager
import org.ryujinx.android.NativeGraphicsInterop
import org.ryujinx.android.NativeHelpers
import org.ryujinx.android.PerformanceManager
import org.ryujinx.android.PhysicalControllerManager
import org.ryujinx.android.RegionCode
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.SystemLanguage
import java.io.File
import java.util.TimeZone

@SuppressLint("WrongConstant")
class MainViewModel(val activity: MainActivity) {
    var physicalControllerManager: PhysicalControllerManager? = null
    var motionSensorManager: MotionSensorManager? = null
    var gameModel: GameModel? = null
    var controller: GameController? = null
    var performanceManager: PerformanceManager? = null
    var selected: GameModel? = null
    var isMiiEditorLaunched = false
    val userViewModel = UserViewModel()
    val logging = Logging(this)
    var firmwareVersion = ""
    var fifoState: MutableState<Double>? = null
    var gameFpsState: MutableState<Double>? = null
    var gameTimeState: MutableState<Double>? = null
    var usedMemState: MutableState<Int>? = null
    var totalMemState: MutableState<Int>? = null
    var batteryTemperatureState: MutableState<Double>? = null // 电池温度状态
    var batteryLevelState: MutableState<Int>? = null // 电池电量状态
    var isChargingState: MutableState<Boolean>? = null // 充电状态
    private var frequenciesState: MutableList<Double>? = null
    private var progress: MutableState<String>? = null
    private var progressValue: MutableState<Float>? = null
    private var showLoading: MutableState<Boolean>? = null
    private var refreshUser: MutableState<Boolean>? = null

    // 修改：表面格式相关字段 - 使用文件缓存
    private var surfaceFormatsCache: Array<String>? = null
    private var lastSurfaceFormatsUpdate: Long = 0
    private val surfaceFormatsLock = Any()

    var gameHost: GameHost? = null
        set(value) {
            field = value
            field?.setProgressStates(showLoading, progressValue, progress)
        }
    var navController: NavHostController? = null

    var homeViewModel: HomeViewModel = HomeViewModel(activity, this)

    init {
        performanceManager = PerformanceManager(activity)
        // 启动时预加载表面格式
        loadInitialSurfaceFormats()
    }

    /**
     * 初始加载表面格式（从文件缓存）
     */
    private fun loadInitialSurfaceFormats() {
        CoroutineScope(Dispatchers.IO).launch {
            // 延迟5秒确保应用初始化完成
            delay(5000)
            refreshSurfaceFormats()
        }
    }

    /**
     * 刷新表面格式列表（从文件缓存读取）
     */
    fun refreshSurfaceFormats(): Array<String> {
        return try {
            android.util.Log.i("Ryujinx", "Refreshing surface formats from file cache...")
            val formats = loadSurfaceFormatsFromFile()
            android.util.Log.i("Ryujinx", "Successfully refreshed ${formats.size} surface formats from file cache")
            
            synchronized(surfaceFormatsLock) {
                surfaceFormatsCache = formats
                lastSurfaceFormatsUpdate = System.currentTimeMillis()
            }
            formats
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to refresh surface formats from file: ${e.message}")
            // 失败时返回空数组
            emptyArray()
        }
    }

    /**
     * 获取表面格式列表（优先使用缓存）
     */
    fun getSurfaceFormats(): Array<String> {
        synchronized(surfaceFormatsLock) {
            // 如果缓存为空，则从文件加载
            if (surfaceFormatsCache == null) {
                android.util.Log.i("Ryujinx", "Surface formats cache is empty, loading from file...")
                return refreshSurfaceFormats()
            }
            
            android.util.Log.i("Ryujinx", "Using cached surface formats: ${surfaceFormatsCache!!.size} formats")
            return surfaceFormatsCache!!
        }
    }

    /**
     * 从文件加载表面格式列表
     */
    private fun loadSurfaceFormatsFromFile(): Array<String> {
        return try {
            val surfaceFormatsFile = File(activity.filesDir, "surface_formats.txt")
            
            if (surfaceFormatsFile.exists()) {
                val formats = surfaceFormatsFile.readLines().toTypedArray()
                android.util.Log.i("Ryujinx", "Loaded ${formats.size} surface formats from file")
                formats
            } else {
                android.util.Log.w("Ryujinx", "Surface formats file does not exist")
                emptyArray()
            }
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to load surface formats from file: ${e.message}")
            emptyArray()
        }
    }

    /**
     * 设置自定义表面格式
     */
    fun setCustomSurfaceFormat(format: Int, colorSpace: Int) {
        try {
            RyujinxNative.setCustomSurfaceFormat(format, colorSpace)
            android.util.Log.i("Ryujinx", "Custom surface format set: format=$format, colorSpace=$colorSpace")
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to set custom surface format: ${e.message}")
        }
    }

    /**
     * 清除自定义表面格式
     */
    fun clearCustomSurfaceFormat() {
        try {
            RyujinxNative.clearCustomSurfaceFormat()
            android.util.Log.i("Ryujinx", "Custom surface format cleared")
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to clear custom surface format: ${e.message}")
        }
    }

    /**
     * 检查自定义表面格式是否有效
     */
    fun isCustomSurfaceFormatValid(): Boolean {
        return try {
            RyujinxNative.isCustomSurfaceFormatValid()
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to check custom surface format: ${e.message}")
            false
        }
    }

    /**
     * 获取当前表面格式信息
     */
    fun getCurrentSurfaceFormatInfo(): String {
        return try {
            RyujinxNative.getCurrentSurfaceFormatInfo()
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to get current surface format: ${e.message}")
            "Unknown"
        }
    }

    fun closeGame() {
        RyujinxNative.jnaInstance.deviceSignalEmulationClose()
        gameHost?.close()
        RyujinxNative.jnaInstance.deviceCloseEmulation()
        motionSensorManager?.unregister()
        physicalControllerManager?.disconnect()
        motionSensorManager?.setControllerId(-1)
    }

    fun refreshFirmwareVersion() {
        firmwareVersion = RyujinxNative.jnaInstance.deviceGetInstalledFirmwareVersion()
    }

    fun loadGame(game: GameModel): Int {
        val descriptor = game.open()

        if (descriptor == 0)
            return 0

        val update = game.openUpdate()

        if(update == -2)
        {
            return -2
        }

        gameModel = game
        isMiiEditorLaunched = false

        val settings = QuickSettings(activity)

        var success = RyujinxNative.jnaInstance.graphicsInitialize(
            enableShaderCache = settings.enableShaderCache,
            enableTextureRecompression = settings.enableTextureRecompression,
            rescale = settings.resScale,
            backendThreading = org.ryujinx.android.BackendThreading.Auto.ordinal
        )

        if (!success)
            return 0

        val nativeHelpers = NativeHelpers.instance
        val nativeInterop = NativeGraphicsInterop()
        nativeInterop.VkRequiredExtensions = arrayOf(
            "VK_KHR_surface", "VK_KHR_android_surface"
        )
        nativeInterop.VkCreateSurface = nativeHelpers.getCreateSurfacePtr()
        nativeInterop.SurfaceHandle = 0

        val driverViewModel = VulkanDriverViewModel(activity)
        val drivers = driverViewModel.getAvailableDrivers()

        var driverHandle = 0L

        if (driverViewModel.selected.isNotEmpty()) {
            val metaData = drivers.find { it.driverPath == driverViewModel.selected }

            metaData?.apply {
                val privatePath = activity.filesDir
                val privateDriverPath = privatePath.canonicalPath + "/driver/"
                val pD = File(privateDriverPath)
                if (pD.exists())
                    pD.deleteRecursively()

                pD.mkdirs()

                val driver = File(driverViewModel.selected)
                val parent = driver.parentFile
                if (parent != null) {
                    for (file in parent.walkTopDown()) {
                        if (file.absolutePath == parent.absolutePath)
                            continue
                        file.copyTo(File(privateDriverPath + file.name), true)
                    }
                }

                driverHandle = NativeHelpers.instance.loadDriver(
                    activity.applicationInfo.nativeLibraryDir!! + "/",
                    privateDriverPath,
                    this.libraryName
                )
            }

        }

        val extensions = nativeInterop.VkRequiredExtensions

        success = RyujinxNative.jnaInstance.graphicsInitializeRenderer(
            extensions!!,
            extensions.size,
            driverHandle
        )
        if (!success)
            return 0

        // 修改：不再需要延迟获取表面格式，因为现在使用文件缓存
        // 表面格式会在LibRyujinx.cs中自动保存到文件

        val semaphore = Semaphore(1, 0)
        runBlocking {
            semaphore.acquire()
            launchOnUiThread {
                // We are only able to initialize the emulation context on the main thread
                val tzId = TimeZone.getDefault().id
                success = RyujinxNative.jnaInstance.deviceInitialize(
                    settings.memoryManagerMode,  // 使用MemoryManagerMode参数
                    settings.useNce,
                    settings.systemLanguage,
                    settings.regionCode,
                    settings.enableVsync,
                    settings.enableDocked,
                    settings.enablePtc,
                    settings.enableJitCacheEviction,
                    false,
                    tzId, // <<< Pass through Android device time zone
                    settings.ignoreMissingServices,
                    settings.audioEngineType, // 新增音频引擎参数
                    settings.memoryConfiguration, // 内存配置
                    settings.systemTimeOffset // 新增系统时间偏移参数
                )

                semaphore.release()
            }
            semaphore.acquire()
            semaphore.release()
        }

        if (!success)
            return 0

        success =
            RyujinxNative.jnaInstance.deviceLoadDescriptor(descriptor, game.type.ordinal, update)

        return if (success) 1 else 0
    }

    fun loadMiiEditor(): Boolean {
        gameModel = null
        isMiiEditorLaunched = true

        val settings = QuickSettings(activity)

        var success = RyujinxNative.jnaInstance.graphicsInitialize(
            enableShaderCache = settings.enableShaderCache,
            enableTextureRecompression = settings.enableTextureRecompression,
            rescale = settings.resScale,
            backendThreading = org.ryujinx.android.BackendThreading.Auto.ordinal
        )

        if (!success)
            return false

        val nativeHelpers = NativeHelpers.instance
        val nativeInterop = NativeGraphicsInterop()
        nativeInterop.VkRequiredExtensions = arrayOf(
            "VK_KHR_surface", "VK_KHR_android_surface"
        )
        nativeInterop.VkCreateSurface = nativeHelpers.getCreateSurfacePtr()
        nativeInterop.SurfaceHandle = 0

        val driverViewModel = VulkanDriverViewModel(activity)
        val drivers = driverViewModel.getAvailableDrivers()

        var driverHandle = 0L

        if (driverViewModel.selected.isNotEmpty()) {
            val metaData = drivers.find { it.driverPath == driverViewModel.selected }

            metaData?.apply {
                val privatePath = activity.filesDir
                val privateDriverPath = privatePath.canonicalPath + "/driver/"
                val pD = File(privateDriverPath)
                if (pD.exists())
                    pD.deleteRecursively()

                pD.mkdirs()

                val driver = File(driverViewModel.selected)
                val parent = driver.parentFile
                if (parent != null) {
                    for (file in parent.walkTopDown()) {
                        if (file.absolutePath == parent.absolutePath)
                            continue
                        file.copyTo(File(privateDriverPath + file.name), true)
                    }
                }

                driverHandle = NativeHelpers.instance.loadDriver(
                    activity.applicationInfo.nativeLibraryDir!! + "/",
                    privateDriverPath,
                    this.libraryName
                )
            }

        }

        val extensions = nativeInterop.VkRequiredExtensions

        success = RyujinxNative.jnaInstance.graphicsInitializeRenderer(
            extensions!!,
            extensions.size,
            driverHandle
        )
        if (!success)
            return false

        // 修改：不再需要延迟获取表面格式，因为现在使用文件缓存

        val semaphore = Semaphore(1, 0)
        runBlocking {
            semaphore.acquire()
            launchOnUiThread {
                // We are only able to initialize the emulation context on the main thread
                val tzId = TimeZone.getDefault().id
                success = RyujinxNative.jnaInstance.deviceInitialize(
                    settings.memoryManagerMode,  // 使用MemoryManagerMode参数
                    settings.useNce,
                    settings.systemLanguage,
                    settings.regionCode,
                    settings.enableVsync,
                    settings.enableDocked,
                    settings.enablePtc,
                    settings.enableJitCacheEviction,
                    false,
                    tzId, // <<< Pass through Android device time zone
                    settings.ignoreMissingServices,
                    settings.audioEngineType, // 新增音频引擎参数
                    settings.memoryConfiguration, // 内存配置
                    settings.systemTimeOffset // 新增系统时间偏移参数
                )

                semaphore.release()
            }
            semaphore.acquire()
            semaphore.release()
        }

        if (!success)
            return false

        success = RyujinxNative.jnaInstance.deviceLaunchMiiEditor()

        return success
    }

    fun clearPptcCache(titleId: String) {
        if (titleId.isNotEmpty()) {
            val basePath = MainActivity.AppPath + "/games/$titleId/cache/cpu"
            if (File(basePath).exists()) {
                var caches = mutableListOf<String>()

                val mainCache = basePath + "${File.separator}0"
                File(mainCache).listFiles()?.forEach {
                    if (it.isFile && it.name.endsWith(".cache"))
                        caches.add(it.absolutePath)
                }
                val backupCache = basePath + "${File.separator}1"
                File(backupCache).listFiles()?.forEach {
                    if (it.isFile && it.name.endsWith(".cache"))
                        caches.add(it.absolutePath)
                }
                for (path in caches)
                    File(path).delete()
            }
        }
    }

    fun purgeShaderCache(titleId: String) {
        if (titleId.isNotEmpty()) {
            val basePath = MainActivity.AppPath + "/games/$titleId/cache/shader"
            if (File(basePath).exists()) {
                var caches = mutableListOf<String>()
                File(basePath).listFiles()?.forEach {
                    if (!it.isFile)
                        it.delete()
                    else {
                        if (it.name.endsWith(".toc") || it.name.endsWith(".data"))
                            caches.add(it.absolutePath)
                    }
                }
                for (path in caches)
                    File(path).delete()
            }
        }
    }

    fun deleteCache(titleId: String) {
        fun deleteDirectory(directory: File) {
            if (directory.exists() && directory.isDirectory) {
                directory.listFiles()?.forEach { file ->
                    if (file.isDirectory) {
                        deleteDirectory(file)
                    } else {
                        file.delete()
                    }
                }
                directory.delete()
            }
        }
        if (titleId.isNotEmpty()) {
            val basePath = MainActivity.AppPath + "/games/$titleId/cache"
            if (File(basePath).exists()) {
                deleteDirectory(File(basePath))
            }
        }
    }

    fun setStatStates(
        fifo: MutableState<Double>,
        gameFps: MutableState<Double>,
        gameTime: MutableState<Double>,
        usedMem: MutableState<Int>,
        totalMem: MutableState<Int>,
        batteryTemperature: MutableState<Double>, // 电池温度
        batteryLevel: MutableState<Int>, // 电池电量
        isCharging: MutableState<Boolean> // 充电状态
    ) {
        fifoState = fifo
        gameFpsState = gameFps
        gameTimeState = gameTime
        usedMemState = usedMem
        totalMemState = totalMem
        batteryTemperatureState = batteryTemperature
        batteryLevelState = batteryLevel
        isChargingState = isCharging
    }

    fun updateStats(
        fifo: Double,
        gameFps: Double,
        gameTime: Double
    ) {
        fifoState?.apply {
            this.value = fifo
        }
        gameFpsState?.apply {
            this.value = gameFps
        }
        gameTimeState?.apply {
            this.value = gameTime
        }
        usedMemState?.let { usedMem ->
            totalMemState?.let { totalMem ->
                MainActivity.performanceMonitor.getMemoryUsage(
                    usedMem,
                    totalMem
                )
            }
        }
        
        // 更新电池温度
        batteryTemperatureState?.apply {
            this.value = MainActivity.performanceMonitor.getBatteryTemperature()
        }
        
        // 更新电池电量
        batteryLevelState?.apply {
            this.value = MainActivity.performanceMonitor.getBatteryLevel()
        }
        
        // 更新充电状态
        isChargingState?.apply {
            this.value = MainActivity.performanceMonitor.isCharging()
        }
    }

    fun setGameController(controller: GameController) {
        this.controller = controller
    }

    fun navigateToGame() {
        activity.setFullScreen(true)
        navController?.navigate("game")
        activity.isGameRunning = true
        if (QuickSettings(activity).enableMotion)
            motionSensorManager?.register()
    }

    fun setProgressStates(
        showLoading: MutableState<Boolean>,
        progressValue: MutableState<Float>,
        progress: MutableState<String>
    ) {
        this.showLoading = showLoading
        this.progressValue = progressValue
        this.progress = progress
        gameHost?.setProgressStates(showLoading, progressValue, progress)
    }
}
