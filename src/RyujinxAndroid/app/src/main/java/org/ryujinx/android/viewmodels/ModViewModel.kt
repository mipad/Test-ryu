// ModViewModel.kt
package org.ryujinx.android.viewmodels

import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import org.ryujinx.android.RyujinxNative
import org.json.JSONArray
import android.util.Log

class ModViewModel : ViewModel() {
    private val _mods = mutableStateListOf<ModModel>()
    val mods: List<ModModel> get() = _mods

    private val _isLoading = mutableStateOf(false)
    val isLoading: Boolean get() = _isLoading.value

    private val _errorMessage = mutableStateOf<String?>(null)
    val errorMessage: String? get() = _errorMessage.value

    // 添加一个状态来跟踪是否已经加载过
    private val _hasLoaded = mutableStateOf(false)

    fun loadMods(titleId: String, forceRefresh: Boolean = false) {
        // 如果正在加载，直接返回
        if (_isLoading.value) {
            Log.d("ModViewModel", "Already loading, skipping")
            return
        }
        
        // 如果没有强制刷新且已经加载过，直接返回
        if (_hasLoaded.value && !forceRefresh) {
            Log.d("ModViewModel", "Mods already loaded and not forcing refresh, skipping")
            return
        }

        viewModelScope.launch {
            _isLoading.value = true
            _errorMessage.value = null
            
            try {
                val modsJson = RyujinxNative.getMods(titleId)
                Log.d("ModViewModel", "Raw mods JSON received, length: ${modsJson.length}")
                
                if (modsJson.isNotEmpty()) {
                    val success = parseModsJson(modsJson)
                    if (success) {
                        _hasLoaded.value = true
                        Log.d("ModViewModel", "Successfully loaded ${_mods.size} mods")
                    } else {
                        Log.e("ModViewModel", "Failed to parse mods JSON")
                        // 解析失败时清空列表
                        _mods.clear()
                    }
                } else {
                    Log.d("ModViewModel", "Empty mods response")
                    _mods.clear()
                    _hasLoaded.value = true
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error loading mods", e)
                _errorMessage.value = "Failed to load mods: ${e.message}"
                // 发生异常时清空列表
                _mods.clear()
            } finally {
                _isLoading.value = false
            }
        }
    }

    private fun parseModsJson(jsonString: String): Boolean {
        try {
            Log.d("ModViewModel", "Parsing JSON: ${jsonString.take(200)}...")
            
            val cleanJson = jsonString.trim()
            if (cleanJson.isEmpty()) {
                Log.w("ModViewModel", "Empty JSON string")
                _mods.clear()
                return true // 空JSON视为成功，只是没有mod
            }

            // 尝试解析JSON数组
            val jsonArray = JSONArray(cleanJson)
            Log.d("ModViewModel", "JSON array length: ${jsonArray.length()}")
            
            val newMods = mutableListOf<ModModel>()
            
            for (i in 0 until jsonArray.length()) {
                try {
                    val modJson = jsonArray.getJSONObject(i)
                    val mod = ModModel(
                        name = modJson.optString("name", "Unknown Mod"),
                        path = modJson.optString("path", ""),
                        enabled = modJson.optBoolean("enabled", false),
                        inExternalStorage = modJson.optBoolean("inExternalStorage", false),
                        type = when (modJson.optString("type", "RomFs")) {
                            "RomFs" -> ModType.RomFs
                            "ExeFs" -> ModType.ExeFs
                            else -> ModType.RomFs
                        }
                    )
                    
                    Log.d("ModViewModel", "Parsed mod: ${mod.name} (enabled: ${mod.enabled})")
                    newMods.add(mod)
                } catch (e: Exception) {
                    Log.e("ModViewModel", "Error parsing mod at index $i", e)
                }
            }
            
            // 一次性更新列表
            _mods.clear()
            _mods.addAll(newMods)
            Log.d("ModViewModel", "Final mods count: ${_mods.size}")
            
            return true // 解析成功
            
        } catch (e: Exception) {
            Log.e("ModViewModel", "Failed to parse mods JSON", e)
            _errorMessage.value = "Failed to parse mods: ${e.message}\nJSON: ${jsonString.take(500)}"
            return false // 解析失败
        }
    }

    fun setModEnabled(titleId: String, mod: ModModel, enabled: Boolean) {
        if (_isLoading.value) {
            Log.d("ModViewModel", "Currently loading, skipping mod enable/disable")
            return
        }
        
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "Setting mod ${mod.name} enabled: $enabled")
                val success = RyujinxNative.setModEnabled(titleId, mod.path, enabled)
                
                if (success) {
                    // 在主线程更新UI状态
                    viewModelScope.launch(Dispatchers.Main) {
                        val index = _mods.indexOfFirst { it.path == mod.path }
                        if (index != -1) {
                            _mods[index] = _mods[index].copy(enabled = enabled)
                            Log.d("ModViewModel", "Updated mod state in UI")
                        }
                    }
                } else {
                    _errorMessage.value = "Failed to update mod state"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error setting mod enabled", e)
                _errorMessage.value = "Error: ${e.message}"
            }
        }
    }

    fun deleteMod(titleId: String, mod: ModModel) {
        if (_isLoading.value) {
            Log.d("ModViewModel", "Currently loading, skipping delete")
            return
        }
        
        viewModelScope.launch(Dispatchers.IO) {
            _isLoading.value = true
            try {
                val success = RyujinxNative.deleteMod(titleId, mod.path)
                if (success) {
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    _errorMessage.value = "Failed to delete mod"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error deleting mod", e)
                _errorMessage.value = "Error: ${e.message}"
            } finally {
                _isLoading.value = false
            }
        }
    }

    fun deleteAllMods(titleId: String) {
        if (_isLoading.value) {
            Log.d("ModViewModel", "Currently loading, skipping delete all")
            return
        }
        
        viewModelScope.launch(Dispatchers.IO) {
            _isLoading.value = true
            try {
                val success = RyujinxNative.deleteAllMods(titleId)
                if (success) {
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    _errorMessage.value = "Failed to delete all mods"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error deleting all mods", e)
                _errorMessage.value = "Error: ${e.message}"
            } finally {
                _isLoading.value = false
            }
        }
    }

    fun enableAllMods(titleId: String) {
        if (_isLoading.value) {
            Log.d("ModViewModel", "Currently loading, skipping enable all")
            return
        }
        
        viewModelScope.launch(Dispatchers.IO) {
            _isLoading.value = true
            try {
                val success = RyujinxNative.enableAllMods(titleId)
                if (success) {
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    _errorMessage.value = "Failed to enable all mods"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error enabling all mods", e)
                _errorMessage.value = "Error: ${e.message}"
            } finally {
                _isLoading.value = false
            }
        }
    }

    fun disableAllMods(titleId: String) {
        if (_isLoading.value) {
            Log.d("ModViewModel", "Currently loading, skipping disable all")
            return
        }
        
        viewModelScope.launch(Dispatchers.IO) {
            _isLoading.value = true
            try {
                val success = RyujinxNative.disableAllMods(titleId)
                if (success) {
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    _errorMessage.value = "Failed to disable all mods"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error disabling all mods", e)
                _errorMessage.value = "Error: ${e.message}"
            } finally {
                _isLoading.value = false
            }
        }
    }

    fun addMod(titleId: String, sourcePath: String, modName: String) {
        if (_isLoading.value) {
            Log.d("ModViewModel", "Currently loading, skipping add mod")
            return
        }
        
        viewModelScope.launch(Dispatchers.IO) {
            _isLoading.value = true
            try {
                val success = RyujinxNative.addMod(titleId, sourcePath, modName)
                if (success) {
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    _errorMessage.value = "Failed to add mod"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error adding mod", e)
                _errorMessage.value = "Error: ${e.message}"
            } finally {
                _isLoading.value = false
            }
        }
    }

    fun clearError() {
        _errorMessage.value = null
    }

    fun resetLoadedState() {
        _hasLoaded.value = false
    }
}

data class ModModel(
    val name: String,
    val path: String,
    val enabled: Boolean,
    val inExternalStorage: Boolean,
    val type: ModType
)

enum class ModType {
    RomFs,
    ExeFs
}