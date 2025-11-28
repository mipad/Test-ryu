package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import androidx.compose.ui.text.toLowerCase
import androidx.documentfile.provider.DocumentFile
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.google.gson.Gson
import org.ryujinx.android.MainActivity
import java.io.File
import java.util.Locale as JavaLocale
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
            saveChanges() // 添加保存更改
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
                            currentPaths.add(file.uri.toString())
                        }
                    }

                    refreshPaths()
                    saveChanges() // 添加保存更改
                }
            }
        }
        storageHelper.openFilePicker(UpdateRequestCode)
    }

    // 新增：处理文件路径的方法（用于autoloadContent）
    fun addFilePaths(filePaths: List<String>) {
        if (filePaths.isNotEmpty()) {
            var addedCount = 0
            for (filePath in filePaths) {
                try {
                    val file = File(filePath)
                    if (file.exists() && file.isFile && isUpdateFile(file)) {
                        val isDuplicate = currentPaths.any { it == filePath }
                        if (!isDuplicate) {
                            currentPaths.add(filePath)
                            addedCount++
                        }
                    }
                } catch (e: Exception) {
                    e.printStackTrace()
                }
            }
            
            if (addedCount > 0) {
                refreshPaths()
                saveChanges() // 添加保存更改
            }
        }
    }

    // 检查是否为更新文件（支持File对象）
    private fun isUpdateFile(file: File): Boolean {
        val extension = file.extension.toLowerCase(JavaLocale.getDefault())
        return (extension == "nsp" || extension == "xci") && file.exists() && file.canRead()
    }

    // 检查是否为更新文件（支持DocumentFile对象）
    private fun isUpdateFile(file: DocumentFile): Boolean {
        val extension = file.extension?.toLowerCase(JavaLocale.getDefault())
        return (extension == "nsp" || extension == "xci") && file.exists() && file.canRead()
    }

    private fun refreshPaths() {
        data?.apply {
            val existingPaths = mutableListOf<String>()
            currentPaths.forEach {
                // 检查路径类型
                if (it.startsWith("content://") || it.startsWith("file://")) {
                    // URI 路径
                    val uri = Uri.parse(it)
                    val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                    if (file?.exists() == true) {
                        existingPaths.add(it)
                    }
                } else {
                    // 文件路径
                    val file = File(it)
                    if (file.exists() && file.isFile) {
                        existingPaths.add(it)
                    }
                }
            }

            if (!existingPaths.contains(selected)) {
                selected = ""
            }
            pathsState?.clear()
            pathsState?.addAll(existingPaths)
            paths = existingPaths
            currentPaths = existingPaths // 更新当前路径
            canClose?.apply {
                value = true
            }
        }
    }

    // 新增：保存更改的方法
    fun saveChanges() {
        val metadata = data ?: TitleUpdateMetadata()
        val gson = Gson()
        File(basePath).mkdirs()

        val savedUpdates = mutableListOf<String>()
        currentPaths.forEach {
            // 检查路径类型
            if (it.startsWith("content://") || it.startsWith("file://")) {
                // URI 路径
                val uri = Uri.parse(it)
                val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                if (file?.exists() == true) {
                    savedUpdates.add(it)
                }
            } else {
                // 文件路径
                val file = File(it)
                if (file.exists() && file.isFile) {
                    savedUpdates.add(it)
                }
            }
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
            saveChanges() // 使用统一的保存方法
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
