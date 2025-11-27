package org.ryujinx.android.viewmodels

import android.content.SharedPreferences
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.documentfile.provider.DocumentFile
import androidx.preference.PreferenceManager
import com.anggrayudi.storage.file.DocumentFileCompat
import com.anggrayudi.storage.file.DocumentFileType
import com.anggrayudi.storage.file.extension
import com.anggrayudi.storage.file.search
import kotlinx.coroutines.DelicateCoroutinesApi
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.GlobalScope
import kotlinx.coroutines.launch
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RyujinxNative
import java.io.File
import java.util.Locale
import kotlin.concurrent.thread
import android.net.Uri

class HomeViewModel(
    val activity: MainActivity? = null,
    val mainViewModel: MainViewModel? = null
) {
    private var shouldReload: Boolean = false
    private var savedFolder: String = ""
    private var loadedCache: MutableList<GameModel> = mutableListOf()
    private var gameFolderPath: DocumentFile? = null
    private var sharedPref: SharedPreferences? = null
    val gameList: SnapshotStateList<GameModel> = SnapshotStateList()
    val isLoading: MutableState<Boolean> = mutableStateOf(false)

    init {
        if (activity != null) {
            sharedPref = PreferenceManager.getDefaultSharedPreferences(activity)
        }
    }

    fun ensureReloadIfNecessary() {
        val oldFolder = savedFolder
        savedFolder = sharedPref?.getString("gameFolder", "") ?: ""

        if (savedFolder.isNotEmpty() && (shouldReload || savedFolder != oldFolder)) {
            gameFolderPath = DocumentFileCompat.fromFullPath(
                mainViewModel?.activity!!,
                savedFolder,
                documentType = DocumentFileType.FOLDER,
                requiresWriteAccess = true
            )

            reloadGameList()
        }
    }

    fun filter(query: String) {
        gameList.clear()
        gameList.addAll(loadedCache.filter {
            val displayName = it.getDisplayName()
            displayName.isNotEmpty() && (query.trim().isEmpty() || 
                displayName.lowercase(Locale.getDefault()).contains(query))
        })
    }

    fun requestReload() {
        shouldReload = true
    }

    @OptIn(DelicateCoroutinesApi::class)
    private fun reloadGameList() {
        activity?.storageHelper ?: return
        val folder = gameFolderPath ?: return

        shouldReload = false
        if (isLoading.value)
            return

        gameList.clear()
        loadedCache.clear()
        isLoading.value = true

        thread {
            try {
                for (file in folder.search(false, DocumentFileType.FILE)) {
                    if (file.extension == "xci" || file.extension == "nsp" || file.extension == "nro")
                        activity.let {
                            val item = GameModel(file, it)

                            if (item.titleId?.isNotEmpty() == true && item.getDisplayName().isNotEmpty() && item.getDisplayName() != "Unknown") {
                                loadedCache.add(item)
                            }
                        }
                }

                // Auto-load DLCs and Title Updates from configured directories (if any)
                try {
                    autoloadContent()
                } catch (_: Throwable) { }
            } finally {
                isLoading.value = false
                GlobalScope.launch(Dispatchers.Main){
                    filter("")
                }
            }
        }
    }

    // Helper function to compare update versions from filenames
    // Returns true if newPath represents a newer version than currentPath
    private fun shouldSelectNewerUpdate(currentPath: String, newPath: String): Boolean {
        // Extract version numbers from filenames using regex pattern [vXXXXXX]
        val versionPattern = Regex("\\[v(\\d+)]")

        val currentVersion = versionPattern.find(currentPath.lowercase(Locale.getDefault()))?.groupValues?.get(1)?.toIntOrNull() ?: 0
        val newVersion = versionPattern.find(newPath.lowercase(Locale.getDefault()))?.groupValues?.get(1)?.toIntOrNull() ?: 0

        return newVersion > currentVersion
    }

    /**
     * 将文件路径转换为URI路径
     */
    private fun convertFilePathToUri(filePath: String): String? {
        return try {
            // 方法1: 尝试通过DocumentFile获取content URI
            val documentFile = DocumentFileCompat.fromFullPath(
                activity!!,
                filePath,
                com.anggrayudi.storage.file.DocumentFileType.FILE,
                requiresWriteAccess = false
            )
            
            if (documentFile != null && documentFile.exists()) {
                val uri = documentFile.uri
                // 获取持久化读取权限
                try {
                    activity.contentResolver.takePersistableUriPermission(
                        uri,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION
                    )
                    android.util.Log.d("Ryujinx", "Acquired permission for auto-update: ${File(filePath).name} -> ${uri}")
                    uri.toString()
                } catch (e: SecurityException) {
                    android.util.Log.e("Ryujinx", "Failed to get permission for auto-update: ${File(filePath).name}", e)
                    null
                }
            } else {
                // 方法2: 使用file URI作为回退
                val fileUri = Uri.fromFile(File(filePath)).toString()
                android.util.Log.d("Ryujinx", "Using file URI for auto-update: ${File(filePath).name} -> ${fileUri}")
                fileUri
            }
        } catch (e: Exception) {
            android.util.Log.e("Ryujinx", "Error converting file path to URI: ${filePath}", e)
            null
        }
    }

    // Scans configured directory for NSPs containing DLCs/Updates and associates them to known titles.
    private fun autoloadContent() {
        val prefs = sharedPref ?: return

        val updatesFolder = prefs.getString("updatesFolder", "") ?: ""

        if (updatesFolder.isEmpty()) return

        // Build a map of titleId -> helpers
        val gamesByTitle = loadedCache.mapNotNull { g ->
            val tid = g.titleId
            if (!tid.isNullOrBlank()) tid.lowercase(Locale.getDefault()) to tid else null
        }.toMap()

        var updatesAdded = 0
        var dlcAdded = 0

        val base = File(updatesFolder)
        if (!base.exists() || !base.isDirectory) return

        base.walkTopDown().forEach fileLoop@{ f ->
            if (!f.isFile) return@fileLoop
            val name = f.name.lowercase(Locale.getDefault())
            if (!name.endsWith(".nsp")) return@fileLoop

            // Extract title ID from filename
            val tidPattern = Regex("\\[([0-9a-fA-F]{16})]")
            val tidMatch = tidPattern.find(name) ?: return@fileLoop
            val fileTid = tidMatch.groupValues[1].lowercase(Locale.getDefault())

            // Try to find DLC content for all games
            var isDlc = false
            try {
                for ((_, tidOrig) in gamesByTitle) {
                    val contents = RyujinxNative.jnaInstance.deviceGetDlcContentList(f.absolutePath, tidOrig.toLong(16))

                    if (contents.isNotEmpty()) {
                        isDlc = true
                        val containerPath = f.absolutePath
                        val vm = DlcViewModel(tidOrig)
                        val already = vm.data?.any { it.path == containerPath } == true

                        if (!already) {
                            val container = DlcContainerList(containerPath)
                            for (content in contents) {
                                container.dlc_nca_list.add(
                                    DlcContainer(
                                        true,
                                        RyujinxNative.jnaInstance.deviceGetDlcTitleId(containerPath, content).toLong(16),
                                        content
                                    )
                                )
                            }
                            vm.data?.add(container)
                            vm.saveChanges()
                            dlcAdded++
                        }
                        break
                    }
                }
            } catch (_: Throwable) { }

            if (isDlc) return@fileLoop

            // Treat as Title Update - convert update ID to base ID
            // Update title IDs end in 800, base game IDs end in 000
            val baseTid = if (fileTid.endsWith("800")) {
                fileTid.substring(0, fileTid.length - 3) + "000"
            } else {
                fileTid
            }

            val originalTid = gamesByTitle[baseTid]
            if (originalTid != null) {
                val vm = TitleUpdateViewModel(originalTid)
                
                // 修复：将文件路径转换为URI路径（与手动安装保持一致）
                val fileUri = convertFilePathToUri(f.absolutePath)
                if (fileUri == null) {
                    android.util.Log.e("Ryujinx", "Failed to convert file path to URI: ${f.absolutePath}")
                    return@fileLoop
                }
                
                // 检查是否已存在
                val exists = vm.data?.paths?.any { existingPath ->
                    // 规范化路径比较
                    normalizeUpdatePath(existingPath) == normalizeUpdatePath(fileUri)
                } == true

                if (!exists) {
                    // Add the new update path
                    vm.data?.paths?.add(fileUri)

                    // 强制选择新添加的更新文件
                    vm.data?.selected = fileUri
                    
                    vm.saveChanges()
                    updatesAdded++
                    android.util.Log.d("Ryujinx", "Auto-added and selected update: ${f.name} for title $originalTid -> $fileUri")
                } else {
                    android.util.Log.d("Ryujinx", "Update already exists: ${f.name} for title $originalTid")
                }
            } else {
                android.util.Log.d("Ryujinx", "No matching game found for update: ${f.name} with base TID: $baseTid")
                android.util.Log.d("Ryujinx", "Available TIDs: ${gamesByTitle.keys}")
            }
        }
        
        // 记录自动更新结果
        android.util.Log.d("Ryujinx", "Auto-load completed: $updatesAdded updates added, $dlcAdded DLCs added")
    }

    /**
     * 规范化更新路径以便比较
     */
    private fun normalizeUpdatePath(path: String): String {
        return if (path.startsWith("content://")) {
            path
        } else if (path.startsWith("file://")) {
            // 提取文件路径部分进行比较
            val file = File(Uri.parse(path).path ?: path)
            file.absolutePath
        } else {
            // 普通文件路径
            File(path).absolutePath
        }
    }
}