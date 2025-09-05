package org.ryujinx.android

import android.app.ActivityManager
import android.content.Context.ACTIVITY_SERVICE
import androidx.compose.runtime.MutableState
import java.io.RandomAccessFile

class PerformanceMonitor {
    val numberOfCores = Runtime.getRuntime().availableProcessors()
    
    // 温度缓存和更新时间戳
    private var lastCpuTemperatureUpdateTime = 0L
    private var cachedCpuTemperature = 0.0

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
    
    // 获取CPU温度（带缓存，每30秒更新一次）
    fun getCpuTemperature(): Double {
        val currentTime = System.currentTimeMillis()
        // 每30秒更新一次温度
        if (currentTime - lastCpuTemperatureUpdateTime > 30000) {
            lastCpuTemperatureUpdateTime = currentTime
            cachedCpuTemperature = readCpuTemperature()
        }
        return cachedCpuTemperature
    }

    // 实际读取CPU温度的方法
    private fun readCpuTemperature(): Double {
        // 尝试从多个可能的路径读取温度
        val paths = mutableListOf<String>()
        
        // 添加 thermal_zone0 到 thermal_zone9
        for (i in 0..9) {
            paths.add("/sys/class/thermal/thermal_zone$i/temp")
            paths.add("/sys/devices/virtual/thermal/thermal_zone$i/temp")
        }
        
        // 添加其他可能的温度传感器路径
        paths.addAll(listOf(
            "/sys/class/hwmon/hwmon0/temp1_input",
            "/sys/class/hwmon/hwmon1/temp1_input",
            "/sys/class/hwmon/hwmon2/temp1_input",
            "/sys/class/hwmon/hwmon3/temp1_input",
            "/sys/class/hwmon/hwmon4/temp1_input",
            "/sys/class/hwmon/hwmon5/temp1_input",
            "/sys/devices/virtual/thermal/thermal_zone0/temp",
            "/sys/devices/virtual/thermal/thermal_zone1/temp",
            "/sys/devices/platform/soc/soc:mtk-thermal/temp",
            "/sys/devices/platform/soc/soc:thermal/temp",
            "/sys/devices/platform/soc/soc:thermal-sensor/temp",
            "/sys/devices/virtual/thermal/tzbypass/temp",
            "/sys/class/thermal/thermal_zone/temp"
        ))
        
        for (path in paths) {
            try {
                val file = RandomAccessFile(path, "r")
                val tempStr = file.readLine().trim()
                file.close()
                
                // 跳过空字符串
                if (tempStr.isEmpty()) continue
                
                // 有些文件返回的是毫摄氏度，所以除以1000
                var temp = tempStr.toDouble() / 1000.0
                
                // 如果温度值异常大，可能是没有除以1000，所以再检查一次
                if (temp > 200) {
                    temp /= 1000.0
                }
                
                // 只返回合理的温度值
                if (temp > 0 && temp < 120) {
                    return temp
                }
            } catch (e: Exception) {
                // 忽略，继续尝试下一个路径
            }
        }
        return 0.0
    }
}
