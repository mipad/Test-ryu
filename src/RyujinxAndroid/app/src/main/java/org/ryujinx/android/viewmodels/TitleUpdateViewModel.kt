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
            // 只有URI路径才需要释放权限
            if (str.startsWith("content://")) {
                Uri.parse(str)?.apply {
                    storageHelper.storage.context.contentResolver.releasePersistableUriPermission(
                        this,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION
                    )
                }
            }
            pathsState?.clear()
            pathsState?.addAll(this)
            currentPaths = this
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
                            // 获取持久化读取权限
                            storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                                file.uri,
                                Intent.FLAG_GRANT_READ_URI_PERMISSION
                            )

                            // 将URI转换为文件绝对路径（与自动安装保持一致）
                            val filePath = convertUriToFilePath(file.uri)
                            
                            if (filePath.isNotEmpty()) {
                                val isDuplicate = currentPaths.contains(filePath) || data?.paths?.contains(filePath) == true

                                if (!isDuplicate) {
                                    currentPaths.add(filePath)
                                }
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

    /**
     * 将URI转换为文件绝对路径
     */
    private fun convertUriToFilePath(uri: Uri): String {
        return try {
            var path = uri.pathSegments.joinToString("/")

            if (path.startsWith("document/")) {
                val relativePath = Uri.decode(path.substring("document/".length))

                if (relativePath.startsWith("root/")) {
                    val rootRelativePath = relativePath.substring("root/".length)

                    val baseDirectories = listOf(
                        storageHelper.storage.context.filesDir,
                        storageHelper.storage.context.getExternalFilesDir(null),
                        Environment.getExternalStorageDirectory()
                    )

                    for (baseDir in baseDirectories) {
                        val potentialFile = File(baseDir, rootRelativePath)
                        if (potentialFile.exists()) {
                            return potentialFile.absolutePath
                        }
                    }
                } else if (relativePath.startsWith("primary:")) {
                    val rootRelativePath = relativePath.substring("primary:".length)

                    val baseDirectories = listOf(
                        storageHelper.storage.context.filesDir,
                        storageHelper.storage.context.getExternalFilesDir(null),
                        Environment.getExternalStorageDirectory()
                    )

                    for (baseDir in baseDirectories) {
                        val potentialFile = File(baseDir, rootRelativePath)
                        if (potentialFile.exists()) {
                            return potentialFile.absolutePath
                        }
                    }
                }
            }
            
            // 如果无法转换，回退到URI字符串
            uri.toString()
        } catch (e: Exception) {
            // 转换失败，使用URI字符串
            uri.toString()
        }
    }

    private fun refreshPaths() {
        data?.apply {
            val existingPaths = mutableListOf<String>()
            currentPaths.forEach {
                // 检查文件是否存在（对于文件路径）
                if (it.startsWith("/")) {
                    val file = File(it)
                    if (file.exists()) {
                        existingPaths.add(it)
                    }
                } else {
                    // 对于URI路径，检查DocumentFile是否存在
                    val uri = Uri.parse(it)
                    val file = DocumentFile.fromSingleUri(storageHelper.storage.context, uri)
                    if (file?.exists() == true) {
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