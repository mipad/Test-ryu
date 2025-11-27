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
     * 获取文件的显示名称
     */
    private fun getDisplayName(path: String): String {
        return try {
            if (path.startsWith("content://")) {
                val documentFile = DocumentFile.fromSingleUri(storageHelper.storage.context, path.toUri())
                documentFile?.name ?: "Unknown"
            } else if (path.startsWith("file://")) {
                File(path.toUri().path ?: "").name ?: "Unknown"
            } else {
                File(path).name
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
                if (removedPath.startsWith("content://")) {
                    storageHelper.storage.context.contentResolver.releasePersistableUriPermission(
                        removedPath.toUri(),
                        Intent.FLAG_GRANT_READ_URI_PERMISSION
                    )
                }
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
            // 过滤掉不存在的文件
            val validPaths = currentPaths.filter { path ->
                try {
                    if (path.startsWith("content://")) {
                        val documentFile = DocumentFile.fromSingleUri(storageHelper.storage.context, path.toUri())
                        documentFile?.exists() == true
                    } else if (path.startsWith("file://")) {
                        File(path.toUri().path ?: "").exists()
                    } else {
                        File(path).exists()
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
            if (metadata.selected.isEmpty() || !validPaths.contains(metadata.selected)) {
                // 自动选择第一个可用路径
                metadata.selected = validPaths.firstOrNull() ?: ""
            }
            
            // 更新UI状态
            pathsState?.clear()
            pathsState?.addAll(validPaths)
            
            android.util.Log.d("Ryujinx", "Refresh paths for $titleId: ${validPaths.size} paths, selected: ${getDisplayName(metadata.selected)}")
            canClose?.value = true
        }
    }

    fun save(
        index: Int,
        openDialog: MutableState<Boolean>
    ) {
        data?.let { metadata ->
            if (metadata.paths.isNotEmpty() && index > 0) {
                val actualIndex = (index - 1).coerceAtMost(metadata.paths.size - 1)
                metadata.selected = metadata.paths[actualIndex]
                android.util.Log.d("Ryujinx", "User selected update: ${getDisplayName(metadata.selected)}")
            } else {
                metadata.selected = ""
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
                gson.fromJson(File(jsonPath).readText(), TitleUpdateMetadata::class.java)
            } catch (e: Exception) {
                TitleUpdateMetadata()
            }
        } else {
            TitleUpdateMetadata()
        }
        
        currentPaths = data?.paths?.toMutableList() ?: mutableListOf()
        storageHelper = MainActivity.StorageHelper!!
        
        refreshPaths()
    }
}

data class TitleUpdateMetadata(
    var selected: String = "",
    var paths: MutableList<String> = mutableListOf()
)