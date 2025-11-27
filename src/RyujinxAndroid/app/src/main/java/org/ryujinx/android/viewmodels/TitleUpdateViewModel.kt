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

    /**
     * 确保路径格式统一为 URI 格式
     */
    private fun ensureUriPath(path: String): String {
        return if (path.startsWith("content://") || path.startsWith("file://")) {
            path // 已经是 URI 路径
        } else {
            // 文件路径转换为 URI 路径
            Uri.fromFile(File(path)).toString()
        }
    }

    /**
     * 获取文件的显示名称
     */
    private fun getDisplayName(path: String): String {
        return try {
            if (path.startsWith("content://")) {
                val documentFile = DocumentFile.fromSingleUri(storageHelper.storage.context, path.toUri())
                documentFile?.name ?: File(path.toUri().path ?: "").name ?: "Unknown"
            } else {
                val file = File(path.toUri().path ?: path)
                file.name
            }
        } catch (e: Exception) {
            "Unknown"
        }
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
                                android.util.Log.d("Ryujinx", "Manually added update: ${file.name}")
                            } catch (e: SecurityException) {
                                // 处理权限获取失败
                                android.util.Log.e("Ryujinx", "Failed to get permission for update: ${file.name}", e)
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
            // 过滤掉不存在的文件并统一路径格式
            val validPaths = currentPaths.map { ensureUriPath(it) }.filter { path ->
                try {
                    if (path.startsWith("content://")) {
                        val documentFile = DocumentFile.fromSingleUri(storageHelper.storage.context, path.toUri())
                        documentFile?.exists() == true
                    } else {
                        val file = File(path.toUri().path ?: path)
                        file.exists()
                    }
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
                android.util.Log.d("Ryujinx", "Cleared invalid selected update")
            }
            
            // 如果没有选中项但有可用路径，自动选择第一个
            if (metadata.selected.isEmpty() && validPaths.isNotEmpty()) {
                metadata.selected = validPaths.first()
                android.util.Log.d("Ryujinx", "Auto-selected first update: ${getDisplayName(metadata.selected)}")
            }
            
            // 更新UI状态
            pathsState?.clear()
            pathsState?.addAll(validPaths)
            
            // 记录当前状态
            android.util.Log.d("Ryujinx", "Refresh paths: ${validPaths.size} valid paths, selected: ${getDisplayName(metadata.selected)}")
            
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
                    val isValid = if (selectedPath.startsWith("content://")) {
                        val documentFile = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                        documentFile?.exists() == true
                    } else {
                        File(uri.path ?: selectedPath).exists()
                    }
                    
                    if (isValid) {
                        metadata.selected = selectedPath
                        android.util.Log.d("Ryujinx", "Saved selected update: ${getDisplayName(selectedPath)}")
                    }
                } catch (e: Exception) {
                    android.util.Log.e("Ryujinx", "Error validating selected path", e)
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
            
            android.util.Log.d("Ryujinx", "Saved updates for $titleId: ${metadata.paths.size} paths, selected: ${getDisplayName(metadata.selected)}")
        }
    }

    var data: TitleUpdateMetadata? = null
    private var jsonPath: String

    init {
        basePath = "${MainActivity.AppPath}/games/${titleId.toLowerCase(Locale.current)}"
        jsonPath = "${basePath}/${updateJsonName}"

        // 加载现有数据
        data = if (File(jsonPath).exists()) {
            try {
                val gson = Gson()
                val loadedData = gson.fromJson(File(jsonPath).readText(), TitleUpdateMetadata::class.java)
                android.util.Log.d("Ryujinx", "Loaded updates for $titleId: ${loadedData.paths.size} paths")
                loadedData
            } catch (e: Exception) {
                android.util.Log.e("Ryujinx", "Error loading updates for $titleId", e)
                TitleUpdateMetadata()
            }
        } else {
            TitleUpdateMetadata()
        }
        
        currentPaths = data?.paths?.toMutableList() ?: mutableListOf()
        storageHelper = MainActivity.StorageHelper!!
        
        // 初始刷新和验证
        refreshPaths()
        
        android.util.Log.d("Ryujinx", "Initialized TitleUpdateViewModel for $titleId with ${currentPaths.size} paths")
    }
}

data class TitleUpdateMetadata(
    var selected: String = "",
    var paths: MutableList<String> = mutableListOf()
)