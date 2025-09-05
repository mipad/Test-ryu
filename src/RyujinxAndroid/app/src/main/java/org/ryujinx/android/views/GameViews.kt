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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.background
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
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
            Box(modifier = Modifier.fillMaxSize()) {
                GameStats(mainViewModel)

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
                                    }
                                }
                            }
                        }
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
        fun GameStats(mainViewModel: MainViewModel) {
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
            // GPU名称状态
            val gpuName = remember {
                mutableStateOf("")
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
                        
                        // 核心性能指标
                        Text(text = "${String.format("%.1f", fifo.value)}%")
                        
                        // 使用Box包装FPS文本，确保对齐不受背景影响
                        Box(
                            modifier = Modifier.align(Alignment.Start) // 确保左对齐
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
                        
                        // 内存使用
                        Text(text = "${usedMem.value}/${totalMem.value} MB")
                        
                        // GPU名称
                        if (gpuName.value.isNotEmpty() && gpuName.value != "Unknown GPU") {
                            Text(
                                text = gpuName.value,
                                fontSize = 8.sp,
                                color = Color.Gray
                            )
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
                            // 电池温度显示
                            if (batteryTemperature.value > 0) {
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
                            
                            // 电池电量显示
                            if (batteryLevel.value >= 0) {
                                Spacer(modifier = Modifier.padding(2.dp))
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
                                            batteryLevel.value < 20 -> Color.Red
                                            batteryLevel.value < 50 -> Color.Yellow
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
                isCharging,
                gpuName
            )
        }
    }
}
