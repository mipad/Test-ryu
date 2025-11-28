package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import androidx.documentfile.provider.DocumentFile
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.google.gson.Gson
import org.ryujinx.android.MainActivity
import java.io.File
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
                storageHelper.storage.context.contentResolver.releasePersistableUriPermission(
                    this,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION
                )
            }
            pathsState?.clear()
            pathsState?.addAll(this)
            currentPaths = this
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

    // 新增：多文件选择方法（与DLC保持一致）
    fun addSelectedFiles(uris: List<Uri>) {
        if (uris.isNotEmpty()) {
            for (uri in uris) {
                val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                file?.apply {
                    if (extension == "nsp") {
                        storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                            uri,
                            Intent.FLAG_GRANT_READ_URI_PERMISSION
                        )
                        currentPaths.add(uri.toString())
                    }
                }
            }
            refreshPaths()
            saveChanges()
        }
    }

    // 新增：文件夹选择方法
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

    // 新增：处理文件夹中的所有NSP文件
    private fun processFolder(folder: DocumentFile) {
        try {
            // 递归扫描文件夹中的所有文件
            scanFolderForNspFiles(folder)
            refreshPaths()
            saveChanges()
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    // 新增：递归扫描NSP文件
    private fun scanFolderForNspFiles(folder: DocumentFile) {
        if (!folder.exists() || !folder.isDirectory) {
            return
        }

        // 获取文件夹中的所有文件
        val files = folder.listFiles()
        for (file in files) {
            if (file.isDirectory) {
                // 递归扫描子文件夹
                scanFolderForNspFiles(file)
            } else if (file.isFile && file.extension == "nsp") {
                // 处理NSP文件
                val uri = file.uri
                storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION
                )
                
                // 检查是否已存在相同的文件
                val isDuplicate = currentPaths.any { it == uri.toString() }
                if (!isDuplicate) {
                    currentPaths.add(uri.toString())
                }
            }
        }
    }

    private fun refreshPaths() {
        data?.apply {
            val existingPaths = mutableListOf<String>()
            currentPaths.forEach {
                val uri = Uri.parse(it)
                val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                if (file?.exists() == true) {
                    existingPaths.add(it)
                }
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

    // 新增：保存更改的方法
    fun saveChanges() {
        data?.apply {
            val gson = Gson()
            File(basePath).mkdirs()

            val metadata = TitleUpdateMetadata()
            val savedUpdates = mutableListOf<String>()
            currentPaths.forEach {
                val uri = Uri.parse(it)
                val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                if (file?.exists() == true) {
                    savedUpdates.add(it)
                }
            }
            metadata.paths = savedUpdates

            if (selected.isNotEmpty()) {
                val uri = Uri.parse(selected)
                val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                if (file?.exists() == true) {
                    metadata.selected = selected
                }
            } else {
                metadata.selected = selected
            }

            val json = gson.toJson(metadata)
            File("$basePath/$updateJsonName").writeText(json)
        }
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
        data?.apply {
            pathsState?.clear()
            pathsState?.addAll(this.paths)
        }
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
