package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.documentfile.provider.DocumentFile
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.google.gson.Gson
import org.ryujinx.android.MainActivity
import java.io.File
import java.util.Locale
import kotlin.math.max

class TitleUpdateViewModel(val titleId: String) {
    private var canClose: MutableState<Boolean>? = null
    private var basePath: String
    private var updateJsonName = "updates.json"
    private var storageHelper: SimpleStorageHelper
    private var currentPaths: MutableList<String> = mutableListOf()
    private var pathsState: SnapshotStateList<String>? = null

    companion object {
        const val UpdateRequestCode = 1002
        const val UpdateFolderRequestCode = 1003
    }

    fun remove(index: Int) {
        if (index <= 0)
            return

        data?.paths?.apply {
            val str = removeAt(index - 1)
            Uri.parse(str)?.apply {
                try {
                    storageHelper.storage.context.contentResolver.releasePersistableUriPermission(
                        this,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION
                    )
                } catch (e: SecurityException) {
                    e.printStackTrace()
                }
            }
            pathsState?.clear()
            pathsState?.addAll(this)
            currentPaths = this
            saveChanges()
        }
    }

    // 原有的单文件添加方法
    fun add() {
        val callBack = storageHelper.onFileSelected

        storageHelper.onFileSelected = { requestCode, files ->
            run {
                storageHelper.onFileSelected = callBack
                if (requestCode == UpdateRequestCode) {
                    addSelectedFiles(files.map { it.uri })
                }
            }
        }
        storageHelper.openFilePicker(UpdateRequestCode)
    }

    // 多文件选择方法
    fun addSelectedFiles(uris: List<Uri>) {
        if (uris.isNotEmpty()) {
            var addedCount = 0
            for (uri in uris) {
                try {
                    val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                    file?.apply {
                        if (isUpdateFile(this)) {
                            // 获取持久化权限
                            storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                                uri,
                                Intent.FLAG_GRANT_READ_URI_PERMISSION
                            )
                            
                            val uriString = uri.toString()
                            val isDuplicate = currentPaths.any { it == uriString }

                            if (!isDuplicate) {
                                currentPaths.add(uriString)
                                addedCount++
                            }
                        }
                    }
                } catch (e: Exception) {
                    e.printStackTrace()
                }
            }
            
            if (addedCount > 0) {
                refreshPaths()
                saveChanges()
            }
        }
    }

    // 文件夹选择方法
    fun addFolder() {
        val callBack = storageHelper.onFolderSelected

        storageHelper.onFolderSelected = { requestCode, folder ->
            run {
                storageHelper.onFolderSelected = callBack
                if (requestCode == UpdateFolderRequestCode) {
                    processFolder(folder)
                }
            }
        }
        storageHelper.openFolderPicker(UpdateFolderRequestCode)
    }

    // 处理文件夹中的所有更新文件
    private fun processFolder(folder: DocumentFile) {
        try {
            if (!folder.exists() || !folder.isDirectory) {
                return
            }
            
            // 递归扫描文件夹
            val foundFiles = scanFolderForUpdateFiles(folder)
            
            if (foundFiles > 0) {
                refreshPaths()
                saveChanges()
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    // 递归扫描更新文件 - 返回找到的文件数量
    private fun scanFolderForUpdateFiles(folder: DocumentFile): Int {
        var foundCount = 0
        
        try {
            if (!folder.exists() || !folder.isDirectory) {
                return 0
            }

            val files = folder.listFiles()
            for (file in files) {
                if (file.isDirectory) {
                    // 递归扫描子文件夹
                    foundCount += scanFolderForUpdateFiles(file)
                } else if (file.isFile && isUpdateFile(file)) {
                    // 处理更新文件
                    try {
                        val uri = file.uri
                        // 获取持久化权限
                        storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                            uri,
                            Intent.FLAG_GRANT_READ_URI_PERMISSION
                        )
                        
                        val uriString = uri.toString()
                        val isDuplicate = currentPaths.any { it == uriString }
                        if (!isDuplicate) {
                            currentPaths.add(uriString)
                            foundCount++
                        }
                    } catch (e: Exception) {
                        e.printStackTrace()
                    }
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
        
        return foundCount
    }

    // 检查是否为更新文件
    private fun isUpdateFile(file: DocumentFile): Boolean {
        val extension = file.extension?.lowercase(Locale.getDefault())
        return (extension == "nsp" || extension == "xci") && file.exists() && file.canRead()
    }

    private fun refreshPaths() {
        // 先清理无效的路径
        val validPaths = mutableListOf<String>()
        currentPaths.forEach { path ->
            try {
                val uri = Uri.parse(path)
                val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                if (file?.exists() == true && file.canRead()) {
                    validPaths.add(path)
                }
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
        
        // 更新当前路径
        currentPaths = validPaths
        
        data?.apply {
            val existingPaths = mutableListOf<String>()
            currentPaths.forEach {
                existingPaths.add(it)
            }

            if (!existingPaths.contains(selected)) {
                selected = ""
            }
            pathsState?.clear()
            pathsState?.addAll(existingPaths)
            paths = existingPaths
            canClose?.apply {
                value = true
            }
        }
    }

    // 保存更改的方法
     fun saveChanges() {
        val metadata = data ?: TitleUpdateMetadata()
        val gson = Gson()
        File(basePath).mkdirs()

        val savedUpdates = mutableListOf<String>()
        currentPaths.forEach {
            savedUpdates.add(it)
        }
        metadata.paths = savedUpdates

        if (metadata.selected.isNotEmpty() && !currentPaths.contains(metadata.selected)) {
            metadata.selected = ""
        }

        val json = gson.toJson(metadata)
        File("$basePath/$updateJsonName").writeText(json)
        
        // 更新data引用
        data = metadata
    }

    fun save(
        index: Int,
        openDialog: MutableState<Boolean>
    ) {
        data?.apply {
            this.selected = ""
            if (paths.isNotEmpty() && index > 0) {
                val ind = max(index - 1, paths.count() - 1)
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

    var data: TitleUpdateMetadata? = null
    private var jsonPath: String

    init {
        basePath = MainActivity.AppPath + "/games/" + titleId.lowercase(Locale.getDefault())
        jsonPath = "${basePath}/${updateJsonName}"

        data = TitleUpdateMetadata()
        if (File(jsonPath).exists()) {
            try {
                val gson = Gson()
                data = gson.fromJson(File(jsonPath).readText(), TitleUpdateMetadata::class.java)
            } catch (e: Exception) {
                e.printStackTrace()
                data = TitleUpdateMetadata()
            }
        }
        currentPaths = data?.paths ?: mutableListOf()
        storageHelper = MainActivity.StorageHelper!!
        
        // 初始化时清理无效路径
        refreshPaths()
        
        File("$basePath/update").deleteRecursively()
    }
}

data class TitleUpdateMetadata(
    var selected: String = "",
    var paths: MutableList<String> = mutableListOf()
)
