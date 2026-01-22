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
            Log.d("ModViewModel", "另一个加载操作正在进行中，跳过")
            return
        }
        
        // 如果titleId没有变化且已经加载过且不是强制刷新，直接返回
        if (_currentTitleId == titleId && _hasLoaded.value && !forceRefresh && !_parseFailed) {
            Log.d("ModViewModel", "Mod已经加载过，跳过")
            _loadingLock.set(false)
            return
        }
        
        // 更新当前titleId
        _currentTitleId = titleId
        
        viewModelScope.launch {
            _isLoading.value = true
            _errorMessage.value = null
            
            try {
                Log.d("ModViewModel", "加载Mod列表: $titleId")
                
                // 在IO线程执行Native调用
                val modsJson = withContext(Dispatchers.IO) {
                    RyujinxNative.getMods(titleId)
                }
                
                Log.d("ModViewModel", "收到原始Mod JSON，长度: ${modsJson.length}")
                
                if (modsJson.isNotEmpty()) {
                    val success = parseModsJson(modsJson)
                    if (success) {
                        _hasLoaded.value = true
                        _parseFailed = false
                        Log.d("ModViewModel", "成功加载 ${_mods.size} 个Mod")
                    } else {
                        // 解析失败，标记为解析失败
                        _parseFailed = true
                        Log.e("ModViewModel", "解析Mod JSON失败")
                        _errorMessage.value = "解析Mod数据失败"
                    }
                } else {
                    Log.d("ModViewModel", "Mod响应为空")
                    // 在主线程清空列表
                    _mods.clear()
                    _hasLoaded.value = true
                    _parseFailed = false
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "加载Mod时出错", e)
                _errorMessage.value = "加载Mod失败: ${e.message}"
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
            Log.d("ModViewModel", "解析JSON: ${jsonString.take(200)}...")
            
            val cleanJson = jsonString.trim()
            if (cleanJson.isEmpty() || cleanJson == "[]" || cleanJson == "{}") {
                Log.w("ModViewModel", "JSON字符串为空或空数组")
                // 在主线程清空列表
                viewModelScope.launch(Dispatchers.Main) {
                    _mods.clear()
                }
                return true // 空JSON视为成功
            }

            // 尝试解析JSON数组
            val jsonArray = JSONArray(cleanJson)
            Log.d("ModViewModel", "JSON数组长度: ${jsonArray.length()}")
            
            val newMods = mutableListOf<ModModel>()
            
            for (i in 0 until jsonArray.length()) {
                try {
                    val modJson = jsonArray.getJSONObject(i)
                    val mod = ModModel(
                        name = modJson.optString("name", "未知Mod").takeIf { it.isNotBlank() } ?: "未知Mod",
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
                        Log.d("ModViewModel", "解析Mod: ${mod.name} (启用: ${mod.enabled}, 类型: ${mod.type})")
                        newMods.add(mod)
                    } else {
                        Log.w("ModViewModel", "跳过路径为空的Mod: ${mod.name}")
                    }
                } catch (e: Exception) {
                    Log.e("ModViewModel", "解析索引 $i 的Mod时出错", e)
                }
            }
            
            // 在主线程更新列表
            viewModelScope.launch(Dispatchers.Main) {
                _mods.clear()
                _mods.addAll(newMods)
                Log.d("ModViewModel", "最终Mod数量: ${_mods.size}")
            }
            
            true // 解析成功
            
        } catch (e: Exception) {
            Log.e("ModViewModel", "解析Mod JSON失败", e)
            Log.e("ModViewModel", "JSON内容: ${jsonString.take(500)}")
            
            _errorMessage.value = "解析Mod失败: ${e.message}"
            false // 解析失败
        }
    }

    fun setModEnabled(titleId: String, mod: ModModel, enabled: Boolean) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "设置Mod ${mod.name} 启用状态: $enabled")
                val success = RyujinxNative.setModEnabled(titleId, mod.path, enabled)
                
                if (success) {
                    // 在主线程更新UI状态
                    viewModelScope.launch(Dispatchers.Main) {
                        val index = _mods.indexOfFirst { it.path == mod.path }
                        if (index != -1) {
                            _mods[index] = _mods[index].copy(enabled = enabled)
                            Log.d("ModViewModel", "在UI中更新Mod状态: ${mod.name}")
                        } else {
                            Log.w("ModViewModel", "在列表中找不到Mod: ${mod.name}")
                        }
                    }
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "更新Mod状态失败"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "设置Mod启用状态时出错", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "错误: ${e.message}"
                }
            }
        }
    }

    fun deleteMod(titleId: String, mod: ModModel) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "删除Mod: ${mod.name} 路径: ${mod.path}")
                val success = RyujinxNative.deleteMod(titleId, mod.path)
                
                if (success) {
                    // 在主线程更新列表
                    viewModelScope.launch(Dispatchers.Main) {
                        val index = _mods.indexOfFirst { it.path == mod.path }
                        if (index != -1) {
                            _mods.removeAt(index)
                            Log.d("ModViewModel", "从UI列表中移除Mod: ${mod.name}")
                        }
                    }
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "删除Mod失败"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "删除Mod时出错", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "错误: ${e.message}"
                }
            }
        }
    }

    fun deleteAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "删除所有Mod: $titleId")
                val success = RyujinxNative.deleteAllMods(titleId)
                
                if (success) {
                    // 在主线程清空列表
                    viewModelScope.launch(Dispatchers.Main) {
                        _mods.clear()
                        _hasLoaded.value = false
                        Log.d("ModViewModel", "从UI列表中清空所有Mod")
                    }
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "删除所有Mod失败"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "删除所有Mod时出错", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "错误: ${e.message}"
                }
            }
        }
    }

    fun enableAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "启用所有Mod: $titleId")
                val success = RyujinxNative.enableAllMods(titleId)
                
                if (success) {
                    // 延迟后重新加载列表
                    delay(500) // 给系统时间处理文件
                    loadMods(titleId, true)
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "启用所有Mod失败"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "启用所有Mod时出错", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "错误: ${e.message}"
                }
            }
        }
    }

    fun disableAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "禁用所有Mod: $titleId")
                val success = RyujinxNative.disableAllMods(titleId)
                
                if (success) {
                    // 延迟后重新加载列表
                    delay(500) // 给系统时间处理文件
                    loadMods(titleId, true)
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "禁用所有Mod失败"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "禁用所有Mod时出错", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "错误: ${e.message}"
                }
            }
        }
    }

    fun addMod(titleId: String, sourcePath: String, modName: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "添加Mod: $modName 从 $sourcePath 到 $titleId")
                val success = RyujinxNative.addMod(titleId, sourcePath, modName)
                Log.d("ModViewModel", "添加Mod结果: $success")
                
                if (success) {
                    // 延迟一段时间后重新加载列表，给系统时间处理文件
                    delay(1500) // 增加到1.5秒
                    
                    // 重置加载状态，强制重新加载
                    _hasLoaded.value = false
                    _parseFailed = false
                    
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    viewModelScope.launch(Dispatchers.Main) {
                        _errorMessage.value = "添加Mod失败"
                    }
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "添加Mod时出错", e)
                viewModelScope.launch(Dispatchers.Main) {
                    _errorMessage.value = "错误: ${e.message}"
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