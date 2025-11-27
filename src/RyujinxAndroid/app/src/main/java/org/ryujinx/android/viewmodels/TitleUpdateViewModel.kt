package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import androidx.compose.ui.text.toLowerCase
import androidx.core.net.toUri
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
            val str = updatesData.paths.removeAt(index - 1)

            currentPaths = ArrayList(updatesData.paths)

            pathsState?.clear()
            pathsState?.addAll(updatesData.paths)

            // 仅对URI格式释放权限
            if (str.startsWith("content://")) {
                str.toUri().let { uri ->
                    try {
                        storageHelper.storage.context.contentResolver.releasePersistableUriPermission(
                            uri,
                            Intent.FLAG_GRANT_READ_URI_PERMISSION
                        )
                    } catch (_: SecurityException) {
                    }
                }
            }

            saveChanges()

            canClose?.let {
                it.value = false
                it.value = true
            }
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
                        if (file.extension == "nsp" || file.extension == "xci") {
                            storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                                file.uri,
                                Intent.FLAG_GRANT_READ_URI_PERMISSION
                            )

                            processUpdateFile(file.uri, storageHelper.storage.context)
                        }
                    }

                    refreshPaths()
                    saveChanges()
                }
            }
        }
        storageHelper.openFilePicker(UpdateRequestCode)
    }

    // 新增方法：处理选中的多个文件
    fun addSelectedFiles(uris: List<Uri>, context: android.content.Context) {
        if (uris.isNotEmpty()) {
            for (uri in uris) {
                processUpdateFile(uri, context)
            }
            refreshPaths()
            saveChanges()
        }
    }

    private fun processUpdateFile(uri: Uri, context: android.content.Context) {
        try {
            // 获取文件扩展名
            val mimeType = context.contentResolver.getType(uri)
            val fileName = getFileNameFromUri(context, uri)
            val fileExtension = fileName?.substringAfterLast('.', "")?.lowercase()

            if (fileExtension == "nsp" || fileExtension == "xci") {
                // 获取持久化权限
                context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION
                )

                var filePath: String? = null
                var path = uri.pathSegments.joinToString("/")

                if (path.startsWith("document/")) {
                    val relativePath = Uri.decode(path.substring("document/".length))

                    if (relativePath.startsWith("root/")) {
                        val rootRelativePath = relativePath.substring("root/".length)
                        filePath = findExistingFilePath(rootRelativePath, context)
                    } else if (relativePath.startsWith("primary:")) {
                        val rootRelativePath = relativePath.substring("primary:".length)
                        filePath = findExistingFilePath(rootRelativePath, context)
                    }
                }

                // 如果无法通过路径解析，尝试使用文件名
                if (filePath == null) {
                    filePath = getFilePathFromUri(context, uri)
                }

                // 如果还是无法获取文件路径，使用URI字符串作为后备
                val finalPath = filePath ?: uri.toString()
                if (finalPath.isNotEmpty()) {
                    val isDuplicate = currentPaths.contains(finalPath) || data?.paths?.contains(finalPath) == true

                    if (!isDuplicate) {
                        currentPaths.add(finalPath)
                    }
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    // 辅助函数：查找存在的文件路径
    private fun findExistingFilePath(relativePath: String, context: android.content.Context): String? {
        val baseDirectories = listOf(
            context.filesDir,
            context.getExternalFilesDir(null),
            Environment.getExternalStorageDirectory()
        )

        for (baseDir in baseDirectories) {
            val potentialFile = File(baseDir, relativePath)
            if (potentialFile.exists()) {
                return potentialFile.absolutePath
            }
        }
        return null
    }

    private fun getFileNameFromUri(context: android.content.Context, uri: Uri): String? {
        return try {
            var result: String? = null
            if (uri.scheme == "content") {
                val cursor = context.contentResolver.query(uri, null, null, null, null)
                cursor?.use {
                    if (it.moveToFirst()) {
                        val displayNameIndex = it.getColumnIndex("_display_name")
                        if (displayNameIndex != -1) {
                            result = it.getString(displayNameIndex)
                        }
                    }
                }
            }
            if (result == null) {
                result = uri.path?.let { path ->
                    path.substringAfterLast('/')
                }
            }
            result
        } catch (e: Exception) {
            e.printStackTrace()
            null
        }
    }

    private fun getFilePathFromUri(context: android.content.Context, uri: Uri): String? {
        return try {
            var filePath: String? = null
            if (uri.scheme == "file") {
                filePath = uri.path
            } else if (uri.scheme == "content") {
                // 尝试从content URI获取实际文件路径
                val cursor = context.contentResolver.query(uri, arrayOf("_data"), null, null, null)
                cursor?.use {
                    if (it.moveToFirst()) {
                        val columnIndex = it.getColumnIndex("_data")
                        if (columnIndex != -1) {
                            filePath = it.getString(columnIndex)
                        }
                    }
                }
            }
            filePath
        } catch (e: Exception) {
            e.printStackTrace()
            null
        }
    }

    fun save(index: Int, openDialog: MutableState<Boolean>) {
        data?.apply {
            this.selected = ""
            if (paths.isNotEmpty() && index > 0) {
                val ind = index - 1
                this.selected = paths[ind]
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

    private fun refreshPaths() {
        data?.apply {
            val existingPaths = mutableListOf<String>()
            currentPaths.forEach { path ->
                // 检查路径是否存在
                val exists = if (path.startsWith("content://")) {
                    // URI路径 - 使用DocumentFile检查
                    val uri = Uri.parse(path)
                    val file = com.anggrayudi.storage.file.DocumentFileCompat.fromUri(storageHelper.storage.context, uri)
                    file?.exists() == true
                } else {
                    // 文件系统路径 - 使用File检查
                    File(path).exists()
                }
                
                if (exists) {
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

    fun saveChanges() {
        val metadata = data ?: TitleUpdateMetadata()
        val gson = Gson()

        File(basePath).mkdirs()

        val json = gson.toJson(metadata)
        File("$basePath/$updateJsonName").writeText(json)
    }

    var data: TitleUpdateMetadata? = null
    private var jsonPath: String

    init {
        basePath = MainActivity.AppPath + "/games/" + titleId.toLowerCase(Locale.current)
        jsonPath = "${basePath}/${updateJsonName}"

        data = TitleUpdateMetadata()
        if (File(jsonPath).exists()) {
            val gson = Gson()
            data = gson.fromJson(File(jsonPath).readText(), TitleUpdateMetadata::class.java)
        }
        currentPaths = data?.paths ?: mutableListOf()
        storageHelper = MainActivity.StorageHelper!!
        refreshPaths()

        File("$basePath/update").deleteRecursively()
    }
}

data class TitleUpdateMetadata(
    var selected: String = "",
    var paths: MutableList<String> = mutableListOf()
)
