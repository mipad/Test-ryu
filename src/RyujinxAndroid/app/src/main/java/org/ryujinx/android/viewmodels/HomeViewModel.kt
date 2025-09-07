// HomeViewModel.kt
package org.ryujinx.android.viewmodels

import android.content.SharedPreferences
import android.graphics.BitmapFactory
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.documentfile.provider.DocumentFile
import androidx.preference.PreferenceManager
import com.anggrayudi.storage.file.DocumentFileCompat
import com.anggrayudi.storage.file.DocumentFileType
import com.anggrayudi.storage.file.extension
import com.anggrayudi.storage.file.search
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import org.ryujinx.android.MainActivity
import java.util.Base64
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

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
    
    // 添加图标缓存
    private val iconCache = ConcurrentHashMap<String, ImageBitmap>()
    private val scope = CoroutineScope(Dispatchers.IO)
    private var loadingJob: Job? = null

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
        // 使用协程进行过滤，避免阻塞UI线程
        scope.launch(Dispatchers.Default) {
            val filteredList = loadedCache.filter {
                val displayName = it.getDisplayName()
                displayName.isNotEmpty() && (query.trim().isEmpty() || 
                    displayName.lowercase(Locale.getDefault()).contains(query))
            }
            
            // 更新到主线程
            launch(Dispatchers.Main) {
                gameList.clear()
                gameList.addAll(filteredList)
            }
        }
    }

    fun requestReload() {
        shouldReload = true
    }

    private fun reloadGameList() {
        activity?.storageHelper ?: return
        val folder = gameFolderPath ?: return

        shouldReload = false
        if (isLoading.value) {
            loadingJob?.cancel() // 取消之前的加载任务
            return
        }

        gameList.clear()
        loadedCache.clear()
        isLoading.value = true

        // 使用协程而不是线程
        loadingJob = scope.launch {
            try {
                val files = folder.search(false, DocumentFileType.FILE)
                val tempList = mutableListOf<GameModel>()
                
                // 预解码图标
                val decoder = Base64.getDecoder()
                
                for (file in files) {
                    if (file.extension == "xci" || file.extension == "nsp" || file.extension == "nro") {
                        activity.let {
                            val item = GameModel(file, it)
                            val titleId = item.titleId // 获取局部变量

                            if (titleId?.isNotEmpty() == true && 
                                item.getDisplayName().isNotEmpty() && 
                                item.getDisplayName() != "Unknown") {
                                
                                // 预解码并缓存图标
                                if (item.icon?.isNotEmpty() == true) {
                                    try {
                                        val pic = decoder.decode(item.icon)
                                        val bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                        if (bitmap != null) {
                                            iconCache[titleId] = bitmap.asImageBitmap() // 使用局部变量titleId
                                        }
                                    } catch (e: Exception) {
                                        // 图标解码失败，跳过
                                    }
                                }
                                
                                tempList.add(item)
                            }
                        }
                    }
                }
                
                // 更新到主线程
                launch(Dispatchers.Main) {
                    loadedCache.addAll(tempList)
                    filter("")
                    isLoading.value = false
                }
            } catch (e: Exception) {
                // 错误处理
                launch(Dispatchers.Main) {
                    isLoading.value = false
                }
            }
        }
    }
    
    // 获取缓存的图标
    fun getCachedIcon(titleId: String?): ImageBitmap? {
        return if (titleId != null) iconCache[titleId] else null
    }
    
    // 清除缓存
    fun clearCache() {
        iconCache.clear()
        loadedCache.clear()
        gameList.clear()
    }
    
    // 预加载菜单数据
    fun preloadMenuData(selectedGame: GameModel?) {
        if (selectedGame != null) {
            val titleId = selectedGame.titleId // 获取局部变量
            // 预加载可能需要的数据
            // 例如: 确保图标已缓存
            if (titleId != null && !iconCache.containsKey(titleId)) {
                scope.launch {
                    selectedGame.icon?.let { iconData ->
                        try {
                            val decoder = Base64.getDecoder()
                            val pic = decoder.decode(iconData)
                            val bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                            if (bitmap != null) {
                                iconCache[titleId] = bitmap.asImageBitmap() // 使用局部变量titleId
                            }
                        } catch (e: Exception) {
                            // 图标解码失败
                        }
                    }
                }
            }
        }
    }
}
