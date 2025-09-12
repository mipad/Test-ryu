@file:OptIn(ExperimentalMaterial3Api::class)

package org.ryujinx.android.views

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.background
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Divider
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
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
                val showFpsScalingDialog = remember { mutableStateOf(false) }
                val fpsScalingFactor = remember { mutableStateOf(1.0f) }

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
                                if (showController.value)
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
                                        // FPS缩放图标（使用文字代替）
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            showFpsScalingDialog.value = true
                                        }) {
                                            Text(
                                                text = "FPS",
                                                color = Color.White,
                                                modifier = Modifier
                                                    .background(
                                                        color = Color.Black.copy(alpha = 0.5f),
                                                        shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                                                    )
                                                    .padding(horizontal = 8.dp, vertical = 4.dp)
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

                    // FPS缩放对话框
                    if (showFpsScalingDialog.value) {
                        FpsScalingDialog(
                            currentFactor = fpsScalingFactor.value,
                            onFactorChanged = { newFactor ->
                                fpsScalingFactor.value = newFactor
                                RyujinxNative.setFpsScalingFactor(newFactor.toDouble())
                                // 强制更新SurfaceFlinger的目标帧率
                                RyujinxNative.updateSurfaceFlingerTargetFps()
                            },
                            onDismiss = { showFpsScalingDialog.value = false }
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
        fun FpsScalingDialog(
            currentFactor: Float,
            onFactorChanged: (Float) -> Unit,
            onDismiss: () -> Unit
        ) {
            val sliderPosition = remember { mutableStateOf(currentFactor * 100) } // 转换为百分比
            
            BasicAlertDialog(onDismissRequest = onDismiss) {
                // 透明背景的对话框
                Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    // 半透明背景
                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .background(Color.Black.copy(alpha = 0.3f))
                    )
                    
                    // 对话框内容
                    Surface(
                        modifier = Modifier
                            .fillMaxWidth(0.8f)
                            .wrapContentHeight(),
                        shape = MaterialTheme.shapes.large,
                        tonalElevation = AlertDialogDefaults.TonalElevation,
                        color = Color.Transparent // 完全透明
                    ) {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .background(
                                    color = Color.Black.copy(alpha = 0.8f), // 半透明黑色背景
                                    shape = MaterialTheme.shapes.large
                                )
                                .padding(16.dp)
                        ) {
                            Text(
                                text = "FPS Scaling",
                                style = MaterialTheme.typography.headlineSmall,
                                color = Color.White,
                                modifier = Modifier
                                    .padding(bottom = 10.dp)
                                    .align(Alignment.CenterHorizontally)
                            )
                            
                            Text(
                                text = "${sliderPosition.value.roundToInt()}%",
                                style = MaterialTheme.typography.headlineMedium,
                                color = Color.White,
                                modifier = Modifier
                                    .padding(vertical = 16.dp)
                                    .align(Alignment.CenterHorizontally)
                            )
                            
                            androidx.compose.material3.Slider(
                                value = sliderPosition.value,
                                onValueChange = {
                                    sliderPosition.value = it
                                    onFactorChanged(it / 100f)
                                },
                                valueRange = 50f..400f,
                                steps = 350, // 350 steps for 50-400 range
                                colors = androidx.compose.material3.SliderDefaults.colors(
                                    thumbColor = MaterialTheme.colorScheme.primary,
                                    activeTrackColor = MaterialTheme.colorScheme.primary,
                                    inactiveTrackColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.3f)
                                ),
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(horizontal = 16.dp)
                            )
                            
                            Row(
                                horizontalArrangement = Arrangement.SpaceBetween,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(top = 8.dp)
                            ) {
                                Text("50%", style = MaterialTheme.typography.bodySmall.copy(color = Color.White))
                                Text("100%", style = MaterialTheme.typography.bodySmall.copy(color = Color.White))
                                Text("200%", style = MaterialTheme.typography.bodySmall.copy(color = Color.White))
                                Text("300%", style = MaterialTheme.typography.bodySmall.copy(color = Color.White))
                                Text("400%", style = MaterialTheme.typography.bodySmall.copy(color = Color.White))
                            }
                            
                            Spacer(modifier = Modifier.padding(8.dp))
                            
                            Row(
                                horizontalArrangement = Arrangement.End,
                                modifier = Modifier.fillMaxWidth()
                            ) {
                                Button(
                                    onClick = onDismiss,
                                    colors = androidx.compose.material3.ButtonDefaults.buttonColors(
                                        containerColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.8f)
                                    )
                                ) {
                                    Text(text = "Close", color = Color.White)
                                }
                            }
                        }
                    }
                }
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
