package org.ryujinx.android.viewmodels

import android.content.Context
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.ryujinx.android.RyujinxNative
import java.io.File
import java.io.FileOutputStream
import java.text.SimpleDateFormat
import java.util.*

class SaveDataViewModel : ViewModel() {

    // 存档信息状态
    var saveDataInfo = mutableStateOf<SaveDataInfo?>(null)
        private set

    // 操作状态
    var operationInProgress = mutableStateOf(false)
        private set
    var operationMessage = mutableStateOf("")
        private set
    var operationSuccess = mutableStateOf(false)
        private set
    var showOperationResult = mutableStateOf(false)
        private set

    // 当前操作的标题ID和游戏名称
    var currentTitleId = mutableStateOf("")
        private set
    var currentGameName = mutableStateOf("")
        private set

    // 应用上下文（用于获取正确的文件路径）
    private var appContext: Context? = null

    /**
     * 初始化存档数据
     */
    fun initialize(titleId: String, gameName: String, context: Context) {
        currentTitleId.value = titleId
        currentGameName.value = gameName
        appContext = context.applicationContext
        loadSaveDataInfo()
    }

    /**
     * 加载存档信息
     */
    fun loadSaveDataInfo() {
        operationInProgress.value = true
        operationMessage.value = "Loading save data info..."

        Thread {
            try {
                // 强制刷新存档列表
                RyujinxNative.refreshSaveData()
                
                val exists = RyujinxNative.saveDataExists(currentTitleId.value)
                if (exists) {
                    val saveId = RyujinxNative.getSaveIdByTitleId(currentTitleId.value)
                    if (!saveId.isNullOrEmpty()) {
                        // 获取详细的存档信息
                        val info = getDetailedSaveDataInfo(saveId)
                        saveDataInfo.value = info
                    } else {
                        saveDataInfo.value = null
                    }
                } else {
                    saveDataInfo.value = null
                }
                
                operationInProgress.value = false
            } catch (e: Exception) {
                operationInProgress.value = false
                operationMessage.value = "Error loading save data: ${e.message}"
                operationSuccess.value = false
                showOperationResult.value = true
            }
        }.start()
    }

    /**
     * 刷新存档信息（静默模式，不显示加载消息）
     */
    private fun refreshSaveDataInfoSilent() {
        Thread {
            try {
                RyujinxNative.refreshSaveData()
                
                val exists = RyujinxNative.saveDataExists(currentTitleId.value)
                if (exists) {
                    val saveId = RyujinxNative.getSaveIdByTitleId(currentTitleId.value)
                    if (!saveId.isNullOrEmpty()) {
                        val info = getDetailedSaveDataInfo(saveId)
                        saveDataInfo.value = info
                    } else {
                        saveDataInfo.value = null
                    }
                } else {
                    saveDataInfo.value = null
                }
            } catch (e: Exception) {
                // 静默失败，不显示错误信息
                e.printStackTrace()
            }
        }.start()
    }

    /**
     * 获取详细的存档信息
     */
    private fun getDetailedSaveDataInfo(saveId: String): SaveDataInfo {
        try {
            // 获取最后修改时间
            val lastModified = getSaveDataLastModified(saveId)
            
            return SaveDataInfo(
                saveId = saveId,
                titleId = currentTitleId.value,
                titleName = currentGameName.value,
                lastModified = lastModified
            )
        } catch (e: Exception) {
            // 返回基本信息
            return SaveDataInfo(
                saveId = saveId,
                titleId = currentTitleId.value,
                titleName = currentGameName.value,
                lastModified = Date()
            )
        }
    }

    /**
     * 获取存档最后修改时间
     */
    private fun getSaveDataLastModified(saveId: String): Date {
        try {
            val saveBasePath = File(getRyujinxBasePath(), "bis/user/save")
            val saveDir = File(saveBasePath, saveId)
            
            if (saveDir.exists()) {
                // 优先检查0和1文件夹的最后修改时间
                var latestModified = saveDir.lastModified()
                
                val folder0 = File(saveDir, "0")
                if (folder0.exists() && folder0.lastModified() > latestModified) {
                    latestModified = folder0.lastModified()
                }
                
                val folder1 = File(saveDir, "1")
                if (folder1.exists() && folder1.lastModified() > latestModified) {
                    latestModified = folder1.lastModified()
                }
                
                return Date(latestModified)
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
        return Date()
    }

    /**
     * 获取Ryujinx基础路径 - 动态获取应用文件目录
     */
    private fun getRyujinxBasePath(): File {
        return appContext?.filesDir?.parentFile ?: File("/storage/emulated/0/Android/data/org.ryujinx.android/files")
    }

    /**
     * 导出存档到指定URI - 改进版本：先导出到应用目录，然后复制到URI
     */
    fun exportSaveDataToUri(uri: android.net.Uri, context: Context) {
        operationInProgress.value = true
        operationMessage.value = "Exporting save data..."

        Thread {
            try {
                // 创建导出文件名
                val fileName = "${currentGameName.value.replace("[^a-zA-Z0-9]".toRegex(), "_")}_save_${System.currentTimeMillis()}.zip"
                val exportDir = File(context.getExternalFilesDir(null), "exports")
                exportDir.mkdirs()
                val exportPath = File(exportDir, fileName).absolutePath

                // 第一步：先导出到应用目录（这是已经验证能正常工作的方式）
                val success = RyujinxNative.exportSaveData(currentTitleId.value, exportPath)

                if (success) {
                    // 检查导出的文件是否存在且大小不为0
                    val exportedFile = File(exportPath)
                    if (exportedFile.exists() && exportedFile.length() > 0) {
                        // 第二步：将成功导出的文件复制到用户选择的URI位置
                        context.contentResolver.openOutputStream(uri)?.use { outputStream ->
                            exportedFile.inputStream().use { inputStream ->
                                // 使用缓冲区复制文件
                                val buffer = ByteArray(8192)
                                var bytesRead: Int
                                while (inputStream.read(buffer).also { bytesRead = it } != -1) {
                                    outputStream.write(buffer, 0, bytesRead)
                                }
                                outputStream.flush()
                            }
                        }
                        
                        // 第三步：删除应用目录中的临时文件
                        exportedFile.delete()
                        
                        operationInProgress.value = false
                        operationSuccess.value = true
                        operationMessage.value = "Save data exported successfully"
                    } else {
                        // 导出的文件有问题（不存在或大小为0）
                        operationInProgress.value = false
                        operationSuccess.value = false
                        operationMessage.value = "Failed to export save data: exported file is empty or missing"
                        // 清理可能存在的空文件
                        if (exportedFile.exists()) {
                            exportedFile.delete()
                        }
                    }
                } else {
                    operationInProgress.value = false
                    operationSuccess.value = false
                    operationMessage.value = "Failed to export save data"
                }
                
                showOperationResult.value = true
                
            } catch (e: Exception) {
                operationInProgress.value = false
                operationSuccess.value = false
                operationMessage.value = "Error exporting save data: ${e.message}"
                showOperationResult.value = true
                
                // 清理可能存在的临时文件
                try {
                    val fileName = "${currentGameName.value.replace("[^a-zA-Z0-9]".toRegex(), "_")}_save_${System.currentTimeMillis()}.zip"
                    val exportDir = File(context.getExternalFilesDir(null), "exports")
                    val exportPath = File(exportDir, fileName).absolutePath
                    File(exportPath).delete()
                } catch (cleanupEx: Exception) {
                    // 忽略清理错误
                }
            }
        }.start()
    }

    /**
     * 导出存档到应用目录（兼容旧方法）
     */
    fun exportSaveData(context: Context) {
        operationInProgress.value = true
        operationMessage.value = "Exporting save data..."

        Thread {
            try {
                // 创建导出文件名
                val fileName = "${currentGameName.value.replace("[^a-zA-Z0-9]".toRegex(), "_")}_save_${System.currentTimeMillis()}.zip"
                val exportDir = File(context.getExternalFilesDir(null), "exports")
                exportDir.mkdirs()
                val exportPath = File(exportDir, fileName).absolutePath

                val success = RyujinxNative.exportSaveData(currentTitleId.value, exportPath)

                operationInProgress.value = false
                operationSuccess.value = success
                operationMessage.value = if (success) 
                    "Save data exported to: ${File(exportPath).name}" 
                else 
                    "Failed to export save data"
                showOperationResult.value = true
                
            } catch (e: Exception) {
                operationInProgress.value = false
                operationSuccess.value = false
                operationMessage.value = "Error exporting save data: ${e.message}"
                showOperationResult.value = true
            }
        }.start()
    }

    /**
     * 导入存档
     */
    fun importSaveData(zipFilePath: String) {
        operationInProgress.value = true
        operationMessage.value = "Importing save data..."

        Thread {
            try {
                val success = RyujinxNative.importSaveData(currentTitleId.value, zipFilePath)

                operationInProgress.value = false
                operationSuccess.value = success
                operationMessage.value = if (success) 
                    "Save data imported successfully" 
                else 
                    "Failed to import save data"
                showOperationResult.value = true

                // 导入成功后静默刷新存档信息
                if (success) {
                    refreshSaveDataInfoSilent()
                }
            } catch (e: Exception) {
                operationInProgress.value = false
                operationSuccess.value = false
                operationMessage.value = "Error importing save data: ${e.message}"
                showOperationResult.value = true
            }
        }.start()
    }

    /**
     * 从URI导入存档
     */
    fun importSaveDataFromUri(uri: android.net.Uri, context: Context) {
        operationInProgress.value = true
        operationMessage.value = "Importing save data..."

        Thread {
            try {
                // 将选中的文件复制到临时位置
                val inputStream = context.contentResolver.openInputStream(uri)
                val tempFile = File.createTempFile("save_import", ".zip", context.cacheDir)
                FileOutputStream(tempFile).use { output ->
                    inputStream?.copyTo(output)
                }
                
                // 调用导入方法
                val success = RyujinxNative.importSaveData(currentTitleId.value, tempFile.absolutePath)
                
                // 删除临时文件
                tempFile.delete()
                
                operationInProgress.value = false
                operationSuccess.value = success
                operationMessage.value = if (success) 
                    "Save data imported successfully" 
                else 
                    "Failed to import save data"
                showOperationResult.value = true
                
                // 导入成功后静默刷新存档信息
                if (success) {
                    refreshSaveDataInfoSilent()
                }
            } catch (e: Exception) {
                operationInProgress.value = false
                operationSuccess.value = false
                operationMessage.value = "Error importing save data: ${e.message}"
                showOperationResult.value = true
            }
        }.start()
    }

    /**
     * 删除存档文件夹（完全删除）
     */
    fun deleteSaveData() {
        operationInProgress.value = true
        operationMessage.value = "Deleting save data folder..."

        Thread {
            try {
                val success = RyujinxNative.deleteSaveData(currentTitleId.value)

                operationInProgress.value = false
                operationSuccess.value = success
                operationMessage.value = if (success) 
                    "Save data folder deleted successfully" 
                else 
                    "Failed to delete save data folder"
                showOperationResult.value = true

                if (success) {
                    // 更新UI状态
                    saveDataInfo.value = null
                }
            } catch (e: Exception) {
                operationInProgress.value = false
                operationSuccess.value = false
                operationMessage.value = "Error deleting save data folder: ${e.message}"
                showOperationResult.value = true
            }
        }.start()
    }

    /**
     * 删除存档文件（只删除0和1文件夹中的内容）
     */
    fun deleteSaveFiles() {
        operationInProgress.value = true
        operationMessage.value = "Deleting save files..."

        Thread {
            try {
                val success = RyujinxNative.deleteSaveFiles(currentTitleId.value)

                operationInProgress.value = false
                operationSuccess.value = success
                operationMessage.value = if (success) 
                    "Save files deleted successfully" 
                else 
                    "Failed to delete save files"
                showOperationResult.value = true

                // 删除文件后静默刷新存档信息
                if (success) {
                    refreshSaveDataInfoSilent()
                }
            } catch (e: Exception) {
                operationInProgress.value = false
                operationSuccess.value = false
                operationMessage.value = "Error deleting save files: ${e.message}"
                showOperationResult.value = true
            }
        }.start()
    }

    /**
     * 重置操作结果
     */
    fun resetOperationResult() {
        showOperationResult.value = false
        operationMessage.value = ""
    }

    /**
     * 检查是否有存档存在
     */
    fun hasSaveData(): Boolean {
        return saveDataInfo.value != null
    }

    /**
     * 获取存档ID
     */
    fun getSaveId(): String {
        return saveDataInfo.value?.saveId ?: ""
    }
    
    /**
     * 强制刷新存档信息（公开方法，用于手动刷新）
     */
    fun refreshSaveData() {
        loadSaveDataInfo()
    }
}

/**
 * 存档信息数据类 
 */
data class SaveDataInfo(
    val saveId: String,
    val titleId: String,
    val titleName: String,
    val lastModified: Date
)

/**
 * 工具函数 - 格式化日期
 */
fun formatDate(date: Date): String {
    val formatter = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault())
    return formatter.format(date)
}
