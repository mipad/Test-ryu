package org.ryujinx.android.viewmodels

import android.content.SharedPreferences
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

    init {
        loadNetworkInterfaces()
        // 启动大厅自动刷新
        startLobbyAutoRefresh()
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

    // ==================== 大厅管理功能 ====================

    /**
     * 创建大厅
     */
    fun createLobby(lobbyName: String, gameTitle: String, maxPlayers: Int, gameId: String = "") {
        coroutineScope.launch(Dispatchers.IO) {
            lobbyState.value = LobbyState.CREATING
            try {
                val success = RyujinxNative.createLobby(lobbyName, gameTitle, maxPlayers, gameId)
                if (success) {
                    // 获取当前大厅信息
                    val currentLobbyJson = RyujinxNative.getCurrentLobby()
                    if (currentLobbyJson.isNotEmpty()) {
                        try {
                            val lobby = Json.decodeFromString<LobbyInfo>(currentLobbyJson)
                            currentLobby.value = lobby
                            lobbyState.value = LobbyState.HOSTING
                            isHostingLobby.value = true
                        } catch (e: Exception) {
                            lobbyState.value = LobbyState.IDLE
                        }
                    }
                } else {
                    lobbyState.value = LobbyState.IDLE
                }
            } catch (e: Exception) {
                lobbyState.value = LobbyState.IDLE
            }
        }
    }

    /**
     * 加入大厅
     */
    fun joinLobby(lobby: LobbyInfo) {
        coroutineScope.launch(Dispatchers.IO) {
            lobbyState.value = LobbyState.JOINING
            try {
                val success = RyujinxNative.joinLobby(lobby.hostIp, lobby.port)
                if (success) {
                    currentLobby.value = lobby
                    lobbyState.value = LobbyState.IN_LOBBY
                    isHostingLobby.value = false
                } else {
                    lobbyState.value = LobbyState.IDLE
                }
            } catch (e: Exception) {
                lobbyState.value = LobbyState.IDLE
            }
        }
    }

    /**
     * 离开大厅
     */
    fun leaveLobby() {
        coroutineScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.leaveLobby()
                currentLobby.value = null
                lobbyState.value = LobbyState.IDLE
                isHostingLobby.value = false
            } catch (e: Exception) {
                // 忽略错误
            }
        }
    }

    /**
     * 刷新大厅列表
     */
    fun refreshLobbyList() {
        if (isScanningLobbies.value) return
        
        coroutineScope.launch(Dispatchers.IO) {
            isScanningLobbies.value = true
            try {
                RyujinxNative.refreshLobbyList()
                
                // 等待扫描完成
                delay(1000)
                
                val lobbyListJson = RyujinxNative.getLobbyList()
                if (lobbyListJson.isNotEmpty()) {
                    try {
                        val lobbies = Json.decodeFromString<List<LobbyInfo>>(lobbyListJson)
                        lobbyList.value = lobbies
                    } catch (e: Exception) {
                        // 解析失败，使用空列表
                        lobbyList.value = emptyList()
                    }
                } else {
                    lobbyList.value = emptyList()
                }
            } catch (e: Exception) {
                lobbyList.value = emptyList()
            } finally {
                isScanningLobbies.value = false
            }
        }
    }

    /**
     * 启动大厅自动刷新
     */
    private fun startLobbyAutoRefresh() {
        lobbyRefreshJob?.cancel()
        lobbyRefreshJob = coroutineScope.launch {
            while (true) {
                if (multiplayerModeIndex.value == 1 && lobbyState.value == LobbyState.IDLE) {
                    refreshLobbyList()
                }
                delay(10000) // 每10秒刷新一次
            }
        }
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
        // 离开大厅
        leaveLobby()
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
