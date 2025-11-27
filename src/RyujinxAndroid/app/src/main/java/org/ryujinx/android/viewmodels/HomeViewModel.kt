package org.ryujinx.android.viewmodels

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
import java.io.InputStream
import java.io.OutputStream
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
                } catch (e: Throwable) {
                    e.printStackTrace()
                }
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
            } catch (e: Throwable) {
                e.printStackTrace()
            }

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
                try {
                    val vm = TitleUpdateViewModel(originalTid)
                    val path = f.absolutePath
                    
                    // 检查是否已存在相同的文件（通过文件名比较）
                    val fileName = f.name
                    val exists = vm.data?.paths?.any { 
                        it.endsWith(fileName) || getFileNameFromUri(it) == fileName
                    } == true

                    if (!exists) {
                        // 将文件复制到应用的可访问位置，并使用URI格式
                        val copiedFileUri = copyToAppDirectory(f, originalTid)
                        if (copiedFileUri != null) {
                            vm.currentPaths.add(copiedFileUri.toString())
                            vm.refreshPaths()
                            
                            // Auto-select this update if it's newer than the currently selected one
                            // or if no update is currently selected
                            val currentSelected = vm.data?.selected ?: ""
                            val shouldSelect = currentSelected.isEmpty() ||
                                shouldSelectNewerUpdate(currentSelected, copiedFileUri.toString())

                            if (shouldSelect) {
                                vm.data?.selected = copiedFileUri.toString()
                            }
                            
                            vm.saveChanges()
                            updatesAdded++
                        }
                    }
                } catch (e: Exception) {
                    e.printStackTrace()
                }
            }
        }
    }

    // 辅助方法：从URI字符串中获取文件名
    private fun getFileNameFromUri(uriString: String): String? {
        return try {
            if (uriString.startsWith("content://")) {
                val uri = Uri.parse(uriString)
                val cursor = activity?.contentResolver?.query(uri, null, null, null, null)
                cursor?.use {
                    if (it.moveToFirst()) {
                        val displayNameIndex = it.getColumnIndex("_display_name")
                        if (displayNameIndex != -1) {
                            return it.getString(displayNameIndex)
                        }
                    }
                }
                // 如果查询失败，则从URI路径中提取
                uri.lastPathSegment
            } else {
                File(uriString).name
            }
        } catch (e: Exception) {
            e.printStackTrace()
            null
        }
    }

    // 添加辅助方法：将文件复制到应用目录
    private fun copyToAppDirectory(sourceFile: File, titleId: String): Uri? {
        return try {
            val context = activity ?: return null
            val appDir = File(context.filesDir, "updates/$titleId")
            appDir.mkdirs()
            
            val destFile = File(appDir, sourceFile.name)
            
            // 复制文件
            sourceFile.inputStream().use { input ->
                destFile.outputStream().use { output ->
                    input.copyTo(output)
                }
            }
            
            // 返回文件URI
            Uri.fromFile(destFile)
        } catch (e: Exception) {
            e.printStackTrace()
            null
        }
    }
}