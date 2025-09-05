package org.ryujinx.android

import android.app.ActivityManager
import android.content.Context.ACTIVITY_SERVICE
import androidx.compose.runtime.MutableState
import java.io.File
import java.io.RandomAccessFile

class PerformanceMonitor {
    val numberOfCores = Runtime.getRuntime().availableProcessors()
    
    // 记录最后成功的温度传感器路径
    private var lastSuccessfulPath: String? = null

    fun getFrequencies(frequencies: MutableList<Double>){
        frequencies.clear()
        for (i in 0..<numberOfCores) {
            var freq = 0.0
            try {
                val reader = RandomAccessFile(
                    "/sys/devices/system/cpu/cpu${i}/cpufreq/scaling_cur_freq",
                    "r"
                )
                val f = reader.readLine()
                reader.close()
                freq = f.toDouble() / 1000.0
            } catch (e: Exception) {
                // 忽略错误
            }
            frequencies.add(freq)
        }
    }

    fun getMemoryUsage(
        usedMem: MutableState<Int>,
        totalMem: MutableState<Int>) {
        MainActivity.mainViewModel?.activity?.apply {
            val actManager = getSystemService(ACTIVITY_SERVICE) as ActivityManager
            val memInfo = ActivityManager.MemoryInfo()
            actManager.getMemoryInfo(memInfo)
            val availMemory = memInfo.availMem.toDouble() / (1024 * 1024)
            val totalMemory = memInfo.totalMem.toDouble() / (1024 * 1024)

            usedMem.value = (totalMemory - availMemory).toInt()
            totalMem.value = totalMemory.toInt()
        }
    }
    
    // 获取CPU温度（不使用缓存）
    fun getCpuTemperature(): Double {
        // 如果之前有成功的路径，先尝试它
        lastSuccessfulPath?.let { path ->
            val temp = readTemperatureFromPath(path)
            if (temp > 0) return temp
        }
        
        // 如果没有成功路径或路径失效，尝试所有可能的路径
        return findAndReadTemperature()
    }

    // 从指定路径读取温度
    private fun readTemperatureFromPath(path: String): Double {
        try {
            val file = File(path)
            if (!file.exists()) return 0.0
            
            val tempStr = file.readText().trim()
            if (tempStr.isEmpty()) return 0.0
            
            // 有些文件返回的是毫摄氏度，所以除以1000
            var temp = tempStr.toDouble() / 1000.0
            
            // 如果温度值异常大，可能是没有除以1000，所以再检查一次
            if (temp > 200) {
                temp /= 1000.0
            }
            
            // 只返回合理的温度值
            if (temp > 0 && temp < 120) {
                lastSuccessfulPath = path // 记录成功路径
                return temp
            }
        } catch (e: Exception) {
            // 忽略错误
        }
        return 0.0
    }

    // 尝试所有可能的温度传感器路径
    private fun findAndReadTemperature(): Double {
        // 尝试所有可能的温度传感器路径
        val allPaths = getAllPossibleTemperaturePaths()
        
        for (path in allPaths) {
            val temp = readTemperatureFromPath(path)
            if (temp > 0) return temp
        }
        
        return 0.0
    }

    // 获取所有可能的温度传感器路径
    private fun getAllPossibleTemperaturePaths(): List<String> {
        val paths = mutableListOf<String>()
        
        // 添加所有可能的thermal_zone路径
        for (i in 0..20) {
            paths.add("/sys/class/thermal/thermal_zone$i/temp")
            paths.add("/sys/devices/virtual/thermal/thermal_zone$i/temp")
        }
        
        // 添加小米/天玑特有的路径
        paths.addAll(listOf(
            // 小米设备常见路径
            "/sys/class/thermal/thermal_zone0/temp",
            "/sys/class/thermal/thermal_zone1/temp",
            "/sys/class/thermal/thermal_zone2/temp",
            "/sys/class/thermal/thermal_zone3/temp",
            "/sys/class/thermal/thermal_zone4/temp",
            "/sys/class/thermal/thermal_zone5/temp",
            "/sys/class/thermal/thermal_zone6/temp",
            "/sys/class/thermal/thermal_zone7/temp",
            "/sys/class/thermal/thermal_zone8/temp",
            "/sys/class/thermal/thermal_zone9/temp",
            "/sys/class/thermal/thermal_zone10/temp",
            
            // 天玑芯片特定路径
            "/sys/class/thermal/thermal_zone/mtktscpu/temp",
            "/sys/class/thermal/thermal_zone/mtktsAP/temp",
            "/sys/class/thermal/thermal_zone/mtktscpu0/temp",
            "/sys/class/thermal/thermal_zone/mtktscpu1/temp",
            "/sys/class/thermal/thermal_zone/mtktscpu2/temp",
            "/sys/class/thermal/thermal_zone/mtktscpu3/temp",
            
            // 小米设备可能使用的路径
            "/sys/class/hwmon/hwmon0/temp1_input",
            "/sys/class/hwmon/hwmon1/temp1_input",
            "/sys/class/hwmon/hwmon2/temp1_input",
            "/sys/class/hwmon/hwmon3/temp1_input",
            "/sys/class/hwmon/hwmon4/temp1_input",
            "/sys/class/hwmon/hwmon5/temp1_input",
            
            // 平台特定路径
            "/sys/devices/platform/soc/soc:mtk-thermal/temp",
            "/sys/devices/platform/soc/soc:thermal/temp",
            "/sys/devices/platform/soc/soc:thermal-sensor/temp",
            "/sys/devices/virtual/thermal/tzbypass/temp",
            
            // 其他可能的路径
            "/sys/class/thermal/thermal_zone/temp",
            "/proc/driver/thermal/temp",
            "/proc/thermal/temp",
            
            // 小米设备可能使用的额外路径
            "/sys/devices/virtual/thermal/thermal_zone11/temp",
            "/sys/devices/virtual/thermal/thermal_zone12/temp",
            "/sys/devices/virtual/thermal/thermal_zone13/temp",
            "/sys/devices/virtual/thermal/thermal_zone14/temp",
            "/sys/devices/virtual/thermal/thermal_zone15/temp"
        ))
        
        return paths
    }
}
