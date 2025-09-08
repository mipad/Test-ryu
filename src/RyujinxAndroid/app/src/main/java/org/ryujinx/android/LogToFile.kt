package org.ryujinx.android

import android.content.Context
import android.os.Environment
import java.io.File
import java.io.FileOutputStream
import java.io.PrintWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

object LogToFile {
    private var logFile: File? = null
    private var outputStream: FileOutputStream? = null
    private var printWriter: PrintWriter? = null
    private val dateFormat = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.getDefault())
    
    fun initialize(context: Context) {
        try {
            // 创建日志目录
            val logDir = File(context.getExternalFilesDir(null), "logs")
            if (!logDir.exists()) {
                logDir.mkdirs()
            }
            
            // 创建日志文件，文件名包含时间戳
            val timestamp = SimpleDateFormat("yyyyMMdd_HHmmss", Locale.getDefault()).format(Date())
            logFile = File(logDir, "ryujinx_$timestamp.log")
            
            // 初始化输出流
            outputStream = FileOutputStream(logFile, true)
            printWriter = PrintWriter(outputStream)
            
            // 写入初始信息
            log("Application", "Log file created: ${logFile?.absolutePath}")
        } catch (e: Exception) {
            // 静默处理异常
        }
    }
    
    fun log(tag: String, message: String) {
        try {
            val timestamp = dateFormat.format(Date())
            val logMessage = "$timestamp [$tag] $message\n"
            
            printWriter?.apply {
                append(logMessage)
                flush()
            }
            
            outputStream?.fd?.sync()
        } catch (e: Exception) {
            // 静默处理异常
        }
    }
    
    fun close() {
        try {
            printWriter?.close()
            outputStream?.close()
        } catch (e: Exception) {
            // 静默处理异常
        }
    }
    
    fun getLogFiles(context: Context): List<File> {
        val logDir = File(context.getExternalFilesDir(null), "logs")
        return if (logDir.exists() && logDir.isDirectory) {
            logDir.listFiles()?.toList() ?: emptyList()
        } else {
            emptyList()
        }
    }
}
