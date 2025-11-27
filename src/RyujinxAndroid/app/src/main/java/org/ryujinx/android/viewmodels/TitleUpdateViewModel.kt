package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import android.os.Environment
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
        if (index <= 0) return

        data?.paths?.apply {
            val path = removeAt(index - 1)
            
            // 如果是URI格式，释放权限
            if (path.startsWith("content://")) {
                Uri.parse(path)?.apply {
                    storageHelper.storage.context.contentResolver.releasePersistableUriPermission(
                        this,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION
                    )
                }
            }
            
            pathsState?.clear()
            pathsState?.addAll(this)
            currentPaths = this
            saveChanges()
        }
    }

    fun add() {
        val callBack = storageHelper.onFileSelected

        storageHelper.onFileSelected = { requestCode, files ->
            run {
                storageHelper.onFileSelected = callBack
                if (requestCode == UpdateRequestCode) {
                    val file = files.firstOrNull()
                    file?.apply {
                        if (file.extension == "nsp") {
                            storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                                file.uri,
                                Intent.FLAG_GRANT_READ_URI_PERMISSION
                            )
                            
                            // 将URI转换为文件路径
                            val filePath = convertUriToFilePath(file.uri, storageHelper.storage.context)
                            if (filePath != null && !currentPaths.contains(filePath)) {
                                currentPaths.add(filePath)
                            }
                        }
                    }

                    refreshPaths()
                    saveChanges()
                }
            }
        }
        storageHelper.openFilePicker(UpdateRequestCode)
    }

    private fun convertUriToFilePath(uri: Uri, context: android.content.Context): String? {
        return try {
            // 如果是文件URI，直接返回路径
            if (uri.scheme == "file") {
                return uri.path
            }
            
            // 如果是content URI，尝试解析
            if (uri.scheme == "content") {
                val pathSegments = uri.pathSegments
                if (pathSegments.isNotEmpty() && pathSegments[0] == "document") {
                    val relativePath = Uri.decode(uri.lastPathSegment ?: "")
                    
                    // 尝试在常见目录中查找文件
                    val baseDirs = arrayOf(
                        Environment.getExternalStorageDirectory(),
                        context.getExternalFilesDir(null),
                        context.filesDir
                    )
                    
                    for (baseDir in baseDirs) {
                        if (baseDir != null) {
                            val potentialFile = File(baseDir, relativePath)
                            if (potentialFile.exists()) {
                                return potentialFile.absolutePath
                            }
                            
                            // 尝试在baseDir的子目录中查找
                            baseDir.listFiles()?.forEach { dir ->
                                val fileInDir = File(dir, relativePath)
                                if (fileInDir.exists()) {
                                    return fileInDir.absolutePath
                                }
                            }
                        }
                    }
                }
                
                // 如果无法找到文件，使用查询方式获取真实路径
                val cursor = context.contentResolver.query(uri, arrayOf("_data"), null, null, null)
                cursor?.use {
                    if (it.moveToFirst()) {
                        val columnIndex = it.getColumnIndex("_data")
                        if (columnIndex != -1) {
                            val filePath = it.getString(columnIndex)
                            if (filePath != null && File(filePath).exists()) {
                                return filePath
                            }
                        }
                    }
                }
            }
            
            // 如果所有方法都失败，返回null
            null
        } catch (e: Exception) {
            e.printStackTrace()
            null
        }
    }

    private fun refreshPaths() {
        data?.apply {
            val existingPaths = mutableListOf<String>()
            currentPaths.forEach { path ->
                // 检查文件是否存在
                val fileExists = if (path.startsWith("/")) {
                    // 文件路径
                    File(path).exists()
                } else {
                    // URI路径
                    val uri = Uri.parse(path)
                    val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                    file?.exists() == true
                }
                
                if (fileExists) {
                    existingPaths.add(path)
                }
            }

            if (!existingPaths.contains(selected)) {
                selected = ""
            }
            pathsState?.clear()
            pathsState?.addAll(existingPaths)
            paths = existingPaths
            currentPaths = existingPaths
            canClose?.apply {
                value = true
            }
        }
    }

    fun save(
        index: Int,
        openDialog: MutableState<Boolean>
    ) {
        data?.apply {
            this.selected = ""
            if (paths.isNotEmpty() && index > 0) {
                val ind = kotlin.math.max(index - 1, paths.count() - 1)
                this.selected = paths[ind]
            }
            
            saveChanges()
            openDialog.value = false
        }
    }

    fun setPaths(paths: SnapshotStateList<String>, canClose: MutableState<Boolean>) {
        pathsState = paths
        this.canClose = canClose
        data?.apply {
            pathsState?.clear()
            pathsState?.addAll(this.paths)
        }
    }
    
    fun saveChanges() {
        val metadata = data ?: TitleUpdateMetadata()
        val gson = Gson()
        
        File(basePath).mkdirs()
        
        // 确保只保存存在的文件路径
        val savedUpdates = mutableListOf<String>()
        currentPaths.forEach { path ->
            val fileExists = if (path.startsWith("/")) {
                File(path).exists()
            } else {
                val uri = Uri.parse(path)
                val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                file?.exists() == true
            }
            
            if (fileExists) {
                savedUpdates.add(path)
            }
        }
        metadata.paths = savedUpdates
        
        if (metadata.selected.isNotEmpty()) {
            val selectedExists = if (metadata.selected.startsWith("/")) {
                File(metadata.selected).exists()
            } else {
                val uri = Uri.parse(metadata.selected)
                val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                file?.exists() == true
            }
            
            if (!selectedExists) {
                metadata.selected = ""
            }
        }
        
        val json = gson.toJson(metadata)
        File("$basePath/$updateJsonName").writeText(json)
    }

    var data: TitleUpdateMetadata? = null
    private var jsonPath: String

    init {
        // 先初始化 storageHelper
        storageHelper = MainActivity.StorageHelper!!
        
        basePath = MainActivity.AppPath + "/games/" + titleId.toLowerCase(Locale.current)
        jsonPath = "${basePath}/${updateJsonName}"

        data = TitleUpdateMetadata()
        if (File(jsonPath).exists()) {
            val gson = Gson()
            data = gson.fromJson(File(jsonPath).readText(), TitleUpdateMetadata::class.java)
            
            // 将旧数据中的URI转换为文件路径
            data?.paths?.let { paths ->
                val convertedPaths = mutableListOf<String>()
                paths.forEach { path ->
                    if (path.startsWith("content://")) {
                        // 尝试将URI转换为文件路径
                        val filePath = convertUriToFilePath(Uri.parse(path), storageHelper.storage.context)
                        if (filePath != null) {
                            convertedPaths.add(filePath)
                        } else {
                            // 如果无法转换，保留原始URI
                            convertedPaths.add(path)
                        }
                    } else {
                        convertedPaths.add(path)
                    }
                }
                data?.paths = convertedPaths
                currentPaths = convertedPaths
            }
        } else {
            currentPaths = data?.paths ?: mutableListOf()
        }
        
        refreshPaths()

        File("$basePath/update").deleteRecursively()
    }
}

data class TitleUpdateMetadata(
    var selected: String = "",
    var paths: MutableList<String> = mutableListOf()
)
