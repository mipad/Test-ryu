@file:OptIn(ExperimentalMaterial3Api::class)

package org.ryujinx.android.views

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.background
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
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Popup
import compose.icons.CssGgIcons
import compose.icons.cssggicons.ToolbarBottom
import org.ryujinx.android.GameController
import org.ryujinx.android.GameHost
import org.ryujinx.android.Icons
import org.ryujinx.android.MainActivity
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.viewmodels.MainViewModel
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

        @OptIn(ExperimentalMaterial3Api::class)
        @Composable
        fun GameOverlay(mainViewModel: MainViewModel) {
            // 全局显示/隐藏状态
            val showStats = remember {
                mutableStateOf(true)
            }
            
            // 各个统计项的独立显示状态
            val showFps = remember { mutableStateOf(true) }
            val showRam = remember { mutableStateOf(true) }
            val showBatteryTemperature = remember { mutableStateOf(false) }
            val showBatteryLevel = remember { mutableStateOf(false) }
            val showFifo = remember { mutableStateOf(true) } // 添加FIFO显示状态

            // 编辑模式状态
            val isEditing = remember { mutableStateOf(false) }

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
                val showMore = remember {
                    mutableStateOf(false)
                }
                val showPerformanceSettings = remember {
                    mutableStateOf(false)
                }
                val showAdjustControlsDialog = remember {
                    mutableStateOf(false)
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
                if (!showLoading.value) {
                    GameController.Compose(mainViewModel)

                    Row(
                        modifier = Modifier
                            .align(Alignment.BottomCenter)
                            .padding(8.dp)
                    ) {
                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                            showMore.value = true
                        }) {
                            Icon(
                                imageVector = CssGgIcons.ToolbarBottom,
                                contentDescription = "Open Panel"
                            )
                        }
                    }

                    if (showMore.value) {
                        Popup(
                            alignment = Alignment.BottomCenter,
                            onDismissRequest = { showMore.value = false }) {
                            Surface(
                                modifier = Modifier.padding(16.dp),
                                shape = MaterialTheme.shapes.medium
                            ) {
                                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                    Row(
                                        modifier = Modifier.padding(horizontal = 16.dp),
                                        horizontalArrangement = Arrangement.SpaceBetween,
                                        verticalAlignment = Alignment.CenterVertically
                                    ) {
                                        Text(
                                            text = "Enable Motion",
                                            modifier = Modifier
                                                .align(Alignment.CenterVertically)
                                                .padding(end = 16.dp)
                                        )
                                        Switch(checked = enableMotion.value, onCheckedChange = {
                                            showMore.value = false
                                            enableMotion.value = !enableMotion.value
                                            val settings = QuickSettings(mainViewModel.activity)
                                            settings.enableMotion = enableMotion.value
                                            settings.save()
                                            if (enableMotion.value)
                                                mainViewModel.motionSensorManager?.register()
                                            else
                                                mainViewModel.motionSensorManager?.unregister()
                                        })
                                    }
                                    Row(
                                        modifier = Modifier.padding(8.dp),
                                        horizontalArrangement = Arrangement.SpaceBetween
                                    ) {
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            showController.value = !showController.value
                                            RyujinxNative.jnaInstance.inputReleaseTouchPoint()
                                            mainViewModel.controller?.setVisible(showController.value)
                                        }) {
                                            Icon(
                                                imageVector = Icons.videoGame(),
                                                contentDescription = "Toggle Virtual Pad"
                                            )
                                        }
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            enableVsync.value = !enableVsync.value
                                            RyujinxNative.jnaInstance.graphicsRendererSetVsync(
                                                enableVsync.value
                                            )
                                        }) {
                                            Icon(
                                                imageVector = Icons.vSync(),
                                                tint = if (enableVsync.value) Color.Green else Color.Red,
                                                contentDescription = "Toggle VSync"
                                            )
                                        }
                                        // 编辑按钮 - 使用文本表情符号
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            isEditing.value = true
                                            mainViewModel.controller?.setEditingMode(true)
                                        }) {
                                            Text(
                                                text = "✏️", // 编辑表情符号
                                                fontSize = 20.sp
                                            )
                                        }
                                        // 性能设置图标
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            showPerformanceSettings.value = true
                                        }) {
                                            Icon(
                                                imageVector = Icons.stats(),
                                                tint = if (showStats.value) Color.Green else Color.Red,
                                                contentDescription = "Performance Settings"
                                            )
                                        }
                                        // 调整按键图标
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            showAdjustControlsDialog.value = true
                                        }) {
                                            Text(
                                                text = "🎮", // 游戏手柄表情符号
                                                fontSize = 20.sp
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 性能设置对话框
                    if (showPerformanceSettings.value) {
                        PerformanceSettingsDialog(
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
                        AdjustControlsDialog(
                            mainViewModel = mainViewModel,
                            onDismiss = { showAdjustControlsDialog.value = false }
                        )
                    }
                }

                val showBackNotice = remember {
                    mutableStateOf(false)
                }

                BackHandler {
                    showBackNotice.value = true
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

                if (showBackNotice.value) {
                    BasicAlertDialog(onDismissRequest = { showBackNotice.value = false }) {
                        Column {
                            Surface(
                                modifier = Modifier
                                    .wrapContentWidth()
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
                                        Text(text = "Are you sure you want to exit the game?")
                                        Text(text = "All unsaved data will be lost!")
                                    }
                                    Row(
                                        horizontalArrangement = Arrangement.End,
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(16.dp)
                                    ) {
                                        Button(onClick = {
                                            showBackNotice.value = false
                                            mainViewModel.closeGame()
                                            mainViewModel.activity.setFullScreen(false)
                                            mainViewModel.navController?.popBackStack()
                                            mainViewModel.activity.isGameRunning = false
                                        }, modifier = Modifier.padding(16.dp)) {
                                            Text(text = "Exit Game")
                                        }

                                        Button(onClick = {
                                            showBackNotice.value = false
                                        }, modifier = Modifier.padding(16.dp)) {
                                            Text(text = "Dismiss")
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                mainViewModel.activity.uiHandler.Compose()
            }
        }

        @Composable
        fun PerformanceSettingsDialog(
            showStats: androidx.compose.runtime.MutableState<Boolean>,
            showFps: androidx.compose.runtime.MutableState<Boolean>,
            showRam: androidx.compose.runtime.MutableState<Boolean>,
            showBatteryTemperature: androidx.compose.runtime.MutableState<Boolean>,
            showBatteryLevel: androidx.compose.runtime.MutableState<Boolean>,
            showFifo: androidx.compose.runtime.MutableState<Boolean>,
            onDismiss: () -> Unit
        ) {
            BasicAlertDialog(onDismissRequest = onDismiss) {
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
                        
                        // 两列布局
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            // 左列
                            Column(
                                modifier = Modifier.weight(1f),
                                verticalArrangement = Arrangement.spacedBy(8.dp)
                            ) {
                                // FIFO显示开关
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "FIFO")
                                    Switch(
                                        checked = showFifo.value,
                                        onCheckedChange = { showFifo.value = it },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                                
                                // FPS显示开关
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "FPS")
                                    Switch(
                                        checked = showFps.value,
                                        onCheckedChange = { showFps.value = it },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                                
                                // 内存显示开关
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "RAM")
                                    Switch(
                                        checked = showRam.value,
                                        onCheckedChange = { showRam.value = it },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                            }
                            
                            // 右列
                            Column(
                                modifier = Modifier.weight(1f),
                                verticalArrangement = Arrangement.spacedBy(8.dp)
                            ) {
                                // 电池温度显示开关
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "Battery Temp")
                                    Switch(
                                        checked = showBatteryTemperature.value,
                                        onCheckedChange = { showBatteryTemperature.value = it },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                                
                                // 电池电量显示开关
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "Level")
                                    Switch(
                                        checked = showBatteryLevel.value,
                                        onCheckedChange = { showBatteryLevel.value = it },
                                        modifier = Modifier.size(width = 36.dp, height = 24.dp)
                                    )
                                }
                            }
                        }
                        
                        // 分隔线
                        HorizontalDivider(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 16.dp),
                            thickness = 1.dp,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.1f)
                        )
                        
                        // 全局显示/隐藏开关（单独一行）
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
                                onCheckedChange = { showStats.value = it },
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
        fun AdjustControlsDialog(
            mainViewModel: MainViewModel,
            onDismiss: () -> Unit
        ) {
            val selectedControl = remember { mutableStateOf<ControlItem?>(null) }

            // 如果选择了具体控件，显示控件调整对话框
            if (selectedControl.value != null) {
                ControlAdjustmentDialog(
                    control = selectedControl.value!!,
                    onDismiss = { selectedControl.value = null },
                    mainViewModel = mainViewModel
                )
                return
            }

            // 主调整对话框
            BasicAlertDialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .fillMaxWidth(0.9f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        // 标题行和按钮行 - 修改为按钮在标题左右
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            // 左侧：全部重置按钮
                            TextButton(
                                onClick = {
                                    // 重置所有单独设置
                                    getControlItems().forEach { control ->
                                        when (control.type) {
                                            ControlType.BUTTON -> {
                                                mainViewModel.controller?.setControlScale(control.id, 50)
                                                mainViewModel.controller?.setControlOpacity(control.id, 100)
                                                mainViewModel.controller?.setControlEnabled(control.id, true)
                                            }
                                            ControlType.JOYSTICK -> {
                                                mainViewModel.controller?.setControlScale(control.id, 50)
                                                mainViewModel.controller?.setControlOpacity(control.id, 100)
                                                mainViewModel.controller?.setControlEnabled(control.id, true)
                                            }
                                            ControlType.DPAD -> {
                                                mainViewModel.controller?.setControlScale(control.id, 50)
                                                mainViewModel.controller?.setControlOpacity(control.id, 100)
                                                mainViewModel.controller?.setControlEnabled(control.id, true)
                                            }
                                        }
                                    }
                                    // 立即刷新控件
                                    mainViewModel.controller?.refreshControls()
                                },
                                colors = ButtonDefaults.textButtonColors(
                                    contentColor = MaterialTheme.colorScheme.secondary
                                )
                            ) {
                                Text(text = "全部重置")
                            }

                            // 中间：标题
                            Text(
                                text = "调整按键设置",
                                style = MaterialTheme.typography.headlineSmall
                            )

                            // 右侧：确定按钮
                            TextButton(
                                onClick = {
                                    // 在关闭对话框时应用所有更改
                                    mainViewModel.controller?.refreshControls()
                                    onDismiss()
                                }
                            ) {
                                Text(text = "确定")
                            }
                        }

                        Spacer(modifier = Modifier.height(16.dp))

                        Text(
                            text = "单个按键设置",
                            style = MaterialTheme.typography.titleMedium,
                            modifier = Modifier.padding(vertical = 8.dp)
                        )

                        // 按键列表 - 增加高度以充分利用空间
                        LazyColumn(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(400.dp) // 增加高度
                        ) {
                            itemsIndexed(getControlItems()) { index, control ->
                                ControlListItem(
                                    control = control,
                                    mainViewModel = mainViewModel,
                                    onClick = { selectedControl.value = control }
                                )
                                if (index < getControlItems().size - 1) {
                                    HorizontalDivider(
                                        modifier = Modifier.padding(horizontal = 8.dp),
                                        thickness = 0.5.dp,
                                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.1f)
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }

        @Composable
        fun ControlListItem(
            control: ControlItem,
            mainViewModel: MainViewModel,
            onClick: () -> Unit
        ) {
            val scale = remember { 
                mutableStateOf(mainViewModel.controller?.getControlScale(control.id) ?: 50)
            }
            val opacity = remember { 
                mutableStateOf(mainViewModel.controller?.getControlOpacity(control.id) ?: 100)
            }
            val enabled = remember { 
                mutableStateOf(mainViewModel.controller?.isControlEnabled(control.id) ?: true)
            }

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
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = control.emoji,
                            fontSize = 20.sp,
                            modifier = Modifier.padding(end = 12.dp)
                        )
                        Column {
                            Text(
                                text = control.name,
                                style = MaterialTheme.typography.bodyMedium
                            )
                            Text(
                                text = control.description,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                            )
                        }
                    }
                    
                    // 显示当前设置状态
                    Column(
                        horizontalAlignment = Alignment.End
                    ) {
                        Text(
                            text = if (enabled.value) "显示中" else "已隐藏",
                            color = if (enabled.value) Color.Green else Color.Red,
                            fontSize = 12.sp
                        )
                        Text(
                            text = "大小:${scale.value}% 透明:${opacity.value}%",
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                            fontSize = 10.sp
                        )
                    }
                }
            }
        }

        @Composable
        fun ControlAdjustmentDialog(
            control: ControlItem,
            onDismiss: () -> Unit,
            mainViewModel: MainViewModel
        ) {
            val scale = remember { 
                mutableStateOf(mainViewModel.controller?.getControlScale(control.id) ?: 50)
            }
            val opacity = remember { 
                mutableStateOf(mainViewModel.controller?.getControlOpacity(control.id) ?: 100)
            }
            val enabled = remember { 
                mutableStateOf(mainViewModel.controller?.isControlEnabled(control.id) ?: true)
            }

            BasicAlertDialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .fillMaxWidth(0.8f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        // 标题行和按钮行 - 同样修改为按钮在标题左右
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            // 左侧：重置按钮
                            TextButton(
                                onClick = {
                                    scale.value = 50
                                    opacity.value = 100
                                    enabled.value = true
                                    mainViewModel.controller?.setControlScale(control.id, 50)
                                    mainViewModel.controller?.setControlOpacity(control.id, 100)
                                    mainViewModel.controller?.setControlEnabled(control.id, true)
                                    // 不立即刷新，避免位置偏移
                                },
                                colors = ButtonDefaults.textButtonColors(
                                    contentColor = MaterialTheme.colorScheme.secondary
                                )
                            ) {
                                Text(text = "重置")
                            }

                            // 中间：标题
                            Row(
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(
                                    text = control.emoji,
                                    fontSize = 24.sp,
                                    modifier = Modifier.padding(end = 12.dp)
                                )
                                Column {
                                    Text(
                                        text = control.name,
                                        style = MaterialTheme.typography.titleMedium
                                    )
                                    Text(
                                        text = control.description,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                                    )
                                }
                            }

                            // 右侧：确定按钮
                            TextButton(
                                onClick = {
                                    // 在返回时刷新一次
                                    mainViewModel.controller?.refreshControls()
                                    onDismiss()
                                }
                            ) {
                                Text(text = "确定")
                            }
                        }

                        Spacer(modifier = Modifier.height(16.dp))

                        // 单独缩放
                        Text(
                            text = "按键大小",
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(bottom = 8.dp)
                        )
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(text = "大小")
                            Text(text = "${scale.value}%")
                        }
                        Slider(
                            value = scale.value.toFloat(),
                            onValueChange = { 
                                scale.value = it.toInt()
                                mainViewModel.controller?.setControlScale(control.id, scale.value)
                                // 不立即刷新，避免位置偏移
                            },
                            valueRange = 10f..200f,
                            modifier = Modifier.fillMaxWidth()
                        )

                        Spacer(modifier = Modifier.height(16.dp))

                        // 单独透明度
                        Text(
                            text = "按键透明度",
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(bottom = 8.dp)
                        )
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(text = "透明度")
                            Text(text = "${opacity.value}%")
                        }
                        Slider(
                            value = opacity.value.toFloat(),
                            onValueChange = { 
                                opacity.value = it.toInt()
                                mainViewModel.controller?.setControlOpacity(control.id, opacity.value)
                                // 不立即刷新，避免位置偏移
                            },
                            valueRange = 0f..100f,
                            modifier = Modifier.fillMaxWidth()
                        )

                        Spacer(modifier = Modifier.height(16.dp))

                        // 隐藏显示开关
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(text = "显示按键")
                            Switch(
                                checked = enabled.value,
                                onCheckedChange = { 
                                    enabled.value = it
                                    mainViewModel.controller?.setControlEnabled(control.id, enabled.value)
                                    // 不立即刷新，避免位置偏移
                                }
                            )
                        }
                    }
                }
            }
        }

        // 控件数据类
        data class ControlItem(
            val id: Int,
            val name: String,
            val description: String,
            val emoji: String,
            val type: ControlType
        )

        enum class ControlType {
            BUTTON, JOYSTICK, DPAD
        }

        // 获取所有控件列表
        fun getControlItems(): List<ControlItem> {
            return listOf(
                // 按钮
                ControlItem(1, "A 按钮", "确认/主要动作", "🅰️", ControlType.BUTTON),
                ControlItem(2, "B 按钮", "取消/次要动作", "🅱️", ControlType.BUTTON),
                ControlItem(3, "X 按钮", "特殊功能", "❎", ControlType.BUTTON),
                ControlItem(4, "Y 按钮", "特殊功能", "💠", ControlType.BUTTON),
                ControlItem(5, "L 肩键", "左肩部按键", "🔗", ControlType.BUTTON),
                ControlItem(6, "R 肩键", "右肩部按键", "🔗", ControlType.BUTTON),
                ControlItem(7, "ZL 扳机", "左扳机键", "🎯", ControlType.BUTTON),
                ControlItem(8, "ZR 扳机", "右扳机键", "🎯", ControlType.BUTTON),
                ControlItem(9, "+ 按钮", "开始/菜单", "➕", ControlType.BUTTON),
                ControlItem(10, "- 按钮", "选择/返回", "➖", ControlType.BUTTON),
                ControlItem(11, "L3 按钮", "左摇杆按下", "🎮", ControlType.BUTTON),
                ControlItem(12, "R3 按钮", "右摇杆按下", "🎮", ControlType.BUTTON),
                
                // 摇杆
                ControlItem(101, "左摇杆", "移动/方向控制", "🕹️", ControlType.JOYSTICK),
                ControlItem(102, "右摇杆", "视角/镜头控制", "🕹️", ControlType.JOYSTICK),
                
                // 方向键
                ControlItem(201, "方向键", "方向选择", "✛", ControlType.DPAD)
            )
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
            // 电池温度状态
            val batteryTemperature = remember {
                mutableDoubleStateOf(0.0)
            }
            // 电池电量状态
            val batteryLevel = remember {
                mutableIntStateOf(-1)
            }
            // 充电状态
            val isCharging = remember {
                mutableStateOf(false)
            }

            // 完全透明的文字面板
            CompositionLocalProvider(
                LocalTextStyle provides TextStyle(
                    fontSize = 10.sp,
                    color = Color.White // 确保文字在游戏画面上可见
                )
            ) {
                Box(modifier = Modifier.fillMaxSize()) {
                    // 左上角的性能指标
                    Column(
                        modifier = Modifier
                            .align(Alignment.TopStart)
                            .padding(16.dp)
                            .background(Color.Transparent) // 完全透明背景
                    ) {
                        val gameTimeVal = if (!gameTime.value.isInfinite()) gameTime.value else 0.0
                        
                        // FIFO显示（根据设置决定是否显示）
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
                                        //.padding(horizontal = 4.dp, vertical = 2.dp)
                                )
                            }
                        }
                        
                        // FPS显示（根据设置决定是否显示）
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
                                        //.padding(horizontal = 4.dp, vertical = 2.dp)
                                )
                            }
                        }
                        
                        // 内存使用（根据设置决定是否显示）
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
                                        //.padding(horizontal = 4.dp, vertical = 2.dp)
                                )
                            }
                        }
                    }

                    // 右上角的电池信息显示
                    Box(
                        modifier = Modifier
                            .align(Alignment.TopEnd)
                            .padding(16.dp)
                    ) {
                        Column(
                            horizontalAlignment = Alignment.End
                        ) {
                            // 电池温度显示（根据设置决定是否显示）
                            if (showBatteryTemperature && batteryTemperature.value > 0) {
                                Box(
                                    modifier = Modifier
                                        .background(
                                            color = Color.Black.copy(alpha = 0.26f),
                                            shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                                        )
                                        //.padding(horizontal = 6.dp, vertical = 2.dp)
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
                            
                            // 电池电量显示（根据设置决定是否显示）
                            if (showBatteryLevel && batteryLevel.value >= 0) {
                                Spacer(modifier = Modifier.padding(2.dp))
                                Box(
                                    modifier = Modifier
                                        .background(
                                            color = Color.Black.copy(alpha = 0.26f),
                                            shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                                        )
                                        //.padding(horizontal = 6.dp, vertical = 2.dp)
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
