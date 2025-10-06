package org.ryujinx.android.viewmodels

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

    /**
     * 初始化存档数据
     */
    fun initialize(titleId: String, gameName: String) {
        currentTitleId.value = titleId
        currentGameName.value = gameName
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
     * 获取详细的存档信息
     */
    private fun getDetailedSaveDataInfo(saveId: String): SaveDataInfo {
        try {
            // 计算实际存档大小 - 只计算0文件夹
            val size = calculateSaveDataSize(saveId)
            
            // 获取最后修改时间
            val lastModified = getSaveDataLastModified(saveId)
            
            return SaveDataInfo(
                saveId = saveId,
                titleId = currentTitleId.value,
                titleName = currentGameName.value,
                lastModified = lastModified,
                size = size
            )
        } catch (e: Exception) {
            // 返回基本信息
            return SaveDataInfo(
                saveId = saveId,
                titleId = currentTitleId.value,
                titleName = currentGameName.value,
                lastModified = Date(),
                size = 0L
            )
        }
    }

    /**
     * 计算存档数据大小 - 只计算0文件夹
     */
    private fun calculateSaveDataSize(saveId: String): Long {
        try {
            val saveBasePath = File(getRyujinxBasePath(), "bis/user/save")
            val saveDir = File(saveBasePath, saveId)
            
            if (saveDir.exists() && saveDir.isDirectory) {
                // 只计算0文件夹的大小
                val folder0 = File(saveDir, "0")
                if (folder0.exists() && folder0.isDirectory) {
                    return calculateDirectorySize(folder0)
                }
            }
        } catch (e: Exception) {
        }
        return 0L
    }

    /**
     * 获取存档最后修改时间
     */
    private fun getSaveDataLastModified(saveId: String): Date {
        try {
            val saveBasePath = File(getRyujinxBasePath(), "bis/user/save")
            val saveDir = File(saveBasePath, saveId)
            
            if (saveDir.exists()) {
                return Date(saveDir.lastModified())
            }
        } catch (e: Exception) {
        }
        return Date()
    }

    /**
     * 递归计算目录大小
     */
    private fun calculateDirectorySize(directory: File): Long {
        var size = 0L
        try {
            val files = directory.listFiles()
            if (files != null) {
                for (file in files) {
                    if (file.isFile) {
                        size += file.length()
                    } else if (file.isDirectory) {
                        size += calculateDirectorySize(file)
                    }
                }
            }
        } catch (e: Exception) {
        }
        return size
    }

    /**
     * 获取Ryujinx基础路径
     */
    private fun getRyujinxBasePath(): File {
        // 这里应该返回Ryujinx的基础数据目录
        // 在实际实现中，这应该从应用程序的上下文中获取
        return File("/storage/emulated/0/Android/data/org.ryujinx.android/files")
    }

    /**
     * 导出存档
     */
    fun exportSaveData(context: android.content.Context) {
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

                // 导入成功后重新加载存档信息
                if (success) {
                    loadSaveDataInfo()
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
    fun importSaveDataFromUri(uri: android.net.Uri, context: android.content.Context) {
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
                
                // 导入成功后重新加载存档信息
                if (success) {
                    loadSaveDataInfo()
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
     * 删除存档
     */
    fun deleteSaveData() {
        operationInProgress.value = true
        operationMessage.value = "Deleting save data..."

        Thread {
            try {
                val success = RyujinxNative.deleteSaveData(currentTitleId.value)

                operationInProgress.value = false
                operationSuccess.value = success
                operationMessage.value = if (success) 
                    "Save data deleted successfully" 
                else 
                    "Failed to delete save data"
                showOperationResult.value = true

                if (success) {
                    // 更新UI状态
                    saveDataInfo.value = null
                }
            } catch (e: Exception) {
                operationInProgress.value = false
                operationSuccess.value = false
                operationMessage.value = "Error deleting save data: ${e.message}"
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
     * 强制刷新存档信息
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
    val lastModified: Date,
    val size: Long
)

/**
 * 工具函数 - 格式化日期
 */
fun formatDate(date: Date): String {
    val formatter = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault())
    return formatter.format(date)
}

/**
 * 工具函数 - 格式化文件大小
 */
fun formatFileSize(size: Long): String {
    return when {
        size < 1024 -> "$size B"
        size < 1024 * 1024 -> "${size / 1024} KB"
        size < 1024 * 1024 * 1024 -> "${size / (1024 * 1024)} MB"
        else -> "${size / (1024 * 1024 * 1024)} GB"
    }
}
