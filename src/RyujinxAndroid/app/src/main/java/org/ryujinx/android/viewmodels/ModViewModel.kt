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

    private val _errorMessage = mutableStateOf<String?>(null)
    val errorMessage: String? get() = _errorMessage.value

    // 刷新Mod列表（直接从Native获取）
    fun refreshMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                val modsJson = RyujinxNative.getMods(titleId)
                val newMods = parseModsJson(modsJson)
                
                // 在主线程更新UI
                viewModelScope.launch(Dispatchers.Main) {
                    _mods.clear()
                    _mods.addAll(newMods)
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "刷新Mod列表失败", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "刷新失败: ${e.message}"
                }
            }
        }
    }

    private fun parseModsJson(jsonString: String): List<ModModel> {
        val mods = mutableListOf<ModModel>()
        
        try {
            val cleanJson = jsonString.trim()
            if (cleanJson.isEmpty() || cleanJson == "[]" || cleanJson == "{}") {
                return emptyList()
            }

            val jsonArray = JSONArray(cleanJson)
            
            for (i in 0 until jsonArray.length()) {
                try {
                    val modJson = jsonArray.getJSONObject(i)
                    val mod = ModModel(
                        name = modJson.optString("name", "未知Mod"),
                        path = modJson.optString("path", ""),
                        enabled = modJson.optBoolean("enabled", false),
                        inExternalStorage = modJson.optBoolean("inExternalStorage", false),
                        type = when (modJson.optString("type", "RomFs").lowercase()) {
                            "romfs" -> ModType.RomFs
                            "exefs" -> ModType.ExeFs
                            else -> ModType.RomFs
                        }
                    )
                    
                    if (mod.path.isNotBlank()) {
                        mods.add(mod)
                    }
                } catch (e: Exception) {
                    // 忽略单个Mod解析错误
                }
            }
        } catch (e: Exception) {
            Log.e("ModViewModel", "解析Mod JSON失败", e)
        }
        
        return mods
    }

    fun setModEnabled(titleId: String, mod: ModModel, enabled: Boolean) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.setModEnabled(titleId, mod.path, enabled)
                
                // 直接在UI中更新状态，不需要重新加载
                viewModelScope.launch(Dispatchers.Main) {
                    val index = _mods.indexOfFirst { it.path == mod.path }
                    if (index != -1) {
                        _mods[index] = _mods[index].copy(enabled = enabled)
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "设置Mod启用状态失败", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "设置失败: ${e.message}"
                }
            }
        }
    }

    fun deleteMod(titleId: String, mod: ModModel) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.deleteMod(titleId, mod.path)
                
                // 直接从列表中移除
                viewModelScope.launch(Dispatchers.Main) {
                    val index = _mods.indexOfFirst { it.path == mod.path }
                    if (index != -1) {
                        _mods.removeAt(index)
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "删除Mod失败", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "删除失败: ${e.message}"
                }
            }
        }
    }

    fun deleteAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.deleteAllMods(titleId)
                
                // 直接清空列表
                viewModelScope.launch(Dispatchers.Main) {
                    _mods.clear()
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "删除所有Mod失败", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "删除所有失败: ${e.message}"
                }
            }
        }
    }

    fun enableAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.enableAllMods(titleId)
                
                // 更新列表中所有Mod为启用
                viewModelScope.launch(Dispatchers.Main) {
                    for (i in _mods.indices) {
                        _mods[i] = _mods[i].copy(enabled = true)
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "启用所有Mod失败", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "启用所有失败: ${e.message}"
                }
            }
        }
    }

    fun disableAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.disableAllMods(titleId)
                
                // 更新列表中所有Mod为禁用
                viewModelScope.launch(Dispatchers.Main) {
                    for (i in _mods.indices) {
                        _mods[i] = _mods[i].copy(enabled = false)
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "禁用所有Mod失败", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "禁用所有失败: ${e.message}"
                }
            }
        }
    }

    fun addMod(titleId: String, sourcePath: String, modName: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                RyujinxNative.addMod(titleId, sourcePath, modName)
                
                // 添加成功后重新获取完整列表
                refreshMods(titleId)
            } catch (e: Exception) {
                Log.e("ModViewModel", "添加Mod失败", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "添加失败: ${e.message}"
                }
            }
        }
    }

    fun clearError() {
        _errorMessage.value = null
    }
    
    fun clearMods() {
        _mods.clear()
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