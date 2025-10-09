package org.ryujinx.android.viewmodels

import android.content.Context
import android.content.SharedPreferences
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.ryujinx.android.RyujinxNative
import java.io.*
import java.nio.charset.Charset

// 修改数据结构以支持分组 - 移到文件顶部
sealed class CheatListItem {
    data class GroupHeader(val fileName: String, val displayName: String) : CheatListItem()
    data class CheatItem(
        val id: String, // 格式为 "buildId-cheatName"
        val name: String,
        val enabled: Boolean,
        val groupName: String // 所属分组的显示名称
    ) : CheatListItem()
}

// 存储金手指文件的自定义名称
data class CheatFileInfo(
    val fileName: String,
    val displayName: String
)

// 编码检测结果
data class EncodingResult(
    val charset: Charset,
    val content: String,
    val detectedEncoding: String
)

class CheatsViewModel(
    private val titleId: String, 
    private val gamePath: String,
    private val packageName: String,
    private val context: Context // 添加 Context 参数用于 SharedPreferences
) : ViewModel() {
    private val _cheats = MutableStateFlow<List<CheatListItem>>(emptyList())
    val cheats: StateFlow<List<CheatListItem>> = _cheats
    
    private val _isLoading = MutableStateFlow(true)
    val isLoading: StateFlow<Boolean> = _isLoading
    
    private val _errorMessage = MutableStateFlow<String?>(null)
    val errorMessage: StateFlow<String?> = _errorMessage

    // 存储金手指文件的自定义名称映射
    private val _cheatFileNames = MutableStateFlow<Map<String, String>>(emptyMap())
    
    // 金手指目录路径 - 使用动态包名
    private val cheatsDir: File by lazy {
        File("/storage/emulated/0/Android/data/$packageName/files/mods/contents/$titleId/cheats")
    }

    // SharedPreferences 用于持久化存储自定义名称
    private val prefs: SharedPreferences by lazy {
        context.getSharedPreferences("cheat_display_names_$titleId", Context.MODE_PRIVATE)
    }

    // 常见编码列表，按可能性排序
    private val commonCharsets = listOf(
        Charset.forName("UTF-8"),
        Charset.forName("GBK"),
        Charset.forName("GB2312"),
        Charset.forName("BIG5"),
        Charset.forName("Shift_JIS"),
        Charset.forName("EUC-JP"),
        Charset.forName("ISO-8859-1"),
        Charset.forName("Windows-1252")
    )

    init {
        loadCheats()
        loadCheatFileNames()
    }

    private fun loadCheatFileNames() {
        viewModelScope.launch {
            try {
                val fileNames = mutableMapOf<String, String>()
                
                // 从 SharedPreferences 加载已保存的自定义名称
                val savedNames = prefs.all
                savedNames.forEach { (fileName, displayName) ->
                    if (displayName is String) {
                        fileNames[fileName] = displayName
                    }
                }
                
                // 同时扫描目录，确保新文件也有默认名称
                if (cheatsDir.exists() && cheatsDir.isDirectory) {
                    cheatsDir.listFiles()?.forEach { file ->
                        if (file.isFile && 
                            (file.extension == "txt" || file.extension == "json") &&
                            !file.name.equals("enabled.txt", ignoreCase = true)) {
                            // 如果还没有自定义名称，使用文件名（不带扩展名）作为默认显示名称
                            if (!fileNames.containsKey(file.name)) {
                                fileNames[file.name] = file.nameWithoutExtension
                                // 保存默认名称到 SharedPreferences
                                saveDisplayNameToPrefs(file.name, file.nameWithoutExtension)
                            }
                        }
                    }
                }
                
                _cheatFileNames.value = fileNames
            } catch (e: Exception) {
                // 忽略错误，使用空映射
                _cheatFileNames.value = emptyMap()
            }
        }
    }

    // 保存显示名称到 SharedPreferences
    private fun saveDisplayNameToPrefs(fileName: String, displayName: String) {
        prefs.edit().putString(fileName, displayName).apply()
    }

    // 从 SharedPreferences 删除显示名称
    private fun removeDisplayNameFromPrefs(fileName: String) {
        prefs.edit().remove(fileName).apply()
    }

    private fun loadCheats() {
        viewModelScope.launch {
            try {
                _isLoading.value = true
                // 通过JNI调用获取金手指列表
                val cheatList = RyujinxNative.getCheats(titleId, gamePath)
                // 获取已启用的金手指列表
                val enabledCheats = RyujinxNative.getEnabledCheats(titleId)
                
                // 按buildId分组
                val groupedCheats = cheatList.groupBy { cheatId ->
                    cheatId.substringBefore('-')
                }
                
                // 构建分组的CheatItem列表
                val cheatItems = mutableListOf<CheatListItem>()
                
                groupedCheats.forEach { (buildId, cheatIds) ->
                    // 查找对应的文件名和显示名称
                    val fileInfo = findFileInfoByBuildId(buildId)
                    
                    // 添加分组标题
                    cheatItems.add(CheatListItem.GroupHeader(
                        fileName = fileInfo.fileName,
                        displayName = fileInfo.displayName
                    ))
                    
                    // 添加该分组下的金手指项
                    cheatIds.forEach { cheatId ->
                        val name = cheatId.substringAfterLast('-')
                        cheatItems.add(CheatListItem.CheatItem(
                            id = cheatId,
                            name = name,
                            enabled = enabledCheats.contains(cheatId),
                            groupName = fileInfo.displayName
                        ))
                    }
                }
                
                _cheats.value = cheatItems
            } catch (e: Exception) {
                _errorMessage.value = "Failed to load cheats: ${e.message}"
            } finally {
                _isLoading.value = false
            }
        }
    }

    // 根据buildId查找对应的文件信息和显示名称
    private fun findFileInfoByBuildId(buildId: String): CheatFileInfo {
        // 首先尝试精确匹配文件名
        val exactMatch = _cheatFileNames.value.keys.firstOrNull { fileName ->
            fileName.startsWith(buildId) || fileName.contains(buildId)
        }
        
        if (exactMatch != null) {
            return CheatFileInfo(exactMatch, _cheatFileNames.value[exactMatch] ?: exactMatch)
        }
        
        // 如果没有精确匹配，尝试在显示名称中查找
        val displayNameMatch = _cheatFileNames.value.entries.firstOrNull { (_, displayName) ->
            displayName.contains(buildId)
        }
        
        return if (displayNameMatch != null) {
            CheatFileInfo(displayNameMatch.key, displayNameMatch.value)
        } else {
            // 如果没有找到匹配的文件，使用buildId作为显示名称
            CheatFileInfo("$buildId.txt", buildId)
        }
    }

    // 检测文件编码并转换为UTF-8
    private fun detectAndConvertEncoding(file: File): EncodingResult {
        // 首先检查是否为UTF-8
        if (isValidUtf8(file)) {
            val content = file.readText(Charsets.UTF_8)
            return EncodingResult(Charsets.UTF_8, content, "UTF-8")
        }
        
        // 如果不是UTF-8，尝试其他常见编码
        for (charset in commonCharsets) {
            if (charset == Charsets.UTF_8) continue // 已经检查过UTF-8
            
            try {
                val content = file.readText(charset)
                // 简单验证：检查是否包含过多乱码字符
                if (!containsTooManyInvalidChars(content)) {
                    return EncodingResult(charset, content, charset.name())
                }
            } catch (e: Exception) {
                // 尝试下一个编码
                continue
            }
        }
        
        // 如果所有编码都失败，默认使用UTF-8并记录警告
        val fallbackContent = try {
            file.readText(Charsets.UTF_8)
        } catch (e: Exception) {
            "Failed to read file with any encoding"
        }
        return EncodingResult(Charsets.UTF_8, fallbackContent, "Unknown (fallback to UTF-8)")
    }
    
    // 检查文件是否为有效的UTF-8
    private fun isValidUtf8(file: File): Boolean {
        return try {
            file.reader(Charsets.UTF_8).use { reader ->
                val buffer = CharArray(1024)
                while (reader.read(buffer) != -1) {
                    // 如果读取过程中没有抛出异常，说明是有效的UTF-8
                }
            }
            true
        } catch (e: Exception) {
            false
        }
    }
    
    // 检查内容是否包含过多无效字符（简单的乱码检测）
    private fun containsTooManyInvalidChars(text: String): Boolean {
        if (text.isEmpty()) return false
        
        val invalidCharCount = text.count { char ->
            // 检查是否为控制字符（除了换行和制表符）或 Unicode 替换字符
            (char.code in 0x0000..0x001F && char != '\n' && char != '\r' && char != '\t') ||
            char == '\uFFFD' // Unicode 替换字符
        }
        
        // 如果超过文本长度的1%是无效字符，认为编码可能不正确
        return (invalidCharCount.toDouble() / text.length) > 0.01
    }
    
    // 根据编码判断可能的语言
    private fun detectLanguageFromEncoding(charset: Charset, content: String): String {
        return when (charset.name().uppercase()) {
            "GBK", "GB2312" -> "Chinese (Simplified)"
            "BIG5" -> "Chinese (Traditional)"
            "SHIFT_JIS", "EUC-JP" -> "Japanese"
            else -> {
                // 通过字符范围进行简单判断
                val hasCJK = content.any { char ->
                    char.code in 0x4E00..0x9FFF || // CJK统一表意文字
                    char.code in 0x3040..0x309F || // 平假名
                    char.code in 0x30A0..0x30FF    // 片假名
                }
                if (hasCJK) "Detected CJK Characters" else "Unknown"
            }
        }
    }

    fun setCheatEnabled(cheatId: String, enabled: Boolean) {
        viewModelScope.launch {
            try {
                // 通过JNI调用设置金手指启用状态
                RyujinxNative.setCheatEnabled(titleId, cheatId, enabled)
                
                // 更新本地列表
                _cheats.value = _cheats.value.map { item ->
                    if (item is CheatListItem.CheatItem && item.id == cheatId) {
                        item.copy(enabled = enabled)
                    } else {
                        item
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

    // 添加金手指文件（现在会自动检测编码并转换为UTF-8）
    fun addCheatFile(cheatFile: File, displayName: String) {
        viewModelScope.launch {
            try {
                // 检查文件名，不允许添加 enabled.txt
                if (cheatFile.name.equals("enabled.txt", ignoreCase = true)) {
                    _errorMessage.value = "Cannot add enabled.txt file. This is a system file."
                    return@launch
                }
                
                if (!cheatsDir.exists()) {
                    cheatsDir.mkdirs()
                }
                
                val targetFile = File(cheatsDir, cheatFile.name)
                
                // 检测编码并转换为UTF-8
                val encodingResult = detectAndConvertEncoding(cheatFile)
                
                // 以UTF-8编码保存文件
                targetFile.writeText(encodingResult.content, Charsets.UTF_8)
                
                // 显示编码检测信息（可选）
                if (encodingResult.detectedEncoding != "UTF-8") {
                    val detectedLanguage = detectLanguageFromEncoding(encodingResult.charset, encodingResult.content)
                    _errorMessage.value = "File converted from ${encodingResult.detectedEncoding} to UTF-8. Detected language: $detectedLanguage"
                }
                
                // 保存自定义显示名称到 SharedPreferences
                saveDisplayNameToPrefs(cheatFile.name, displayName)
                
                // 更新内存中的映射
                val updatedNames = _cheatFileNames.value.toMutableMap()
                updatedNames[cheatFile.name] = displayName
                _cheatFileNames.value = updatedNames
                
                // 重新加载金手指列表
                loadCheats()
            } catch (e: Exception) {
                _errorMessage.value = "Failed to add cheat file: ${e.message}"
            }
        }
    }

    // 更新金手指文件显示名称
    fun updateCheatFileName(fileName: String, newDisplayName: String) {
        viewModelScope.launch {
            try {
                // 保存到 SharedPreferences
                saveDisplayNameToPrefs(fileName, newDisplayName)
                
                // 更新内存中的映射
                val updatedNames = _cheatFileNames.value.toMutableMap()
                updatedNames[fileName] = newDisplayName
                _cheatFileNames.value = updatedNames
                
                // 重新加载金手指列表
                loadCheats()
            } catch (e: Exception) {
                _errorMessage.value = "Failed to update cheat file name: ${e.message}"
            }
        }
    }

    // 删除所有金手指文件（排除 enabled.txt）
    fun deleteAllCheats() {
        viewModelScope.launch {
            try {
                if (cheatsDir.exists() && cheatsDir.isDirectory) {
                    cheatsDir.listFiles()?.forEach { file ->
                        if (file.isFile && 
                            (file.extension == "txt" || file.extension == "json") &&
                            !file.name.equals("enabled.txt", ignoreCase = true)) {
                            file.delete()
                            // 从 SharedPreferences 中删除对应的显示名称
                            removeDisplayNameFromPrefs(file.name)
                        }
                    }
                }
                
                // 清空自定义名称映射
                _cheatFileNames.value = emptyMap()
                
                // 重新加载金手指列表
                loadCheats()
            } catch (e: Exception) {
                _errorMessage.value = "Failed to delete cheats: ${e.message}"
            }
        }
    }

    // 获取当前金手指文件数量（排除 enabled.txt）
    fun getCheatFileCount(): Int {
        return if (cheatsDir.exists() && cheatsDir.isDirectory) {
            cheatsDir.listFiles()?.count { 
                it.isFile && 
                (it.extension == "txt" || it.extension == "json") &&
                !it.name.equals("enabled.txt", ignoreCase = true)
            } ?: 0
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
