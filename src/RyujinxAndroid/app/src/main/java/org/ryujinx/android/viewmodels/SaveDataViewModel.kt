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
                val exists = RyujinxNative.saveDataExists(currentTitleId.value)
                if (exists) {
                    val saveId = RyujinxNative.getSaveIdByTitleId(currentTitleId.value)
                    if (!saveId.isNullOrEmpty()) {
                        // 这里可以加载更详细的存档信息，比如大小和修改时间
                        val info = SaveDataInfo(
                            saveId = saveId,
                            titleId = currentTitleId.value,
                            titleName = currentGameName.value,
                            lastModified = Date(),
                            size = calculateSaveDataSize(saveId) // 计算实际大小
                        )
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
     * 计算存档数据大小
     */
    private fun calculateSaveDataSize(saveId: String): Long {
        // 这里可以实现计算存档文件夹大小的逻辑
        // 暂时返回0，实际使用时可以计算真实大小
        return 0L
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
                    "Save data exported to: $exportPath" 
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
                val tempFile = File.createTempFile("save_import", ".zip")
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
        else -> "${size / (1024 * 1024)} MB"
    }
}
