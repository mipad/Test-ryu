// ModViewModel.kt
package org.ryujinx.android.viewmodels

import android.util.Log
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.ryujinx.android.RyujinxNative
import java.io.File

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

    // 添加状态跟踪
    private var _lastLoadTime = 0L
    private var _loadCount = 0

    fun loadMods(titleId: String, forceRefresh: Boolean = false) {
        var actualForceRefresh = forceRefresh
        
        // 如果解析失败过，强制重新加载
        if (_parseFailed) {
            actualForceRefresh = true
            _parseFailed = false
        }
        
        // 避免过于频繁的加载
        val now = System.currentTimeMillis()
        if (!actualForceRefresh && _hasLoaded.value && (now - _lastLoadTime < 1000)) {
            Log.d("ModViewModel", "跳过加载，距离上次加载时间太短")
            return
        }
        
        if (_hasLoaded.value && !actualForceRefresh) {
            Log.d("ModViewModel", "Mods already loaded, skipping")
            return
        }

        viewModelScope.launch {
            _isLoading.value = true
            _errorMessage.value = null
            _loadCount++
            Log.d("ModViewModel", "开始加载mods (尝试次数: $_loadCount)")
            
            try {
                val modsJson = RyujinxNative.getMods(titleId)
                Log.d("ModViewModel", "Raw mods JSON received, length: ${modsJson.length}")
                Log.d("ModViewModel", "JSON内容 (前500字符): ${modsJson.take(500)}")
                
                if (modsJson.isNotEmpty()) {
                    val success = parseModsJson(modsJson)
                    if (success) {
                        _hasLoaded.value = true
                        _lastLoadTime = now
                        Log.d("ModViewModel", "成功加载 ${_mods.size} 个mods")
                    } else {
                        // 解析失败，标记为解析失败，下次强制重新加载
                        _parseFailed = true
                        Log.e("ModViewModel", "解析mods JSON失败")
                        
                        // 解析失败时，清空列表并显示错误
                        withContext(Dispatchers.Main) {
                            _mods.clear()
                        }
                        _errorMessage.value = "解析mods列表失败，可能是数据格式错误"
                    }
                } else {
                    Log.d("ModViewModel", "空的mods响应")
                    withContext(Dispatchers.Main) {
                        _mods.clear()
                    }
                    _hasLoaded.value = true
                    _lastLoadTime = now
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "加载mods时出错", e)
                _errorMessage.value = "加载mods失败: ${e.message}"
                // 发生异常时也标记为解析失败
                _parseFailed = true
                
                // 异常时清空列表
                withContext(Dispatchers.Main) {
                    _mods.clear()
                }
            } finally {
                _isLoading.value = false
                Log.d("ModViewModel", "加载完成，当前mods数量: ${_mods.size}")
            }
        }
    }

    private fun parseModsJson(jsonString: String): Boolean {
        try {
            Log.d("ModViewModel", "开始解析JSON")
            
            val cleanJson = jsonString.trim()
            if (cleanJson.isEmpty()) {
                Log.w("ModViewModel", "空的JSON字符串")
                return true // 空JSON视为成功，只是没有mod
            }

            // 验证JSON格式
            if (!cleanJson.startsWith("[") || !cleanJson.endsWith("]")) {
                Log.e("ModViewModel", "无效的JSON格式，不是数组: ${cleanJson.take(100)}")
                return false
            }

            // 尝试解析JSON数组
            val jsonArray = JSONArray(cleanJson)
            Log.d("ModViewModel", "JSON数组长度: ${jsonArray.length()}")
            
            val newMods = mutableListOf<ModModel>()
            
            for (i in 0 until jsonArray.length()) {
                try {
                    val modJson = jsonArray.getJSONObject(i)
                    
                    // 验证必需的字段
                    val name = modJson.optString("name", "Unknown Mod")
                    val path = modJson.optString("path", "")
                    
                    if (name.isEmpty() || path.isEmpty()) {
                        Log.w("ModViewModel", "跳过无效的mod数据，缺少名称或路径")
                        continue
                    }
                    
                    val mod = ModModel(
                        name = name,
                        path = path,
                        enabled = modJson.optBoolean("enabled", false),
                        inExternalStorage = modJson.optBoolean("inExternalStorage", false),
                        type = when (modJson.optString("type", "RomFs")) {
                            "RomFs" -> ModType.RomFs
                            "ExeFs" -> ModType.ExeFs
                            else -> ModType.RomFs
                        }
                    )
                    
                    Log.d("ModViewModel", "解析mod: ${mod.name} (启用: ${mod.enabled}, 路径: ${mod.path})")
                    newMods.add(mod)
                } catch (e: Exception) {
                    Log.e("ModViewModel", "解析索引 $i 的mod时出错", e)
                    // 跳过这个mod，继续处理下一个
                }
            }
            
            // 一次性更新列表
            _mods.clear()
            _mods.addAll(newMods)
            Log.d("ModViewModel", "最终mods数量: ${_mods.size}")
            
            return true // 解析成功
            
        } catch (e: Exception) {
            Log.e("ModViewModel", "解析mods JSON失败", e)
            _errorMessage.value = "解析mods失败: ${e.message}\nJSON: ${jsonString.take(500)}"
            return false // 解析失败
        }
    }

    fun setModEnabled(titleId: String, mod: ModModel, enabled: Boolean) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "设置mod ${mod.name} 启用状态: $enabled")
                val success = RyujinxNative.setModEnabled(titleId, mod.path, enabled)
                
                if (success) {
                    // 在主线程更新UI状态
                    withContext(Dispatchers.Main) {
                        val index = _mods.indexOfFirst { it.path == mod.path }
                        if (index != -1) {
                            _mods[index] = _mods[index].copy(enabled = enabled)
                            Log.d("ModViewModel", "在UI中更新mod状态")
                        } else {
                            Log.w("ModViewModel", "未找到要更新的mod: ${mod.name}")
                        }
                    }
                } else {
                    Log.e("ModViewModel", "Native调用设置mod状态失败")
                    _errorMessage.value = "更新mod状态失败"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "设置mod启用状态时出错", e)
                _errorMessage.value = "错误: ${e.message}"
            }
        }
    }

    fun deleteMod(titleId: String, mod: ModModel) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "开始删除mod: ${mod.name}")
                val success = RyujinxNative.deleteMod(titleId, mod.path)
                Log.d("ModViewModel", "删除mod结果: $success")
                
                if (success) {
                    // 延迟一下确保操作完成
                    delay(300)
                    // 重新加载列表
                    loadMods(titleId, true)
                    
                    // 等待加载完成
                    while (isLoading) {
                        delay(100)
                    }
                } else {
                    _errorMessage.value = "删除mod失败"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "删除mod时出错", e)
                _errorMessage.value = "错误: ${e.message}"
            }
        }
    }

    fun deleteAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "开始删除所有mods")
                val success = RyujinxNative.deleteAllMods(titleId)
                Log.d("ModViewModel", "删除所有mods结果: $success")
                
                if (success) {
                    // 延迟一下确保操作完成
                    delay(500)
                    // 重新加载列表
                    loadMods(titleId, true)
                    
                    // 等待加载完成
                    while (isLoading) {
                        delay(100)
                    }
                } else {
                    _errorMessage.value = "删除所有mods失败"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "删除所有mods时出错", e)
                _errorMessage.value = "错误: ${e.message}"
            }
        }
    }

    fun enableAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                val success = RyujinxNative.enableAllMods(titleId)
                if (success) {
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    _errorMessage.value = "启用所有mods失败"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "启用所有mods时出错", e)
                _errorMessage.value = "错误: ${e.message}"
            }
        }
    }

    fun disableAllMods(titleId: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                val success = RyujinxNative.disableAllMods(titleId)
                if (success) {
                    // 重新加载列表
                    loadMods(titleId, true)
                } else {
                    _errorMessage.value = "禁用所有mods失败"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "禁用所有mods时出错", e)
                _errorMessage.value = "错误: ${e.message}"
            }
        }
    }

    fun addMod(titleId: String, sourcePath: String, modName: String) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                Log.d("ModViewModel", "开始添加Mod: $modName")
                Log.d("ModViewModel", "源路径: $sourcePath")
                Log.d("ModViewModel", "目标游戏ID: $titleId")
                
                // 验证源路径
                val sourceFile = File(sourcePath)
                if (!sourceFile.exists()) {
                    Log.e("ModViewModel", "源路径不存在: $sourcePath")
                    _errorMessage.value = "错误: 源文件夹不存在"
                    return@launch
                }
                
                if (!sourceFile.isDirectory) {
                    Log.e("ModViewModel", "源路径不是文件夹: $sourcePath")
                    _errorMessage.value = "错误: 请选择文件夹而不是文件"
                    return@launch
                }
                
                // 检查文件夹是否为空
                val files = sourceFile.listFiles()
                if (files == null || files.isEmpty()) {
                    Log.w("ModViewModel", "源文件夹为空: $sourcePath")
                    _errorMessage.value = "警告: 选择的文件夹为空"
                }
                
                Log.d("ModViewModel", "调用Native函数添加mod")
                val success = RyujinxNative.addMod(titleId, sourcePath, modName)
                Log.d("ModViewModel", "添加mod结果: $success")
                
                if (success) {
                    Log.d("ModViewModel", "Mod添加成功: $modName")
                    // 延迟一下确保文件操作完成
                    delay(500)
                } else {
                    Log.e("ModViewModel", "Native函数返回添加mod失败")
                    _errorMessage.value = "添加mod失败，请检查日志"
                }
            } catch (e: Exception) {
                Log.e("ModViewModel", "添加mod时出错", e)
                _errorMessage.value = "添加mod时出错: ${e.message}"
            }
        }
    }

    fun clearError() {
        _errorMessage.value = null
    }

    fun resetLoadedState() {
        _hasLoaded.value = false
        _parseFailed = false
        _loadCount = 0
        Log.d("ModViewModel", "重置加载状态")
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