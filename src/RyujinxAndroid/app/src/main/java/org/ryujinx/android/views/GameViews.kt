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
import compose.icons.cssggicons.*
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
                    .width(300.dp)
                    .background(
                        MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.98f),
                        MaterialTheme.shapes.large
                    )
                    .border(
                        width = 1.dp,
                        color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f),
                        shape = MaterialTheme.shapes.large
                    )
            ) {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .verticalScroll(rememberScrollState())
                        .padding(vertical = 8.dp)
                ) {
                    // 游戏标题
                    Text(
                        text = gameTitle,
                        style = MaterialTheme.typography.headlineSmall,
                        fontWeight = FontWeight.Bold,
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 20.dp, vertical = 16.dp)
                    )

                    HorizontalDivider(
                        color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f),
                        thickness = 1.dp
                    )

                    Spacer(modifier = Modifier.height(8.dp))

                    // 菜单项 - 直接显示，无动画
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 12.dp)
                    ) {
                        // 暂停/继续游戏 - 使用 css-gg 图标
                        EnhancedSideMenuItem(
                            icon = CssGgIcons::class.java.getField(if (isPaused.value) "PlayButtonO" else "PlayPauseO").get(null),
                            text = if (isPaused.value) "Continue Game" else "Pause Game",
                            backgroundColor = if (isPaused.value) MaterialTheme.colorScheme.primaryContainer 
                                            else MaterialTheme.colorScheme.secondaryContainer,
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

                        Spacer(modifier = Modifier.height(8.dp))

                        // 虚拟手柄开关 - 使用游戏手柄图标
                        EnhancedSideMenuItem(
                            icon = CssGgIcons.Gamepad,
                            text = "Virtual Controller",
                            trailingContent = {
                                Text(
                                    text = if (showController.value) "显示" else "隐藏",
                                    style = MaterialTheme.typography.labelMedium,
                                    color = if (showController.value) MaterialTheme.colorScheme.primary 
                                           else MaterialTheme.colorScheme.outline
                                )
                            },
                            onClick = {
                                showController.value = !showController.value
                                RyujinxNative.jnaInstance.inputReleaseTouchPoint()
                                mainViewModel.controller?.setVisible(showController.value)
                            }
                        )

                        // VSync 开关 - 使用同步图标
                        EnhancedSideMenuItem(
                            icon = CssGgIcons.Sync,
                            text = "Vertical Sync",
                            trailingContent = {
                                Switch(
                                    checked = enableVsync.value,
                                    onCheckedChange = {
                                        enableVsync.value = it
                                        RyujinxNative.jnaInstance.graphicsRendererSetVsync(it)
                                    },
                                    modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                )
                            },
                            onClick = { /* 开关已处理 */ }
                        )

                        // Enable Motion - 使用手机图标
                        EnhancedSideMenuItem(
                            icon = CssGgIcons.Smartphone,
                            text = "Motion Controls",
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

                        Spacer(modifier = Modifier.height(8.dp))

                        // 编辑模式 - 使用编辑图标
                        EnhancedSideMenuItem(
                            icon = CssGgIcons.Pen,
                            text = "Edit Controls Layout",
                            onClick = {
                                onDismiss()
                                isEditing.value = true
                                mainViewModel.controller?.setEditingMode(true)
                            }
                        )

                        // 调整按键 - 使用设置图标
                        EnhancedSideMenuItem(
                            icon = CssGgIcons.Settings,
                            text = "Controller Settings",
                            onClick = {
                                onDismiss()
                                showAdjustControlsDialog.value = true
                            }
                        )

                        // 性能设置 - 使用图表图标
                        EnhancedSideMenuItem(
                            icon = CssGgIcons.Chart,
                            text = "Performance Stats",
                            onClick = {
                                onDismiss()
                                showPerformanceSettings.value = true
                            }
                        )

                        Spacer(modifier = Modifier.height(16.dp))

                        HorizontalDivider(
                            color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f),
                            thickness = 1.dp
                        )

                        Spacer(modifier = Modifier.height(8.dp))

                        // 退出游戏 - 使用退出图标
                        EnhancedSideMenuItem(
                            icon = CssGgIcons.LogOut,
                            text = "Exit Game",
                            backgroundColor = MaterialTheme.colorScheme.errorContainer,
                            textColor = MaterialTheme.colorScheme.onErrorContainer,
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
        fun EnhancedSideMenuItem(
            icon: Any?,
            text: String,
            backgroundColor: Color = Color.Transparent,
            textColor: Color = MaterialTheme.colorScheme.onSurface,
            trailingContent: @Composable (() -> Unit)? = null,
            onClick: () -> Unit
        ) {
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 8.dp, vertical = 4.dp),
                color = backgroundColor,
                shape = MaterialTheme.shapes.medium,
                tonalElevation = if (backgroundColor == Color.Transparent) 0.dp else 2.dp,
                onClick = onClick
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 14.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.weight(1f)
                    ) {
                        when {
                            icon is androidx.compose.ui.graphics.vector.ImageVector -> {
                                Icon(
                                    imageVector = icon,
                                    contentDescription = null,
                                    tint = textColor,
                                    modifier = Modifier
                                        .size(20.dp)
                                        .padding(end = 12.dp)
                                )
                            }
                            else -> {
                                Spacer(modifier = Modifier.size(20.dp).padding(end = 12.dp))
                            }
                        }
                        
                        Text(
                            text = text,
                            style = MaterialTheme.typography.bodyMedium,
                            color = textColor,
                            modifier = Modifier.weight(1f)
                        )
                    }
                    
                    trailingContent?.invoke() ?: Icon(
                        imageVector = Icons.Default.ArrowForward,
                        contentDescription = null,
                        tint = textColor.copy(alpha = 0.7f),
                        modifier = Modifier.size(16.dp)
                    )
                }
            }
        }

        @Composable
        fun ExitConfirmDialog(
            mainViewModel: MainViewModel,
            onDismiss: () -> Unit
        ) {
            Dialog(onDismissRequest = onDismiss) {
                Card(
                    modifier = Modifier
                        .fillMaxWidth(0.8f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.extraLarge,
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceVariant
                    )
                ) {
                    Column(
                        modifier = Modifier.padding(24.dp)
                    ) {
                        Text(
                            text = "Exit Game?",
                            style = MaterialTheme.typography.headlineSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Spacer(modifier = Modifier.height(16.dp))
                        Text(
                            text = "Are you sure you want to exit the game? All unsaved progress will be lost.",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.8f)
                        )
                        Spacer(modifier = Modifier.height(24.dp))
                        Row(
                            horizontalArrangement = Arrangement.End,
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            TextButton(
                                onClick = onDismiss,
                                modifier = Modifier.padding(end = 12.dp),
                                colors = ButtonDefaults.textButtonColors(
                                    contentColor = MaterialTheme.colorScheme.onSurfaceVariant
                                )
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
                                },
                                colors = ButtonDefaults.buttonColors(
                                    containerColor = MaterialTheme.colorScheme.error,
                                    contentColor = MaterialTheme.colorScheme.onError
                                )
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
                Card(
                    modifier = Modifier
                        .fillMaxWidth(0.85f)
                        .fillMaxHeight(0.8f), // 限制对话框高度为屏幕的80%
                    shape = MaterialTheme.shapes.extraLarge,
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceVariant
                    )
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(24.dp)
                    ) {
                        Text(
                            text = "Performance Stats",
                            style = MaterialTheme.typography.headlineSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier
                                .padding(bottom = 16.dp)
                                .align(Alignment.CenterHorizontally)
                        )
                        
                        // 可滚动的选项列表
                        Column(
                            modifier = Modifier
                                .weight(1f) // 占据剩余空间
                                .verticalScroll(rememberScrollState()),
                            verticalArrangement = Arrangement.spacedBy(12.dp)
                        ) {
                            StatSwitchItem(
                                text = "FIFO Percentage",
                                checked = showFifo.value,
                                onCheckedChange = { 
                                    showFifo.value = it
                                    saveSettings()
                                }
                            )
                            
                            StatSwitchItem(
                                text = "FPS Counter",
                                checked = showFps.value,
                                onCheckedChange = { 
                                    showFps.value = it
                                    saveSettings()
                                }
                            )
                            
                            StatSwitchItem(
                                text = "Memory Usage",
                                checked = showRam.value,
                                onCheckedChange = { 
                                    showRam.value = it
                                    saveSettings()
                                }
                            )
                            
                            StatSwitchItem(
                                text = "Battery Temperature",
                                checked = showBatteryTemperature.value,
                                onCheckedChange = { 
                                    showBatteryTemperature.value = it
                                    saveSettings()
                                }
                            )
                            
                            StatSwitchItem(
                                text = "Battery Level",
                                checked = showBatteryLevel.value,
                                onCheckedChange = { 
                                    showBatteryLevel.value = it
                                    saveSettings()
                                }
                            )
                        }
                        
                        Spacer(modifier = Modifier.height(20.dp))
                        
                        HorizontalDivider(
                            color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)
                        )
                        
                        Spacer(modifier = Modifier.height(16.dp))
                        
                        // 总开关
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(
                                text = "Show All Stats",
                                style = MaterialTheme.typography.bodyLarge,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Switch(
                                checked = showStats.value,
                                onCheckedChange = { 
                                    showStats.value = it
                                    saveSettings()
                                }
                            )
                        }
                        
                        Spacer(modifier = Modifier.height(24.dp))
                        
                        Button(
                            onClick = onDismiss,
                            modifier = Modifier.align(Alignment.End),
                            colors = ButtonDefaults.buttonColors(
                                containerColor = MaterialTheme.colorScheme.primary,
                                contentColor = MaterialTheme.colorScheme.onPrimary
                            )
                        ) {
                            Text(text = "Close")
                        }
                    }
                }
            }
        }

        @Composable
        fun StatSwitchItem(
            text: String,
            checked: Boolean,
            onCheckedChange: (Boolean) -> Unit
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = text,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Switch(
                    checked = checked,
                    onCheckedChange = onCheckedChange,
                    modifier = Modifier.size(width = 36.dp, height = 24.dp)
                )
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
                    fontSize = 11.sp,
                    color = Color.White,
                    fontWeight = FontWeight.Medium
                )
            ) {
                Box(modifier = Modifier.fillMaxSize()) {
                    Column(
                        modifier = Modifier
                            .align(Alignment.TopStart)
                            .padding(16.dp)
                    ) {
                        val gameTimeVal = if (!gameTime.value.isInfinite()) gameTime.value else 0.0
                        
                        if (showFifo) {
                            StatItem(
                                text = "${String.format("%.1f", fifo.value)}%",
                                backgroundColor = Color.Black.copy(alpha = 0.4f)
                            )
                        }
                        
                        if (showFps) {
                            StatItem(
                                text = "${String.format("%.1f", gameFps.value)} FPS",
                                backgroundColor = Color.Black.copy(alpha = 0.4f)
                            )
                        }
                        
                        if (showRam) {
                            StatItem(
                                text = "${usedMem.value}/${totalMem.value} MB",
                                backgroundColor = Color.Black.copy(alpha = 0.4f)
                            )
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
                                StatItem(
                                    text = "${String.format("%.1f", batteryTemperature.value)}°C",
                                    backgroundColor = Color.Black.copy(alpha = 0.4f),
                                    textColor = when {
                                        batteryTemperature.value > 40 -> Color(0xFFFF6B6B)
                                        batteryTemperature.value > 35 -> Color(0xFFFFD166)
                                        else -> Color.White
                                    }
                                )
                            }
                            
                            if (showBatteryLevel && batteryLevel.value >= 0) {
                                if (showBatteryTemperature && batteryTemperature.value > 0) {
                                    Spacer(modifier = Modifier.padding(horizontal = 4.dp))
                                }
                                StatItem(
                                    text = if (isCharging.value) {
                                        "${batteryLevel.value}% ⚡"
                                    } else {
                                        "${batteryLevel.value}%"
                                    },
                                    backgroundColor = Color.Black.copy(alpha = 0.4f),
                                    textColor = when {
                                        batteryLevel.value < 15 -> Color(0xFFFF6B6B)
                                        batteryLevel.value < 40 -> Color(0xFFFFD166)
                                        else -> Color.White
                                    }
                                )
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

        @Composable
        fun StatItem(
            text: String,
            backgroundColor: Color,
            textColor: Color = Color.White
        ) {
            Box(
                modifier = Modifier
                    .background(
                        color = backgroundColor,
                        shape = androidx.compose.foundation.shape.RoundedCornerShape(6.dp)
                    )
                    .padding(horizontal = 8.dp, vertical = 4.dp)
            ) {
                Text(
                    text = text,
                    color = textColor
                )
            }
        }
    }
}
