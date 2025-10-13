package org.ryujinx.android.viewmodels

import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import androidx.preference.PreferenceManager
import org.ryujinx.android.MainActivity
import java.net.NetworkInterface
import java.util.Collections

class NetworkViewModel(activity: MainActivity) {
    private var sharedPref: SharedPreferences = PreferenceManager.getDefaultSharedPreferences(activity)
    private val context = activity.applicationContext
    private val _networkInterfaces = mutableListOf<NetworkInterfaceInfo>()
    val networkInterfaceList: List<NetworkInterfaceInfo> get() = _networkInterfaces

    // 多人游戏模式
    var multiplayerModeIndex: Int
        get() = sharedPref.getInt("multiplayerModeIndex", 0)
        set(value) = sharedPref.edit().putInt("multiplayerModeIndex", value).apply()

    // 启用互联网访问
    var enableInternetAccess: Boolean
        get() = sharedPref.getBoolean("enableInternetAccess", false)
        set(value) = sharedPref.edit().putBoolean("enableInternetAccess", value).apply()

    // 网络接口索引
    var networkInterfaceIndex: Int
        get() = sharedPref.getInt("networkInterfaceIndex", 0)
        set(value) = sharedPref.edit().putInt("networkInterfaceIndex", value).apply()

    init {
        loadNetworkInterfaces()
    }

    /**
     * 加载可用的网络接口
     */
    private fun loadNetworkInterfaces() {
        _networkInterfaces.clear()
        
        // 添加默认选项
        _networkInterfaces.add(NetworkInterfaceInfo("Default", "0", "自动选择最佳网络接口"))
        
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
                    _networkInterfaces.add(interfaceInfo)
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
            // 如果枚举网络接口失败，至少保证有默认选项
            if (_networkInterfaces.size == 1) {
                _networkInterfaces.add(NetworkInterfaceInfo("Fallback", "eth0", "回退网络接口"))
            }
        }
    }

    /**
     * 构建网络接口描述信息
     */
    private fun buildInterfaceDescription(networkInterface: NetworkInterface): String {
        val sb = StringBuilder()
        
        // 添加接口类型信息
        sb.append("${getInterfaceType(networkInterface)}")
        
        // 添加MTU信息
        sb.append(" • MTU: ${networkInterface.mtu}")
        
        // 添加接口状态
        val status = when {
            networkInterface.isUp -> "Up"
            else -> "Down"
        }
        sb.append(" • $status")
        
        return sb.toString()
    }

    /**
     * 获取网络接口类型
     */
    private fun getInterfaceType(networkInterface: NetworkInterface): String {
        return when {
            networkInterface.isLoopback -> "Loopback"
            networkInterface.isPointToPoint -> "PPP"
            networkInterface.isVirtual -> "Virtual"
            networkInterface.name.startsWith("wlan") || networkInterface.name.startsWith("wlp") -> "WiFi"
            networkInterface.name.startsWith("eth") || networkInterface.name.startsWith("enp") -> "Ethernet"
            networkInterface.name.startsWith("rmnet") || networkInterface.name.startsWith("pdp") -> "Mobile"
            networkInterface.name.startsWith("tun") || networkInterface.name.startsWith("tap") -> "VPN"
            else -> "Network"
        }
    }

    /**
     * 刷新网络接口列表
     */
    fun refreshNetworkInterfaces() {
        loadNetworkInterfaces()
    }

    /**
     * 获取当前选中的网络接口ID
     */
    fun getSelectedInterfaceId(): String {
        return if (networkInterfaceIndex in 0 until _networkInterfaces.size) {
            _networkInterfaces[networkInterfaceIndex].id
        } else {
            "0" // 默认
        }
    }

    /**
     * 获取多人游戏模式名称
     */
    fun getMultiplayerModeName(index: Int): String {
        return when (index) {
            0 -> "禁用"
            1 -> "LDN 本地无线"
            else -> "未知"
        }
    }

    /**
     * 检查网络连接状态
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
     * 获取网络状态显示文本
     */
    fun getNetworkStatusText(): String {
        return when (getNetworkStatus()) {
            NetworkStatus.CONNECTED_WIFI -> "已连接 (WiFi)"
            NetworkStatus.CONNECTED_MOBILE -> "已连接 (移动网络)"
            NetworkStatus.CONNECTED_ETHERNET -> "已连接 (以太网)"
            NetworkStatus.CONNECTED_UNKNOWN -> "已连接"
            NetworkStatus.DISCONNECTED -> "未连接"
            NetworkStatus.UNKNOWN -> "状态未知"
        }
    }

    /**
     * 获取网络状态颜色（在UI中使用）
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
}

/**
 * 网络接口信息数据类
 */
data class NetworkInterfaceInfo(
    val name: String,
    val id: String,
    val description: String = ""
)

/**
 * 网络连接状态枚举
 */
enum class NetworkStatus {
    CONNECTED_WIFI,
    CONNECTED_MOBILE,
    CONNECTED_ETHERNET,
    CONNECTED_UNKNOWN,
    DISCONNECTED,
    UNKNOWN
}
