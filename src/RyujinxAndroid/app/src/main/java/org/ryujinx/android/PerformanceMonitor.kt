package org.ryujinx.android

import android.app.ActivityManager
import android.content.Context
import android.content.Context.BATTERY_SERVICE
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Build
import androidx.compose.runtime.MutableState
import java.io.File
import java.io.BufferedReader
import java.io.InputStreamReader

class PerformanceMonitor {
    val numberOfCores = Runtime.getRuntime().availableProcessors()

    fun getFrequencies(frequencies: MutableList<Double>){
        frequencies.clear()
        for (i in 0..<numberOfCores) {
            var freq = 0.0
            try {
                val file = File("/sys/devices/system/cpu/cpu${i}/cpufreq/scaling_cur_freq")
                if (file.exists()) {
                    val f = file.readText().trim()
                    freq = f.toDouble() / 1000.0
                }
            } catch (e: Exception) {
                // 忽略异常
            }
            frequencies.add(freq)
        }
    }

    fun getMemoryUsage(
        usedMem: MutableState<Int>,
        totalMem: MutableState<Int>) {
        MainActivity.mainViewModel?.activity?.apply {
            val actManager = getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
            val memInfo = ActivityManager.MemoryInfo()
            actManager.getMemoryInfo(memInfo)
            val availMemory = memInfo.availMem.toDouble() / (1024 * 1024)
            val totalMemory = memInfo.totalMem.toDouble() / (1024 * 1024)

            usedMem.value = (totalMemory - availMemory).toInt()
            totalMem.value = totalMemory.toInt()
        }
    }
    
    // 获取电池温度
    fun getBatteryTemperature(): Double {
        return try {
            MainActivity.mainViewModel?.activity?.let { activity ->
                // 使用 Intent 获取电池信息
                val batteryIntent = activity.registerReceiver(null, 
                    IntentFilter(Intent.ACTION_BATTERY_CHANGED))
                batteryIntent?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0)?.div(10.0) ?: 0.0
            } ?: 0.0
        } catch (e: Exception) {
            0.0
        }
    }
    
    // 获取电池电量
    fun getBatteryLevel(): Int {
        return try {
            MainActivity.mainViewModel?.activity?.let { activity ->
                // 使用 Intent 获取电池信息
                val batteryIntent = activity.registerReceiver(null, 
                    IntentFilter(Intent.ACTION_BATTERY_CHANGED))
                batteryIntent?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
            } ?: -1
        } catch (e: Exception) {
            -1
        }
    }
    
    // 获取充电状态
    fun isCharging(): Boolean {
        return try {
            MainActivity.mainViewModel?.activity?.let { activity ->
                // 使用 Intent 获取电池信息
                val batteryIntent = activity.registerReceiver(null, 
                    IntentFilter(Intent.ACTION_BATTERY_CHANGED))
                val status = batteryIntent?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
                status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL
            } ?: false
        } catch (e: Exception) {
            false
        }
    }
    
    // 获取GPU名称（使用多种方法尝试）
    fun getGpuName(): String {
        // 方法1: 尝试从系统属性获取
        var gpuName = getGpuNameFromSystemProperties()
        if (gpuName != "Unknown GPU") {
            return gpuName
        }
        
        // 方法2: 尝试从文件系统获取
        gpuName = getGpuNameFromFilesystem()
        if (gpuName != "Unknown GPU") {
            return gpuName
        }
        
        // 方法3: 尝试通过命令行获取
        gpuName = getGpuNameFromCommand()
        if (gpuName != "Unknown GPU") {
            return gpuName
        }
        
        // 方法4: 尝试通过Build信息获取（适用于某些设备）
        gpuName = getGpuNameFromBuild()
        if (gpuName != "Unknown GPU") {
            return gpuName
        }
        
        return "Unknown GPU"
    }
    
    // 从系统属性获取GPU名称
    private fun getGpuNameFromSystemProperties(): String {
        val props = listOf(
            "ro.hardware.gpu",
            "ro.chipname",
            "ro.board.platform",
            "ro.hardware"
        )
        
        for (prop in props) {
            try {
                val process = Runtime.getRuntime().exec(arrayOf("getprop", prop))
                val reader = BufferedReader(InputStreamReader(process.inputStream))
                val result = reader.readLine()?.trim()
                reader.close()
                
                if (!result.isNullOrEmpty() && result != "null") {
                    return result
                }
            } catch (e: Exception) {
                // 忽略异常
            }
        }
        
        return "Unknown GPU"
    }
    
    // 从文件系统获取GPU名称
    private fun getGpuNameFromFilesystem(): String {
        val paths = listOf(
            "/proc/gpuinfo",
            "/sys/kernel/gpu/gpu_model",
            "/sys/class/kgsl/kgsl-3d0/gpu_model",
            "/sys/class/misc/mali0/device/gpuinfo",
            "/sys/class/misc/mali0/device/model",
            "/sys/class/drm/card0/device/gpu_busy_percent"
        )
        
        for (path in paths) {
            try {
                val file = File(path)
                if (file.exists() && file.canRead()) {
                    val content = file.readText().trim()
                    if (content.isNotEmpty()) {
                        return extractGpuNameFromContent(content)
                    }
                }
            } catch (e: Exception) {
                // 忽略异常
            }
        }
        
        return "Unknown GPU"
    }
    
    // 从命令行获取GPU名称
    private fun getGpuNameFromCommand(): String {
        val commands = listOf(
            "dumpsys | grep -i gpu",
            "cat /proc/cpuinfo | grep -i hardware",
            "lshw -c display 2>/dev/null | grep -i product"
        )
        
        for (cmd in commands) {
            try {
                val process = Runtime.getRuntime().exec(arrayOf("sh", "-c", cmd))
                val reader = BufferedReader(InputStreamReader(process.inputStream))
                var line: String?
                while (reader.readLine().also { line = it } != null) {
                    line?.let {
                        val name = extractGpuNameFromContent(it)
                        if (name != "Unknown GPU") {
                            return name
                        }
                    }
                }
                reader.close()
            } catch (e: Exception) {
                // 忽略异常
            }
        }
        
        return "Unknown GPU"
    }
    
    // 从Build信息获取GPU名称
    private fun getGpuNameFromBuild(): String {
        try {
            // 尝试获取硬件信息
            val hardware = Build.HARDWARE
            if (hardware.isNotEmpty() && hardware != "unknown") {
                return hardware
            }
            
            // 尝试获取主板信息
            val board = Build.BOARD
            if (board.isNotEmpty() && board != "unknown") {
                return board
            }
            
            // 尝试获取品牌信息
            val brand = Build.BRAND
            if (brand.isNotEmpty() && brand != "unknown") {
                return "$brand GPU"
            }
        } catch (e: Exception) {
            // 忽略异常
        }
        
        return "Unknown GPU"
    }
    
    // 从内容中提取GPU名称
    private fun extractGpuNameFromContent(content: String): String {
        // 尝试匹配常见的GPU名称模式
        val patterns = listOf(
            Regex("adreno|adreno\\s*\\d+", RegexOption.IGNORE_CASE),
            Regex("mali|mali\\s*[\\-\\s]*[tg]\\d+", RegexOption.IGNORE_CASE),
            Regex("powervr|powervr\\s*\\w+", RegexOption.IGNORE_CASE),
            Regex("nvidia|tegra|geforce", RegexOption.IGNORE_CASE),
            Regex("intel|hd\\s*graphics", RegexOption.IGNORE_CASE),
            Regex("amd|radeon", RegexOption.IGNORE_CASE)
        )
        
        for (pattern in patterns) {
            val match = pattern.find(content)
            if (match != null) {
                return match.value
            }
        }
        
        // 如果找到包含"gpu"的行，尝试提取
        if (content.contains("gpu", ignoreCase = true)) {
            val lines = content.split("\n")
            for (line in lines) {
                if (line.contains("gpu", ignoreCase = true) && 
                    !line.contains("unknown", ignoreCase = true)) {
                    return line.trim()
                }
            }
        }
        
        return "Unknown GPU"
    }
}
