package org.ryujinx.android.viewmodels

import android.content.Intent
import android.content.SharedPreferences
import android.net.Uri
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
                
                // 第一步：先使用文件路径安装
                val filePath = f.absolutePath
                val exists = (vm.data?.paths?.contains(filePath) == true)

                if (!exists) {
                    // Add the new update path
                    vm.data?.paths?.add(filePath)

                    // Auto-select this update if it's newer than the currently selected one
                    // or if no update is currently selected
                    val currentSelected = vm.data?.selected ?: ""
                    val shouldSelect = currentSelected.isEmpty() ||
                        shouldSelectNewerUpdate(currentSelected, filePath)

                    if (shouldSelect) {
                        vm.data?.selected = filePath
                    }

                    vm.saveChanges()
                    updatesAdded++
                    android.util.Log.d("Ryujinx", "Auto-added update using file path: ${f.name} for title $originalTid -> $filePath")
                    
                    // 第二步：转换为URI路径（在后台进行，不影响当前操作）
                    convertToUriAndUpdate(originalTid, filePath)
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
     * 在后台将文件路径转换为URI路径并更新
     */
    private fun convertToUriAndUpdate(titleId: String, filePath: String) {
        thread {
            try {
                // 尝试获取content URI
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
                        
                        // 更新为URI路径
                        val vm = TitleUpdateViewModel(titleId)
                        val uriString = uri.toString()
                        
                        // 替换文件路径为URI路径
                        val updated = vm.data?.paths?.replaceAll { path ->
                            if (path == filePath) uriString else path
                        }
                        
                        // 如果选中的是此文件，也更新选中路径
                        if (vm.data?.selected == filePath) {
                            vm.data?.selected = uriString
                        }
                        
                        vm.saveChanges()
                        android.util.Log.d("Ryujinx", "Successfully converted to URI: $filePath -> $uriString")
                    } catch (e: SecurityException) {
                        android.util.Log.e("Ryujinx", "Failed to get permission for: $filePath", e)
                    }
                } else {
                    android.util.Log.w("Ryujinx", "Cannot convert to content URI, keeping file path: $filePath")
                }
            } catch (e: Exception) {
                android.util.Log.e("Ryujinx", "Error converting file path to URI: $filePath", e)
            }
        }
    }
}

// 扩展函数：替换列表中的元素
private fun <T> MutableList<T>.replaceAll(transform: (T) -> T) {
    for (i in indices) {
        this[i] = transform(this[i])
    }
}