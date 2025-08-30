package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.text.intl.Locale
import androidx.compose.ui.text.toLowerCase
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.anggrayudi.storage.file.getAbsolutePath
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RyujinxNative
import java.io.File

class DlcViewModel(val titleId: String) {
    private var storageHelper: SimpleStorageHelper

    companion object {
        const val UpdateRequestCode = 1002
    }

    // 添加了refresh参数
    fun remove(item: DlcItem, refresh: MutableState<Boolean>) {
        data?.apply {
            this.removeAll { it.path == item.containerPath }
            refresh.value = true // 触发刷新
        }
    }

    fun add(refresh: MutableState<Boolean>) {
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

                            val uri = file.uri
                            var filePath: String? = null
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
                                            filePath = potentialFile.absolutePath
                                            break
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
                                            filePath = potentialFile.absolutePath
                                            break
                                        }
                                    }
                                }
                            }

                            // 回退到getAbsolutePath
                            if (filePath == null) {
                                filePath = file.getAbsolutePath(storageHelper.storage.context)
                            }
                            
                            if (filePath.isNotEmpty()) {
                                data?.apply {
                                    val isDuplicate = this.any { it.path == filePath }

                                    if (!isDuplicate) {
                                        val contents = RyujinxNative.jnaInstance.deviceGetDlcContentList(
                                            filePath,
                                            titleId.toLong(16)
                                        )

                                        if (contents.isNotEmpty()) {
                                            val contentPath = filePath
                                            val container = DlcContainerList(contentPath)

                                            for (content in contents) {
                                                val dlcTitleId = RyujinxNative.jnaInstance.deviceGetDlcTitleId(
                                                    contentPath,
                                                    content
                                                )
                                                container.dlc_nca_list.add(
                                                    DlcContainer(
                                                        true,
                                                        dlcTitleId,
                                                        content
                                                    )
                                                )
                                            }

                                            this.add(container)
                                        }
                                    }
                                }
                            }
                        }
                    }
                    refresh.value = true
                }
            }
        }
        storageHelper.openFilePicker(UpdateRequestCode)
    }

    fun save(items: List<DlcItem>) {
        data?.apply {
            // 更新启用状态
            items.forEach { item ->
                for (container in this) {
                    if (container.path == item.containerPath) {
                        for (dlc in container.dlc_nca_list) {
                            if (dlc.fullPath == item.fullPath) {
                                dlc.enabled = item.isEnabled.value
                                break
                            }
                        }
                    }
                }
            }

            val gson = Gson()
            val json = gson.toJson(this)
            jsonPath = MainActivity.AppPath + "/games/" + titleId.toLowerCase(Locale.current)
            File(jsonPath).mkdirs()
            File("$jsonPath/dlc.json").writeText(json)
        }
    }

    @Composable
    fun getDlc(): List<DlcItem> {
        var items = mutableListOf<DlcItem>()

        data?.apply {
            for (container in this) {
                val containerPath = container.path

                if (!File(containerPath).exists())
                    continue

                for (dlc in container.dlc_nca_list) {
                    val enabled = remember {
                        mutableStateOf(dlc.enabled)
                    }
                    items.add(
                        DlcItem(
                            File(containerPath).name,
                            enabled,
                            containerPath,
                            dlc.fullPath,
                            dlc.titleId
                        )
                    )
                }
            }
        }

        return items.toList()
    }

    var data: MutableList<DlcContainerList>? = null
    private var jsonPath: String

    init {
        jsonPath =
            MainActivity.AppPath + "/games/" + titleId.toLowerCase(Locale.current) + "/dlc.json"
        storageHelper = MainActivity.StorageHelper!!

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
    var enabled: Boolean = false,
    var titleId: String = "",
    var fullPath: String = ""
)

data class DlcItem(
    var name: String = "",
    var isEnabled: MutableState<Boolean> = mutableStateOf(false),
    var containerPath: String = "",
    var fullPath: String = "",
    var titleId: String = ""
)
