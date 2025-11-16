package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RyujinxNative
import java.io.File

class DlcViewModel(val titleId: String) {
    private var canClose: MutableState<Boolean>? = null
    private var dlcItemsState: SnapshotStateList<DlcItem>? = null

    companion object {
        const val UpdateRequestCode = 1002
    }

    fun remove(item: DlcItem) {
        data?.apply {
            this.removeAll { it.path == item.containerPath }
            refreshDlcItems()
            saveChanges()

            canClose?.let {
                it.value = false
                it.value = true
            }
        }
    }

    // 新增方法：处理选中的多个文件
    fun addSelectedFiles(uris: List<Uri>, context: android.content.Context) {
        if (uris.isNotEmpty()) {
            for (uri in uris) {
                processDlcFile(uri, context)
            }
            refreshDlcItems()
            saveChanges()
        }
    }

    private fun processDlcFile(uri: Uri, context: android.content.Context) {
        try {
            // 获取文件扩展名
            val mimeType = context.contentResolver.getType(uri)
            val fileName = getFileNameFromUri(context, uri)
            val fileExtension = fileName?.substringAfterLast('.', "")?.lowercase() // 修复：toLowerCase() -> lowercase()

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

                        val baseDirectories = listOf(
                            context.filesDir,
                            context.getExternalFilesDir(null),
                            Environment.getExternalStorageDirectory()
                        )

                        for (baseDir in baseDirectories) {
                            val potentialFile = File(baseDir, rootRelativePath)
                            if (potentialFile.exists()) {
                                filePath = potentialFile.absolutePath
                                break
                            }
                        }
                    } else if (relativePath.startsWith("primary:")) {
                        val rootRelativePath = relativePath.substring("primary:".length)

                        val baseDirectories = listOf(
                            context.filesDir,
                            context.getExternalFilesDir(null),
                            Environment.getExternalStorageDirectory()
                        )

                        for (baseDir in baseDirectories) {
                            val potentialFile = File(baseDir, rootRelativePath)
                            if (potentialFile.exists()) {
                                filePath = potentialFile.absolutePath
                                break
                            }
                        }
                    }
                }

                // 如果无法通过路径解析，尝试使用文件名
                if (filePath == null) {
                    filePath = getFilePathFromUri(context, uri)
                }

                if (!filePath.isNullOrEmpty()) {
                    data?.apply {
                        val isDuplicate = this.any { it.path == filePath }

                        if (!isDuplicate) {
                            val contents =
                                RyujinxNative.jnaInstance.deviceGetDlcContentList(
                                    filePath,
                                    titleId.toLong(16)
                                )

                            if (contents.isNotEmpty()) {
                                val contentPath = filePath
                                val container = DlcContainerList(contentPath)

                                for (content in contents)
                                    container.dlc_nca_list.add(
                                        DlcContainer(
                                            true,
                                            RyujinxNative.jnaInstance.deviceGetDlcTitleId(
                                                contentPath,
                                                content
                                            ).toLong(16),
                                            content
                                        )
                                    )

                                this.add(container)
                            }
                        }
                    }
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
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

    fun save(openDialog: MutableState<Boolean>) {
        saveChanges()
        openDialog.value = false
    }

    fun setDlcItems(items: SnapshotStateList<DlcItem>, canClose: MutableState<Boolean>) {
        dlcItemsState = items
        this.canClose = canClose
        refreshDlcItems()
    }

    private fun refreshDlcItems() {
        val items = mutableListOf<DlcItem>()

        data?.apply {
            for (container in this) {
                val containerPath = container.path

                if (!File(containerPath).exists())
                    continue

                for (dlc in container.dlc_nca_list) {
                    val enabled = mutableStateOf(dlc.is_enabled)
                    items.add(
                        DlcItem(
                            File(containerPath).name,
                            enabled,
                            containerPath,
                            dlc.path,
                            RyujinxNative.jnaInstance.deviceGetDlcTitleId(
                                containerPath,
                                dlc.path
                            )
                        )
                    )
                }
            }
        }

        dlcItemsState?.clear()
        dlcItemsState?.addAll(items)
        canClose?.apply {
            value = true
        }
    }

    private fun saveChanges() {
        data?.apply {
            dlcItemsState?.forEach { item ->
                for (container in this) {
                    if (container.path == item.containerPath) {
                        for (dlc in container.dlc_nca_list) {
                            if (dlc.path == item.fullPath) {
                                dlc.is_enabled = item.isEnabled.value
                                break
                            }
                        }
                    }
                }
            }

            val gson = Gson()
            val json = gson.toJson(this)
            val savePath = MainActivity.AppPath + "/games/" + titleId.lowercase() // 修复：toLowerCase() -> lowercase()
            File(savePath).mkdirs()
            File("$savePath/dlc.json").writeText(json)
        }
    }

    var data: MutableList<DlcContainerList>? = null
    private var jsonPath: String

    init {
        jsonPath =
            MainActivity.AppPath + "/games/" + titleId.lowercase() + "/dlc.json" // 修复：toLowerCase() -> lowercase()

        reloadFromDisk()
    }

    private fun reloadFromDisk() {
        data = mutableListOf()
        if (File(jsonPath).exists()) {
            val gson = Gson()
            val typeToken = object : TypeToken<MutableList<DlcContainerList>>() {}.type
            data =
                gson.fromJson<MutableList<DlcContainerList>>(File(jsonPath).readText(), typeToken)
        }
    }
}

data class DlcContainerList(
    var path: String = "",
    var dlc_nca_list: MutableList<DlcContainer> = mutableListOf()
)

data class DlcContainer(
    var is_enabled: Boolean = false,
    var title_id: Long = 0,
    var path: String = ""
)

data class DlcItem(
    var name: String = "",
    var isEnabled: MutableState<Boolean> = mutableStateOf(false),
    var containerPath: String = "",
    var fullPath: String = "",
    var titleId: String = ""
)
