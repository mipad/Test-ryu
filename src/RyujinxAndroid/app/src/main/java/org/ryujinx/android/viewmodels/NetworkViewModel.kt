package org.ryujinx.android.viewmodels

import android.content.SharedPreferences
import kotlinx.coroutines.withContext
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.preference.PreferenceManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RyujinxNative
import java.net.InetAddress
import java.net.NetworkInterface
import java.util.Collections

class NetworkViewModel(activity: MainActivity) : ViewModel() {
    private var sharedPref: SharedPreferences = PreferenceManager.getDefaultSharedPreferences(activity)
    private val context = activity.applicationContext
    private val coroutineScope = CoroutineScope(Dispatchers.Main)
    private var lobbyRefreshJob: Job? = null

    // 使用 mutableStateOf 确保UI自动更新
    private val _networkInterfaces = mutableStateOf<List<NetworkInterfaceInfo>>(emptyList())
    val networkInterfaceList: List<NetworkInterfaceInfo> get() = _networkInterfaces.value

    // Multiplayer mode - using mutableStateOf
    var multiplayerModeIndex = mutableStateOf(sharedPref.getInt("multiplayerModeIndex", 0))
        private set
    
    // Enable internet access - using mutableStateOf
    var enableInternetAccess = mutableStateOf(sharedPref.getBoolean("enableInternetAccess", false))
        private set
    
    // Network interface index - using mutableStateOf
    var networkInterfaceIndex = mutableStateOf(sharedPref.getInt("networkInterfaceIndex", 0))
        private set

    // 大厅管理相关状态
    var lobbyList = mutableStateOf<List<LobbyInfo>>(emptyList())
        private set
    
    var currentLobby = mutableStateOf<LobbyInfo?>(null)
        private set
    
    var lobbyState = mutableStateOf(LobbyState.IDLE)
        private set
    
    var isScanningLobbies = mutableStateOf(false)
        private set
    
    var isHostingLobby = mutableStateOf(false)
        private set
    
    var showCreateLobbyDialog = mutableStateOf(false)
        private set

    // 新增状态变量
    var showGameSelectionDialog = mutableStateOf(false)
        private set
    
    var showRoomStatusDialog = mutableStateOf(false)
        private set

    // 添加游戏列表
    var gameList = mutableStateOf<List<GameModel>>(emptyList())
        private set

    init {
        loadNetworkInterfaces()
        // 初始化网络通信
        initializeNetwork()
        // 移除自动刷新大厅列表，改为手动刷新
        // startAutoLobbyRefresh()
    }

    /**
     * 设置游戏列表（从HomeViewModel传入）
     */
    fun setGameList(games: List<GameModel>) {
        gameList.value = games
    }

    /**
     * Load available network interfaces
     */
    private fun loadNetworkInterfaces() {
        val interfacesList = mutableListOf<NetworkInterfaceInfo>()
        
        // Add default option
        interfacesList.add(NetworkInterfaceInfo("Default", "0", "Automatically select the best network interface"))
        
        try {
            val interfaces = Collections.list(NetworkInterface.getNetworkInterfaces())
            for (networkInterface in interfaces) {
                if (networkInterface.isUp && !networkInterface.isLoopback) {
                    val displayName = networkInterface.displayName ?: networkInterface.name
                    val interfaceInfo = NetworkInterfaceInfo(
                        name = displayName,
                        id = networkInterface.name,
                        description = buildInterfaceDescription(networkInterface)
                    )
                    interfacesList.add(interfaceInfo)
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
            // If enumerating network interfaces fails, ensure at least the default option exists
            if (interfacesList.size == 1) {
                interfacesList.add(NetworkInterfaceInfo("Fallback", "eth0", "Fallback network interface"))
            }
        }
        
        _networkInterfaces.value = interfacesList
    }

    /**
     * Build network interface description
     */
    private fun buildInterfaceDescription(networkInterface: NetworkInterface): String {
        val sb = StringBuilder()
        
        // Add interface type information
        sb.append("${getInterfaceType(networkInterface)}")
        
        // Add MTU information
        sb.append(" • MTU: ${networkInterface.mtu}")
        
        // Add interface status
        val status = when {
            networkInterface.isUp -> "Up"
            else -> "Down"
        }
        sb.append(" • $status")
        
        return sb.toString()
    }

    /**
     * Get network interface type
     */
    private fun getInterfaceType(networkInterface: NetworkInterface): String {
        return when {
            networkInterface.isLoopback -> "Loopback"
            networkInterface.isPointToPoint -> "PPP"
            networkInterface.isVirtual -> "Virtual"
            networkInterface.name.startsWith("wlan") -> "WiFi"
            networkInterface.name.startsWith("wlp") -> "WiFi" 
            networkInterface.name.startsWith("p2p") -> "WiFi Direct"
            networkInterface.name.startsWith("ap") -> "WiFi Hotspot"
            networkInterface.name.endsWith("-mon") -> "WiFi Monitor"
            networkInterface.name.startsWith("eth") || networkInterface.name.startsWith("enp") -> "Ethernet"
            networkInterface.name.startsWith("rmnet") || networkInterface.name.startsWith("pdp") -> "Mobile"
            networkInterface.name.startsWith("ccmni") -> "Mobile"
            networkInterface.name.startsWith("tun") || networkInterface.name.startsWith("tap") -> "VPN"
            networkInterface.name.startsWith("dummy") -> "Virtual"
            else -> "Network"
        }
    }

    /**
     * Set multiplayer mode
     */
    fun setMultiplayerMode(index: Int) {
        multiplayerModeIndex.value = index
        sharedPref.edit().putInt("multiplayerModeIndex", index).apply()
        
        // 如果切换到禁用模式，离开当前大厅
        if (index == 0) {
            leaveLobby()
        }
    }

    /**
     * Set enable internet access
     */
    fun setEnableInternetAccess(enabled: Boolean) {
        enableInternetAccess.value = enabled
        sharedPref.edit().putBoolean("enableInternetAccess", enabled).apply()
    }

    /**
     * Set network interface index
     */
    fun setNetworkInterfaceIndex(index: Int) {
        networkInterfaceIndex.value = index
        sharedPref.edit().putInt("networkInterfaceIndex", index).apply()
        
        // 设置网络接口到原生层
        val interfaceId = getSelectedInterfaceId()
        RyujinxNative.setLanInterface(interfaceId)
        
        // 重新初始化网络以应用新的接口设置
        reinitializeNetwork()
    }

    /**
     * 重新初始化网络
     */
    private fun reinitializeNetwork() {
        coroutineScope.launch(Dispatchers.IO) {
            try {
                // 先停止网络
                RyujinxNative.stopNetwork()
                delay(500) // 等待网络完全停止
                // 重新初始化网络
                RyujinxNative.initializeNetwork()
                println("DEBUG: Network reinitialized with interface: ${getSelectedInterfaceId()}")
            } catch (e: Exception) {
                println("DEBUG: Network reinitialization failed: ${e.message}")
            }
        }
    }

    /**
     * Refresh network interfaces list
     */
    fun refreshNetworkInterfaces() {
        loadNetworkInterfaces()
    }

    /**
     * Get currently selected network interface ID
     */
    fun getSelectedInterfaceId(): String {
        return if (networkInterfaceIndex.value in 0 until _networkInterfaces.value.size) {
            _networkInterfaces.value[networkInterfaceIndex.value].id
        } else {
            "0" // Default
        }
    }

    /**
     * Get multiplayer mode name
     */
    fun getMultiplayerModeName(index: Int): String {
        return when (index) {
            0 -> "Disabled"
            1 -> "LDN Local Wireless"
            else -> "Unknown"
        }
    }

    /**
     * Check network connection status
     */
    fun getNetworkStatus(): NetworkStatus {
        return try {
            val connectivityManager = context.getSystemService(ConnectivityManager::class.java)
            val network = connectivityManager.activeNetwork
            val capabilities = connectivityManager.getNetworkCapabilities(network)
            
            if (capabilities != null) {
                when {
                    capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> 
                        NetworkStatus.CONNECTED_WIFI
                    capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> 
                        NetworkStatus.CONNECTED_MOBILE
                    capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> 
                        NetworkStatus.CONNECTED_ETHERNET
                    else -> NetworkStatus.CONNECTED_UNKNOWN
                }
            } else {
                NetworkStatus.DISCONNECTED
            }
        } catch (e: Exception) {
            NetworkStatus.UNKNOWN
        }
    }

    /**
     * Get network status display text
     */
    fun getNetworkStatusText(): String {
        return when (getNetworkStatus()) {
            NetworkStatus.CONNECTED_WIFI -> "Connected (WiFi)"
            NetworkStatus.CONNECTED_MOBILE -> "Connected (Mobile)"
            NetworkStatus.CONNECTED_ETHERNET -> "Connected (Ethernet)"
            NetworkStatus.CONNECTED_UNKNOWN -> "Connected"
            NetworkStatus.DISCONNECTED -> "Disconnected"
            NetworkStatus.UNKNOWN -> "Status Unknown"
        }
    }

    /**
     * Get network status color (for UI use)
     */
    fun getNetworkStatusColor(): String {
        return when (getNetworkStatus()) {
            NetworkStatus.CONNECTED_WIFI,
            NetworkStatus.CONNECTED_MOBILE,
            NetworkStatus.CONNECTED_ETHERNET,
            NetworkStatus.CONNECTED_UNKNOWN -> "connected"
            NetworkStatus.DISCONNECTED -> "disconnected"
            NetworkStatus.UNKNOWN -> "unknown"
        }
    }

    // ==================== 网络初始化功能 ====================

    /**
     * 初始化网络通信
     */
    private fun initializeNetwork() {
        coroutineScope.launch(Dispatchers.IO) {
            try {
                // 设置网络接口
                val interfaceId = getSelectedInterfaceId()
                RyujinxNative.setLanInterface(interfaceId)
                
                // 初始化网络
                RyujinxNative.initializeNetwork()
                println("DEBUG: Network initialized successfully with interface: $interfaceId")
            } catch (e: Exception) {
                println("DEBUG: Network initialization failed: ${e.message}")
            }
        }
    }

    /**
     * 停止网络通信
     */
    private fun stopNetwork() {
        coroutineScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.stopNetwork()
                println("DEBUG: Network stopped successfully")
            } catch (e: Exception) {
                println("DEBUG: Network stop failed: ${e.message}")
            }
        }
    }

    // ==================== 大厅管理功能 ====================

    /**
     * 创建大厅
     */
    fun createLobby(lobbyName: String, gameTitle: String, maxPlayers: Int, username: String = "") {
        println("DEBUG: Creating lobby - Name: $lobbyName, Game: $gameTitle, MaxPlayers: $maxPlayers, Username: $username")
        
        coroutineScope.launch(Dispatchers.IO) {
            lobbyState.value = LobbyState.CREATING
            try {
                val success = RyujinxNative.createLobby(lobbyName, gameTitle, maxPlayers, username)
                println("DEBUG: Lobby creation result: $success")
                
                if (success) {
                    // 延迟一下，确保C#层有足够时间创建大厅
                    delay(500)
                    
                    // 获取当前大厅信息
                    val currentLobbyJson = RyujinxNative.getCurrentLobby()
                    println("DEBUG: Current lobby JSON: $currentLobbyJson")
                    
                    if (currentLobbyJson.isNotEmpty()) {
                        try {
                            val lobby = Json.decodeFromString<LobbyInfo>(currentLobbyJson)
                            println("DEBUG: Successfully parsed lobby: ${lobby.name}")
                            
                            // 切换到主线程更新UI状态
                            withContext(Dispatchers.Main) {
                                currentLobby.value = lobby
                                lobbyState.value = LobbyState.HOSTING
                                isHostingLobby.value = true
                                showCreateLobbyDialog.value = false
                                showRoomStatusDialog.value = true // 显示房间状态对话框
                                println("DEBUG: UI state updated to HOSTING")
                            }
                        } catch (e: Exception) {
                            println("DEBUG: JSON parsing error: ${e.message}")
                            // 如果JSON解析失败，手动创建大厅信息
                            createFallbackLobby(lobbyName, gameTitle, maxPlayers, username)
                        }
                    } else {
                        println("DEBUG: Current lobby JSON is empty, creating fallback lobby")
                        // 如果获取不到大厅信息，手动创建
                        createFallbackLobby(lobbyName, gameTitle, maxPlayers, username)
                    }
                } else {
                    println("DEBUG: Lobby creation failed in native layer")
                    lobbyState.value = LobbyState.IDLE
                }
            } catch (e: Exception) {
                println("DEBUG: Exception in createLobby: ${e.message}")
                lobbyState.value = LobbyState.IDLE
            }
        }
    }

    /**
     * 创建备用大厅信息（当C#层返回失败时使用）
     */
    private fun createFallbackLobby(lobbyName: String, gameTitle: String, maxPlayers: Int, username: String = "") {
        coroutineScope.launch(Dispatchers.Main) {
            val fallbackLobby = LobbyInfo(
                id = System.currentTimeMillis().toString(),
                name = lobbyName,
                gameTitle = gameTitle,
                hostName = if (username.isNotBlank()) username else "Host",
                playerCount = 1,
                maxPlayers = maxPlayers,
                ping = 0,
                isPasswordProtected = false,
                hostIp = getLocalIpAddress(),
                port = 11452,
                gameId = "",
                createdTime = System.currentTimeMillis()
            )
            
            currentLobby.value = fallbackLobby
            lobbyState.value = LobbyState.HOSTING
            isHostingLobby.value = true
            showCreateLobbyDialog.value = false
            showRoomStatusDialog.value = true // 显示房间状态对话框
            println("DEBUG: Fallback lobby created and UI updated")
        }
    }

    /**
     * 加入大厅
     */
    fun joinLobby(lobby: LobbyInfo) {
        println("DEBUG: Joining lobby: ${lobby.name} at ${lobby.hostIp}:${lobby.port}")
        
        coroutineScope.launch(Dispatchers.IO) {
            lobbyState.value = LobbyState.JOINING
            try {
                val success = RyujinxNative.joinLobby(lobby.hostIp, lobby.port)
                println("DEBUG: Join lobby result: $success")
                
                if (success) {
                    withContext(Dispatchers.Main) {
                        currentLobby.value = lobby
                        lobbyState.value = LobbyState.IN_LOBBY
                        isHostingLobby.value = false
                        showRoomStatusDialog.value = true // 显示房间状态对话框
                    }
                } else {
                    lobbyState.value = LobbyState.IDLE
                }
            } catch (e: Exception) {
                println("DEBUG: Exception in joinLobby: ${e.message}")
                lobbyState.value = LobbyState.IDLE
            }
        }
    }

    /**
     * 离开大厅
     */
    fun leaveLobby() {
        println("DEBUG: Leaving current lobby")
        
        coroutineScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.leaveLobby()
                withContext(Dispatchers.Main) {
                    currentLobby.value = null
                    lobbyState.value = LobbyState.IDLE
                    isHostingLobby.value = false
                    showRoomStatusDialog.value = false // 隐藏房间状态对话框
                    // 刷新大厅列表
                    refreshLobbyList()
                }
            } catch (e: Exception) {
                println("DEBUG: Exception in leaveLobby: ${e.message}")
                // 即使出错也重置状态
                withContext(Dispatchers.Main) {
                    currentLobby.value = null
                    lobbyState.value = LobbyState.IDLE
                    isHostingLobby.value = false
                    showRoomStatusDialog.value = false
                }
            }
        }
    }

    /**
     * 刷新大厅列表 - 修复版本
     */
    fun refreshLobbyList() {
        if (isScanningLobbies.value) {
            println("DEBUG: Lobby scan already in progress, skipping")
            return
        }
        
        coroutineScope.launch(Dispatchers.IO) {
            isScanningLobbies.value = true
            try {
                println("DEBUG: Starting lobby list refresh...")
                
                // 调用原生方法刷新大厅列表
                RyujinxNative.refreshLobbyList()
                
                // 等待网络扫描完成 - 增加到3秒
                delay(3000)
                
                // 获取大厅列表
                val lobbyListJson = RyujinxNative.getLobbyList()
                println("DEBUG: Raw lobby list JSON: $lobbyListJson")
                
                if (lobbyListJson.isNotEmpty() && lobbyListJson != "[]" && lobbyListJson != "null") {
                    try {
                        val lobbies = Json.decodeFromString<List<LobbyInfo>>(lobbyListJson)
                        println("DEBUG: Successfully parsed ${lobbies.size} lobbies")
                        
                        // 过滤掉无效的大厅
                        val validLobbies = lobbies.filter { 
                            it.name.isNotBlank() && it.hostIp.isNotBlank() && it.hostIp != "127.0.0.1"
                        }
                        
                        println("DEBUG: Filtered to ${validLobbies.size} valid lobbies")
                        
                        withContext(Dispatchers.Main) {
                            lobbyList.value = validLobbies
                            println("DEBUG: Updated lobby list with ${validLobbies.size} real network lobbies")
                        }
                    } catch (e: Exception) {
                        println("DEBUG: Lobby list JSON parsing error: ${e.message}")
                        println("DEBUG: JSON content: $lobbyListJson")
                        // 移除模拟大厅创建，只返回空列表
                        withContext(Dispatchers.Main) {
                            lobbyList.value = emptyList()
                        }
                    }
                } else {
                    println("DEBUG: No lobbies found in network scan or empty JSON")
                    withContext(Dispatchers.Main) {
                        lobbyList.value = emptyList()
                    }
                }
            } catch (e: Exception) {
                println("DEBUG: Exception in refreshLobbyList: ${e.message}")
                withContext(Dispatchers.Main) {
                    lobbyList.value = emptyList()
                }
            } finally {
                withContext(Dispatchers.Main) {
                    isScanningLobbies.value = false
                    println("DEBUG: Lobby scan completed, isScanning set to false")
                }
            }
        }
    }

    /**
     * 获取本地IP地址
     */
    fun getLocalIpAddress(): String {
        return try {
            val interfaces = Collections.list(NetworkInterface.getNetworkInterfaces())
            for (intf in interfaces) {
                if (intf.isUp && !intf.isLoopback) {
                    val addrs = Collections.list(intf.inetAddresses)
                    for (addr in addrs) {
                        if (!addr.isLoopbackAddress && addr is InetAddress) {
                            val sAddr = addr.hostAddress
                            if (sAddr != null && sAddr.indexOf(':') < 0) {
                                return sAddr
                            }
                        }
                    }
                }
            }
            "127.0.0.1"
        } catch (e: Exception) {
            "127.0.0.1"
        }
    }

    /**
     * 启动自动刷新大厅列表 - 已移除
     */
    private fun startAutoLobbyRefresh() {
        // 不再自动刷新，改为手动刷新
        lobbyRefreshJob?.cancel()
    }

    /**
     * 检查是否正在扫描大厅
     */
    fun checkScanningStatus(): Boolean {
        return RyujinxNative.isScanningLobbies()
    }

    /**
     * 检查是否正在托管大厅
     */
    fun checkHostingStatus(): Boolean {
        return RyujinxNative.isHostingLobby()
    }

    override fun onCleared() {
        super.onCleared()
        lobbyRefreshJob?.cancel()
        // 注意：这里不自动离开大厅，让用户手动管理
        // 停止网络通信
        stopNetwork()
    }
}

/**
 * Network interface information data class
 */
data class NetworkInterfaceInfo(
    val name: String,
    val id: String,
    val description: String = ""
)

/**
 * Network connection status enum
 */
enum class NetworkStatus {
    CONNECTED_WIFI,
    CONNECTED_MOBILE,
    CONNECTED_ETHERNET,
    CONNECTED_UNKNOWN,
    DISCONNECTED,
    UNKNOWN
}

/**
 * 大厅状态枚举
 */
enum class LobbyState {
    IDLE,           // 空闲状态
    CREATING,       // 正在创建大厅
    JOINING,        // 正在加入大厅
    HOSTING,        // 正在托管大厅
    IN_LOBBY        // 已加入大厅
}

/**
 * 大厅信息数据类
 */
@kotlinx.serialization.Serializable
data class LobbyInfo(
    val id: String = "",
    val name: String = "",
    val gameTitle: String = "",
    val hostName: String = "",
    val playerCount: Int = 0,
    val maxPlayers: Int = 4,
    val ping: Int = 0,
    val isPasswordProtected: Boolean = false,
    val hostIp: String = "",
    val port: Int = 11452,
    val gameId: String = "",
    val createdTime: Long = 0
)
