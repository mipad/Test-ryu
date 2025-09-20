package org.ryujinx.android.views

import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.runtime.*
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.border
import androidx.compose.foundation.background
import androidx.compose.ui.graphics.Color
import android.annotation.SuppressLint
import android.content.ActivityNotFoundException
import android.content.Intent
import android.provider.DocumentsContract
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.tween
import androidx.compose.animation.core.updateTransition
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.sizeIn
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Label
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.PlainTooltip
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.documentfile.provider.DocumentFile
import com.anggrayudi.storage.file.extension
import org.ryujinx.android.Helpers
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RegionCode
import org.ryujinx.android.SystemLanguage
import org.ryujinx.android.providers.DocumentProvider
import org.ryujinx.android.viewmodels.FirmwareInstallState
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.SettingsViewModel
import org.ryujinx.android.viewmodels.VulkanDriverViewModel
import kotlin.concurrent.thread

class SettingViews {
    companion object {
        const val EXPANSTION_TRANSITION_DURATION = 450
        const val IMPORT_CODE = 12341

        @OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
        @Composable
        fun Main(settingsViewModel: SettingsViewModel, mainViewModel: MainViewModel) {
            val loaded = remember {
                mutableStateOf(false)
            }

            val isHostMapped = remember {
                mutableStateOf(false)
            }
            val useNce = remember {
                mutableStateOf(false)
            }
            val enableVsync = remember {
                mutableStateOf(false)
            }
            val enableDocked = remember {
                mutableStateOf(false)
            }
            val enablePtc = remember {
                mutableStateOf(false)
            }
            val enableJitCacheEviction = remember { 
                mutableStateOf(false)
             }
            val ignoreMissingServices = remember {
                mutableStateOf(false)
            }
            val enableShaderCache = remember {
                mutableStateOf(false)
            }
            val enableTextureRecompression = remember {
                mutableStateOf(false)
            }
            val resScale = remember {
                mutableStateOf(1f)
            }
            val aspectRatio = remember { mutableStateOf(1) } // 默认16:9
            val useVirtualController = remember {
                mutableStateOf(true)
            }
            val showFirwmareDialog = remember {
                mutableStateOf(false)
            }
            val firmwareInstallState = remember {
                mutableStateOf(FirmwareInstallState.None)
            }
            val firmwareVersion = remember {
                mutableStateOf(mainViewModel.firmwareVersion)
            }
            val isGrid = remember { mutableStateOf(true) }
            val useSwitchLayout = remember { mutableStateOf(true) }
            val enableMotion = remember { mutableStateOf(true) }
            val enablePerformanceMode = remember { mutableStateOf(true) }
            val controllerStickSensitivity = remember { mutableStateOf(1.0f) }

            val enableDebugLogs = remember { mutableStateOf(true) }
            val enableStubLogs = remember { mutableStateOf(true) }
            val enableInfoLogs = remember { mutableStateOf(true) }
            val enableWarningLogs = remember { mutableStateOf(true) }
            val enableErrorLogs = remember { mutableStateOf(true) }
            val enableGuestLogs = remember { mutableStateOf(true) }
            val enableAccessLogs = remember { mutableStateOf(true) }
            val enableTraceLogs = remember { mutableStateOf(true) }
            val enableGraphicsLogs = remember { mutableStateOf(true) }
            val skipMemoryBarriers = remember { mutableStateOf(false) } // 新增状态变量
            val regionCode = remember { mutableStateOf(RegionCode.USA.ordinal) } // 新增状态变量：区域代码
            val systemLanguage = remember { mutableStateOf(SystemLanguage.AmericanEnglish.ordinal) } // 新增状态变量：系统语言
            val audioEngineType = remember { mutableStateOf(1) } // 0=禁用，1=OpenAL, 2=SDL2, 3=Oboe
            val scalingFilter = remember { mutableStateOf(0) } // 0=Bilinear, 1=Nearest, 2=FSR
            val scalingFilterLevel = remember { mutableStateOf(25) } // 默认25%
            val antiAliasing = remember { mutableStateOf(0) } // 0=None, 1=Fxaa, 2=SmaaLow, 3=SmaaMedium, 4=SmaaHigh, 5=SmaaUltra
            val memoryConfiguration = remember { mutableStateOf(0) } // 新增状态变量：内存配置
           val customTimeEnabled = remember { mutableStateOf(false) }
           val customTimeYear = remember { mutableStateOf(2023) }
           val customTimeMonth = remember { mutableStateOf(9) }
           val customTimeDay = remember { mutableStateOf(12) }
           val customTimeHour = remember { mutableStateOf(10) }
           val customTimeMinute = remember { mutableStateOf(27) }
           val customTimeSecond = remember { mutableStateOf(0) }
           
           val showCustomTimeDialog = remember { mutableStateOf(false) }          
            val showAntiAliasingDialog = remember { mutableStateOf(false) } // 控制抗锯齿对话框显示
            // 新增状态变量用于控制选项显示
            val showResScaleOptions = remember { mutableStateOf(false) }
            val showAspectRatioOptions = remember { mutableStateOf(false) }
            val showAudioEngineDialog = remember { mutableStateOf(false) } // 控制音频引擎对话框显示
            val showScalingFilterDialog = remember { mutableStateOf(false) } // 控制Scaling Filter对话框显示
            val showMemoryConfigDialog = remember { mutableStateOf(false) } // 控制内存配置对话框显示
            

            if (!loaded.value) {
                settingsViewModel.initializeState(
                    isHostMapped,
                    useNce,
                    enableVsync, enableDocked, enablePtc, enableJitCacheEviction, ignoreMissingServices,
                    enableShaderCache,
                    enableTextureRecompression,
                    resScale,
                    aspectRatio, // 新增参数
                    useVirtualController,
                    isGrid,
                    useSwitchLayout,
                    enableMotion,
                    enablePerformanceMode,
                    controllerStickSensitivity,
                    enableDebugLogs,
                    enableStubLogs,
                    enableInfoLogs,
                    enableWarningLogs,
                    enableErrorLogs,
                    enableGuestLogs,
                    enableAccessLogs,
                    enableTraceLogs,
                    enableGraphicsLogs,
                    skipMemoryBarriers, // 新增参数
                    regionCode, // 新增参数
                    systemLanguage, // 新增参数
                    audioEngineType, // 新增参数
                    scalingFilter, // 新增：缩放过滤器
                    scalingFilterLevel, // 新增：缩放过滤器级别
                    antiAliasing, // 新增：抗锯齿模式
                    memoryConfiguration, // 新增DRAM参数
                    customTimeEnabled,
        customTimeYear,
        customTimeMonth,
        customTimeDay,
        customTimeHour,
        customTimeMinute,
        customTimeSecond
                )
                loaded.value = true
            }
            
            // 当NCE状态改变时，自动设置JIT Cache Eviction的状态
            if (useNce.value) {
                // 如果NCE开启，则关闭JIT Cache Eviction
                enableJitCacheEviction.value = false
            } else {
                // 如果NCE关闭，则开启JIT Cache Eviction
                enableJitCacheEviction.value = true
            }
            
            Scaffold(modifier = Modifier.fillMaxSize(),
                topBar = {
                    TopAppBar(title = {
                        Text(text = "Settings")
                    },
                        modifier = Modifier.padding(top = 16.dp),
                        navigationIcon = {
                            IconButton(onClick = {
                                settingsViewModel.save(
                                    isHostMapped,
                                    useNce,
                                    enableVsync,
                                    enableDocked,
                                    enablePtc,
                                    enableJitCacheEviction,
                                    ignoreMissingServices,
                                    enableShaderCache,
                                    enableTextureRecompression,
                                    resScale,
                    aspectRatio, // 新增参数
                    useVirtualController,
                    isGrid,
                    useSwitchLayout,
                    enableMotion,
                    enablePerformanceMode,
                    controllerStickSensitivity,
                    enableDebugLogs,
                    enableStubLogs,
                    enableInfoLogs,
                    enableWarningLogs,
                    enableErrorLogs,
                    enableGuestLogs,
                    enableAccessLogs,
                    enableTraceLogs,
                    enableGraphicsLogs,
                    skipMemoryBarriers, // 新增参数
                    regionCode, // 新增参数
                    systemLanguage, // 新增参数
                    audioEngineType, // 新增参数
                    scalingFilter, // 新增：缩放过滤器
                    scalingFilterLevel, // 新增：缩放过滤器级别
                    antiAliasing, // 新增：抗锯齿模式
                    memoryConfiguration, // 新增DRAM参数
                    customTimeEnabled,
        customTimeYear,
        customTimeMonth,
        customTimeDay,
        customTimeHour,
        customTimeMinute,
        customTimeSecond
                                )
                                settingsViewModel.navController.popBackStack()
                            }) {
                                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                            }
                        })
                }) { contentPadding ->
                Column(
                    modifier = Modifier
                        .padding(contentPadding)
                        .verticalScroll(rememberScrollState())
                ) {
                    ExpandableView(onCardArrowClick = { }, title = "App") {
                        Column(modifier = Modifier.fillMaxWidth()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Use Grid")
                                Switch(checked = isGrid.value, onCheckedChange = {
                                    isGrid.value = !isGrid.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Game Folder")
                                Button(onClick = {
                                    settingsViewModel.openGameFolder()
                                }) {
                                    Text(text = "Choose Folder")
                                }
                            }

                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "System Firmware")
                                Text(text = firmwareVersion.value)
                            }

                            FlowRow(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                Button(onClick = {
                                    fun createIntent(action: String): Intent {
                                        val intent = Intent(action)
                                        intent.addCategory(Intent.CATEGORY_DEFAULT)
                                        intent.data = DocumentsContract.buildRootUri(
                                            DocumentProvider.AUTHORITY,
                                            DocumentProvider.ROOT_ID
                                        )
                                        intent.addFlags(Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
                                        return intent
                                    }
                                    try {
                                        mainViewModel.activity.startActivity(createIntent(Intent.ACTION_VIEW))
                                        return@Button
                                    } catch (_: ActivityNotFoundException) {
                                    }
                                    try {
                                        mainViewModel.activity.startActivity(createIntent("android.provider.action.BROWSE"))
                                        return@Button
                                    } catch (_: ActivityNotFoundException) {
                                    }
                                    try {
                                        mainViewModel.activity.startActivity(createIntent("com.google.android.documentsui"))
                                        return@Button
                                    } catch (_: ActivityNotFoundException) {
                                    }
                                    try {
                                        mainViewModel.activity.startActivity(createIntent("com.android.documentsui"))
                                        return@Button
                                    } catch (_: ActivityNotFoundException) {
                                    }
                                }) {
                                    Text(text = "Open App Folder")
                                }

                                Button(onClick = {
                                    settingsViewModel.importProdKeys()
                                }) {
                                    Text(text = "Import prod Keys")
                                }

                                Button(onClick = {
                                    showFirwmareDialog.value = true
                                }) {
                                    Text(text = "Install Firmware")
                                }
                            }
                        }
                    }

                    if (showFirwmareDialog.value) {
                        BasicAlertDialog(onDismissRequest = {
                            if (firmwareInstallState.value != FirmwareInstallState.Install) {
                                showFirwmareDialog.value = false
                                settingsViewModel.clearFirmwareSelection(firmwareInstallState)
                            }
                        }) {
                            Card(
                                modifier = Modifier
                                    .padding(16.dp)
                                    .fillMaxWidth(),
                                shape = MaterialTheme.shapes.medium
                            ) {
                                Column(
                                    modifier = Modifier
                                        .padding(16.dp)
                                        .fillMaxWidth()
                                        .align(Alignment.CenterHorizontally),
                                    verticalArrangement = Arrangement.SpaceBetween
                                ) {
                                    if (firmwareInstallState.value == FirmwareInstallState.None) {
                                        Text(text = "Select a zip or XCI file to install from.")
                                        Row(
                                            horizontalArrangement = Arrangement.End,
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .padding(top = 4.dp)
                                        ) {
                                            Button(onClick = {
                                                settingsViewModel.selectFirmware(
                                                    firmwareInstallState
                                                )
                                            }, modifier = Modifier.padding(horizontal = 8.dp)) {
                                                Text(text = "Select File")
                                            }
                                            Button(onClick = {
                                                showFirwmareDialog.value = false
                                                settingsViewModel.clearFirmwareSelection(
                                                    firmwareInstallState
                                                )
                                            }, modifier = Modifier.padding(horizontal = 8.dp)) {
                                                Text(text = "Cancel")
                                            }
                                        }
                                    } else if (firmwareInstallState.value == FirmwareInstallState.Query) {
                                        Text(text = "Firmware ${settingsViewModel.selectedFirmwareVersion} will be installed. Do you want to continue?")
                                        Row(
                                            horizontalArrangement = Arrangement.End,
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .padding(top = 4.dp)
                                        ) {
                                            Button(onClick = {
                                                settingsViewModel.installFirmware(
                                                    firmwareInstallState
                                                )

                                                if (firmwareInstallState.value == FirmwareInstallState.None) {
                                                    showFirwmareDialog.value = false
                                                    settingsViewModel.clearFirmwareSelection(
                                                    firmwareInstallState
                                                )
                                                }
                                            }, modifier = Modifier.padding(horizontal = 8.dp)) {
                                                Text(text = "Yes")
                                            }
                                            Button(onClick = {
                                                showFirwmareDialog.value = false
                                                settingsViewModel.clearFirmwareSelection(
                                                    firmwareInstallState
                                                )
                                            }, modifier = Modifier.padding(horizontal = 8.dp)) {
                                                Text(text = "No")
                                            }
                                        }
                                    } else if (firmwareInstallState.value == FirmwareInstallState.Install) {
                                        Text(text = "Installing Firmware ${settingsViewModel.selectedFirmwareVersion}...")
                                        LinearProgressIndicator(
                                            modifier = Modifier
                                                .padding(top = 4.dp)
                                        )
                                    } else if (firmwareInstallState.value == FirmwareInstallState.Verifying) {
                                        Text(text = "Verifying selected file...")
                                        LinearProgressIndicator(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                        )
                                    } else if (firmwareInstallState.value == FirmwareInstallState.Done) {
                                        Text(text = "Installed Firmware ${settingsViewModel.selectedFirmwareVersion}")
                                        firmwareVersion.value = mainViewModel.firmwareVersion
                                    } else if (firmwareInstallState.value == FirmwareInstallState.Cancelled) {
                                        val file = settingsViewModel.selectedFirmwareFile
                                        if (file != null) {
                                            if (file.extension == "xci" || file.extension == "zip") {
                                                if (settingsViewModel.selectedFirmwareVersion.isEmpty()) {
                                                    Text(text = "Unable to find version in selected file")
                                                } else {
                                                    Text(text = "Unknown Error has occurred. Please check logs")
                                                }
                                            } else {
                                                Text(text = "File type is not supported")
                                            }
                                        } else {
                                            Text(text = "File type is not supported")
                                        }
                                    }
                                }
                            }
                        }
                    }
                    ExpandableView(onCardArrowClick = { }, title = "System") {
                        Column(modifier = Modifier.fillMaxWidth()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Use NCE")
                                Switch(checked = useNce.value, onCheckedChange = {
                                    useNce.value = it
                                    // 当NCE状态改变时，自动设置JIT Cache Eviction的状态
                                    enableJitCacheEviction.value = !it
                                })
                            }
                            
                            // 只在NCE关闭时显示Jit Cache Eviction选项
                            AnimatedVisibility(visible = !useNce.value) {
                                Column {
                                    Row(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(8.dp),
                                        horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column {
                                    Text(text = "Enable Jit Cache Eviction")
                                    Text(
                                        text = "Used with JIT mode",
                                        fontSize = 12.sp,
                                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f)
                                    )
                                }
                                Switch(
                                    checked = enableJitCacheEviction.value, 
                                    onCheckedChange = {
                                        enableJitCacheEviction.value = it
                                    }
                                )
                            }
                        }
                    }
                            
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Is Host Mapped")
                                Switch(checked = isHostMapped.value, onCheckedChange = {
                                    isHostMapped.value = !isHostMapped.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable VSync")
                                Switch(checked = enableVsync.value, onCheckedChange = {
                                    enableVsync.value = !enableVsync.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable PTC")
                                Switch(checked = enablePtc.value, onCheckedChange = {
                                    enablePtc.value = !enablePtc.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Docked Mode")
                                Switch(checked = enableDocked.value, onCheckedChange = {
                                    enableDocked.value = !enableDocked.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Ignore Missing Services")
                                Switch(checked = ignoreMissingServices.value, onCheckedChange = {
                                    ignoreMissingServices.value = !ignoreMissingServices.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                .fillMaxWidth()
                                .padding(8.dp),
                                 horizontalArrangement = Arrangement.SpaceBetween,
                                 verticalAlignment = Alignment.CenterVertically
                             ) {
                            Column(
                            modifier = Modifier.weight(1f)
                             ) {
                             Text(text = "Enable Performance Mode")
                             Text(
                                text = "Forces CPU and GPU to run at max clocks if available.",
                                fontSize = 12.sp
                               )
                             Text(
                              text = "OS power settings may override this.",
                              fontSize = 12.sp
                              )
                            }
                         Switch(
                          checked = enablePerformanceMode.value,
                          onCheckedChange = {
                           enablePerformanceMode.value = !enablePerformanceMode.value
                           }
                          )
                         }
                            val isImporting = remember {
                                mutableStateOf(false)
                            }
                            val showImportWarning = remember {
                                mutableStateOf(false)
                            }
                            val showImportCompletion = remember {
                                mutableStateOf(false)
                            }
                            var importFile = remember {
                                mutableStateOf<DocumentFile?>(null)
                            }
                            Button(onClick = {
                                val storage = MainActivity.StorageHelper
                                storage?.apply {
                                    val callBack = this.onFileSelected
                                    onFileSelected = { requestCode, files ->
                                        run {
                                            onFileSelected = callBack
                                            if (requestCode == IMPORT_CODE) {
                                                var file = files.firstOrNull()
                                                file?.apply {
                                                    if (this.extension == "zip") {
                                                        importFile.value = this
                                                        showImportWarning.value = true
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    openFilePicker(
                                        IMPORT_CODE,
                                        filterMimeTypes = arrayOf("application/zip")
                                    )
                                }
                            }) {
                                Text(text = "Import App Data")
                            }

                            if (showImportWarning.value) {
                                BasicAlertDialog(onDismissRequest = {
                                    showImportWarning.value = false
                                    importFile.value = null
                                }) {
                                    Card(
                                        modifier = Modifier
                                    .padding(16.dp)
                                    .fillMaxWidth(),
                                shape = MaterialTheme.shapes.medium
                            ) {
                                Column(
                                    modifier = Modifier
                                        .padding(16.dp)
                                        .fillMaxWidth()
                                ) {
                                    Text(text = "Importing app data will delete your current profile. Do you still want to continue?")
                                    Row(
                                        horizontalArrangement = Arrangement.End,
                                        modifier = Modifier.fillMaxWidth()
                                    ) {
                                        Button(onClick = {
                                            var file = importFile.value
                                            showImportWarning.value = false
                                            importFile.value = null
                                            file?.apply {
                                                thread {
                                                    Helpers.importAppData(this, isImporting)
                                                    showImportCompletion.value = true
                                                    mainViewModel.userViewModel.refreshUsers()
                                                }
                                            }
                                        }, modifier = Modifier.padding(horizontal = 8.dp)) {
                                            Text(text = "Yes")
                                        }
                                        Button(onClick = {
                                            showImportWarning.value = false
                                            importFile.value = null
                                        }, modifier = Modifier.padding(horizontal = 8.dp)) {
                                            Text(text = "No")
                                        }
                                    }
                                }

                            }
                        }
                    }

                    if (showImportCompletion.value) {
                        BasicAlertDialog(onDismissRequest = {
                            showImportCompletion.value = false
                            importFile.value = null
                            mainViewModel.userViewModel.refreshUsers()
                            mainViewModel.homeViewModel.requestReload()
                        }) {
                            Card(
                                modifier = Modifier,
                                shape = MaterialTheme.shapes.medium
                            ) {
                                Text(
                                    modifier = Modifier
                                        .padding(24.dp),
                                    text = "App Data import completed."
                                )
                            }
                        }
                    }

                    if (isImporting.value) {
                        Text(text = "Importing Files")

                        LinearProgressIndicator(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(8.dp)
                        )
                    }
                }
            }
                    ExpandableView(onCardArrowClick = { }, title = "Graphics") {
                        Column(modifier = Modifier.fillMaxWidth()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Shader Cache")
                                Switch(checked = enableShaderCache.value, onCheckedChange = {
                                    enableShaderCache.value = !enableShaderCache.value
                                })
                            }
                            
                            // 分辨率比例显示
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Resolution Scale")
                                Text(
                                    text = "%.2fx".format(resScale.value),
                                    modifier = Modifier.clickable(
                                        interactionSource = remember { MutableInteractionSource() },
                                        indication = null
                                    ) { 
                                        showResScaleOptions.value = !showResScaleOptions.value
                                        // 当显示分辨率选项时，隐藏其他选项
                                        if (showResScaleOptions.value) {
                                            showAspectRatioOptions.value = false
                                            
                                        }
                                    }
                                )
                            }
                            
                            // 分辨率比例选项修改
AnimatedVisibility(visible = showResScaleOptions.value) {
    Column(modifier = Modifier.fillMaxWidth()) {
        // 预设按钮组
        val resolutionPresets = listOf(0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.85f, 0.9f, 0.95f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f, 4f)
        
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 8.dp, vertical = 4.dp)
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            resolutionPresets.forEach { preset ->
                val isSelected = resScale.value == preset
                
                TextButton(
                    onClick = {
                        resScale.value = preset
                        showResScaleOptions.value = false // 选择后隐藏选项
                    },
                    modifier = Modifier
                        .height(36.dp)
                        .then(
                            if (isSelected) {
                                Modifier.border(
                                    width = 1.dp,
                                    color = MaterialTheme.colorScheme.primary,
                                    shape = MaterialTheme.shapes.small
                                )
                            } else {
                                Modifier
                            }
                        ),
                    colors = ButtonDefaults.textButtonColors(
                        containerColor = if (isSelected) MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.3f) else Color.Transparent,
                        contentColor = if (isSelected) MaterialTheme.colorScheme.onPrimaryContainer 
                                     else MaterialTheme.colorScheme.onSurface
                    )
                ) {
                    Text(
                        text = "%.2fx".format(preset),
                        fontSize = 12.sp
                                                )
                                            }
                                        }
                                    }
                                }
                            }
        
                            // 画面比例显示
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Aspect Ratio")
                                val aspectRatioMap = listOf("4:3", "16:9", "16:10", "21:9", "32:9", "Stretched")
                                Text(
                                    text = aspectRatioMap[aspectRatio.value],
                                    modifier = Modifier.clickable(
                                        interactionSource = remember { MutableInteractionSource() },
                                        indication = null
                                    ) { 
                                        showAspectRatioOptions.value = !showAspectRatioOptions.value
                                        // 当显示画面比例选项时，隐藏其他选项
                                        if (showAspectRatioOptions.value) {
                                            showResScaleOptions.value = false
                                        }
                                    }
                                )
                            }
                            
                            // 画面比例选项修改
AnimatedVisibility(visible = showAspectRatioOptions.value) {
    Column(modifier = Modifier.fillMaxWidth()) {
        val aspectRatioOptions = listOf("4:3", "16:9", "16:10", "21:9", "32:9", "Stretched")
        
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 8.dp, vertical = 4.dp)
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            aspectRatioOptions.forEachIndexed { index, option ->
                val isSelected = aspectRatio.value == index
                
                // 使用Box来确保点击区域匹配矩形大小
                Box(
                    modifier = Modifier
                        .width(80.dp) // 矩形宽度
                        .height(40.dp) // 矩形高度
                        .clip(MaterialTheme.shapes.small)
                        .clickable(
                            interactionSource = remember { MutableInteractionSource() },
                            indication = null
                        ) { 
                            aspectRatio.value = index
                            showAspectRatioOptions.value = false // 选择后隐藏选项
                        }
                        .then(
                            if (isSelected) {
                                Modifier.border(
                                    width = 1.dp,
                                    color = MaterialTheme.colorScheme.primary,
                                    shape = MaterialTheme.shapes.small
                                )
                            } else {
                                Modifier
                            }
                        )
                        .then(
                            if (isSelected) Modifier.background(
                                MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.3f)
                            ) else Modifier
                        ),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = option,
                        fontSize = 12.sp,
                        textAlign = TextAlign.Center,
                        color = if (isSelected) MaterialTheme.colorScheme.onPrimaryContainer 
                               else MaterialTheme.colorScheme.onSurface
                                                )
                                            }
                                        }
                                    }
                                }
                            }
        
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Texture Recompression")
                                Switch(
                                    checked = enableTextureRecompression.value,
                                    onCheckedChange = {
                                        enableTextureRecompression.value =
                                            !enableTextureRecompression.value
                                    })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.Start,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                var isDriverSelectorOpen = remember {
                                    mutableStateOf(false)
                                }
                                var driverViewModel =
                                    VulkanDriverViewModel(settingsViewModel.activity)
                                var isChanged = remember {
                                    mutableStateOf(false)
                                }
                                var refresh = remember {
                                    mutableStateOf(false)
                                }
                                var drivers = driverViewModel.getAvailableDrivers()
                                var selectedDriver = remember {
                                    mutableStateOf(0)
                                }

                                if (refresh.value) {
                                    isChanged.value = true
                                    refresh.value = false
                                }

                                if (isDriverSelectorOpen.value) {
                                    BasicAlertDialog(onDismissRequest = {
                                        isDriverSelectorOpen.value = false

                                        if (isChanged.value) {
                                            driverViewModel.saveSelected()
                                        }
                                    }) {
                                        Column {
                                            Surface(
                                                modifier = Modifier
                                                    .wrapContentWidth()
                                                    .wrapContentHeight(),
                                                shape = MaterialTheme.shapes.large,
                                                tonalElevation = AlertDialogDefaults.TonalElevation
                                            ) {
                                                if (!isChanged.value) {
                                                    selectedDriver.value =
                                                        drivers.indexOfFirst { it.driverPath == driverViewModel.selected } + 1
                                                    isChanged.value = true
                                                }
                                                Column {
                                                    Column(
                                                        modifier = Modifier
                                                            .fillMaxWidth()
                                                            .height(350.dp)
                                                            .verticalScroll(rememberScrollState())
                                                    ) {
                                                        Row(
                                                            modifier = Modifier
                                                                .fillMaxWidth()
                                                                .padding(8.dp),
                                                            verticalAlignment = Alignment.CenterVertically
                                                        ) {
                                                            RadioButton(
                                                                selected = selectedDriver.value == 0 || driverViewModel.selected.isEmpty(),
                                                                onClick = {
                                                                    selectedDriver.value = 0
                                                                    isChanged.value = true
                                                                    driverViewModel.selected = ""
                                                                })
                                                            Column {
                                                                Text(text = "Default",
                                                                    modifier = Modifier
                                                                        .fillMaxWidth()
                                                                        .clickable {
                                                                            selectedDriver.value = 0
                                                                            isChanged.value = true
                                                                            driverViewModel.selected =
                                                                                ""
                                                                        })
                                                            }
                                                        }
                                                        var driverIndex = 1
                                                        for (driver in drivers) {
                                                            var ind = driverIndex
                                                            Row(
                                                                modifier = Modifier
                                                                    .fillMaxWidth()
                                                                    .padding(4.dp),
                                                                verticalAlignment = Alignment.CenterVertically
                                                            ) {
                                                                RadioButton(
                                                                    selected = selectedDriver.value == ind,
                                                                onClick = {
                                                                    selectedDriver.value = ind
                                                                    isChanged.value = true
                                                                    driverViewModel.selected =
                                                                        driver.driverPath
                                                                })
                                                                Column(modifier = Modifier.clickable {
                                                                    selectedDriver.value =
                                                                        ind
                                                                    isChanged.value =
                                                                        true
                                                                    driverViewModel.selected =
                                                                        driver.driverPath
                                                                }) {
                                                                    Text(
                                                                        text = driver.libraryName,
                                                                        modifier = Modifier
                                                                            .fillMaxWidth()
                                                                    )
                                                                    Text(
                                                                        text = driver.driverVersion,
                                                                        modifier = Modifier
                                                                            .fillMaxWidth()
                                                                    )
                                                                    Text(
                                                                        text = driver.description,
                                                                        modifier = Modifier
                                                                            .fillMaxWidth()
                                                                    )
                                                                }
                                                            }

                                                            driverIndex++
                                                        }
                                                    }
                                                    Row(
                                                        horizontalArrangement = Arrangement.End,
                                                        modifier = Modifier
                                                            .fillMaxWidth()
                                                            .padding(16.dp)
                                                    ) {
                                                        Button(onClick = {
                                                            driverViewModel.removeSelected()
                                                            refresh.value = true
                                                        }, modifier = Modifier.padding(8.dp)) {
                                                            Text(text = "Remove")
                                                        }

                                                        Button(onClick = {
                                                            driverViewModel.add(refresh)
                                                            refresh.value = true
                                                        }, modifier = Modifier.padding(8.dp)) {
                                                            Text(text = "Add")
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                TextButton(
                                    {
                                        isChanged.value = false
                                        isDriverSelectorOpen.value = !isDriverSelectorOpen.value
                                    },
                                    modifier = Modifier.align(Alignment.CenterVertically)
                                ) {
                                    Text(text = "Drivers")
                                }
                            }

                        }
                    }
                    
                    // 新增后处理设置部分
ExpandableView(onCardArrowClick = { }, title = "Post-Processing") {
    Column(modifier = Modifier.fillMaxWidth()) {
        // 抗锯齿设置
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(8.dp)
                .clickable { showAntiAliasingDialog.value = true },
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(text = "Anti-Aliasing")
            Text(
                text = when (antiAliasing.value) {
                    1 -> "Fxaa"
                    2 -> "SmaaLow"
                    3 -> "SmaaMedium"
                    4 -> "SmaaHigh"
                    5 -> "SmaaUltra"
                    else -> "None"
                },
                color = MaterialTheme.colorScheme.primary
            )
        }
        
        // Scaling Filter 设置       
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(8.dp)
                .clickable { showScalingFilterDialog.value = true },
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(text = "Scaling Filter")
            Text(
                text = when (scalingFilter.value) {
                    0 -> "Bilinear"
                    1 -> "Nearest"
                    2 -> "FSR"
                    3 -> "Area" // 添加Area
                    else -> "Bilinear"
                },
                color = MaterialTheme.colorScheme.primary
            )
        }
        
        // Scaling Filter Level 设置
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(8.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(text = "Scaling Filter Level")
            Text(
                text = "${scalingFilterLevel.value}%",
                color = MaterialTheme.colorScheme.primary
            )
        }
        
        // 滑动条
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically
        ) {
            androidx.compose.material3.Slider(
                value = scalingFilterLevel.value.toFloat(),
                onValueChange = { newValue ->
                    scalingFilterLevel.value = newValue.toInt()
                },
                valueRange = 0f..100f,
                steps = 20,
                modifier = Modifier.fillMaxWidth()
            )
        }
    }
}

// 抗锯齿选择对话框
if (showAntiAliasingDialog.value) {
    BasicAlertDialog(
        onDismissRequest = { showAntiAliasingDialog.value = false }
    ) {
        Surface(
            modifier = Modifier
                .wrapContentWidth()
                .wrapContentHeight(),
            shape = MaterialTheme.shapes.large,
            tonalElevation = AlertDialogDefaults.TonalElevation
        ) {
            Column(
                modifier = Modifier.padding(16.dp)
            ) {
                Text(
                    text = "Select Anti-Aliasing",
                    style = MaterialTheme.typography.headlineSmall,
                    modifier = Modifier.padding(bottom = 16.dp)
                )
                
                // None 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            antiAliasing.value = 0
                            showAntiAliasingDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = antiAliasing.value == 0,
                        onClick = {
                            antiAliasing.value = 0
                            showAntiAliasingDialog.value = false
                        }
                    )
                    Text(
                        text = "None",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // Fxaa 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            antiAliasing.value = 1
                            showAntiAliasingDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = antiAliasing.value == 1,
                        onClick = {
                            antiAliasing.value = 1
                            showAntiAliasingDialog.value = false
                        }
                    )
                    Text(
                        text = "Fxaa",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // SmaaLow 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            antiAliasing.value = 2
                            showAntiAliasingDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = antiAliasing.value == 2,
                        onClick = {
                            antiAliasing.value = 2
                            showAntiAliasingDialog.value = false
                        }
                    )
                    Text(
                        text = "SmaaLow",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // SmaaMedium 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            antiAliasing.value = 3
                            showAntiAliasingDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = antiAliasing.value == 3,
                        onClick = {
                            antiAliasing.value = 3
                            showAntiAliasingDialog.value = false
                        }
                    )
                    Text(
                        text = "SmaaMedium",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // SmaaHigh 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            antiAliasing.value = 4
                            showAntiAliasingDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = antiAliasing.value == 4,
                        onClick = {
                            antiAliasing.value = 4
                            showAntiAliasingDialog.value = false
                        }
                    )
                    Text(
                        text = "SmaaHigh",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // SmaaUltra 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            antiAliasing.value = 5
                            showAntiAliasingDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = antiAliasing.value == 5,
                        onClick = {
                            antiAliasing.value = 5
                            showAntiAliasingDialog.value = false
                        }
                    )
                    Text(
                        text = "SmaaUltra",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // 添加取消按钮
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 16.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    TextButton(
                        onClick = { showAntiAliasingDialog.value = false }
                    ) {
                        Text("Cancel")
                    }
                }
            }
        }
    }
}

// Scaling Filter 选择对话框
if (showScalingFilterDialog.value) {
    BasicAlertDialog(
        onDismissRequest = { showScalingFilterDialog.value = false }
    ) {
        Surface(
            modifier = Modifier
                .wrapContentWidth()
                .wrapContentHeight(),
            shape = MaterialTheme.shapes.large,
            tonalElevation = AlertDialogDefaults.TonalElevation
        ) {
            Column(
                modifier = Modifier.padding(16.dp)
            ) {
                Text(
                    text = "Select Scaling Filter",
                    style = MaterialTheme.typography.headlineSmall,
                    modifier = Modifier.padding(bottom = 16.dp)
                )
                
                // Bilinear 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            scalingFilter.value = 0
                            showScalingFilterDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = scalingFilter.value == 0,
                        onClick = {
                            scalingFilter.value = 0
                            showScalingFilterDialog.value = false
                        }
                    )
                    Text(
                        text = "Bilinear",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // Nearest 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            scalingFilter.value = 1
                            showScalingFilterDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = scalingFilter.value == 1,
                        onClick = {
                            scalingFilter.value = 1
                            showScalingFilterDialog.value = false
                        }
                    )
                    Text(
                        text = "Nearest",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // FSR 选项
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            scalingFilter.value = 2
                            showScalingFilterDialog.value = false
                        }
                        .padding(vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(
                        selected = scalingFilter.value == 2,
                        onClick = {
                            scalingFilter.value = 2
                            showScalingFilterDialog.value = false
                        }
                    )
                    Text(
                        text = "FSR",
                        modifier = Modifier.padding(start = 16.dp)
                    )
                }
                
                // 在这里添加Area选项
Row(
    modifier = Modifier
        .fillMaxWidth()
        .clickable {
            scalingFilter.value = 3 // Area 对应的值
            showScalingFilterDialog.value = false
        }
        .padding(vertical = 12.dp),
    verticalAlignment = Alignment.CenterVertically
) {
    RadioButton(
        selected = scalingFilter.value == 3,
        onClick = {
            scalingFilter.value = 3
            showScalingFilterDialog.value = false
        }
    )
    Text(
        text = "Area",
        modifier = Modifier.padding(start = 16.dp)
    )
}

                // 添加取消按钮
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 16.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    TextButton(
                        onClick = { showScalingFilterDialog.value = false }
                    ) {
                        Text("Cancel")
                    }
                }
            }
        }
    }
}

                    // 新增音频设置部分
                    ExpandableView(onCardArrowClick = { }, title = "Audio") {
                        Column(modifier = Modifier.fillMaxWidth()) {
                            // 音频引擎设置
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp)
                                    .clickable { showAudioEngineDialog.value = true },
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Audio Engine")
                                Text(
                                    text = when (audioEngineType.value) {
                                        1 -> "OpenAL"
                                        2 -> "SDL2"
                                        3 -> "Oboe"
                                        else -> "Disabled"
                                    },
                                    color = MaterialTheme.colorScheme.primary
                                )
                            }
                        }
                    }
                    
                    // 区域与语言设置 - 使用对话框替代下拉菜单
ExpandableView(onCardArrowClick = { }, title = "Region & Language") {
    Column(modifier = Modifier.fillMaxWidth()) {
        // 区域设置 - 保持不变
        var expandedRegion by remember { mutableStateOf(false) }
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .padding(8.dp)
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { expandedRegion = true },
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(text = "Region")
                val regionNames = listOf("Japan", "USA", "Europe", "Australia", "China", "Korea", "Taiwan")
                Text(text = regionNames[regionCode.value])
                Icon(
                    imageVector = Icons.Filled.ArrowDropDown,
                    contentDescription = "Expand region options"
                )
            }
            
            DropdownMenu(
                expanded = expandedRegion,
                onDismissRequest = { expandedRegion = false }
            ) {
                val regionOptions = listOf("Japan", "USA", "Europe", "Australia", "China", "Korea", "Taiwan")
                regionOptions.forEachIndexed { index, option ->
                    DropdownMenuItem(
                        text = { Text(option) },
                        onClick = {
                            regionCode.value = index
                            expandedRegion = false
                        }
                    )
                }
            }
        }
        
        // 语言设置 - 使用BasicAlertDialog替代
        var showLanguageDialog by remember { mutableStateOf(false) }
        
        // 语言选择对话框
        if (showLanguageDialog) {
            BasicAlertDialog(
                onDismissRequest = { showLanguageDialog = false }
            ) {
                Surface(
                    modifier = Modifier
                        .wrapContentWidth()
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation
                ) {
                    Column(
                        modifier = Modifier.padding(16.dp)
                    ) {
                        Text(
                            text = "Select Language",
                            style = MaterialTheme.typography.headlineSmall,
                            modifier = Modifier.padding(bottom = 16.dp)
                        )
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .heightIn(max = 400.dp)
                                .verticalScroll(rememberScrollState())
                        ) {
                            val languageOptions = listOf(
                                "Japanese", "American English", "French", "German", "Italian", 
                                "Spanish", "Chinese", "Korean", "Dutch", "Portuguese", 
                                "Russian", "Taiwanese", "British English", "Canadian French", 
                                "Latin American Spanish", "Simplified Chinese", "Traditional Chinese", 
                                "Brazilian Portuguese"
                            )
                            
                            languageOptions.forEachIndexed { index, option ->
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            systemLanguage.value = index
                                            showLanguageDialog = false
                                        }
                                        .padding(vertical = 12.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    RadioButton(
                                        selected = systemLanguage.value == index,
                                        onClick = {
                                            systemLanguage.value = index
                                            showLanguageDialog = false
                                        }
                                    )
                                    Text(
                                        text = option,
                                        modifier = Modifier.padding(start = 16.dp)
                                    )
                                }
                            }
                        }
                        // 添加取消按钮
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = 16.dp),
                            horizontalArrangement = Arrangement.End
                        ) {
                            TextButton(
                                onClick = { showLanguageDialog = false }
                            ) {
                                Text("Cancel")
                            }
                        }
                    }
                }
            }
        }
        
        // 语言选择行
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(8.dp)
                .clickable { showLanguageDialog = true },
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(text = "Language")
            val languageNames = listOf(
                "Japanese", "American English", "French", "German", "Italian", 
                "Spanish", "Chinese", "Korean", "Dutch", "Portuguese", 
                "Russian", "Taiwanese", "British English", "Canadian French", 
                "Latin American Spanish", "Simplified Chinese", "Traditional Chinese", 
                "Brazilian Portuguese"
            )
            Text(
                text = languageNames[systemLanguage.value],
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
        
        // 自定义时间开关
Row(
    modifier = Modifier
        .fillMaxWidth()
        .padding(8.dp),
    horizontalArrangement = Arrangement.SpaceBetween,
    verticalAlignment = Alignment.CenterVertically
) {
    Text(text = "Custom System Time")
    Switch(
        checked = customTimeEnabled.value,
        onCheckedChange = { customTimeEnabled.value = it }
    )
}

// 当自定义时间开关打开时，显示时间设置选项
AnimatedVisibility(visible = customTimeEnabled.value) {
    Column(modifier = Modifier.fillMaxWidth()) {
        // 显示当前设置的时间
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(8.dp)
                .clickable { showCustomTimeDialog.value = true },
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(text = "Set Custom Time")
            Text(
                text = "${customTimeYear.value}-${customTimeMonth.value.toString().padStart(2, '0')}-${customTimeDay.value.toString().padStart(2, '0')} ${customTimeHour.value.toString().padStart(2, '0')}:${customTimeMinute.value.toString().padStart(2, '0')}:${customTimeSecond.value.toString().padStart(2, '0')}",
                color = MaterialTheme.colorScheme.primary
            )
        }
    }
}

// 自定义时间设置对话框
if (showCustomTimeDialog.value) {
    CustomTimeDialog(
        currentYear = customTimeYear.value,
        currentMonth = customTimeMonth.value,
        currentDay = customTimeDay.value,
        currentHour = customTimeHour.value,
        currentMinute = customTimeMinute.value,
        currentSecond = customTimeSecond.value,
        onDismiss = { showCustomTimeDialog.value = false },
        onTimeSet = { year, month, day, hour, minute, second ->
            customTimeYear.value = year
            customTimeMonth.value = month
            customTimeDay.value = day
            customTimeHour.value = hour
            customTimeMinute.value = minute
            customTimeSecond.value = second
            showCustomTimeDialog.value = false
        }
    )
}
    }
}
                    
                    ExpandableView(onCardArrowClick = { }, title = "Hack") {
                        Column(modifier = Modifier.fillMaxWidth()) {
                            // 内存配置设置
Row(
    modifier = Modifier
        .fillMaxWidth()
        .padding(8.dp)
        .clickable { showMemoryConfigDialog.value = true },
    horizontalArrangement = Arrangement.SpaceBetween,
    verticalAlignment = Alignment.CenterVertically
) {
    Text(text = "DRAM Configuration")
    val memoryConfigNames = listOf("4GB", "4GB Applet Dev", "4GB System Dev", "6GB", "6GB Applet Dev", "8GB")
    Text(
        text = memoryConfigNames[memoryConfiguration.value],
        color = MaterialTheme.colorScheme.primary
    )
}

// 内存配置选择对话框
if (showMemoryConfigDialog.value) {
    BasicAlertDialog(
        onDismissRequest = { showMemoryConfigDialog.value = false }
    ) {
        Surface(
            modifier = Modifier
                .wrapContentWidth()
                .wrapContentHeight(),
            shape = MaterialTheme.shapes.large,
            tonalElevation = AlertDialogDefaults.TonalElevation
        ) {
            Column(
                modifier = Modifier.padding(16.dp)
            ) {
                Text(
                    text = "Select Memory Configuration",
                    style = MaterialTheme.typography.headlineSmall,
                    modifier = Modifier.padding(bottom = 16.dp)
                )
                
                val memoryOptions = listOf("4GB", "4GB Applet Dev", "4GB System Dev", "6GB", "6GB Applet Dev", "8GB")
                
                memoryOptions.forEachIndexed { index, option ->
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clickable {
                                memoryConfiguration.value = index
                                showMemoryConfigDialog.value = false
                            }
                            .padding(vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        RadioButton(
                            selected = memoryConfiguration.value == index,
                            onClick = {
                                memoryConfiguration.value = index
                                showMemoryConfigDialog.value = false
                            }
                        )
                        Text(
                            text = option,
                            modifier = Modifier.padding(start = 16.dp)
                        )
                    }
                }
                
                // 添加取消按钮
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 16.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    TextButton(
                        onClick = { showMemoryConfigDialog.value = false }
                    ) {
                        Text("Cancel")
                    }
                }
            }
        }
    }
}

                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Skip Memory Barriers")
                                Switch(checked = skipMemoryBarriers.value, onCheckedChange = {
                                    skipMemoryBarriers.value = !skipMemoryBarriers.value
                                })
                            }
                            Text(
                                text = "Warning: This may improve performance but can cause instability in some games",
                                fontSize = 12.sp,
                                modifier = Modifier.padding(8.dp)
                            )
                        }
                    }
                    ExpandableView(onCardArrowClick = { }, title = "Input") {
                        Column(modifier = Modifier.fillMaxWidth()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Show virtual controller")
                                Switch(checked = useVirtualController.value, onCheckedChange = {
                                    useVirtualController.value = !useVirtualController.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Motion")
                                Switch(checked = enableMotion.value, onCheckedChange = {
                                    enableMotion.value = !enableMotion.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Use Switch Controller Layout")
                                Switch(checked = useSwitchLayout.value, onCheckedChange = {
                                    useSwitchLayout.value = !useSwitchLayout.value
                                })
                            }

                            val interactionSource: MutableInteractionSource = remember { MutableInteractionSource() }

                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Controller Stick Sensitivity")
                                androidx.compose.material3.Slider(modifier = Modifier.width(250.dp), value = controllerStickSensitivity.value, onValueChange = {
                                    controllerStickSensitivity.value = it
                                }, valueRange = 0.1f..2f,
                                    steps = 20,
                                    interactionSource = interactionSource,
                                    thumb = {
                                        Label(
                                            label = {
                                                PlainTooltip(modifier = Modifier
                                                    .sizeIn(45.dp, 25.dp)
                                                    .wrapContentWidth()) {
                                                    Text("%.2f".format(controllerStickSensitivity.value))
                                                }
                                            },
                                            interactionSource = interactionSource
                                        ) {
                                            Icon(
                                                imageVector = org.ryujinx.android.Icons.circle(
                                                    color = MaterialTheme.colorScheme.primary
                                                ),
                                                contentDescription = null,
                                                modifier = Modifier.size(ButtonDefaults.IconSize),
                                                tint = MaterialTheme.colorScheme.primary
                                            )
                                        }
                                    }
                                )
                            }

                        }
                    }
                    ExpandableView(onCardArrowClick = { }, title = "Log") {
                        Column(modifier = Modifier.fillMaxWidth()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Debug Logs")
                                Switch(checked = enableDebugLogs.value, onCheckedChange = {
                                    enableDebugLogs.value = !enableDebugLogs.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Stub Logs")
                                Switch(checked = enableStubLogs.value, onCheckedChange = {
                                    enableStubLogs.value = !enableStubLogs.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Info Logs")
                                Switch(checked = enableInfoLogs.value, onCheckedChange = {
                                    enableInfoLogs.value = !enableInfoLogs.value
                                })
                            }
                           Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Warning Logs")
                                Switch(checked = enableWarningLogs.value, onCheckedChange = {
                                    enableWarningLogs.value = !enableWarningLogs.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Error Logs")
                                Switch(checked = enableErrorLogs.value, onCheckedChange = {
                                    enableErrorLogs.value = !enableErrorLogs.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Guest Logs")
                                Switch(checked = enableGuestLogs.value, onCheckedChange = {
                                    enableGuestLogs.value = !enableGuestLogs.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Access Logs")
                                Switch(checked = enableAccessLogs.value, onCheckedChange = {
                                    enableAccessLogs.value = !enableAccessLogs.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Trace Logs")
                                Switch(checked = enableTraceLogs.value, onCheckedChange = {
                                    enableTraceLogs.value = !enableTraceLogs.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "Enable Graphics Debug Logs")
                                Switch(checked = enableGraphicsLogs.value, onCheckedChange = {
                                    enableGraphicsLogs.value = !enableGraphicsLogs.value
                                })
                            }
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Button(onClick = {
                                    mainViewModel.logging.requestExport()
                                }) {
                                    Text(text = "Send Logs")
                                }
                            }
                        }
                    }
                }

                // 音频引擎选择对话框
                if (showAudioEngineDialog.value) {
                    BasicAlertDialog(
                        onDismissRequest = { showAudioEngineDialog.value = false }
                    ) {
                        Surface(
                            modifier = Modifier
                                .wrapContentWidth()
                                .wrapContentHeight(),
                            shape = MaterialTheme.shapes.large,
                            tonalElevation = AlertDialogDefaults.TonalElevation
                        ) {
                            Column(
                                modifier = Modifier.padding(16.dp)
                            ) {
                                Text(
                                    text = "Select Audio Engine",
                                    style = MaterialTheme.typography.headlineSmall,
                                    modifier = Modifier.padding(bottom = 16.dp)
                                )
                                
                                // OpenAL选项
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            audioEngineType.value = 1
                                            showAudioEngineDialog.value = false
                                        }
                                        .padding(vertical = 12.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    RadioButton(
                                        selected = audioEngineType.value == 1,
                                        onClick = {
                                            audioEngineType.value = 1
                                            showAudioEngineDialog.value = false
                                        }
                                    )
                                    Text(
                                        text = "OpenAL",
                                        modifier = Modifier.padding(start = 16.dp)
                                    )
                                }
                                
                                // SDL2选项
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            audioEngineType.value = 2
                                            showAudioEngineDialog.value = false
                                        }
                                        .padding(vertical = 12.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    RadioButton(
                                        selected = audioEngineType.value == 2,
                                        onClick = {
                                            audioEngineType.value = 2
                                            showAudioEngineDialog.value = false
                                        }
                                    )
                                    Text(
                                        text = "SDL2",
                                        modifier = Modifier.padding(start = 16.dp)
                                    )
                                }
                                
                                // Oboe选项
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            audioEngineType.value = 3
                                            showAudioEngineDialog.value = false
                                        }
                                        .padding(vertical = 12.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    RadioButton(
                                        selected = audioEngineType.value == 3,
                                        onClick = {
                                            audioEngineType.value = 3
                                            showAudioEngineDialog.value = false
                                        }
                                    )
                                    Text(
                                        text = "Oboe",
                                        modifier = Modifier.padding(start = 16.dp)
                                    )
                                }
                                
                                // 禁用音频选项
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            audioEngineType.value = 0
                                            showAudioEngineDialog.value = false
                                        }
                                        .padding(vertical = 12.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    RadioButton(
                                        selected = audioEngineType.value == 0,
                                        onClick = {
                                            audioEngineType.value = 0
                                            showAudioEngineDialog.value = false
                                        }
                                    )
                                    Text(
                                        text = "Disabled",
                                        modifier = Modifier.padding(start = 16.dp)
                                    )
                                }
                                
                                // 添加取消按钮
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(top = 16.dp),
                                    horizontalArrangement = Arrangement.End
                                ) {
                                    TextButton(
                                        onClick = { showAudioEngineDialog.value = false }
                                    ) {
                                        Text("Cancel")
                                    }
                                }
                            }
                        }
                    }
                }

                BackHandler {
                    settingsViewModel.save(
                        isHostMapped,
                        useNce, enableVsync, enableDocked, enablePtc, enableJitCacheEviction, ignoreMissingServices,
                        enableShaderCache,
                        enableTextureRecompression,
                        resScale,
                        aspectRatio, // 新增参数
                        useVirtualController,
                        isGrid,
                        useSwitchLayout,
                        enableMotion,
                        enablePerformanceMode,
                        controllerStickSensitivity,
                        enableDebugLogs,
                        enableStubLogs,
                        enableInfoLogs,
                        enableWarningLogs,
                        enableErrorLogs,
                        enableGuestLogs,
                        enableAccessLogs,
                        enableTraceLogs,
                        enableGraphicsLogs,
                        skipMemoryBarriers, // 新增参数
                        regionCode, // 新增参数
                        systemLanguage, // 新增参数
                        audioEngineType, // 新增参数
                        scalingFilter, // 新增：缩放过滤器
                        scalingFilterLevel, // 新增：缩放过滤器级别
                        antiAliasing, // 新增：抗锯齿模式
                        memoryConfiguration, // 新增DRAM参数
                        customTimeEnabled,
    customTimeYear,
    customTimeMonth,
    customTimeDay,
    customTimeHour,
    customTimeMinute,
    customTimeSecond
                    )
                    settingsViewModel.navController.popBackStack()
                }
            }
        }

        @OptIn(ExperimentalMaterial3Api::class)
        @Composable
        @SuppressLint("UnusedTransitionTargetStateParameter")
        fun ExpandableView(
            onCardArrowClick: () -> Unit,
            title: String,
            content: @Composable () -> Unit
        ) {
            val expanded = false
            val mutableExpanded = remember {
                mutableStateOf(expanded)
            }
            val transitionState = remember {
                MutableTransitionState(expanded).apply {
                    targetState = !mutableExpanded.value
                }
            }
            val transition = updateTransition(transitionState, label = "transition")
            val arrowRotationDegree by transition.animateFloat({
                tween(durationMillis = EXPANSTION_TRANSITION_DURATION)
            }, label = "rotationDegreeTransition") {
                if (mutableExpanded.value) 0f else 180f
            }

            Card(
                shape = MaterialTheme.shapes.medium,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(
                        horizontal = 24.dp,
                        vertical = 8.dp
                    )
            ) {
                Column {
                    Card(
                        onClick = {
                            mutableExpanded.value = !mutableExpanded.value
                            onCardArrowClick()
                        }) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            CardTitle(title = title)
                            CardArrow(
                                degrees = arrowRotationDegree,
                            )

                        }
                    }
                    ExpandableContent(visible = mutableExpanded.value, content = content)
                }
            }
        }

        @Composable
        fun CardArrow(
            degrees: Float,
        ) {
            Icon(
                Icons.Filled.KeyboardArrowUp,
                contentDescription = "Expandable Arrow",
                modifier = Modifier
                    .padding(8.dp)
                    .rotate(degrees),
            )
        }

        @Composable
        fun CardTitle(title: String) {
            Text(
                text = title,
                modifier = Modifier
                    .padding(16.dp),
                textAlign = TextAlign.Center,
            )
        }

        @Composable
        fun ExpandableContent(
            visible: Boolean = true,
            content: @Composable () -> Unit
            ) {
            val enterTransition = remember {
                expandVertically(
                    expandFrom = Alignment.Top,
                    animationSpec = tween(EXPANSTION_TRANSITION_DURATION)
                ) + fadeIn(
                    initialAlpha = 0.3f,
                    animationSpec = tween(EXPANSTION_TRANSITION_DURATION)
                )
            }
            val exitTransition = remember {
                shrinkVertically(
                    // Expand from the top.
                    shrinkTowards = Alignment.Top,
                    animationSpec = tween(EXPANSTION_TRANSITION_DURATION)
                ) + fadeOut(
                    // Fade in with the initial alpha of 0.3f.
                    animationSpec = tween(EXPANSTION_TRANSITION_DURATION)
                )
            }

            AnimatedVisibility(
                visible = visible,
                enter = enterTransition,
                exit = exitTransition
            ) {
                Column(modifier = Modifier.padding(8.dp)) {
                    content()
                }
            }
        }
    }
}
