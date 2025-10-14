package org.ryujinx.android.viewmodels

import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.preference.PreferenceManager
import kotlinx.coroutines.*
import org.ryujinx.android.MainActivity
import java.net.*
import java.util.*

class NetworkViewModel(activity: MainActivity) : ViewModel() {
    private var sharedPref: SharedPreferences = PreferenceManager.getDefaultSharedPreferences(activity)
    private val context = activity.applicationContext
    
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

    // Device discovery
    var discoveredDevices = mutableStateOf<List<DiscoveredDevice>>(emptyList())
        private set
    
    var isDiscovering = mutableStateOf(false)
        private set

    // Network discovery constants
    companion object {
        private const val DISCOVERY_PORT = 17777
        private const val DISCOVERY_MULTICAST_GROUP = "239.177.77.1"
        private const val DISCOVERY_TIMEOUT = 5000L // 5 seconds
        private const val BEACON_INTERVAL = 3000L // 3 seconds
    }

    private var discoveryJob: Job? = null
    private var beaconJob: Job? = null
    private var multicastSocket: MulticastSocket? = null
    private var shouldStopDiscovery = false

    init {
        loadNetworkInterfaces()
    }

    /**
     * Start device discovery
     */
    fun startDiscovery() {
        if (isDiscovering.value) return
        
        isDiscovering.value = true
        discoveredDevices.value = emptyList()
        shouldStopDiscovery = false
        
        discoveryJob = CoroutineScope(Dispatchers.IO).launch {
            try {
                // Join multicast group
                multicastSocket = MulticastSocket(DISCOVERY_PORT).apply {
                    reuseAddress = true
                    soTimeout = 1000 // 1 second timeout for receive
                    
                    // Join multicast group on all available interfaces
                    networkInterfaceList.forEach { iface ->
                        try {
                            val networkInterface = NetworkInterface.getByName(iface.id)
                            if (networkInterface != null && networkInterface.isUp && !networkInterface.isLoopback) {
                                joinGroup(InetSocketAddress(InetAddress.getByName(DISCOVERY_MULTICAST_GROUP), DISCOVERY_PORT), networkInterface)
                            }
                        } catch (e: Exception) {
                            e.printStackTrace()
                        }
                    }
                }

                // Start beacon to announce our presence
                startBeacon()

                val buffer = ByteArray(1024)
                val packet = DatagramPacket(buffer, buffer.size)

                while (!shouldStopDiscovery && isActive) {
                    try {
                        multicastSocket?.receive(packet)
                        val message = String(packet.data, 0, packet.length).trim()
                        
                        if (message.startsWith("RYUJINX_DISCOVER:")) {
                            val parts = message.split(":")
                            if (parts.size >= 3) {
                                val deviceName = parts[1]
                                val deviceId = parts[2]
                                val deviceIp = packet.address.hostAddress
                                
                                // Don't add our own device
                                if (deviceId != getDeviceId()) {
                                    withContext(Dispatchers.Main) {
                                        val existingDevice = discoveredDevices.value.find { it.id == deviceId }
                                        if (existingDevice == null) {
                                            // New device
                                            discoveredDevices.value = discoveredDevices.value + DiscoveredDevice(
                                                name = deviceName,
                                                id = deviceId,
                                                ip = deviceIp,
                                                lastSeen = System.currentTimeMillis()
                                            )
                                        } else {
                                            // Update existing device
                                            discoveredDevices.value = discoveredDevices.value.map {
                                                if (it.id == deviceId) {
                                                    it.copy(lastSeen = System.currentTimeMillis(), ip = deviceIp)
                                                } else {
                                                    it
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    } catch (e: SocketTimeoutException) {
                        // Timeout is expected, continue listening
                    } catch (e: Exception) {
                        if (!shouldStopDiscovery) {
                            e.printStackTrace()
                        }
                    }
                }
            } catch (e: Exception) {
                e.printStackTrace()
                withContext(Dispatchers.Main) {
                    isDiscovering.value = false
                }
            } finally {
                multicastSocket?.close()
                multicastSocket = null
            }
        }
    }

    /**
     * Stop device discovery
     */
    fun stopDiscovery() {
        shouldStopDiscovery = true
        isDiscovering.value = false
        
        beaconJob?.cancel()
        discoveryJob?.cancel()
        
        multicastSocket?.close()
        multicastSocket = null
    }

    /**
     * Start broadcasting our presence
     */
    private fun startBeacon() {
        beaconJob = CoroutineScope(Dispatchers.IO).launch {
            while (!shouldStopDiscovery && isActive) {
                try {
                    broadcastPresence()
                    delay(BEACON_INTERVAL)
                } catch (e: Exception) {
                    if (!shouldStopDiscovery) {
                        e.printStackTrace()
                    }
                }
            }
        }
    }

    /**
     * Broadcast our presence to the network
     */
    private fun broadcastPresence() {
        try {
            val message = "RYUJINX_DISCOVER:${getDeviceName()}:${getDeviceId()}"
            val data = message.toByteArray()
            
            // Send via multicast
            val group = InetAddress.getByName(DISCOVERY_MULTICAST_GROUP)
            val packet = DatagramPacket(data, data.size, group, DISCOVERY_PORT)
            multicastSocket?.send(packet)
            
            // Also send broadcast for devices that don't support multicast
            val broadcastPacket = DatagramPacket(data, data.size, InetAddress.getByName("255.255.255.255"), DISCOVERY_PORT)
            multicastSocket?.send(broadcastPacket)
            
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    /**
     * Get unique device ID
     */
    private fun getDeviceId(): String {
        return Build.SERIAL ?: "unknown"
    }

    /**
     * Get device name for display
     */
    private fun getDeviceName(): String {
        return "${Build.MANUFACTURER} ${Build.MODEL}"
    }

    /**
     * Refresh discovered devices list
     */
    fun refreshDiscoveredDevices() {
        // Remove devices that haven't been seen in a while
        val now = System.currentTimeMillis()
        discoveredDevices.value = discoveredDevices.value.filter {
            now - it.lastSeen < DISCOVERY_TIMEOUT * 2
        }
        
        // Re-broadcast our presence
        CoroutineScope(Dispatchers.IO).launch {
            broadcastPresence()
        }
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

    override fun onCleared() {
        super.onCleared()
        stopDiscovery()
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
 * Discovered device information
 */
data class DiscoveredDevice(
    val name: String,
    val id: String,
    val ip: String,
    val lastSeen: Long
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
