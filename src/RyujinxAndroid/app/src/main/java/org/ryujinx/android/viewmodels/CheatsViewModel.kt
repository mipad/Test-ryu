package org.ryujinx.android.viewmodels

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.ryujinx.android.NativeLib

data class CheatItem(
    val id: String, // 格式为 "buildId-cheatName"
    val name: String,
    val enabled: Boolean
)

class CheatsViewModel(private val titleId: String, private val gamePath: String) : ViewModel() {
    private val _cheats = MutableStateFlow<List<CheatItem>>(emptyList())
    val cheats: StateFlow<List<CheatItem>> = _cheats
    
    private val _isLoading = MutableStateFlow(true)
    val isLoading: StateFlow<Boolean> = _isLoading
    
    private val _errorMessage = MutableStateFlow<String?>(null)
    val errorMessage: StateFlow<String?> = _errorMessage

    init {
        loadCheats()
    }

    private fun loadCheats() {
        viewModelScope.launch {
            try {
                _isLoading.value = true
                // 通过JNI调用获取金手指列表
                val cheatList = NativeLib.getCheats(titleId, gamePath)
                // 获取已启用的金手指列表
                val enabledCheats = NativeLib.getEnabledCheats(titleId)
                
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
                NativeLib.setCheatEnabled(titleId, cheatId, enabled)
                
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
                NativeLib.saveCheats(titleId)
            } catch (e: Exception) {
                _errorMessage.value = "Failed to save cheats: ${e.message}"
            }
        }
    }
    
    fun clearError() {
        _errorMessage.value = null
    }
}
