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

        val updatesFolderPath = prefs.getString("updatesFolder", "") ?: ""
        if (updatesFolderPath.isEmpty()) return

        try {
            // 使用 DocumentFile 方式打开文件夹
            val updatesFolder = DocumentFileCompat.fromFullPath(
                activity!!,
                updatesFolderPath,
                documentType = DocumentFileType.FOLDER,
                requiresWriteAccess = false
            )

            if (updatesFolder == null || !updatesFolder.exists() || !updatesFolder.isDirectory) {
                return
            }

            // Build a map of titleId -> helpers
            val gamesByTitle = loadedCache.mapNotNull { g ->
                val tid = g.titleId
                if (!tid.isNullOrBlank()) tid.lowercase(Locale.getDefault()) to tid else null
            }.toMap()

            // 使用 DocumentFile 的搜索功能扫描文件
            scanDocumentFolderForContent(updatesFolder, gamesByTitle)

        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    // 使用 DocumentFile 递归扫描文件夹
    private fun scanDocumentFolderForContent(folder: DocumentFile, gamesByTitle: Map<String, String>) {
        try {
            val files = folder.listFiles()
            for (file in files) {
                if (file.isDirectory) {
                    // 递归扫描子文件夹
                    scanDocumentFolderForContent(file, gamesByTitle)
                } else if (file.isFile && file.extension == "nsp") {
                    // 处理 NSP 文件
                    processContentFile(file, gamesByTitle)
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    // 处理单个内容文件（DLC 或更新）
    private fun processContentFile(file: DocumentFile, gamesByTitle: Map<String, String>) {
        try {
            val name = file.name?.lowercase(Locale.getDefault()) ?: return
            
            // Extract title ID from filename
            val tidPattern = Regex("\\[([0-9a-fA-F]{16})]")
            val tidMatch = tidPattern.find(name) ?: return
            val fileTid = tidMatch.groupValues[1].lowercase(Locale.getDefault())

            // 使用 getAbsolutePath 方法获取文件路径
            val filePath = com.anggrayudi.storage.file.getAbsolutePath(activity!!, file.uri)
            if (filePath.isEmpty()) return

            // Try to find DLC content for all games
            var isDlc = false
            try {
                for ((_, tidOrig) in gamesByTitle) {
                    val contents = RyujinxNative.jnaInstance.deviceGetDlcContentList(filePath, tidOrig.toLong(16))

                    if (contents.isNotEmpty()) {
                        isDlc = true
                        val containerPath = filePath
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
                        }
                        break
                    }
                }
            } catch (_: Throwable) { }

            if (isDlc) return

            // Treat as Title Update - convert update ID to base ID
            val baseTid = if (fileTid.endsWith("800")) {
                fileTid.substring(0, fileTid.length - 3) + "000"
            } else {
                fileTid
            }

            val originalTid = gamesByTitle[baseTid]
            if (originalTid != null) {
                val vm = TitleUpdateViewModel(originalTid)
                val path = filePath
                val exists = (vm.data?.paths?.contains(path) == true)

                if (!exists) {
                    // Add the new update path
                    vm.data?.paths?.add(path)

                    // Auto-select this update if it's newer than the currently selected one
                    val currentSelected = vm.data?.selected ?: ""
                    val shouldSelect = currentSelected.isEmpty() ||
                        shouldSelectNewerUpdate(currentSelected, path)

                    if (shouldSelect) {
                        vm.data?.selected = path
                    }

                    vm.saveChanges()
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }
}
