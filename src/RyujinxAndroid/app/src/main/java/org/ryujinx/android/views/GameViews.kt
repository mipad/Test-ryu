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
    // 添加CPU温度状态
    val cpuTemperature = remember {
        mutableDoubleStateOf(0.0)
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
            }

            // 右上角的温度显示
            Box(
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .padding(16.dp)
            ) {
                if (cpuTemperature.value > 0) {
                    Box(
                        modifier = Modifier
                            .background(
                                color = Color.Black.copy(alpha = 0.26f),
                                shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                            )
                            .padding(horizontal = 6.dp, vertical = 2.dp)
                    ) {
                        Text(
                            text = "${String.format("%.1f", cpuTemperature.value)}°C",
                            color = when {
                                cpuTemperature.value > 70 -> Color.Red
                                cpuTemperature.value > 60 -> Color.Yellow
                                else -> Color.White
                            }
                        )
                    }
                } else {
                    // 如果没有温度数据，显示一个占位符或调试信息
                    Box(
                        modifier = Modifier
                            .background(
                                color = Color.Black.copy(alpha = 0.26f),
                                shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)
                            )
                            .padding(horizontal = 6.dp, vertical = 2.dp)
                    ) {
                        Text(
                            text = "N/A°C",
                            color = Color.Gray
                        )
                    }
                }
            }
        }
    }

    mainViewModel.setStatStates(fifo, gameFps, gameTime, usedMem, totalMem, cpuTemperature)
}
