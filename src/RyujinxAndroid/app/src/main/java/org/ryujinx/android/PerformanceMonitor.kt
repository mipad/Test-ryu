package org.ryujinx.android

import android.app.ActivityManager
import android.content.Context
import android.content.Context.BATTERY_SERVICE
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import androidx.compose.runtime.MutableState
import java.io.File

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
}
