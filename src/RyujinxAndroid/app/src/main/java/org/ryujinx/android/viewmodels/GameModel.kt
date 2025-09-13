package org.ryujinx.android.viewmodels

import android.content.Context
import android.net.Uri
import android.os.ParcelFileDescriptor
import androidx.documentfile.provider.DocumentFile
import com.anggrayudi.storage.file.extension
import org.ryujinx.android.RyujinxNative
import android.content.SharedPreferences
import androidx.preference.PreferenceManager
import android.util.Log

class GameModel(var file: DocumentFile, val context: Context) {
    private var updateDescriptor: ParcelFileDescriptor? = null
    var type: FileType
    var descriptor: ParcelFileDescriptor? = null
    var fileName: String?
    var fileSize = 0.0
    var titleName: String? = null
    var titleId: String? = null
    var developer: String? = null
    var version: String? = null
    var icon: String? = null
    var customName: String? = null
        set(value) {
            field = value
            // 如果设置的是空字符串，则清除自定义名称
            if (value.isNullOrEmpty()) {
                sharedPref?.edit()?.remove("custom_name_$titleId")?.apply()
            } else {
                sharedPref?.edit()?.putString("custom_name_$titleId", value)?.apply()
            }
        }
    private var sharedPref: SharedPreferences? = null
    
    // 添加 path 属性
    val path: String
        get() = file.uri.toString()

    init {
        fileName = file.name
        val pid = open()
        val gameInfo = GameInfo()
        RyujinxNative.jnaInstance.deviceGetGameInfo(pid, file.extension, gameInfo)
        close()

        fileSize = gameInfo.FileSize
        titleId = gameInfo.TitleId
        titleName = gameInfo.TitleName
        developer = gameInfo.Developer
        version = gameInfo.Version
        icon = gameInfo.Icon
        type = when {
            (file.extension == "xci") -> FileType.Xci
            (file.extension == "nsp") -> FileType.Nsp
            (file.extension == "nro") -> FileType.Nro
            else -> FileType.None
        }

        if (type == FileType.Nro && (titleName.isNullOrEmpty() || titleName == "Unknown")) {
            titleName = file.name
        }
        
        // 检测是否有更新版本
        val updatePid = openUpdate()
        if (updatePid > 0) {
            try {
                val updateGameInfo = GameInfo()
                // 更新文件都是NSP格式
                RyujinxNative.jnaInstance.deviceGetGameInfo(updatePid, "nsp", updateGameInfo)
                
                // 检查版本信息是否有效
                if (isValidVersion(updateGameInfo.Version)) {
                    version = updateGameInfo.Version
                    Log.d("GameModel", "使用更新版本: ${updateGameInfo.Version}")
                } else {
                    // 尝试从文件名中提取版本信息
                    val versionFromFilename = extractVersionFromFilename(getUpdateFileName())
                    if (versionFromFilename != null) {
                        version = versionFromFilename
                        Log.d("GameModel", "从文件名中提取版本: $versionFromFilename")
                    } else {
                        Log.d("GameModel", "更新文件版本信息无效，保留基础版本: $version")
                    }
                }
            } catch (e: Exception) {
                Log.e("GameModel", "读取更新文件信息失败: ${e.message}")
            } finally {
                // 确保关闭更新文件描述符
                updateDescriptor?.close()
                updateDescriptor = null
            }
        }
        
        // 初始化SharedPreferences
        sharedPref = PreferenceManager.getDefaultSharedPreferences(context)
        
        // 加载自定义名称 - 如果是空字符串则视为未设置
        val savedName = sharedPref?.getString("custom_name_$titleId", null)
        customName = if (savedName.isNullOrEmpty()) null else savedName
    }

    // 检查版本是否有效
    private fun isValidVersion(version: String?): Boolean {
        return !version.isNullOrEmpty() && version != "0" && version != "v0"
    }

    // 从文件名中提取版本信息
    private fun extractVersionFromFilename(filename: String?): String? {
        if (filename.isNullOrEmpty()) return null
        
        // 尝试匹配常见的版本格式，如 "v1.0.2", "[1.0.2]", "(1.0.2)" 等
        val patterns = listOf(
            Regex("""\[(\d+\.\d+\.\d+)\]"""),  // [1.0.2]
            Regex("""v(\d+\.\d+\.\d+)"""),     // v1.0.2
            Regex("""\((\d+\.\d+\.\d+)\)"""),  // (1.0.2)
            Regex("""(\d+\.\d+\.\d+)""")       // 1.0.2
        )
        
        for (pattern in patterns) {
            val match = pattern.find(filename)
            if (match != null) {
                return match.groupValues[1]
            }
        }
        
        return null
    }

    // 获取更新文件的名称
    private fun getUpdateFileName(): String? {
        if (titleId?.isNotEmpty() == true) {
            val vm = TitleUpdateViewModel(titleId ?: "")
            if (vm.data?.selected?.isNotEmpty() == true) {
                val uri = Uri.parse(vm.data.selected)
                val updateFile = DocumentFile.fromSingleUri(context, uri)
                return updateFile?.name
            }
        }
        return null
    }

    // 获取显示名称（优先使用自定义名称）
    fun getDisplayName(): String {
        return if (customName.isNullOrEmpty()) {
            titleName ?: fileName ?: "Unknown"
        } else {
            customName!!
        }
    }
    
    // 清除自定义名称
    fun clearCustomName() {
        customName = null
        sharedPref?.edit()?.remove("custom_name_$titleId")?.apply()
    }

    fun open(): Int {
        descriptor = context.contentResolver.openFileDescriptor(file.uri, "rw")
        return descriptor?.fd ?: 0
    }

    fun openUpdate(): Int {
        if (titleId?.isNotEmpty() == true) {
            val vm = TitleUpdateViewModel(titleId ?: "")

            if (vm.data?.selected?.isNotEmpty() == true) {
                val uri = Uri.parse(vm.data.selected)
                val file = DocumentFile.fromSingleUri(context, uri)
                if (file?.exists() == true) {
                    try {
                        updateDescriptor =
                            context.contentResolver.openFileDescriptor(file.uri, "rw")
                        return updateDescriptor?.fd ?: -1
                    } catch (e: Exception) {
                        return -2
                    }
                }
            }
        }
        return -1
    }

    fun close() {
        descriptor?.close()
        descriptor = null
        updateDescriptor?.close()
        updateDescriptor = null
    }
}

enum class FileType {
    None,
    Nsp,
    Xci,
    Nro
}
