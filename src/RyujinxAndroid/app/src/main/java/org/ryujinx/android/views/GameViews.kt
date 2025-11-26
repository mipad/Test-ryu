@file:OptIn(ExperimentalMaterial3Api::class)

package org.ryujinx.android.views

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.background
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
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

                    // 只在需要时渲染侧边菜单
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

                // 性能设置对话框 - 只在需要时渲染
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

                // 调整按键对话框 - 只在需要时渲染
                if (showAdjustControlsDialog.value) {
                    ControlEditViews.AdjustControlsDialog(
                        mainViewModel = mainViewModel,
                        onDismiss = { showAdjustControlsDialog.value = false }
                    )
                }

                // 退出确认对话框 - 只在需要时渲染
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

                // UI Handler - 只在没有其他对话框时渲染
                if (!showSideMenu.value && !showPerformanceSettings.value && 
                    !showAdjustControlsDialog.value && !showExitConfirmDialog.value) {
                    mainViewModel.activity.uiHandler.Compose()
                }
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

            // 使用 Surface 进行独立渲染，解决滚动卡顿问题
            Surface(
                modifier = Modifier
                    .fillMaxHeight()
                    .width(280.dp), // 稍微减小宽度
                color = Color.Transparent
            ) {
                Box(
                    modifier = Modifier
                        .fillMaxHeight()
                        .width(280.dp)
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
                            .padding(vertical = 6.dp) // 减小垂直内边距
                    ) {
                        // 游戏标题 - 减小字体和边距
                        Text(
                            text = gameTitle,
                            style = MaterialTheme.typography.titleMedium, // 使用更小的字体
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.primary,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 16.dp, vertical = 12.dp) // 减小边距
                        )

                        HorizontalDivider(
                            color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f),
                            thickness = 1.dp
                        )

                        Spacer(modifier = Modifier.height(6.dp)) // 减小间距

                        // 菜单项 - 直接显示，无动画
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 8.dp) // 减小水平内边距
                        ) {
                            // 暂停/继续游戏 - 使用 css-gg 图标
                            EnhancedSideMenuItem(
                                icon = CssGgIcons.PlayButton,
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

                            Spacer(modifier = Modifier.height(4.dp)) // 减小间距

                            // 虚拟手柄开关 - 使用游戏控制器图标
                            EnhancedSideMenuItem(
                                icon = CssGgIcons.Controller,
                                text = "Virtual Controller",
                                trailingContent = {
                                    Text(
                                        text = if (showController.value) "显示" else "隐藏",
                                        style = MaterialTheme.typography.labelSmall, // 使用更小的字体
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
                                    Text(
                                        text = if (enableVsync.value) "On" else "Off",
                                        style = MaterialTheme.typography.labelSmall, // 使用更小的字体
                                        color = if (enableVsync.value) MaterialTheme.colorScheme.primary 
                                               else MaterialTheme.colorScheme.outline
                                    )
                                },
                                onClick = {
                                    enableVsync.value = !enableVsync.value
                                    RyujinxNative.jnaInstance.graphicsRendererSetVsync(enableVsync.value)
                                }
                            )

                            // Enable Motion - 使用手机图标
                            EnhancedSideMenuItem(
                                icon = CssGgIcons.Smartphone,
                                text = "Motion Controls",
                                trailingContent = {
                                    Text(
                                        text = if (enableMotion.value) "On" else "Off",
                                        style = MaterialTheme.typography.labelSmall, // 使用更小的字体
                                        color = if (enableMotion.value) MaterialTheme.colorScheme.primary 
                                               else MaterialTheme.colorScheme.outline
                                    )
                                },
                                onClick = {
                                    enableMotion.value = !enableMotion.value
                                    val settings = QuickSettings(mainViewModel.activity)
                                    settings.enableMotion = enableMotion.value
                                    settings.save()
                                    if (enableMotion.value)
                                        mainViewModel.motionSensorManager?.register()
                                    else
                                        mainViewModel.motionSensorManager?.unregister()
                                }
                            )

                            Spacer(modifier = Modifier.height(4.dp)) // 减小间距

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

                            // 调整按键 - 使用控制器图标
                            EnhancedSideMenuItem(
                                icon = CssGgIcons.Controller,
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

                            Spacer(modifier = Modifier.height(12.dp)) // 减小间距

                            HorizontalDivider(
                                color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f),
                                thickness = 1.dp
                            )

                            Spacer(modifier = Modifier.height(6.dp)) // 减小间距

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
                    .padding(horizontal = 6.dp, vertical = 2.dp), // 减小内边距
                color = backgroundColor,
                shape = MaterialTheme.shapes.medium,
                tonalElevation = if (backgroundColor == Color.Transparent) 0.dp else 2.dp,
                onClick = onClick
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp, vertical = 10.dp), // 减小内边距
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
                                        .size(32.dp) // 减小图标尺寸
                                        .padding(end = 10.dp) // 减小图标和文字间距
                                )
                            }
                            else -> {
                                // 对于没有图标的项目，保持间距一致
                                Spacer(modifier = Modifier.size(32.dp).padding(end = 10.dp))
                            }
                        }
                        
                        Text(
                            text = text,
                            style = MaterialTheme.typography.bodyMedium, // 使用更小的字体
                            color = textColor,
                            modifier = Modifier.weight(1f)
                        )
                    }
                    
                    // 只显示自定义的尾部内容，不显示默认箭头
                    trailingContent?.invoke()
                }
            }
        }

        @Composable
        fun ExitConfirmDialog(
            mainViewModel: MainViewModel,
            onDismiss: () -> Unit
        ) {
            Dialog(
                onDismissRequest = onDismiss
            ) {
                Card(
                    modifier = Modifier
                        .width(400.dp) // 固定宽度
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

            Dialog(
                onDismissRequest = onDismiss
            ) {
                Card(
                    modifier = Modifier
                        .width(600.dp) // 固定宽度
                        .height(500.dp), // 固定高度
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
                                .padding(bottom = 20.dp)
                                .align(Alignment.CenterHorizontally)
                        )
                        
                        // 使用两列布局
                        Row(
                            modifier = Modifier
                                .weight(1f)
                                .verticalScroll(rememberScrollState())
                        ) {
                            // 左列
                            Column(
                                modifier = Modifier
                                    .weight(1f)
                                    .padding(end = 12.dp),
                                verticalArrangement = Arrangement.spacedBy(16.dp)
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
                            }
                            
                            // 右列
                            Column(
                                modifier = Modifier
                                    .weight(1f)
                                    .padding(start = 12.dp),
                                verticalArrangement = Arrangement.spacedBy(16.dp)
                            ) {
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
                        }
                        
                        HorizontalDivider(
                            color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f),
                            modifier = Modifier.padding(vertical = 16.dp)
                        )
                        
                        // 总开关和关闭按钮布局
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
                            
                            Row(
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Switch(
                                    checked = showStats.value,
                                    onCheckedChange = { 
                                        showStats.value = it
                                        saveSettings()
                                    }
                                )
                                
                                Spacer(modifier = Modifier.width(16.dp))
                                
                                Button(
                                    onClick = onDismiss,
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
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f)
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
