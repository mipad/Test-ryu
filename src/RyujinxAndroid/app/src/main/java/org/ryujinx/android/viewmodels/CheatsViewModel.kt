package org.ryujinx.android.viewmodels

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.ryujinx.android.RyujinxNative
import java.io.File

data class CheatItem(
    val id: String, // 格式为 "buildId-cheatName"
    val name: String,
    val enabled: Boolean
)

class CheatsViewModel(
    private val titleId: String, 
    private val gamePath: String,
    private val packageName: String // 添加包名参数
) : ViewModel() {
    private val _cheats = MutableStateFlow<List<CheatItem>>(emptyList())
    val cheats: StateFlow<List<CheatItem>> = _cheats
    
    private val _isLoading = MutableStateFlow(true)
    val isLoading: StateFlow<Boolean> = _isLoading
    
    private val _errorMessage = MutableStateFlow<String?>(null)
    val errorMessage: StateFlow<String?> = _errorMessage

    // 金手指目录路径 - 使用动态包名
    private val cheatsDir: File by lazy {
        File("/storage/emulated/0/Android/data/$packageName/files/mods/contents/$titleId/cheats")
    }

    init {
        loadCheats()
    }

    private fun loadCheats() {
        viewModelScope.launch {
            try {
                _isLoading.value = true
                // 通过JNI调用获取金手指列表
                val cheatList = RyujinxNative.getCheats(titleId, gamePath)
                // 获取已启用的金手指列表
                val enabledCheats = RyujinxNative.getEnabledCheats(titleId)
                
                // 构建CheatItem列表
                val cheats = cheatList.map { cheatId ->
                    val name = cheatId.substringAfterLast('-')
                    CheatItem(
                        id = cheatId,
                        name = name,
                        enabled = enabledCheats.contains(cheatId)
                    )
                }
                _cheats.value = cheats
            } catch (e: Exception) {
                _errorMessage.value = "Failed to load cheats: ${e.message}"
            } finally {
                _isLoading.value = false
            }
        }
    }

    fun setCheatEnabled(cheatId: String, enabled: Boolean) {
        viewModelScope.launch {
            try {
                // 通过JNI调用设置金手指启用状态
                RyujinxNative.setCheatEnabled(titleId, cheatId, enabled)
                
                // 更新本地列表
                _cheats.value = _cheats.value.map {
                    if (it.id == cheatId) {
                        it.copy(enabled = enabled)
                    } else {
                        it
                    }
                }
            } catch (e: Exception) {
                _errorMessage.value = "Failed to update cheat: ${e.message}"
            }
        }
    }

    fun saveCheats() {
        viewModelScope.launch {
            try {
                RyujinxNative.saveCheats(titleId)
            } catch (e: Exception) {
                _errorMessage.value = "Failed to save cheats: ${e.message}"
            }
        }
    }

    // 添加金手指文件
    fun addCheatFile(cheatFile: File) {
        viewModelScope.launch {
            try {
                if (!cheatsDir.exists()) {
                    cheatsDir.mkdirs()
                }
                
                val targetFile = File(cheatsDir, cheatFile.name)
                cheatFile.inputStream().use { input ->
                    targetFile.outputStream().use { output ->
                        input.copyTo(output)
                    }
                }
                
                // 重新加载金手指列表
                loadCheats()
            } catch (e: Exception) {
                _errorMessage.value = "Failed to add cheat file: ${e.message}"
            }
        }
    }

    // 删除所有金手指文件
    fun deleteAllCheats() {
        viewModelScope.launch {
            try {
                if (cheatsDir.exists() && cheatsDir.isDirectory) {
                    cheatsDir.listFiles()?.forEach { file ->
                        if (file.isFile && (file.extension == "txt" || file.extension == "json")) {
                            file.delete()
                        }
                    }
                }
                
                // 重新加载金手指列表
                loadCheats()
            } catch (e: Exception) {
                _errorMessage.value = "Failed to delete cheats: ${e.message}"
            }
        }
    }

    // 获取当前金手指文件数量
    fun getCheatFileCount(): Int {
        return if (cheatsDir.exists() && cheatsDir.isDirectory) {
            cheatsDir.listFiles()?.count { it.isFile && (it.extension == "txt" || it.extension == "json") } ?: 0
        } else {
            0
        }
    }

    // 设置错误消息
    fun setErrorMessage(message: String) {
        _errorMessage.value = message
    }
    
    fun clearError() {
        _errorMessage.value = null
    }
}
