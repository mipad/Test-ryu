package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import androidx.compose.ui.text.toLowerCase
import androidx.core.net.toUri
import androidx.documentfile.provider.DocumentFile
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.google.gson.Gson
import org.ryujinx.android.MainActivity
import java.io.File

class TitleUpdateViewModel(val titleId: String) {
    private var canClose: MutableState<Boolean>? = null
    private var basePath: String
    private var updateJsonName = "updates.json"
    private var storageHelper: SimpleStorageHelper
    private var currentPaths: MutableList<String> = mutableListOf()
    private var pathsState: SnapshotStateList<String>? = null

    companion object {
        const val UpdateRequestCode = 1002
    }

    fun remove(index: Int) {
        if (index <= 0) {
            return
        }

        val updatesData = data
        if (updatesData != null && updatesData.paths.isNotEmpty() && index - 1 < updatesData.paths.size) {
            val removedPath = updatesData.paths.removeAt(index - 1)
            currentPaths.remove(removedPath)

            // 如果删除的是选中的路径，清空选中状态
            if (updatesData.selected == removedPath) {
                updatesData.selected = ""
            }

            pathsState?.clear()
            pathsState?.addAll(updatesData.paths)

            // 尝试释放URI权限
            try {
                storageHelper.storage.context.contentResolver.releasePersistableUriPermission(
                    removedPath.toUri(),
                    Intent.FLAG_GRANT_READ_URI_PERMISSION
                )
            } catch (e: SecurityException) {
                // 忽略权限释放失败
            }

            saveChanges()

            canClose?.value = true
        }
    }

    fun add() {
        val originalCallback = storageHelper.onFileSelected

        storageHelper.onFileSelected = { requestCode, files ->
            storageHelper.onFileSelected = originalCallback
            
            if (requestCode == UpdateRequestCode) {
                files.firstOrNull()?.let { file ->
                    if (file.extension == "nsp" || file.extension == "xci") {
                        // 获取URI字符串表示
                        val uriString = file.uri.toString()
                        
                        // 检查是否已存在
                        val isDuplicate = currentPaths.any { path ->
                            path.equals(uriString, ignoreCase = true)
                        }
                        
                        if (!isDuplicate) {
                            // 获取持久化读取权限
                            try {
                                storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                                    file.uri,
                                    Intent.FLAG_GRANT_READ_URI_PERMISSION
                                )
                                
                                currentPaths.add(uriString)
                                refreshPaths()
                                saveChanges()
                            } catch (e: SecurityException) {
                                // 处理权限获取失败
                                e.printStackTrace()
                            }
                        }
                    }
                }
            }
        }
        
        storageHelper.openFilePicker(UpdateRequestCode)
    }

    private fun refreshPaths() {
        data?.let { metadata ->
            // 过滤掉不存在的文件
            val validPaths = currentPaths.filter { path ->
                try {
                    val uri = path.toUri()
                    val documentFile = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                    documentFile?.exists() == true
                } catch (e: Exception) {
                    false
                }
            }
            
            // 更新当前路径列表
            currentPaths.clear()
            currentPaths.addAll(validPaths)
            
            // 更新metadata
            metadata.paths.clear()
            metadata.paths.addAll(validPaths)
            
            // 如果选中的路径不存在，清空选中状态
            if (metadata.selected.isNotEmpty() && !validPaths.contains(metadata.selected)) {
                metadata.selected = ""
            }
            
            // 更新UI状态
            pathsState?.clear()
            pathsState?.addAll(validPaths)
            
            canClose?.value = true
        }
    }

    fun save(
        index: Int,
        openDialog: MutableState<Boolean>
    ) {
        data?.let { metadata ->
            metadata.selected = ""
            if (metadata.paths.isNotEmpty() && index > 0) {
                val actualIndex = (index - 1).coerceAtMost(metadata.paths.size - 1)
                val selectedPath = metadata.paths[actualIndex]
                
                // 验证选中的路径是否仍然有效
                try {
                    val uri = selectedPath.toUri()
                    val documentFile = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                    if (documentFile?.exists() == true) {
                        metadata.selected = selectedPath
                    }
                } catch (e: Exception) {
                    // 路径无效，保持为空
                }
            }
            
            saveChanges()
            openDialog.value = false
        }
    }

    fun setPaths(paths: SnapshotStateList<String>, canClose: MutableState<Boolean>) {
        pathsState = paths
        this.canClose = canClose
        refreshPaths()
    }

    fun saveChanges() {
        data?.let { metadata ->
            val gson = Gson()
            File(basePath).mkdirs()
            
            val json = gson.toJson(metadata)
            File("$basePath/$updateJsonName").writeText(json)
        }
    }

    var data: TitleUpdateMetadata? = null
    private var jsonPath: String

    init {
        basePath = "${MainActivity.AppPath}/games/${titleId.toLowerCase(Locale.current)}"
        jsonPath = "$basePath/$updateJsonName"

        // 加载现有数据
        data = if (File(jsonPath).exists()) {
            try {
                val gson = Gson()
                gson.fromJson(File(jsonPath).readText(), TitleUpdateMetadata::class.java)
            } catch (e: Exception) {
                TitleUpdateMetadata()
            }
        } else {
            TitleUpdateMetadata()
        }
        
        currentPaths = data?.paths?.toMutableList() ?: mutableListOf()
        storageHelper = MainActivity.StorageHelper!!
        
        // 初始刷新和验证
        refreshPaths()
        
        // 清理旧的更新目录
        try {
            File("$basePath/update").deleteRecursively()
        } catch (e: Exception) {
            // 忽略删除失败
        }
    }
}

data class TitleUpdateMetadata(
    var selected: String = "",
    var paths: MutableList<String> = mutableListOf()
)
