package org.ryujinx.android.viewmodels

import android.content.Context
import android.net.Uri
import android.os.ParcelFileDescriptor
import androidx.documentfile.provider.DocumentFile
import com.anggrayudi.storage.file.extension
import org.ryujinx.android.RyujinxNative
import android.content.SharedPreferences
import androidx.preference.PreferenceManager

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
        
        // 初始化SharedPreferences
        sharedPref = PreferenceManager.getDefaultSharedPreferences(context)
        
        // 加载自定义名称 - 如果是空字符串则视为未设置
        val savedName = sharedPref?.getString("custom_name_$titleId", null)
        customName = if (savedName.isNullOrEmpty()) null else savedName
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
                val uri = Uri.parse(vm.data?.selected)
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
