package org.ryujinx.android.viewmodels

import android.annotation.SuppressLint
import androidx.compose.runtime.MutableState
import androidx.navigation.NavHostController
import com.anggrayudi.storage.extension.launchOnUiThread
import kotlinx.coroutines.runBlocking
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
import org.ryujinx.android.views.PlayerSetting
import java.io.File

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

    var gameHost: GameHost? = null
        set(value) {
            field = value
            field?.setProgressStates(showLoading, progressValue, progress)
        }
    var navController: NavHostController? = null

    var homeViewModel: HomeViewModel = HomeViewModel(activity, this)

    // 玩家设置列表（使用 0-based 索引，0-7为普通玩家，8为掌机模式）
    var playerSettings: MutableList<PlayerSetting> = mutableListOf()

    init {
        performanceManager = PerformanceManager(activity)
        loadPlayerSettings()
    }

    // 加载玩家设置（包括掌机模式）
    private fun loadPlayerSettings() {
        // 初始化默认玩家设置（使用 0-based 索引）
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
        
        // 尝试从设置中加载保存的玩家设置（包括掌机模式）
        try {
            val quickSettings = QuickSettings(activity)
            
            // 加载普通玩家设置 (0-7)
            playerSettings = quickSettings.playerSettings.toMutableList()
            
            // 添加掌机模式设置 (索引8) - 使用getAllPlayerSettings方法
            val allSettings = quickSettings.getAllPlayerSettings()
            val handheldSetting = allSettings.find { it.playerIndex == 8 }
            if (handheldSetting != null) {
                playerSettings.add(handheldSetting)
            }
        } catch (e: Exception) {
            // 如果加载失败，保持默认设置
            e.printStackTrace()
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
            playerSettings[index] = playerSetting
            
            // 如果是已连接的玩家，立即应用设置
            if (playerSetting.isConnected) {
                // 将控制器类型索引转换为位掩码值
                val controllerTypeBitmask = controllerTypeIndexToBitmask(playerSetting.controllerType)
                
                // 对于掌机模式，使用玩家索引8，其他使用正常索引
                val targetPlayerIndex = if (playerSetting.playerIndex == 8) {
                    8 // 掌机模式使用索引8
                } else {
                    playerSetting.playerIndex // 其他玩家使用正常索引
                }
                
                RyujinxNative.jnaInstance.setControllerType(targetPlayerIndex, controllerTypeBitmask)
                
                // 如果是玩家1（索引0）或掌机模式（索引8），更新GameController的布局
                if (playerSetting.playerIndex == 0 || playerSetting.playerIndex == 8) {
                    controller?.updateControllerTypeFromSettings()
                }
            }
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

        val semaphore = Semaphore(1, 0)
        runBlocking {
            semaphore.acquire()
            launchOnUiThread {
                // We are only able to initialize the emulation context on the main thread
                success = RyujinxNative.jnaInstance.deviceInitialize(
                    settings.isHostMapped,
                    settings.useNce,
                    settings.systemLanguage,
                    settings.regionCode,
                    settings.enableVsync,
                    settings.enableDocked,
                    settings.enablePtc,
                    settings.enableJitCacheEviction,
                    false,
                    "UTC",
                    settings.ignoreMissingServices,
                    settings.audioEngineType, // 新增音频引擎参数
                    settings.memoryConfiguration //内存配置
                )

                // 为所有连接的玩家设置控制器类型 - 使用 0-based 玩家索引 (0-8)
                playerSettings.forEach { playerSetting ->
                    if (playerSetting.isConnected) {
                        // 将控制器类型索引转换为位掩码值
                        val controllerTypeBitmask = controllerTypeIndexToBitmask(playerSetting.controllerType)
                        
                        // 对于掌机模式，使用玩家索引8，其他使用正常索引
                        val targetPlayerIndex = if (playerSetting.playerIndex == 8) {
                            8 // 掌机模式使用索引8
                        } else {
                            playerSetting.playerIndex // 其他玩家使用正常索引
                        }
                        
                        RyujinxNative.jnaInstance.setControllerType(targetPlayerIndex, controllerTypeBitmask)
                    }
                }

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

        val semaphore = Semaphore(1, 0)
        runBlocking {
            semaphore.acquire()
            launchOnUiThread {
                // We are only able to initialize the emulation context on the main thread
                success = RyujinxNative.jnaInstance.deviceInitialize(
                    settings.isHostMapped,
                    settings.useNce,
                    settings.systemLanguage,
                    settings.regionCode,
                    settings.enableVsync,
                    settings.enableDocked,
                    settings.enablePtc,
                    settings.enableJitCacheEviction,
                    false,
                    "UTC",
                    settings.ignoreMissingServices,
                    settings.audioEngineType, // 新增音频引擎参数
                    settings.memoryConfiguration //内存配置
                )

                // 为所有连接的玩家设置控制器类型 - 使用 0-based 玩家索引 (0-8)
                playerSettings.forEach { playerSetting ->
                    if (playerSetting.isConnected) {
                        // 将控制器类型索引转换为位掩码值
                        val controllerTypeBitmask = controllerTypeIndexToBitmask(playerSetting.controllerType)
                        
                        // 对于掌机模式，使用玩家索引8，其他使用正常索引
                        val targetPlayerIndex = if (playerSetting.playerIndex == 8) {
                            8 // 掌机模式使用索引8
                        } else {
                            playerSetting.playerIndex // 其他玩家使用正常索引
                        }
                        
                        RyujinxNative.jnaInstance.setControllerType(targetPlayerIndex, controllerTypeBitmask)
                    }
                }

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
                android.util.Log.d("MainViewModel", "Connected player index: $playerIndex")
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
                android.util.Log.d("MainViewModel", "Disconnected player index: $playerIndex")
            }
        }
    }
    
    // 新增方法：连接掌机模式
    fun connectHandheld() {
        val handheldSetting = getPlayerSetting(8)
        if (handheldSetting != null && !handheldSetting.isConnected) {
            handheldSetting.isConnected = true
            updatePlayerSetting(handheldSetting)
            android.util.Log.d("MainViewModel", "Connected handheld mode")
        }
    }
    
    // 新增方法：断开掌机模式连接
    fun disconnectHandheld() {
        val handheldSetting = getPlayerSetting(8)
        if (handheldSetting != null && handheldSetting.isConnected) {
            handheldSetting.isConnected = false
            updatePlayerSetting(handheldSetting)
            android.util.Log.d("MainViewModel", "Disconnected handheld mode")
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
    
    // 修改方法：保存玩家设置到QuickSettings - 使用公共方法
    fun savePlayerSettingsToQuickSettings() {
        try {
            val quickSettings = QuickSettings(activity)
            
            // 保存普通玩家设置 (0-7)
            val regularPlayers = playerSettings.filter { it.playerIndex in 0..7 }
            quickSettings.playerSettings = regularPlayers.toMutableList()
            
            // 保存掌机模式设置 (索引8) - 使用updateHandheldSetting方法
            val handheldSetting = playerSettings.find { it.playerIndex == 8 }
            if (handheldSetting != null) {
                quickSettings.updateHandheldSetting(handheldSetting)
            }
            
            quickSettings.save()
        } catch (e: Exception) {
            android.util.Log.e("MainViewModel", "Failed to save player settings to QuickSettings", e)
        }
    }
    
    // 修改方法：从QuickSettings加载玩家设置 - 使用公共方法
    fun loadPlayerSettingsFromQuickSettings() {
        try {
            val quickSettings = QuickSettings(activity)
            
            // 清空当前设置
            playerSettings.clear()
            
            // 加载普通玩家设置 (0-7)
            playerSettings.addAll(quickSettings.playerSettings)
            
            // 加载掌机模式设置 (索引8) - 使用getAllPlayerSettings方法
            val allSettings = quickSettings.getAllPlayerSettings()
            val handheldSetting = allSettings.find { it.playerIndex == 8 }
            if (handheldSetting != null) {
                playerSettings.add(handheldSetting)
            }
        } catch (e: Exception) {
            android.util.Log.e("MainViewModel", "Failed to load player settings from QuickSettings", e)
        }
    }
    
    // 修改方法：获取掌机模式设置 - 使用本地playerSettings
    fun getHandheldSetting(): PlayerSetting? {
        return getPlayerSetting(8)
    }
    
    // 修改方法：更新掌机模式设置 - 使用本地playerSettings
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
}
