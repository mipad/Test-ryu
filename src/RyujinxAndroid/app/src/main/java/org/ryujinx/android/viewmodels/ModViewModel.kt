// ModViewModel.kt
package org.ryujinx.android.viewmodels

import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.ryujinx.android.RyujinxNative
import org.json.JSONArray
import org.json.JSONObject
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken

class ModViewModel : ViewModel() {
    private var _mods = mutableStateListOf<ModModel>()
    val mods: List<ModModel> get() = _mods

    private var _isLoading = mutableStateOf(false)
    val isLoading: Boolean get() = _isLoading.value

    private var _errorMessage = mutableStateOf<String?>(null)
    val errorMessage: String? get() = _errorMessage.value

    private var _selectedMods = mutableStateListOf<ModModel>()
    val selectedMods: List<ModModel> get() = _selectedMods

    // 使用 Gson 进行 JSON 解析
    private val gson = Gson()

    suspend fun loadMods(titleId: String) {
        _isLoading.value = true
        _errorMessage.value = null
        
        try {
            withContext(Dispatchers.IO) {
                val modsJson = RyujinxNative.getMods(titleId)
                if (modsJson.isNotEmpty()) {
                    parseModsJson(modsJson)
                } else {
                    _mods.clear()
                    _selectedMods.clear()
                }
            }
        } catch (e: Exception) {
            _errorMessage.value = "Failed to load mods: ${e.message}"
            _mods.clear()
            _selectedMods.clear()
        } finally {
            _isLoading.value = false
        }
    }

    private fun parseModsJson(jsonString: String) {
        _mods.clear()
        _selectedMods.clear()
        
        try {
            // 使用 Gson 解析 JSON 数组
            val listType = object : TypeToken<List<ModJsonModel>>() {}.type
            val modJsonList: List<ModJsonModel> = gson.fromJson(jsonString, listType)
            
            modJsonList.forEach { modJson ->
                val mod = ModModel(
                    name = modJson.name ?: "Unknown",
                    path = modJson.path ?: "",
                    enabled = modJson.enabled ?: false,
                    inExternalStorage = modJson.inExternalStorage ?: false,
                    type = when (modJson.type) {
                        "RomFs" -> ModType.RomFs
                        "ExeFs" -> ModType.ExeFs
                        else -> ModType.RomFs
                    }
                )
                
                _mods.add(mod)
                if (mod.enabled) {
                    _selectedMods.add(mod)
                }
            }
        } catch (e: Exception) {
            // 如果 Gson 解析失败，尝试回退到 Android JSON 解析
            try {
                parseModsJsonWithAndroidJson(jsonString)
            } catch (e2: Exception) {
                _errorMessage.value = "Failed to parse mods data: ${e2.message}\nJSON: ${jsonString.take(200)}..."
            }
        }
    }

    private fun parseModsJsonWithAndroidJson(jsonString: String) {
        _mods.clear()
        _selectedMods.clear()
        
        try {
            val jsonArray = JSONArray(jsonString)
            for (i in 0 until jsonArray.length()) {
                val modJson = jsonArray.getJSONObject(i)
                val mod = ModModel(
                    name = modJson.optString("name", "Unknown"),
                    path = modJson.optString("path", ""),
                    enabled = modJson.optBoolean("enabled", false),
                    inExternalStorage = modJson.optBoolean("inExternalStorage", false),
                    type = when (modJson.optString("type", "RomFs")) {
                        "RomFs" -> ModType.RomFs
                        "ExeFs" -> ModType.ExeFs
                        else -> ModType.RomFs
                    }
                )
                
                _mods.add(mod)
                if (mod.enabled) {
                    _selectedMods.add(mod)
                }
            }
        } catch (e: Exception) {
            throw e
        }
    }

    // 其余方法保持不变...
    suspend fun setModEnabled(titleId: String, mod: ModModel, enabled: Boolean): Boolean {
        return withContext(Dispatchers.IO) {
            try {
                val success = RyujinxNative.setModEnabled(titleId, mod.path, enabled)
                if (success) {
                    // 更新本地状态
                    val index = _mods.indexOfFirst { it.path == mod.path }
                    if (index != -1) {
                        _mods[index] = _mods[index].copy(enabled = enabled)
                        
                        if (enabled) {
                            if (!_selectedMods.any { it.path == mod.path }) {
                                _selectedMods.add(_mods[index])
                            }
                        } else {
                            _selectedMods.removeAll { it.path == mod.path }
                        }
                    }
                }
                success
            } catch (e: Exception) {
                _errorMessage.value = "Failed to set mod enabled: ${e.message}"
                false
            }
        }
    }

    suspend fun deleteMod(titleId: String, mod: ModModel): Boolean {
        return withContext(Dispatchers.IO) {
            try {
                val success = RyujinxNative.deleteMod(titleId, mod.path)
                if (success) {
                    _mods.remove(mod)
                    _selectedMods.remove(mod)
                }
                success
            } catch (e: Exception) {
                _errorMessage.value = "Failed to delete mod: ${e.message}"
                false
            }
        }
    }

    suspend fun deleteAllMods(titleId: String): Boolean {
        return withContext(Dispatchers.IO) {
            try {
                val success = RyujinxNative.deleteAllMods(titleId)
                if (success) {
                    _mods.clear()
                    _selectedMods.clear()
                }
                success
            } catch (e: Exception) {
                _errorMessage.value = "Failed to delete all mods: ${e.message}"
                false
            }
        }
    }

    suspend fun enableAllMods(titleId: String): Boolean {
        return withContext(Dispatchers.IO) {
            try {
                val success = RyujinxNative.enableAllMods(titleId)
                if (success) {
                    // 更新本地状态
                    _mods.forEachIndexed { index, mod ->
                        _mods[index] = mod.copy(enabled = true)
                    }
                    _selectedMods.clear()
                    _selectedMods.addAll(_mods)
                }
                success
            } catch (e: Exception) {
                _errorMessage.value = "Failed to enable all mods: ${e.message}"
                false
            }
        }
    }

    suspend fun disableAllMods(titleId: String): Boolean {
        return withContext(Dispatchers.IO) {
            try {
                val success = RyujinxNative.disableAllMods(titleId)
                if (success) {
                    // 更新本地状态
                    _mods.forEachIndexed { index, mod ->
                        _mods[index] = mod.copy(enabled = false)
                    }
                    _selectedMods.clear()
                }
                success
            } catch (e: Exception) {
                _errorMessage.value = "Failed to disable all mods: ${e.message}"
                false
            }
        }
    }

    suspend fun addMod(titleId: String, sourcePath: String, modName: String): Boolean {
        return withContext(Dispatchers.IO) {
            try {
                val success = RyujinxNative.addMod(titleId, sourcePath, modName)
                if (success) {
                    // 重新加载Mod列表
                    loadMods(titleId)
                }
                success
            } catch (e: Exception) {
                _errorMessage.value = "Failed to add mod: ${e.message}"
                false
            }
        }
    }

    fun clearError() {
        _errorMessage.value = null
    }

    fun onModSelectionChanged(mod: ModModel, selected: Boolean) {
        if (selected && !_selectedMods.any { it.path == mod.path }) {
            _selectedMods.add(mod)
        } else if (!selected) {
            _selectedMods.removeAll { it.path == mod.path }
        }
    }
}

// 用于 Gson 解析的中间数据类
data class ModJsonModel(
    val name: String? = null,
    val path: String? = null,
    val enabled: Boolean? = null,
    val inExternalStorage: Boolean? = null,
    val type: String? = null
)

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
