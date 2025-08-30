package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import androidx.compose.ui.text.toLowerCase
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RyujinxNative
import java.io.File

class DlcViewModel(val titleId: String) {
    private var canClose: MutableState<Boolean>? = null
    private var storageHelper: SimpleStorageHelper
    private var dlcItemsState: SnapshotStateList<DlcItem>? = null
    var batchInstallProgress: MutableState<BatchInstallProgress> = mutableStateOf(BatchInstallProgress.IDLE)

    companion object {
        const val UpdateRequestCode = 1002
        const val BatchUpdateRequestCode = 1003
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

    fun removeAll() {
        data?.clear()
        refreshDlcItems()
        saveChanges()
        
        canClose?.let {
            it.value = false
            it.value = true
        }
    }

    fun add() {
        val callBack = storageHelper.onFileSelected

        storageHelper.onFileSelected = { requestCode, files ->
            run {
                storageHelper.onFileSelected = callBack
                if (requestCode == UpdateRequestCode) {
                    processSelectedFiles(files)
                }
            }
        }

        storageHelper.openFilePicker(UpdateRequestCode)
    }

    fun addBatch() {
        val callBack = storageHelper.onFileSelected

        storageHelper.onFileSelected = { requestCode, files ->
            run {
                storageHelper.onFileSelected = callBack
                if (requestCode == BatchUpdateRequestCode) {
                    processBatchFiles(files)
                }
            }
        }

        // 设置多选模式
        storageHelper.openFilePicker(BatchUpdateRequestCode, allowMultiple = true)
    }

    private fun processSelectedFiles(files: List<com.anggrayudi.storage.file.File>) {
        val file = files.firstOrNull()
        file?.apply {
            if (extension == "nsp" || extension == "xci") {
                storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                    uri,
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
                            val potentialFile = java.io.File(baseDir, rootRelativePath)
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
                            val potentialFile = java.io.File(baseDir, rootRelativePath)
                            if (potentialFile.exists()) {
                                filePath = potentialFile.absolutePath
                                break
                            }
                        }
                    }
                }

                if (filePath != null) {
                    path = filePath
                    if (path.isNotEmpty()) {
                        data?.apply {
                            val isDuplicate = this.any { it.path == path }

                            if (!isDuplicate) {
                                val contents = RyujinxNative.jnaInstance.deviceGetDlcContentList(
                                    path,
                                    titleId.toLong(16)
                                )

                                if (contents.isNotEmpty()) {
                                    val contentPath = path
                                    val container = DlcContainerList(contentPath)

                                    for (content in contents)
                                        container.dlc_nca_list.add(
                                            DlcContainer(
                                                true,
                                                titleId,
                                                content
                                            )
                                        )

                                    this.add(container)
                                }
                            }
                        }
                    }
                }
            }
        }

        refreshDlcItems()
        saveChanges()
    }

    private fun processBatchFiles(files: List<com.anggrayudi.storage.file.File>) {
        batchInstallProgress.value = BatchInstallProgress.RUNNING(0, files.size)
        
        // 在后台线程处理批量文件
        Thread {
            var processedCount = 0
            var successCount = 0
            
            files.forEach { file ->
                try {
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
                                    val potentialFile = java.io.File(baseDir, rootRelativePath)
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
                                    val potentialFile = java.io.File(baseDir, rootRelativePath)
                                    if (potentialFile.exists()) {
                                        filePath = potentialFile.absolutePath
                                        break
                                    }
                                }
                            }
                        }

                        if (filePath != null) {
                            path = filePath
                            if (path.isNotEmpty()) {
                                data?.apply {
                                    val isDuplicate = this.any { it.path == path }

                                    if (!isDuplicate) {
                                        val contents = RyujinxNative.jnaInstance.deviceGetDlcContentList(
                                            path,
                                            titleId.toLong(16)
                                        )

                                        if (contents.isNotEmpty()) {
                                            val contentPath = path
                                            val container = DlcContainerList(contentPath)

                                            for (content in contents)
                                                container.dlc_nca_list.add(
                                                    DlcContainer(
                                                        true,
                                                        titleId,
                                                        content
                                                    )
                                                )

                                            this.add(container)
                                            successCount++
                                        }
                                    }
                                }
                            }
                        }
                    }
                } catch (e: Exception) {
                    e.printStackTrace()
                } finally {
                    processedCount++
                    batchInstallProgress.value = BatchInstallProgress.RUNNING(processedCount, files.size)
                }
            }
            
            // 更新进度到完成状态
            batchInstallProgress.value = BatchInstallProgress.COMPLETED(successCount, files.size)
            
            // 在主线程刷新UI
            android.os.Handler(android.os.Looper.getMainLooper()).post {
                refreshDlcItems()
                saveChanges()
            }
        }.start()
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

                if (!java.io.File(containerPath).exists())
                    continue

                for (dlc in container.dlc_nca_list) {
                    val enabled = mutableStateOf(dlc.enabled)
                    items.add(
                        DlcItem(
                            java.io.File(containerPath).name,
                            enabled,
                            containerPath,
                            dlc.fullPath,
                            RyujinxNative.jnaInstance.deviceGetDlcTitleId(
                                containerPath,
                                dlc.fullPath
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
            val savePath = MainActivity.AppPath + "/games/" + titleId.toLowerCase(Locale.current)
            java.io.File(savePath).mkdirs()
            java.io.File("$savePath/dlc.json").writeText(json)
        }
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
        if (java.io.File(jsonPath).exists()) {
            val gson = Gson()
            val typeToken = object : TypeToken<MutableList<DlcContainerList>>() {}.type
            data = gson.fromJson<MutableList<DlcContainerList>>(java.io.File(jsonPath).readText(), typeToken)
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

sealed class BatchInstallProgress {
    object IDLE : BatchInstallProgress()
    data class RUNNING(val processed: Int, val total: Int) : BatchInstallProgress()
    data class COMPLETED(val success: Int, val total: Int) : BatchInstallProgress()
}
