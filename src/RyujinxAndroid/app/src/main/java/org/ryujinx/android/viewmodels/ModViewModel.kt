// ModViewModel.kt
package org.ryujinx.android.viewmodels

import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.ryujinx.android.RyujinxNative
import org.json.JSONArray
import android.util.Log
import java.util.concurrent.atomic.AtomicBoolean

class ModViewModel : ViewModel() {
    private val _mods = mutableStateListOf<ModModel>()
    val mods: List<ModModel> get() = _mods

    private val _isLoading = mutableStateOf(false)
    val isLoading: Boolean get() = _isLoading.value

    private val _errorMessage = mutableStateOf<String?>(null)
    val errorMessage: String? get() = _errorMessage.value

    // 添加一个状态来跟踪是否已经加载过
    private val _hasLoaded = mutableStateOf(false)
    val hasLoaded: Boolean get() = _hasLoaded.value

    // 添加一个状态来跟踪是否解析失败
    private var _parseFailed = false
    
    // 添加一个状态来跟踪当前正在加载的titleId
    private var _currentTitleId = ""
    
    // 添加一个锁来防止并发加载
    private val _loadingLock = AtomicBoolean(false)

    fun loadMods(titleId: String, forceRefresh: Boolean = false) {
        // 使用原子操作防止并发加载
        if (!_loadingLock.compareAndSet(false, true)) {
            Log.d("ModViewModel", "Another load operation is in progress, skipping")
            return
        }
        
        // 如果titleId没有变化且已经加载过且不是强制刷新，直接返回
        if (_currentTitleId == titleId && _hasLoaded.value && !forceRefresh && !_parseFailed) {
            Log.d("ModViewModel", "Mods already loaded for $titleId, skipping")
            _loadingLock.set(false)
            return
        }
        
        // 更新当前titleId
        _currentTitleId = titleId
        
        viewModelScope.launch {
            _isLoading.value = true
            _errorMessage.value = null
            
            try {
                Log.d("ModViewModel", "Loading mods for titleId: $titleId")
                
                // 在IO线程执行Native调用
                val modsJson = withContext(Dispatchers.IO) {
                    RyujinxNative.getMods(titleId)
                }
                
                Log.d("ModViewModel", "Raw mods JSON received, length: ${modsJson.length}")
                
                if (modsJson.isNotEmpty()) {
                    val success = parseModsJson(modsJson)
                    if (success) {
                        _hasLoaded.value = true
                        _parseFailed = false
                        Log.d("ModViewModel", "Successfully loaded ${_mods.size} mods for $titleId")
                    } else {
                        // 解析失败，标记为解析失败
                        _parseFailed = true
                        Log.e("ModViewModel", "Failed to parse mods JSON for $titleId")
                        _errorMessage.value = "Failed to parse mods data"
                    }
                } else {
                    Log.d("ModViewModel", "Empty mods response for $titleId")
                    // 在主线程清空列表
                    _mods.clear()
                    _hasLoaded.value = true
                    _parseFailed = false
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error loading mods for $titleId", e)
                _errorMessage.value = "Failed to load mods: ${e.message}"
                // 发生异常时标记为解析失败
                _parseFailed = true
            } finally {
                _isLoading.value = false
                _loadingLock.set(false)
            }
        }
    }

    private fun parseModsJson(jsonString: String): Boolean {
        return try {
            Log.d("ModViewModel", "Parsing JSON: ${jsonString.take(200)}...")
            
            val cleanJson = jsonString.trim()
            if (cleanJson.isEmpty() || cleanJson == "[]" || cleanJson == "{}") {
                Log.w("ModViewModel", "Empty JSON string or empty array")
                // 在主线程清空列表
                viewModelScope.launch(Dispatchers.Main) {
                    _mods.clear()
                }
                return true // 空JSON视为成功
            }

            // 尝试解析JSON数组
            val jsonArray = JSONArray(cleanJson)
            Log.d("ModViewModel", "JSON array length: ${jsonArray.length()}")
            
            val newMods = mutableListOf<ModModel>()
            
            for (i in 0 until jsonArray.length()) {
                try {
                    val modJson = jsonArray.getJSONObject(i)
                    val mod = ModModel(
                        name = modJson.optString("name", "Unknown Mod").takeIf { it.isNotBlank() } ?: "Unknown Mod",
                        path = modJson.optString("path", ""),
                        enabled = modJson.optBoolean("enabled", false),
                        inExternalStorage = modJson.optBoolean("inExternalStorage", false),
                        type = when (modJson.optString("type", "RomFs").lowercase()) {
                            "romfs" -> ModType.RomFs
                            "exefs" -> ModType.ExeFs
                            else -> ModType.RomFs
                        }
                    )
                    
                    // 验证mod数据
                    if (mod.path.isNotBlank()) {
                        Log.d("ModViewModel", "Parsed mod: ${mod.name} (enabled: ${mod.enabled}, type: ${mod.type})")
                        newMods.add(mod)
                    } else {
                        Log.w("ModViewModel", "Skipping mod with empty path: ${mod.name}")
                    }
                } catch (e: Exception) {
                    Log.e("ModViewModel", "Error parsing mod at index $i", e)
                }
            }
            
            // 在主线程更新列表
            viewModelScope.launch(Dispatchers.Main) {
                _mods.clear()
                _mods.addAll(newMods)
                Log.d("ModViewModel", "Final mods count: ${_mods.size}")
            }
            
            true // 解析成功
            
        } catch (e: Exception) {
            Log.e("ModViewModel", "Failed to parse mods JSON", e)
            Log.e("ModViewModel", "JSON content: ${jsonString.take(500)}")
            
            // 尝试处理可能的JSON格式问题
            if (jsonString.contains("[")) {
                try {
                    // 尝试手动解析简单的JSON数组
                    val manualMods = mutableListOf<ModModel>()
                    val lines = jsonString.split("},{")
                    for (line in lines) {
                        try {
                            val cleanedLine = line.replace("[", "").replace("]", "").replace("{", "").replace("}", "").trim()
                            if (cleanedLine.isNotEmpty()) {
                                val parts = cleanedLine.split(",")
                                var name = "Unknown Mod"
                                var path = ""
                                var enabled = false
                                
                                for (part in parts) {
                                    val keyValue = part.split(":")
                                    if (keyValue.size >= 2) {
                                        val key = keyValue[0].trim().replace("\"", "").lowercase()
                                        val value = keyValue[1].trim().replace("\"", "")
                                        
                                        when (key) {
                                            "name" -> name = value
                                            "path" -> path = value
                                            "enabled" -> enabled = value.lowercase() == "true"
                                        }
                                    }
                                }
                                
                                if (path.isNotBlank()) {
                                    manualMods.add(ModModel(
                                        name = name,
                                        path = path,
                                        enabled = enabled,
                                        inExternalStorage = path.contains("/storage/emulated/0"),
                                        type = ModType.RomFs
                                    ))
                                }
                            }
                        } catch (e: Exception) {
                            Log.e("ModViewModel", "Error parsing line: $line", e)
                        }
                    }
                    
                    if (manualMods.isNotEmpty()) {
                        viewModelScope.launch(Dispatchers.Main) {
                            _mods.clear()
                            _mods.addAll(manualMods)
                        }
                        Log.d("ModViewModel", "Manually parsed ${manualMods.size} mods")
                        return true
                    }
                } catch (e: Exception) {
                    Log.e("ModViewModel", "Failed to manually parse JSON", e)
                }
            }
            
            _errorMessage.value = "Failed to parse mods: ${e.message}"
            false // 解析失败
        }
    }

    fun setModEnabled(titleId: String, mod: ModModel, enabled: Boolean) {
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
                            Log.d("ModViewModel", "Updated mod state in UI for: ${mod.name}")
                        } else {
                            Log.w("ModViewModel", "Mod not found in list: ${mod.name}")
                        }
                    }
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "Failed to update mod state"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error setting mod enabled", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "Error: ${e.message}"
                }
            }
        }
    }

    fun deleteMod(titleId: String, mod: ModModel) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "Deleting mod: ${mod.name} at ${mod.path}")
                val success = RyujinxNative.deleteMod(titleId, mod.path)
                
                if (success) {
                    // 在主线程更新列表
                    viewModelScope.launch(Dispatchers.Main) {
                        val index = _mods.indexOfFirst { it.path == mod.path }
                        if (index != -1) {
                            _mods.removeAt(index)
                            Log.d("ModViewModel", "Removed mod from UI list: ${mod.name}")
                        }
                    }
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "Failed to delete mod"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error deleting mod", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "Error: ${e.message}"
                }
            }
        }
    }

    fun deleteAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "Deleting all mods for: $titleId")
                val success = RyujinxNative.deleteAllMods(titleId)
                
                if (success) {
                    // 在主线程清空列表
                    viewModelScope.launch(Dispatchers.Main) {
                        _mods.clear()
                        _hasLoaded.value = false
                        Log.d("ModViewModel", "Cleared all mods from UI list")
                    }
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "Failed to delete all mods"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error deleting all mods", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "Error: ${e.message}"
                }
            }
        }
    }

    fun enableAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "Enabling all mods for: $titleId")
                val success = RyujinxNative.enableAllMods(titleId)
                
                if (success) {
                    // 延迟后重新加载列表
                    delay(500) // 给系统时间处理文件
                    loadMods(titleId, true)
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "Failed to enable all mods"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error enabling all mods", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "Error: ${e.message}"
                }
            }
        }
    }

    fun disableAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "Disabling all mods for: $titleId")
                val success = RyujinxNative.disableAllMods(titleId)
                
                if (success) {
                    // 延迟后重新加载列表
                    delay(500) // 给系统时间处理文件
                    loadMods(titleId, true)
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "Failed to disable all mods"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error disabling all mods", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "Error: ${e.message}"
                }
            }
        }
    }

    fun addMod(titleId: String, sourcePath: String, modName: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "Adding mod: $modName from $sourcePath for $titleId")
                val success = RyujinxNative.addMod(titleId, sourcePath, modName)
                Log.d("ModViewModel", "Add mod result: $success")
                
                if (success) {
                    // 延迟一段时间后重新加载列表，给系统时间处理文件
                    delay(1500) // 增加延迟到1.5秒
                    
                    // 重置加载状态，强制重新加载
                    _hasLoaded.value = false
                    _parseFailed = false
                    
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "Failed to add mod"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "Error adding mod", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "Error: ${e.message}"
                }
            }
        }
    }

    fun clearError() {
        _errorMessage.value = null
    }

    fun resetLoadedState() {
        _hasLoaded.value = false
        _parseFailed = false
        _currentTitleId = ""
        _loadingLock.set(false)
    }
    
    fun clearMods() {
        _mods.clear()
        _hasLoaded.value = false
        _parseFailed = false
        _currentTitleId = ""
        _loadingLock.set(false)
    }
    
    // 添加一个方法来手动刷新列表（带回调）
    fun refreshMods(titleId: String, onComplete: (() -> Unit)? = null) {
        viewModelScope.launch {
            resetLoadedState()
            loadMods(titleId, true)
            // 等待加载完成
            delay(1000)
            onComplete?.invoke()
        }
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