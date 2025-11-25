@file:OptIn(ExperimentalMaterial3Api::class, ExperimentalAnimationApi::class)

package org.ryujinx.android.views

import androidx.activity.compose.BackHandler
import androidx.compose.animation.*
import androidx.compose.animation.core.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.background
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.border
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ExitToApp
import androidx.compose.material.icons.filled.ArrowForward
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.material3.HorizontalDivider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.PointerEventType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextAlign
import compose.icons.CssGgIcons
import compose.icons.cssggicons.ToolbarBottom
import org.ryujinx.android.GameController
import org.ryujinx.android.GameHost
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.PerformanceStatsSettings
import org.ryujinx.android.viewmodels.QuickSettings
import kotlin.math.roundToInt

class GameViews {
    companion object {
        @Composable
        fun Main() {
            Surface(
                modifier = Modifier.fillMaxSize(),
                color = MaterialTheme.colorScheme.background
            ) {
                GameView(mainViewModel = MainActivity.mainViewModel!!)
            }
        }

        @Composable
        fun GameView(mainViewModel: MainViewModel) {
            Box(modifier = Modifier.fillMaxSize()) {
                AndroidView(
                    modifier = Modifier.fillMaxSize(),
                    factory = { context ->
                        GameHost(context, mainViewModel)
                    }
                )
                GameOverlay(mainViewModel)
            }
        }

        @Composable
        fun GameOverlay(mainViewModel: MainViewModel) {
            // 从MainViewModel加载持久化的性能统计显示设置
            val initialStatsSettings = mainViewModel.getPerformanceStatsSettings()
            
            // 全局显示/隐藏状态
            val showStats = remember {
                mutableStateOf(initialStatsSettings.showStats)
            }
            
            // 各个统计项的独立显示状态
            val showFps = remember { mutableStateOf(initialStatsSettings.showFps) }
            val showRam = remember { mutableStateOf(initialStatsSettings.showRam) }
            val showBatteryTemperature = remember { mutableStateOf(initialStatsSettings.showBatteryTemperature) }
            val showBatteryLevel = remember { mutableStateOf(initialStatsSettings.showBatteryLevel) }
            val showFifo = remember { mutableStateOf(initialStatsSettings.showFifo) }

            // 编辑模式状态
            val isEditing = remember { mutableStateOf(false) }

            // 侧边菜单状态
            val showSideMenu = remember { mutableStateOf(false) }

            // 对话框状态
            val showPerformanceSettings = remember { mutableStateOf(false) }
            val showAdjustControlsDialog = remember { mutableStateOf(false) }
            val showExitConfirmDialog = remember { mutableStateOf(false) }

            // 暂停状态
            val isPaused = remember { mutableStateOf(false) }

            Box(modifier = Modifier.fillMaxSize()) {
                if (showStats.value) {
                    GameStats(
                        mainViewModel = mainViewModel,
                        showFps = showFps.value,
                        showRam = showRam.value,
                        showBatteryTemperature = showBatteryTemperature.value,
                        showBatteryLevel = showBatteryLevel.value,
                        showFifo = showFifo.value
                    )
                }

                val showController = remember {
                    mutableStateOf(QuickSettings(mainViewModel.activity).useVirtualController)
                }
                val enableVsync = remember {
                    mutableStateOf(QuickSettings(mainViewModel.activity).enableVsync)
                }
                val enableMotion = remember {
                    mutableStateOf(QuickSettings(mainViewModel.activity).enableMotion)
                }

                val showLoading = remember {
                    mutableStateOf(true)
                }

                val progressValue = remember {
                    mutableStateOf(0.0f)
                }

                val progress = remember {
                    mutableStateOf("Loading")
                }

                mainViewModel.setProgressStates(showLoading, progressValue, progress)

                // touch surface
                Surface(color = Color.Transparent, modifier = Modifier
                    .fillMaxSize()
                    .padding(0.dp)
                    .pointerInput(Unit) {
                        awaitPointerEventScope {
                            while (true) {
                                val event = awaitPointerEvent()
                                if (showController.value || isEditing.value)
                                    continue

                                val change = event
                                    .component1()
                                    .firstOrNull()
                                change?.apply {
                                    val position = this.position

                                    when (event.type) {
                                        PointerEventType.Press -> {
                                            RyujinxNative.jnaInstance.inputSetTouchPoint(
                                                position.x.roundToInt(),
                                                position.y.roundToInt()
                                            )
                                        }

                                        PointerEventType.Release -> {
                                            RyujinxNative.jnaInstance.inputReleaseTouchPoint()

                                        }

                                        PointerEventType.Move -> {
                                            RyujinxNative.jnaInstance.inputSetTouchPoint(
                                                position.x.roundToInt(),
                                                position.y.roundToInt()
                                            )

                                        }
                                    }
                                }
                            }
                        }
                    }) {
                }

                // 点击外部关闭侧边菜单
                if (showSideMenu.value) {
                    Surface(
                        color = Color.Transparent,
                        modifier = Modifier
                            .fillMaxSize()
                            .pointerInput(Unit) {
                                awaitPointerEventScope {
                                    while (true) {
                                        val event = awaitPointerEvent()
                                        if (event.type == PointerEventType.Press) {
                                            showSideMenu.value = false
                                        }
                                    }
                                }
                            }
                    ) {}
                }

                if (!showLoading.value) {
                    GameController.Compose(mainViewModel)

                    // 侧边菜单 - 瞬时显示（移除动画）
                    if (showSideMenu.value) {
                        SideMenu(
                            mainViewModel = mainViewModel,
                            showController = showController,
                            enableVsync = enableVsync,
                            enableMotion = enableMotion,
                            isEditing = isEditing,
                            isPaused = isPaused,
                            showPerformanceSettings = showPerformanceSettings,
                            showAdjustControlsDialog = showAdjustControlsDialog,
                            showExitConfirmDialog = showExitConfirmDialog,
                            onDismiss = { showSideMenu.value = false }
                        )
                    }

                    // 返回键处理 - 打开侧边菜单
                    BackHandler(enabled = !showSideMenu.value) {
                        showSideMenu.value = true
                    }

                    // 返回键处理 - 关闭侧边菜单
                    BackHandler(enabled = showSideMenu.value) {
                        showSideMenu.value = false
                    }
                }

                // 性能设置对话框
                if (showPerformanceSettings.value) {
                    PerformanceSettingsDialog(
                        mainViewModel = mainViewModel,
                        showStats = showStats,
                        showFps = showFps,
                        showRam = showRam,
                        showBatteryTemperature = showBatteryTemperature,
                        showBatteryLevel = showBatteryLevel,
                        showFifo = showFifo,
                        onDismiss = { showPerformanceSettings.value = false }
                    )
                }

                // 调整按键对话框
                if (showAdjustControlsDialog.value) {
                    ControlEditViews.AdjustControlsDialog(
                        mainViewModel = mainViewModel,
                        onDismiss = { showAdjustControlsDialog.value = false }
                    )
                }

                // 退出确认对话框
                if (showExitConfirmDialog.value) {
                    ExitConfirmDialog(
                        mainViewModel = mainViewModel,
                        onDismiss = { showExitConfirmDialog.value = false }
                    )
                }

                if (showLoading.value) {
                    Card(
                        modifier = Modifier
                            .padding(16.dp)
                            .fillMaxWidth(0.5f)
                            .align(Alignment.Center),
                        shape = MaterialTheme.shapes.medium
                    ) {
                        Column(
                            modifier = Modifier
                                .padding(16.dp)
                                .fillMaxWidth()
                        ) {
                            Text(text = progress.value)

                            if (progressValue.value > -1)
                                LinearProgressIndicator(
                                    progress = {
                                        progressValue.value
                                    },
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(top = 16.dp),
                                )
                            else
                                LinearProgressIndicator(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(top = 16.dp)
                                )
                        }
                    }
                }

                mainViewModel.activity.uiHandler.Compose()
            }
        }

        @Composable
        fun SideMenu(
            mainViewModel: MainViewModel,
            showController: androidx.compose.runtime.MutableState<Boolean>,
            enableVsync: androidx.compose.runtime.MutableState<Boolean>,
            enableMotion: androidx.compose.runtime.MutableState<Boolean>,
            isEditing: androidx.compose.runtime.MutableState<Boolean>,
            isPaused: androidx.compose.runtime.MutableState<Boolean>,
            showPerformanceSettings: androidx.compose.runtime.MutableState<Boolean>,
            showAdjustControlsDialog: androidx.compose.runtime.MutableState<Boolean>,
            showExitConfirmDialog: androidx.compose.runtime.MutableState<Boolean>,
            onDismiss: () -> Unit
        ) {
            // 获取当前游戏标题
            val gameTitle = mainViewModel.gameModel?.getDisplayName() ?: "Unknown Game"

            // 直接渲染菜单，无动画
            Box(
                modifier = Modifier
                    .fillMaxHeight()
                    .width(280.dp)
                    .background(
                        MaterialTheme.colorScheme.surface.copy(alpha = 0.95f),
                        MaterialTheme.shapes.medium
                    )
            ) {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .verticalScroll(rememberScrollState())
                ) {
                    // 游戏标题
                    Text(
                        text = gameTitle,
                        style = MaterialTheme.typography.headlineSmall,
                        fontWeight = FontWeight.Bold,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    )

                    HorizontalDivider()

                    Spacer(modifier = Modifier.height(8.dp))

                    // 菜单项 - 直接显示，无动画
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 8.dp)
                    ) {
                        // 暂停/继续游戏
                        SideMenuItem(
                            icon = null, // 不使用图标，使用表情符号
                            text = if (isPaused.value) "▶️ Continue" else "⏸️ Pause",
                            onClick = {
                                if (isPaused.value) {
                                    // 继续游戏
                                    RyujinxNative.resumeEmulation()
                                    isPaused.value = false
                                } else {
                                    // 暂停游戏
                                    RyujinxNative.pauseEmulation()
                                    isPaused.value = true
                                }
                                onDismiss()
                            }
                        )

                        HorizontalDivider(modifier = Modifier.padding(vertical = 8.dp))

                        // Enable Motion
                        SideMenuItem(
                            icon = null,
                            text = "Enable Motion",
                            trailingContent = {
                                Switch(
                                    checked = enableMotion.value,
                                    onCheckedChange = {
                                        enableMotion.value = it
                                        val settings = QuickSettings(mainViewModel.activity)
                                        settings.enableMotion = enableMotion.value
                                        settings.save()
                                        if (enableMotion.value)
                                            mainViewModel.motionSensorManager?.register()
                                        else
                                            mainViewModel.motionSensorManager?.unregister()
                                    },
                                    modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                )
                            },
                            onClick = { /* 开关已处理 */ }
                        )

                        // 虚拟手柄开关
                        SideMenuItem(
                            icon = null,
                            text = "🎮 Virtual Controller",
                            onClick = {
                                onDismiss()
                                showController.value = !showController.value
                                RyujinxNative.jnaInstance.inputReleaseTouchPoint()
                                mainViewModel.controller?.setVisible(showController.value)
                            }
                        )

                        // VSync 开关
                        SideMenuItem(
                            icon = null,
                            text = "🔄 VSync",
                            trailingContent = {
                                Text(
                                    text = if (enableVsync.value) "ON" else "OFF",
                                    color = if (enableVsync.value) Color.Green else Color.Red
                                )
                            },
                            onClick = {
                                onDismiss()
                                enableVsync.value = !enableVsync.value
                                RyujinxNative.jnaInstance.graphicsRendererSetVsync(
                                    enableVsync.value
                                )
                            }
                        )

                        HorizontalDivider(modifier = Modifier.padding(vertical = 8.dp))

                        // 编辑模式
                        SideMenuItem(
                            icon = null,
                            text = "✏️ Edit Controls",
                            onClick = {
                                onDismiss()
                                isEditing.value = true
                                mainViewModel.controller?.setEditingMode(true)
                            }
                        )

                        // 调整按键
                        SideMenuItem(
                            icon = null,
                            text = "⚙️ Adjust Controls",
                            onClick = {
                                onDismiss()
                                showAdjustControlsDialog.value = true
                            }
                        )

                        // 性能设置
                        SideMenuItem(
                            icon = null,
                            text = "📊 Performance information",
                            onClick = {
                                onDismiss()
                                showPerformanceSettings.value = true
                            }
                        )

                        HorizontalDivider(modifier = Modifier.padding(vertical = 8.dp))

                        // 退出游戏
                        SideMenuItem(
                            icon = Icons.Default.ExitToApp,
                            text = "Exit Game",
                            onClick = {
                                onDismiss()
                                showExitConfirmDialog.value = true
                            }
                        )
                    }
                }
            }
        }

        @Composable
        fun SideMenuItem(
            icon: Any?,
            text: String,
            trailingContent: @Composable (() -> Unit)? = null,
            onClick: () -> Unit
        ) {
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 4.dp),
                color = Color.Transparent,
                onClick = onClick
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        when {
                            icon is androidx.compose.ui.graphics.vector.ImageVector -> {
                                Icon(
                                    imageVector = icon,
                                    contentDescription = null,
                                    modifier = Modifier
                                        .size(24.dp)
                                        .padding(end = 12.dp)
                                )
                            }
                            icon == null && text.contains(Regex("[\\p{So}\\p{Cn}]")) -> {
                                val emoji = text.takeWhile { it.isEmoji() }
                                Text(
                                    text = emoji,
                                    fontSize = 20.sp,
                                    modifier = Modifier
                                        .size(24.dp)
                                        .padding(end = 12.dp)
                                )
                            }
                            else -> {
                                Spacer(modifier = Modifier.size(24.dp).padding(end = 12.dp))
                            }
                        }
                        
                        Text(
                            text = if (icon == null && text.contains(Regex("[\\p{So}\\p{Cn}]"))) {
                                text.dropWhile { it.isEmoji() }.trim()
                            } else {
                                text
                            },
                            style = MaterialTheme.typography.bodyMedium
                        )
                    }
                    
                    trailingContent?.invoke() ?: Icon(
                        imageVector = Icons.Default.ArrowForward,
                        contentDescription = null,
                        modifier = Modifier.size(16.dp)
                    )
                }
            }
        }

        // 扩展函数：检查字符是否为表情符号
        private fun Char.isEmoji(): Boolean {
            return this in '\uE000'..'\uF8FF' || 
                   this in '\uD83C'..'\uDBFF' || 
                   this in '\uDC00'..'\uDFFF' ||
                   this in '\u2000'..'\u2BFF' ||
                   this in '\u2600'..'\u26FF' ||
                   this in '\u2700'..'\u27BF'
        }

        @Composable
        fun ExitConfirmDialog(
            mainViewModel: MainViewModel,
            onDismiss: () -> Unit
        ) {
            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .fillMaxWidth(0.8f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation
                ) {
                    Column {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(16.dp)
                        ) {
                            Text(
                                text = "Exit Game?",
                                style = MaterialTheme.typography.headlineSmall
                            )
                            Spacer(modifier = Modifier.height(8.dp))
                            Text(text = "Are you sure you want to exit the game?")
                            Text(text = "All unsaved data will be lost!")
                        }
                        Row(
                            horizontalArrangement = Arrangement.End,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(16.dp)
                        ) {
                            TextButton(
                                onClick = onDismiss,
                                modifier = Modifier.padding(end = 8.dp)
                            ) {
                                Text(text = "Cancel")
                            }
                            Button(
                                onClick = {
                                    onDismiss()
                                    mainViewModel.closeGame()
                                    mainViewModel.activity.setFullScreen(false)
                                    mainViewModel.navController?.popBackStack()
                                    mainViewModel.activity.isGameRunning = false
                                }
                            ) {
                                Text(text = "Exit")
                            }
                        }
                    }
                }
            }
        }

        @Composable
        fun PerformanceSettingsDialog(
            mainViewModel: MainViewModel,
            showStats: androidx.compose.runtime.MutableState<Boolean>,
            showFps: androidx.compose.runtime.MutableState<Boolean>,
            showRam: androidx.compose.runtime.MutableState<Boolean>,
            showBatteryTemperature: androidx.compose.runtime.MutableState<Boolean>,
            showBatteryLevel: androidx.compose.runtime.MutableState<Boolean>,
            showFifo: androidx.compose.runtime.MutableState<Boolean>,
            onDismiss: () -> Unit
        ) {
            fun saveSettings() {
                val settings = PerformanceStatsSettings(
                    showStats = showStats.value,
                    showFps = showFps.value,
                    showRam = showRam.value,
                    showBatteryTemperature = showBatteryTemperature.value,
                    showBatteryLevel = showBatteryLevel.value,
                    showFifo = showFifo.value
                )
                mainViewModel.savePerformanceStatsSettings(settings)
            }

            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .fillMaxWidth(0.8f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.9f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        Text(
                            text = "Game Stats",
                            style = MaterialTheme.typography.headlineSmall,
                            modifier = Modifier
                                .padding(bottom = 10.dp)
                                .align(Alignment.CenterHorizontally)
                        )
                        
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            Column(
                                modifier = Modifier.weight(1f),
                                verticalArrangement = Arrangement.spacedBy(8.dp)
                            ) {
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "FIFO")
                                    Switch(
                                        checked = showFifo.value,
                                        onCheckedChange = { 
                                            showFifo.value = it
                                            saveSettings()
                                        },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                                
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "FPS")
                                    Switch(
                                        checked = showFps.value,
                                        onCheckedChange = { 
                                            showFps.value = it
                                            saveSettings()
                                        },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                                
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "RAM")
                                    Switch(
                                        checked = showRam.value,
                                        onCheckedChange = { 
                                            showRam.value = it
                                            saveSettings()
                                        },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                            }
                            
                            Column(
                                modifier = Modifier.weight(1f),
                                verticalArrangement = Arrangement.spacedBy(8.dp)
                            ) {
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "Battery Temp")
                                    Switch(
                                        checked = showBatteryTemperature.value,
                                        onCheckedChange = { 
                                            showBatteryTemperature.value = it
                                            saveSettings()
                                        },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                                
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "Level")
                                    Switch(
                                        checked = showBatteryLevel.value,
                                        onCheckedChange = { 
                                            showBatteryLevel.value = it
                                            saveSettings()
                                        },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                            }
                        }
                        
                        HorizontalDivider(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 16.dp),
                            thickness = 1.dp,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.1f)
                        )
                        
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = 8.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(
                                text = "All Stats",
                                style = MaterialTheme.typography.bodyLarge
                            )
                            Switch(
                                checked = showStats.value,
                                onCheckedChange = { 
                                    showStats.value = it
                                    saveSettings()
                                },
                                modifier = Modifier.size(width = 36.dp, height = 24.dp)
                            )
                        }
                        
                        Spacer(modifier = Modifier.padding(8.dp))
                        
                        Row(
                            horizontalArrangement = Arrangement.End,
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Button(
                                onClick = onDismiss
                            ) {
                                Text(text = "Close")
                            }
                        }
                    }
                }
            }
        }

        @Composable
        fun GameStats(
            mainViewModel: MainViewModel,
            showFps: Boolean,
            showRam: Boolean,
            showBatteryTemperature: Boolean,
            showBatteryLevel: Boolean,
            showFifo: Boolean
        ) {
            val fifo = remember {
                mutableDoubleStateOf(0.0)
            }
            val gameFps = remember {
                mutableDoubleStateOf(0.0)
            }
            val gameTime = remember {
                mutableDoubleStateOf(0.0)
            }
            val usedMem = remember {
                mutableIntStateOf(0)
            }
            val totalMem = remember {
                mutableIntStateOf(0)
            }
            val batteryTemperature = remember {
                mutableDoubleStateOf(0.0)
            }
            val batteryLevel = remember {
                mutableIntStateOf(-1)
            }
            val isCharging = remember {
                mutableStateOf(false)
            }

            CompositionLocalProvider(
                LocalTextStyle provides TextStyle(
                    fontSize = 10.sp,
                    color = Color.White
                )
            ) {
                Box(modifier = Modifier.fillMaxSize()) {
                    Column(
                        modifier = Modifier
                            .align(Alignment.TopStart)
                            .padding(16.dp)
                            .background(Color.Transparent)
                    ) {
                        val gameTimeVal = if (!gameTime.value.isInfinite()) gameTime.value else 0.0
                        
                        if (showFifo) {
                            Box(
                                modifier = Modifier.align(Alignment.Start)
                            ) {
                                Text(
                                    text = "${String.format("%.1f", fifo.value)}%",
                                    modifier = Modifier
                                        .background(
                                            color = Color.Black.copy(alpha = 0.26f),
                                            shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                                        )
                                )
                            }
                        }
                        
                        if (showFps) {
                            Box(
                                modifier = Modifier.align(Alignment.Start)
                            ) {
                                Text(
                                    text = "${String.format("%.1f", gameFps.value)} FPS",
                                    modifier = Modifier
                                        .background(
                                            color = Color.Black.copy(alpha = 0.26f),
                                            shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                                        )
                                )
                            }
                        }
                        
                        if (showRam) {
                            Box(
                                modifier = Modifier.align(Alignment.Start)
                            ) {
                                Text(
                                    text = "${totalMem.value}/${usedMem.value} MB",
                                    modifier = Modifier
                                        .background(
                                            color = Color.Black.copy(alpha = 0.26f),
                                            shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                                        )
                                )
                            }
                        }
                    }

                    Box(
                        modifier = Modifier
                            .align(Alignment.TopCenter)
                            .padding(top = 16.dp)
                    ) {
                        Row(
                            horizontalArrangement = Arrangement.Center,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            if (showBatteryTemperature && batteryTemperature.value > 0) {
                                Box(
                                    modifier = Modifier
                                        .background(
                                            color = Color.Black.copy(alpha = 0.26f),
                                            shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                                        )
                                        .padding(horizontal = 6.dp, vertical = 2.dp)
                                ) {
                                    Text(
                                        text = "${String.format("%.1f", batteryTemperature.value)}°C",
                                        color = when {
                                            batteryTemperature.value > 40 -> Color.Red
                                            batteryTemperature.value > 35 -> Color.Yellow
                                            else -> Color.White
                                        }
                                    )
                                }
                            }
                            
                            if (showBatteryLevel && batteryLevel.value >= 0) {
                                if (showBatteryTemperature && batteryTemperature.value > 0) {
                                    Spacer(modifier = Modifier.padding(horizontal = 4.dp))
                                }
                                Box(
                                    modifier = Modifier
                                        .background(
                                            color = Color.Black.copy(alpha = 0.26f),
                                            shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                                        )
                                        .padding(horizontal = 6.dp, vertical = 2.dp)
                                ) {
                                    Text(
                                        text = if (isCharging.value) {
                                            "${batteryLevel.value}% ⚡"
                                        } else {
                                            "${batteryLevel.value}%"
                                        },
                                        color = when {
                                            batteryLevel.value < 15 -> Color.Red
                                            batteryLevel.value < 40 -> Color.Yellow
                                            else -> Color.White
                                        }
                                    )
                                }
                            }
                        }
                    }
                }
            }

            mainViewModel.setStatStates(
                fifo, 
                gameFps, 
                gameTime, 
                usedMem, 
                totalMem, 
                batteryTemperature,
                batteryLevel,
                isCharging
            )
        }
    }
}
