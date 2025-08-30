package org.ryujinx.android.viewmodels

import android.content.SharedPreferences
import androidx.compose.runtime.MutableState
import androidx.documentfile.provider.DocumentFile
import androidx.navigation.NavHostController
import androidx.preference.PreferenceManager
import com.anggrayudi.storage.callback.FileCallback
import com.anggrayudi.storage.file.FileFullPath
import com.anggrayudi.storage.file.copyFileTo
import com.anggrayudi.storage.file.extension
import com.anggrayudi.storage.file.getAbsolutePath
import com.anggrayudi.storage.file.openInputStream
import net.lingala.zip4j.io.inputstream.ZipInputStream
import org.ryujinx.android.LogLevel
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RyujinxNative
import java.io.BufferedOutputStream
import java.io.File
import java.io.FileOutputStream
import kotlin.concurrent.thread

class SettingsViewModel(var navController: NavHostController, val activity: MainActivity) {
    var selectedFirmwareVersion: String = ""
    private var previousFileCallback: ((requestCode: Int, files: List<DocumentFile>) -> Unit)?
    private var previousFolderCallback: ((requestCode: Int, folder: DocumentFile) -> Unit)?
    private var sharedPref: SharedPreferences
    var selectedKeyFile: DocumentFile? = null
    var selectedFirmwareFile: DocumentFile? = null

    init {
        sharedPref = getPreferences()
        previousFolderCallback = activity.storageHelper!!.onFolderSelected
        previousFileCallback = activity.storageHelper!!.onFileSelected
    }

    private fun getPreferences(): SharedPreferences {
        return PreferenceManager.getDefaultSharedPreferences(activity)
    }

    fun initializeState(
        isHostMapped: MutableState<Boolean>,
        useNce: MutableState<Boolean>,
        enableVsync: MutableState<Boolean>,
        enableDocked: MutableState<Boolean>,
        enablePtc: MutableState<Boolean>,
        ignoreMissingServices: MutableState<Boolean>,
        enableShaderCache: MutableState<Boolean>,
        enableTextureRecompression: MutableState<Boolean>,
        resScale: MutableState<Float>,
        useVirtualController: MutableState<Boolean>,
        isGrid: MutableState<Boolean>,
        useSwitchLayout: MutableState<Boolean>,
        enableMotion: MutableState<Boolean>,
        enablePerformanceMode: MutableState<Boolean>,
        controllerStickSensitivity: MutableState<Float>,
        enableDebugLogs: MutableState<Boolean>,
        enableStubLogs: MutableState<Boolean>,
        enableInfoLogs: MutableState<Boolean>,
        enableWarningLogs: MutableState<Boolean>,
        enableErrorLogs: MutableState<Boolean>,
        enableGuestLogs: MutableState<Boolean>,
        enableAccessLogs: MutableState<Boolean>,
        enableTraceLogs: MutableState<Boolean>,
        enableGraphicsLogs: MutableState<Boolean>,
        skipMemoryBarriers: MutableState<Boolean>
    ) {

        isHostMapped.value = sharedPref.getBoolean("isHostMapped", true)
        useNce.value = sharedPref.getBoolean("useNce", true)
        enableVsync.value = sharedPref.getBoolean("enableVsync", true)
        enableDocked.value = sharedPref.getBoolean("enableDocked", true)
        enablePtc.value = sharedPref.getBoolean("enablePtc", true)
        ignoreMissingServices.value = sharedPref.getBoolean("ignoreMissingServices", false)
        enableShaderCache.value = sharedPref.getBoolean("enableShaderCache", true)
        enableTextureRecompression.value =
            sharedPref.getBoolean("enableTextureRecompression", false)
        resScale.value = sharedPref.getFloat("resScale", 1f)
        useVirtualController.value = sharedPref.getBoolean("useVirtualController", true)
        isGrid.value = sharedPref.getBoolean("isGrid", true)
        useSwitchLayout.value = sharedPref.getBoolean("useSwitchLayout", true)
        enableMotion.value = sharedPref.getBoolean("enableMotion", true)
        enablePerformanceMode.value = sharedPref.getBoolean("enablePerformanceMode", false)
        controllerStickSensitivity.value = sharedPref.getFloat("controllerStickSensitivity", 1.0f)
        skipMemoryBarriers.value = sharedPref.getBoolean("skipMemoryBarriers", false)

        enableDebugLogs.value = sharedPref.getBoolean("enableDebugLogs", false)
        enableStubLogs.value = sharedPref.getBoolean("enableStubLogs", false)
        enableInfoLogs.value = sharedPref.getBoolean("enableInfoLogs", true)
        enableWarningLogs.value = sharedPref.getBoolean("enableWarningLogs", true)
        enableErrorLogs.value = sharedPref.getBoolean("enableErrorLogs", true)
        enableGuestLogs.value = sharedPref.getBoolean("enableGuestLogs", true)
        enableAccessLogs.value = sharedPref.getBoolean("enableAccessLogs", false)
        enableTraceLogs.value = sharedPref.getBoolean("enableStubLogs", false)
        enableGraphicsLogs.value = sharedPref.getBoolean("enableGraphicsLogs", false)
    }

    fun save(
        isHostMapped: MutableState<Boolean>,
        useNce: MutableState<Boolean>,
        enableVsync: MutableState<Boolean>,
        enableDocked: MutableState<Boolean>,
        enablePtc: MutableState<Boolean>,
        ignoreMissingServices: MutableState<Boolean>,
        enableShaderCache: MutableState<Boolean>,
        enableTextureRecompression: MutableState<Boolean>,
        resScale: MutableState<Float>,
        useVirtualController: MutableState<Boolean>,
        isGrid: MutableState<Boolean>,
        useSwitchLayout: MutableState<Boolean>,
        enableMotion: MutableState<Boolean>,
        enablePerformanceMode: MutableState<Boolean>,
        controllerStickSensitivity: MutableState<Float>,
        enableDebugLogs: MutableState<Boolean>,
        enableStubLogs: MutableState<Boolean>,
        enableInfoLogs: MutableState<Boolean>,
        enableWarningLogs: MutableState<Boolean>,
        enableErrorLogs: MutableState<Boolean>,
        enableGuestLogs: MutableState<Boolean>,
        enableAccessLogs: MutableState<Boolean>,
        enableTraceLogs: MutableState<Boolean>,
        enableGraphicsLogs: MutableState<Boolean>,
        skipMemoryBarriers: MutableState<Boolean>
    ) {
        val editor = sharedPref.edit()

        editor.putBoolean("isHostMapped", isHostMapped.value)
        editor.putBoolean("useNce", useNce.value)
        editor.putBoolean("enableVsync", enableVsync.value)
        editor.putBoolean("enableDocked", enableDocked.value)
        editor.putBoolean("enablePtc", enablePtc.value)
        editor.putBoolean("ignoreMissingServices", ignoreMissingServices.value)
        editor.putBoolean("enableShaderCache", enableShaderCache.value)
        editor.putBoolean("enableTextureRecompression", enableTextureRecompression.value)
        editor.putFloat("resScale", resScale.value)
        editor.putBoolean("useVirtualController", useVirtualController.value)
        editor.putBoolean("isGrid", isGrid.value)
        editor.putBoolean("useSwitchLayout", useSwitchLayout.value)
        editor.putBoolean("enableMotion", enableMotion.value)
        editor.putBoolean("enablePerformanceMode", enablePerformanceMode.value)
        editor.putFloat("controllerStickSensitivity", controllerStickSensitivity.value)
        editor.putBoolean("skipMemoryBarriers", skipMemoryBarriers.value)

        editor.putBoolean("enableDebugLogs", enableDebugLogs.value)
        editor.putBoolean("enableStubLogs", enableStubLogs.value)
        editor.putBoolean("enableInfoLogs", enableInfoLogs.value)
        editor.putBoolean("enableWarningLogs", enableWarningLogs.value)
        editor.putBoolean("enableErrorLogs", enableErrorLogs.value)
        editor.putBoolean("enableGuestLogs", enableGuestLogs.value)
        editor.putBoolean("enableAccessLogs", enableAccessLogs.value)
        editor.putBoolean("enableTraceLogs", enableTraceLogs.value)
        editor.putBoolean("enableGraphicsLogs", enableGraphicsLogs.value)

        editor.apply()
        activity.storageHelper!!.onFolderSelected = previousFolderCallback

        // 设置跳过内存屏障
        RyujinxNative.jnaInstance.setSkipMemoryBarriers(skipMemoryBarriers.value)

        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Debug.ordinal, enableDebugLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Info.ordinal, enableInfoLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Stub.ordinal, enableStubLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.Warning.ordinal,
            enableWarningLogs.value
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Error.ordinal, enableErrorLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(
            LogLevel.AccessLog.ordinal,
            enableAccessLogs.value
        )
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Guest.ordinal, enableGuestLogs.value)
        RyujinxNative.jnaInstance.loggingSetEnabled(LogLevel.Trace.ordinal, enableTraceLogs.value)
        RyujinxNative.jnaInstance.loggingEnabledGraphicsLog(enableGraphicsLogs.value)
    }

    fun openGameFolder() {
        val path = sharedPref.getString("gameFolder", "") ?: ""

        activity.storageHelper!!.onFolderSelected = { _, folder ->
            val p = folder.getAbsolutePath(activity)
            sharedPref.edit {
                putString("gameFolder", p)
            }
            activity.storageHelper!!.onFolderSelected = previousFolderCallback
        }

        if (path.isEmpty())
            activity.storageHelper?.storage?.openFolderPicker()
        else
            activity.storageHelper?.storage?.openFolderPicker(
                activity.storageHelper!!.storage.requestCodeFolderPicker,
                FileFullPath(activity, path)
            )
    }

    fun selectKey(installState: MutableState<KeyInstallState>) {
        if (installState.value != KeyInstallState.File)
            return

        activity.storageHelper!!.onFileSelected = { _, files ->
            val file = files.firstOrNull()
            file?.apply {
                if (name == "prod.keys") {
                    selectedKeyFile = file
                    installState.value = KeyInstallState.Query
                } else {
                    installState.value = KeyInstallState.Cancelled
                }
            }
            activity.storageHelper!!.onFileSelected = previousFileCallback
        }
        activity.storageHelper?.storage?.openFilePicker()
    }

    fun installKey(installState: MutableState<KeyInstallState>) {
        if (installState.value != KeyInstallState.Query)
            return
        if (selectedKeyFile == null) {
            installState.value = KeyInstallState.File
            return
        }
        selectedKeyFile?.apply {
            val outputFolder = File(MainActivity.AppPath + "/system")
            val outputFile = File(MainActivity.AppPath + "/system/" + name)
            outputFile.delete()
            installState.value = KeyInstallState.Install
            thread {
                Thread.sleep(1000)
                this.copyFileTo(
                    activity,
                    outputFolder,
                    callback = object : FileCallback() {
                        override fun onCompleted(result: Any) {
                            RyujinxNative.jnaInstance.deviceReloadFilesystem()
                            installState.value = KeyInstallState.Done
                        }
                    }
                )
            }
        }
    }

    fun clearKeySelection(installState: MutableState<KeyInstallState>) {
        selectedKeyFile = null
        installState.value = KeyInstallState.File
    }

    fun selectFirmware(installState: MutableState<FirmwareInstallState>) {
        if (installState.value != FirmwareInstallState.File)
            return

        activity.storageHelper!!.onFileSelected = { _, files ->
            val file = files.firstOrNull()
            file?.apply {
                if (extension == "xci" || extension == "zip") {
                    installState.value = FirmwareInstallState.Verifying
                    thread {
                        val descriptor = activity.contentResolver.openFileDescriptor(file.uri, "rw")
                        descriptor?.use { d ->
                            selectedFirmwareVersion = RyujinxNative.jnaInstance.deviceVerifyFirmware(
                                d.fd,
                                extension == "xci"
                            )
                            selectedFirmwareFile = file
                            if (!selectedFirmwareVersion.isEmpty()) {
                                installState.value = FirmwareInstallState.Query
                            } else {
                                installState.value = FirmwareInstallState.Cancelled
                            }
                        }
                    }
                } else {
                    installState.value = FirmwareInstallState.Cancelled
                }
            }
            activity.storageHelper!!.onFileSelected = previousFileCallback
        }
        activity.storageHelper?.storage?.openFilePicker()
    }

    fun installFirmware(installState: MutableState<FirmwareInstallState>) {
        if (installState.value != FirmwareInstallState.Query)
            return
        if (selectedFirmwareFile == null) {
            installState.value = FirmwareInstallState.File
            return
        }
        selectedFirmwareFile?.apply {
            val descriptor = activity.contentResolver.openFileDescriptor(uri, "rw")

            if(descriptor != null)
            {
                installState.value = FirmwareInstallState.Install
                thread {
                    Thread.sleep(1000)

                    try {
                        RyujinxNative.jnaInstance.deviceInstallFirmware(descriptor.fd, extension == "xci")
                    } finally {
                        MainActivity.mainViewModel?.refreshFirmwareVersion()
                        installState.value = FirmwareInstallState.Done
                    }
                }
            }
        }
    }

    fun clearFirmwareSelection(installState: MutableState<FirmwareInstallState>) {
        selectedFirmwareFile = null
        selectedFirmwareVersion = ""
        installState.value = FirmwareInstallState.File
    }

    fun resetAppData(
        dataResetState: MutableState<DataResetState>
    ) {
        dataResetState.value = DataResetState.Reset
        thread {
            Thread.sleep(1000)

            try {
                MainActivity.StorageHelper?.apply {
                    val folders = listOf("bis", "games", "profiles", "system")
                    for (f in folders) {
                        val dir = File(MainActivity.AppPath + "${File.separator}${f}")
                        if (dir.exists()) {
                            dir.deleteRecursively()
                        }

                        dir.mkdirs()
                    }
                }
            } finally {
                dataResetState.value = DataResetState.Done
                RyujinxNative.jnaInstance.deviceReloadFilesystem()
                MainActivity.mainViewModel?.refreshFirmwareVersion()
            }
        }
    }

    fun importAppData(
        file: DocumentFile,
        dataImportState: MutableState<DataImportState>
    ) {
        dataImportState.value = DataImportState.Import
        thread {
            Thread.sleep(1000)

            try {
                MainActivity.StorageHelper?.apply {
                    val stream = file.openInputStream(storage.context)
                    stream?.apply {
                        val folders = listOf("bis", "games", "profiles", "system")
                        for (f in folders) {
                            val dir = File(MainActivity.AppPath + "${File.separator}${f}")
                            if (dir.exists()) {
                                dir.deleteRecursively()
                            }

                            dir.mkdirs()
                        }
                        ZipInputStream(stream).use { zip ->
                            while (true) {
                                val header = zip.nextEntry ?: break
                                if (!folders.any { header.fileName.startsWith(it) }) {
                                    continue
                                }
                                val filePath =
                                    MainActivity.AppPath + File.separator + header.fileName

                                if (!header.isDirectory) {
                                    val bos = BufferedOutputStream(FileOutputStream(filePath))
                                    val bytesIn = ByteArray(4096)
                                    var read: Int = 0
                                    while (zip.read(bytesIn).also { read = it } > 0) {
                                        bos.write(bytesIn, 0, read)
                                    }
                                    bos.close()
                                } else {
                                    val dir = File(filePath)
                                    dir.mkdir()
                                }
                            }
                        }
                        stream.close()
                    }
                }
            } finally {
                dataImportState.value = DataImportState.Done
                RyujinxNative.jnaInstance.deviceReloadFilesystem()
                MainActivity.mainViewModel?.refreshFirmwareVersion()
            }
        }
    }
}

enum class KeyInstallState {
    File,
    Cancelled,
    Query,
    Install,
    Done
}

enum class FirmwareInstallState {
    File,
    Cancelled,
    Verifying,
    Query,
    Install,
    Done
}

enum class DataResetState {
    Query,
    Reset,
    Done
}

enum class DataImportState {
    File,
    Query,
    Import,
    Done
}
