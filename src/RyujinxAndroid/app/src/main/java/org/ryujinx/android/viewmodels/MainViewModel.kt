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

    // 新增：表面格式相关字段
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
     * 初始加载表面格式（延迟执行）
     */
    private fun loadInitialSurfaceFormats() {
        CoroutineScope(Dispatchers.IO).launch {
            // 延迟25秒确保图形初始化完成
            delay(25000)
            refreshSurfaceFormats()
        }
    }

    /**
     * 刷新表面格式列表（强制从Native获取最新）
     */
    fun refreshSurfaceFormats(): Array<String> {
        return try {
            android.util.Log.i("Ryujinx", "Refreshing surface formats from native...")
            val formats = RyujinxNative.getAvailableSurfaceFormats()
            android.util.Log.i("Ryujinx", "Successfully refreshed ${formats.size} surface formats from native")
            
            synchronized(surfaceFormatsLock) {
                surfaceFormatsCache = formats
                lastSurfaceFormatsUpdate = System.currentTimeMillis()
                // 同时保存到SharedPreferences作为备份
                saveSurfaceFormatsToPreferences(formats)
            }
            formats
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to refresh surface formats: ${e.message}")
            // 失败时尝试从缓存或SharedPreferences加载
            loadSurfaceFormatsFromCacheOrPreferences()
        }
    }

    /**
     * 获取表面格式列表（优先使用缓存）
     */
    fun getSurfaceFormats(): Array<String> {
        synchronized(surfaceFormatsLock) {
            // 如果缓存为空或超过30秒没有更新，则刷新
            if (surfaceFormatsCache == null || 
                System.currentTimeMillis() - lastSurfaceFormatsUpdate > 30000) {
                android.util.Log.i("Ryujinx", "Surface formats cache is stale, refreshing...")
                return refreshSurfaceFormats()
            }
            
            android.util.Log.i("Ryujinx", "Using cached surface formats: ${surfaceFormatsCache!!.size} formats")
            return surfaceFormatsCache!!
        }
    }

    /**
     * 从缓存或SharedPreferences加载表面格式
     */
    private fun loadSurfaceFormatsFromCacheOrPreferences(): Array<String> {
        synchronized(surfaceFormatsLock) {
            // 首先尝试缓存
            surfaceFormatsCache?.let {
                android.util.Log.i("Ryujinx", "Using memory cache for surface formats")
                return it
            }

            // 然后尝试SharedPreferences
            val prefsFormats = loadSurfaceFormatsFromPreferences()
            if (prefsFormats.isNotEmpty()) {
                android.util.Log.i("Ryujinx", "Using SharedPreferences cache for surface formats: ${prefsFormats.size} formats")
                surfaceFormatsCache = prefsFormats
                lastSurfaceFormatsUpdate = System.currentTimeMillis()
                return prefsFormats
            }

            // 最后返回空数组
            android.util.Log.w("Ryujinx", "No surface formats available in cache or preferences")
            return emptyArray()
        }
    }

    /**
     * 保存表面格式列表到 SharedPreferences
     */
    private fun saveSurfaceFormatsToPreferences(formats: Array<String>) {
        try {
            val prefs = activity.getSharedPreferences("RyujinxSettings", Context.MODE_PRIVATE)
            val editor = prefs.edit()
            
            // 将格式列表转换为 JSON 字符串保存
            val formatList = formats.toList()
            val gson = Gson()
            val jsonFormats = gson.toJson(formatList)
            
            editor.putString("surface_formats", jsonFormats)
            editor.putLong("surface_formats_timestamp", System.currentTimeMillis())
            editor.apply()
            
            android.util.Log.i("Ryujinx", "Saved ${formats.size} surface formats to preferences")
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to save surface formats: ${e.message}")
        }
    }

    /**
     * 从 SharedPreferences 加载表面格式列表
     */
    private fun loadSurfaceFormatsFromPreferences(): Array<String> {
        return try {
            val prefs = activity.getSharedPreferences("RyujinxSettings", Context.MODE_PRIVATE)
            val timestamp = prefs.getLong("surface_formats_timestamp", 0)
            
            // 检查缓存是否过期（1小时）
            if (System.currentTimeMillis() - timestamp > 3600000) {
                android.util.Log.i("Ryujinx", "Surface formats cache expired")
                return emptyArray()
            }
            
            val jsonFormats = prefs.getString("surface_formats", null)
            
            if (jsonFormats != null) {
                val gson = Gson()
                val type = object : TypeToken<List<String>>() {}.type
                val formatList: List<String> = gson.fromJson(jsonFormats, type)
                android.util.Log.i("Ryujinx", "Loaded ${formatList.size} surface formats from preferences")
                formatList.toTypedArray()
            } else {
                emptyArray()
            }
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Failed to load surface formats: ${e.message}")
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

        // 新增：在图形初始化后延迟获取表面格式
        CoroutineScope(Dispatchers.IO).launch {
            delay(20000) // 延迟20秒确保交换链创建完成
            refreshSurfaceFormats()
        }

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

        // 新增：在图形初始化后延迟获取表面格式
        CoroutineScope(Dispatchers.IO).launch {
            delay(20000) // 延迟20秒确保交换链创建完成
            refreshSurfaceFormats()
        }

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
