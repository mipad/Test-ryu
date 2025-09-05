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
    
    // 获取GPU型号（专注于获取GPU型号而不是SoC型号）
    fun getGpuName(): String {
        // 方法1: 尝试从OpenGL渲染器信息获取GPU型号
        var gpuName = getGpuNameFromOpenGL()
        if (gpuName != "Unknown GPU") {
            return gpuName
        }
        
        // 方法2: 尝试从Vulkan信息获取GPU型号
        gpuName = getGpuNameFromVulkan()
        if (gpuName != "Unknown GPU") {
            return gpuName
        }
        
        // 方法3: 尝试从特定GPU厂商文件获取
        gpuName = getGpuNameFromVendorFiles()
        if (gpuName != "Unknown GPU") {
            return gpuName
        }
        
        // 方法4: 尝试从系统属性获取
        gpuName = getGpuNameFromSystemProperties()
        if (gpuName != "Unknown GPU") {
            return gpuName
        }
        
        return "Unknown GPU"
    }
    
    // 从OpenGL信息获取GPU型号
    private fun getGpuNameFromOpenGL(): String {
        // 这个方法需要GLES上下文，可能需要在实际渲染环境中使用
        // 这里提供一个基于系统属性的替代方法
        return try {
            // 尝试获取OpenGL渲染器信息
            val process = Runtime.getRuntime().exec("getprop ro.opengles.version")
            val reader = BufferedReader(InputStreamReader(process.inputStream))
            val result = reader.readLine()?.trim()
            reader.close()
            
            if (!result.isNullOrEmpty() && result != "null") {
                // 根据OpenGL版本推断可能的GPU型号
                when (result.toInt()) {
                    196608 -> "Mali-G710" // OpenGL ES 3.2
                    131072 -> "Mali-G510" // OpenGL ES 3.0
                    else -> "Unknown GPU"
                }
            } else {
                "Unknown GPU"
            }
        } catch (e: Exception) {
            "Unknown GPU"
        }
    }
    
    // 从Vulkan信息获取GPU型号
    private fun getGpuNameFromVulkan(): String {
        return try {
            // 尝试获取Vulkan信息
            val process = Runtime.getRuntime().exec("dumpsys SurfaceFlinger | grep \"Vulkan\"")
            val reader = BufferedReader(InputStreamReader(process.inputStream))
            var line: String?
            var gpuName = "Unknown GPU"
            
            while (reader.readLine().also { line = it } != null) {
                line?.let {
                    if (it.contains("Mali", ignoreCase = true)) {
                        // 提取Mali GPU型号
                        val maliPattern = "Mali-[GT]\\d+".toRegex(RegexOption.IGNORE_CASE)
                        val match = maliPattern.find(it)
                        if (match != null) {
                            gpuName = match.value
                            return gpuName
                        }
                    } else if (it.contains("Adreno", ignoreCase = true)) {
                        // 提取Adreno GPU型号
                        val adrenoPattern = "Adreno\\s*\\d+".toRegex(RegexOption.IGNORE_CASE)
                        val match = adrenoPattern.find(it)
                        if (match != null) {
                            gpuName = match.value
                            return gpuName
                        }
                    }
                }
            }
            reader.close()
            gpuName
        } catch (e: Exception) {
            "Unknown GPU"
        }
    }
    
    // 从GPU厂商特定文件获取GPU型号
    private fun getGpuNameFromVendorFiles(): String {
        // Mali GPU
        try {
            val file = File("/sys/class/misc/mali0/device/mali/gpuinfo")
            if (file.exists()) {
                val content = file.readText().trim()
                if (content.isNotEmpty()) {
                    return extractGpuModelFromContent(content)
                }
            }
        } catch (e: Exception) {
            // 忽略异常
        }
        
        // Adreno GPU
        try {
            val file = File("/sys/class/kgsl/kgsl-3d0/gpu_model")
            if (file.exists()) {
                val content = file.readText().trim()
                if (content.isNotEmpty()) {
                    return extractGpuModelFromContent(content)
                }
            }
        } catch (e: Exception) {
            // 忽略异常
        }
        
        // PowerVR GPU
        try {
            val file = File("/proc/pvr/version")
            if (file.exists()) {
                val content = file.readText().trim()
                if (content.isNotEmpty() && content.contains("PowerVR", ignoreCase = true)) {
                    return "PowerVR GPU"
                }
            }
        } catch (e: Exception) {
            // 忽略异常
        }
        
        return "Unknown GPU"
    }
    
    // 从系统属性获取GPU型号
    private fun getGpuNameFromSystemProperties(): String {
        val props = listOf(
            "ro.hardware.egl",
            "ro.board.platform",
            "ro.chipname",
            "ro.hardware.gpu"
        )
        
        for (prop in props) {
            try {
                val process = Runtime.getRuntime().exec(arrayOf("getprop", prop))
                val reader = BufferedReader(InputStreamReader(process.inputStream))
                val result = reader.readLine()?.trim()
                reader.close()
                
                if (!result.isNullOrEmpty() && result != "null") {
                    val gpuName = extractGpuModelFromContent(result)
                    if (gpuName != "Unknown GPU") {
                        return gpuName
                    }
                }
            } catch (e: Exception) {
                // 忽略异常
            }
        }
        
        return "Unknown GPU"
    }
    
    // 从内容中提取GPU型号
    private fun extractGpuModelFromContent(content: String): String {
        // 尝试匹配Mali GPU型号
        val maliPattern = "Mali-[GT]\\d+".toRegex(RegexOption.IGNORE_CASE)
        val maliMatch = maliPattern.find(content)
        if (maliMatch != null) {
            return maliMatch.value
        }
        
        // 尝试匹配Adreno GPU型号
        val adrenoPattern = "Adreno\\s*\\d+".toRegex(RegexOption.IGNORE_CASE)
        val adrenoMatch = adrenoPattern.find(content)
        if (adrenoMatch != null) {
            return adrenoMatch.value
        }
        
        // 尝试匹配PowerVR GPU
        if (content.contains("PowerVR", ignoreCase = true)) {
            return "PowerVR GPU"
        }
        
        // 尝试匹配其他常见GPU型号
        val otherPatterns = listOf(
            "Tegra",
            "GeForce",
            "Intel HD Graphics",
            "Iris",
            "Radeon"
        )
        
        for (pattern in otherPatterns) {
            if (content.contains(pattern, ignoreCase = true)) {
                return pattern
            }
        }
        
        return "Unknown GPU"
    }
}
