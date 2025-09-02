package org.ryujinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import androidx.compose.ui.text.toLowerCase
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import org.ryujinx.android.MainActivity
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.util.zip.ZipFile
import net.lingala.zip4j.ZipFile as Zip4JFile
import net.lingala.zip4j.exception.ZipException

class ModViewModel(val titleId: String) {
    private var canClose: MutableState<Boolean>? = null
    private var storageHelper: SimpleStorageHelper
    private var modItemsState: SnapshotStateList<ModItem>? = null
    
    // 基础路径定义
    private val baseModsPath = "${MainActivity.AppPath}/mods"
    private val contentsPath = "$baseModsPath/contents"
    private val gameModPath = "$contentsPath/$titleId"
    
    companion object {
        const val ModRequestCode = 1004
        private val SUPPORTED_ARCHIVES = listOf("zip", "rar", "7z")
    }
    
    init {
        storageHelper = MainActivity.StorageHelper!!
        // 确保所有必要的目录都存在
        ensureDirectoriesExist()
        refreshModList()
    }
    
    private fun ensureDirectoriesExist() {
        File(baseModsPath).mkdirs()
        File(contentsPath).mkdirs()
        File(gameModPath).mkdirs()
    }
    
    fun remove(modItem: ModItem) {
        val file = File(modItem.path)
        if (file.exists()) {
            if (file.isDirectory) {
                file.deleteRecursively()
            } else {
                file.delete()
            }
            refreshModList()
        }
    }
    
    fun add() {
        val callBack = storageHelper.onFileSelected

        storageHelper.onFileSelected = { requestCode, files ->
            run {
                storageHelper.onFileSelected = callBack
                if (requestCode == ModRequestCode) {
                    val file = files.firstOrNull()
                    file?.apply {
                        if (SUPPORTED_ARCHIVES.contains(file.extension?.toLowerCase())) {
                            storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                                file.uri,
                                Intent.FLAG_GRANT_READ_URI_PERMISSION
                            )
                            
                            // 解压压缩包到游戏mod目录
                            extractArchive(file.uri, titleId)
                            refreshModList()
                        }
                    }
                }
            }
        }
        storageHelper.openFilePicker(ModRequestCode, allowMultiple = false)
    }
    
    // 添加mod文件的方法
    fun addMod(filePath: String) {
        val file = File(filePath)
        if (file.exists()) {
            if (file.isDirectory) {
                // 如果是文件夹，直接复制到mod目录
                copyDirectory(file, File(gameModPath, file.name))
            } else if (SUPPORTED_ARCHIVES.contains(file.extension.toLowerCase())) {
                // 如果是压缩文件，解压
                extractArchive(Uri.fromFile(file), titleId)
            } else {
                // 其他文件直接复制
                file.copyTo(File(gameModPath, file.name), true)
            }
            refreshModList()
        }
    }
    
    // 切换mod启用状态
    fun toggleMod(modItem: ModItem, enabled: Boolean) {
        modItem.isEnabled.value = enabled
        saveModState()
    }
    
    // 保存mod状态到配置文件
    private fun saveModState() {
        val modStates = modItemsState?.associate { it.path to it.isEnabled.value } ?: emptyMap()
        val json = Gson().toJson(modStates)
        
        val configFile = File("$gameModPath/mods_config.json")
        configFile.writeText(json)
    }
    
    // 从配置文件加载mod状态
    private fun loadModState() {
        val configFile = File("$gameModPath/mods_config.json")
        if (configFile.exists()) {
            try {
                val json = configFile.readText()
                val type = object : TypeToken<Map<String, Boolean>>() {}.type
                val modStates: Map<String, Boolean> = Gson().fromJson(json, type)
                
                modItemsState?.forEach { modItem ->
                    modStates[modItem.path]?.let { enabled ->
                        modItem.isEnabled.value = enabled
                    }
                }
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
    }
    
    /**
     * 智能解压函数：检测压缩包内是否包含ID目录
     */
    private fun extractArchive(uri: Uri, gameId: String) {
        try {
            val context = storageHelper.storage.context
            val contentResolver = context.contentResolver
            val inputStream = contentResolver.openInputStream(uri)
            val tempFile = File.createTempFile("mod_temp", ".archive")
            
            // 复制到临时文件
            inputStream?.use { input ->
                FileOutputStream(tempFile).use { output ->
                    input.copyTo(output)
                }
            }
            
            // 检测压缩包结构
            val containsIdDirectory = checkIfArchiveContainsIdDirectory(tempFile, gameId)
            
            // 根据检测结果决定解压路径
            val extractPath = if (containsIdDirectory) {
                // 如果包含ID目录，直接解压到contents下
                contentsPath
            } else {
                // 如果不包含ID目录，解压到contents/gameId下
                gameModPath
            }
            
            // 根据文件类型解压
            when (tempFile.extension.toLowerCase()) {
                "zip" -> extractZip(tempFile, extractPath)
                "rar" -> extractRar(tempFile, extractPath)
                "7z" -> extract7z(tempFile, extractPath)
            }
            
            // 清理临时文件
            tempFile.delete()
        } catch (e: Exception) {
            e.printStackTrace()
            // 处理解压错误
        }
    }
    
    /**
     * 检测压缩包是否包含ID目录
     */
    private fun checkIfArchiveContainsIdDirectory(archiveFile: File, gameId: String): Boolean {
        return when (archiveFile.extension.toLowerCase()) {
            "zip" -> checkZipForIdDirectory(archiveFile, gameId)
            "rar" -> checkRarForIdDirectory(archiveFile, gameId)
            "7z" -> check7zForIdDirectory(archiveFile, gameId)
            else -> false
        }
    }
    
    /**
     * 检测ZIP文件是否包含ID目录
     */
    private fun checkZipForIdDirectory(zipFile: File, gameId: String): Boolean {
        return try {
            val zip = ZipFile(zipFile)
            var containsIdDir = false
            
            zip.entries().asSequence().forEach { entry ->
                // 检查路径是否以gameId开头
                if (entry.name.startsWith("$gameId/") || entry.name.startsWith("$gameId\\")) {
                    containsIdDir = true
                    return@forEach
                }
            }
            
            containsIdDir
        } catch (e: Exception) {
            e.printStackTrace()
            false
        }
    }
    
    // RAR和7Z的检测方法需要相应的库支持
    private fun checkRarForIdDirectory(rarFile: File, gameId: String): Boolean {
        // 使用RAR库实现类似检测逻辑
        // 这里简化处理，实际使用时需要根据所选RAR库的API实现
        return false
    }
    
    private fun check7zForIdDirectory(sevenZFile: File, gameId: String): Boolean {
        // 使用7Z库实现类似检测逻辑
        // 这里简化处理，实际使用时需要根据所选7Z库的API实现
        return false
    }
    
    /**
     * 解压ZIP文件
     */
    private fun extractZip(file: File, destinationPath: String) {
        try {
            // 使用zip4j以获得更好的兼容性
            val zipFile = Zip4JFile(file)
            zipFile.extractAll(destinationPath)
        } catch (e: ZipException) {
            e.printStackTrace()
            // 回退到标准ZIP解压
            ZipFile(file).use { zip ->
                zip.entries().asSequence().forEach { entry ->
                    val outputFile = File(destinationPath, entry.name)
                    if (entry.isDirectory) {
                        outputFile.mkdirs()
                    } else {
                        outputFile.parentFile.mkdirs()
                        zip.getInputStream(entry).use { input ->
                            FileOutputStream(outputFile).use { output ->
                                input.copyTo(output)
                            }
                        }
                    }
                }
            }
        }
    }
    
    // RAR和7Z的解压方法需要相应的库支持
    private fun extractRar(file: File, destinationPath: String) {
        // 实现RAR解压逻辑
        // 需要添加RAR解压库依赖
    }
    
    private fun extract7z(file: File, destinationPath: String) {
        // 实现7Z解压逻辑
        // 需要添加7Z解压库依赖
    }
    
    /**
     * 刷新MOD列表
     */
    fun refreshModList() {
        val mods = mutableListOf<ModItem>()
        val modDir = File(gameModPath)
        
        if (modDir.exists() && modDir.isDirectory) {
            modDir.listFiles()?.forEach { file ->
                mods.add(
                    ModItem(
                        name = file.name,
                        path = file.absolutePath,
                        isDirectory = file.isDirectory,
                        size = if (file.isDirectory) {
                            formatFileSize(getFolderSize(file))
                        } else {
                            formatFileSize(file.length())
                        },
                        isEnabled = mutableStateOf(false) // 默认禁用
                    )
                )
            }
        }
        
        modItemsState?.clear()
        modItemsState?.addAll(mods)
        loadModState() // 加载保存的状态
        canClose?.value = true
    }
    
    /**
     * 计算文件夹大小
     */
    private fun getFolderSize(folder: File): Long {
        var length: Long = 0
        folder.listFiles()?.forEach { file ->
            length += if (file.isFile) file.length()
            else getFolderSize(file)
        }
        return length
    }
    
    /**
     * 格式化文件大小
     */
    private fun formatFileSize(size: Long): String {
        if (size <= 0) return "0 B"
        val units = arrayOf("B", "KB", "MB", "GB", "TB")
        val digitGroups = (Math.log10(size.toDouble()) / Math.log10(1024.0)).toInt()
        return String.format("%.1f %s", size / Math.pow(1024.0, digitGroups.toDouble()), units[digitGroups])
    }
    
    fun setModItems(items: SnapshotStateList<ModItem>, canClose: MutableState<Boolean>) {
        modItemsState = items
        this.canClose = canClose
        refreshModList()
    }
    
    // 文件操作功能
    fun copyFile(sourcePath: String, destinationPath: String): Boolean {
        val source = File(sourcePath)
        val destination = File(destinationPath)
        
        return if (source.exists()) {
            if (source.isDirectory) {
                copyDirectory(source, destination)
            } else {
                source.copyTo(destination, true)
                true
            }
        } else {
            false
        }
    }
    
    private fun copyDirectory(source: File, destination: File): Boolean {
        if (!source.isDirectory) return false
        
        if (!destination.exists()) {
            destination.mkdirs()
        }
        
        source.listFiles()?.forEach { file ->
            val newFile = File(destination, file.name)
            if (file.isDirectory) {
                copyDirectory(file, newFile)
            } else {
                file.copyTo(newFile, true)
            }
        }
        
        return true
    }
    
    fun moveFile(sourcePath: String, destinationPath: String): Boolean {
        val source = File(sourcePath)
        val destination = File(destinationPath)
        
        return if (source.exists()) {
            source.renameTo(destination)
        } else {
            false
        }
    }
    
    fun createDirectory(path: String, name: String): Boolean {
        val dir = File(path, name)
        return dir.mkdirs()
    }
    
    fun renameFile(oldPath: String, newName: String): Boolean {
        val oldFile = File(oldPath)
        val newFile = File(oldFile.parent, newName)
        return oldFile.renameTo(newFile)
    }
}

data class ModItem(
    var name: String = "",
    var path: String = "",
    var isDirectory: Boolean = false,
    var size: String = "",
    var isEnabled: MutableState<Boolean> = mutableStateOf(false) // 添加启用状态
)
