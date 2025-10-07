package org.ryujinx.android.views

import android.net.Uri
import androidx.compose.ui.unit.dp
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.NavHostController
import kotlinx.coroutines.delay
import org.ryujinx.android.viewmodels.SaveDataViewModel
import org.ryujinx.android.viewmodels.formatDate

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SaveDataViews(navController: NavHostController, titleId: String, gameName: String) {
    val context = LocalContext.current
    val viewModel: SaveDataViewModel = viewModel()
    
    // 文件选择器 - 用于导入
    val importFilePickerLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.GetContent()
    ) { uri ->
        uri?.let { selectedUri ->
            viewModel.importSaveDataFromUri(selectedUri, context)
        }
    }
    
    // 文件创建器 - 用于导出
    val exportFileLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.CreateDocument("application/zip")
    ) { uri ->
        uri?.let { destinationUri ->
            viewModel.exportSaveDataToUri(destinationUri, context)
        }
    }
    
    // 删除确认对话框状态
    var showDeleteFilesConfirmation by remember { mutableStateOf(false) }
    var showDeleteFolderConfirmation by remember { mutableStateOf(false) }
    
    // 初始化ViewModel
    LaunchedEffect(titleId, gameName) {
        viewModel.initialize(titleId, gameName, context)
    }
    
    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text("Save Data Management") },
                navigationIcon = {
                    IconButton(onClick = { navController.popBackStack() }) {
                        Icon(Icons.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    IconButton(
                        onClick = { viewModel.refreshSaveData() },
                        enabled = !viewModel.operationInProgress.value
                    ) {
                        Icon(Icons.Filled.Refresh, contentDescription = "Refresh")
                    }
                }
            )
        }
    ) { contentPadding ->
        Column(
            modifier = Modifier
                .padding(contentPadding)
                .fillMaxSize()
                .padding(16.dp)
        ) {
            // 游戏信息
            Card(
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(
                    modifier = Modifier.padding(16.dp)
                ) {
                    // 使用可滚动的游戏名称
                    ScrollableGameName(
                        gameName = gameName,
                        modifier = Modifier.fillMaxWidth()
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        text = "Title ID: $titleId",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(16.dp))
            
            // 存档信息
            if (viewModel.saveDataInfo.value != null) {
                Card(
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Column(
                        modifier = Modifier.padding(16.dp)
                    ) {
                        Text(
                            text = "Save Data Info",
                            style = MaterialTheme.typography.titleMedium
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        InfoRow("Save ID:", viewModel.saveDataInfo.value!!.saveId)
                        InfoRow("Last Modified:", formatDate(viewModel.saveDataInfo.value!!.lastModified))
                    }
                }
            } else {
                Card(
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Column(
                        modifier = Modifier.padding(16.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Text(
                            "No save data found for this game",
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        Text(
                            "Start the game to create save data",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.outline
                        )
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(24.dp))
            
            // 操作按钮
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                ActionButton(
                    text = "Export Save Data",
                    enabled = viewModel.hasSaveData() && !viewModel.operationInProgress.value
                ) {
                    // 使用URI导出，让用户选择保存位置
                    val fileName = "${gameName.replace("[^a-zA-Z0-9]".toRegex(), "_")}_save_${System.currentTimeMillis()}.zip"
                    exportFileLauncher.launch(fileName)
                }
                
                ActionButton(
                    text = "Import Save Data", 
                    enabled = !viewModel.operationInProgress.value
                ) {
                    // 打开文件选择器选择ZIP文件
                    importFilePickerLauncher.launch("application/zip")
                }
                
                // 两个独立的删除选项
                ActionButton(
                    text = "Delete Save Files",
                    enabled = viewModel.hasSaveData() && !viewModel.operationInProgress.value,
                    isDestructive = true
                ) {
                    // 显示删除存档文件确认对话框
                    showDeleteFilesConfirmation = true
                }
                
                ActionButton(
                    text = "Delete Save Folder",
                    enabled = viewModel.hasSaveData() && !viewModel.operationInProgress.value,
                    isDestructive = true
                ) {
                    // 显示删除存档文件夹确认对话框
                    showDeleteFolderConfirmation = true
                }
            }
        }
    }
    
    // 操作进度对话框
    if (viewModel.operationInProgress.value) {
        AlertDialog(
            onDismissRequest = { /* 不允许取消 */ },
            title = { Text("Processing") },
            text = { 
                Column {
                    Text(viewModel.operationMessage.value)
                    Spacer(modifier = Modifier.height(16.dp))
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                }
            },
            confirmButton = {}
        )
    }
    
    // 操作结果对话框
    if (viewModel.showOperationResult.value) {
        AlertDialog(
            onDismissRequest = { viewModel.resetOperationResult() },
            title = { 
                Text(if (viewModel.operationSuccess.value) "Success" else "Error") 
            },
            text = { Text(viewModel.operationMessage.value) },
            confirmButton = {
                TextButton(onClick = { 
                    viewModel.resetOperationResult()
                }) {
                    Text("OK")
                }
            }
        )
    }
    
    // 删除存档文件确认对话框
    if (showDeleteFilesConfirmation) {
        AlertDialog(
            onDismissRequest = { showDeleteFilesConfirmation = false },
            title = { Text("Confirm Delete Save Files") },
            text = { 
                Column {
                    Text("Are you sure you want to delete the save files for this game?")
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        "This will delete only the save files inside the 0 and 1 folders of the current save ID. The game will be able to continue using the same save folder.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        showDeleteFilesConfirmation = false
                        // 调用删除存档文件的方法
                        viewModel.deleteSaveFiles()
                    },
                    colors = ButtonDefaults.textButtonColors(
                        contentColor = MaterialTheme.colorScheme.error
                    )
                ) {
                    Text("Delete Files")
                }
            },
            dismissButton = {
                TextButton(onClick = { showDeleteFilesConfirmation = false }) {
                    Text("Cancel")
                }
            }
        )
    }
    
    // 删除存档文件夹确认对话框
    if (showDeleteFolderConfirmation) {
        AlertDialog(
            onDismissRequest = { showDeleteFolderConfirmation = false },
            title = { Text("Confirm Delete Save Folder") },
            text = { 
                Column {
                    Text("Are you sure you want to delete the entire save folder for this game?")
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        "This will completely remove the save data folder including the save ID. You will need to start the game again to create a new save data folder.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        showDeleteFolderConfirmation = false
                        // 调用现有的删除存档文件夹方法
                        viewModel.deleteSaveData()
                    },
                    colors = ButtonDefaults.textButtonColors(
                        contentColor = MaterialTheme.colorScheme.error
                    )
                ) {
                    Text("Delete Folder")
                }
            },
            dismissButton = {
                TextButton(onClick = { showDeleteFolderConfirmation = false }) {
                    Text("Cancel")
                }
            }
        )
    }
}

/**
 * 可滚动的游戏名称组件
 */
@Composable
fun ScrollableGameName(
    gameName: String,
    modifier: Modifier = Modifier,
    textStyle: TextStyle = MaterialTheme.typography.headlineSmall.copy(fontSize = 18.sp),
    maxLines: Int = 1
) {
    var containerWidth by remember { mutableStateOf(0) }
    var textWidth by remember { mutableStateOf(0) }
    var isScrolling by remember { mutableStateOf(false) }
    var shouldScroll by remember { mutableStateOf(false) }
    
    val density = LocalDensity.current
    
    // 计算文本是否需要滚动（文本宽度大于容器宽度）
    val needsScrolling = remember(textWidth, containerWidth) {
        textWidth > containerWidth
    }
    
    // 滚动动画
    val scrollOffset by animateFloatAsState(
        targetValue = if (isScrolling && shouldScroll) {
            // 滚动到完全显示文本（负值表示向左移动）
            -(textWidth - containerWidth).toFloat()
        } else {
            // 回到起始位置
            0f
        },
        animationSpec = tween(
            durationMillis = if (isScrolling) {
                // 根据文本长度计算滚动时间，让速度相对均匀
                (textWidth * 30 / density.density).toInt().coerceIn(2000, 8000)
            } else {
                500 // 快速回到起始位置
            },
            delayMillis = if (isScrolling) 300 else 0 // 滚动前稍作延迟
        ),
        label = "gameNameScroll"
    )
    
    // 当滚动完成后自动停止
    LaunchedEffect(isScrolling) {
        if (isScrolling) {
            // 计算滚动持续时间
            val scrollDuration = (textWidth * 30 / density.density).toInt().coerceIn(2000, 8000)
            delay(scrollDuration.toLong() + 300) // 等待动画完成
            isScrolling = false
        }
    }
    
    Box(
        modifier = modifier
            .height(IntrinsicSize.Min)
            .clipToBounds()
            .clickable(
                enabled = needsScrolling,
                onClick = {
                    if (needsScrolling) {
                        shouldScroll = !shouldScroll
                        isScrolling = true
                    }
                }
            )
    ) {
        // 测量容器宽度
        Box(
            modifier = Modifier
                .fillMaxSize()
                .onSizeChanged { containerWidth = it.width }
        )
        
        // 可滚动的文本
        SelectionContainer {
            Text(
                text = gameName,
                style = textStyle,
                maxLines = maxLines,
                overflow = androidx.compose.ui.text.style.TextOverflow.Clip,
                modifier = Modifier
                    .graphicsLayer {
                        translationX = scrollOffset
                    }
                    .onSizeChanged { textWidth = it.width }
            )
        }
        
        // 如果文本需要滚动但当前没有在滚动，显示提示
        if (needsScrolling && !isScrolling && !shouldScroll) {
            Text(
                text = "⋯",
                style = textStyle,
                modifier = Modifier.align(Alignment.CenterEnd),
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f)
            )
        }
    }
}

@Composable
fun InfoRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(
            text = label, 
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Text(
            text = value, 
            style = MaterialTheme.typography.bodyMedium
        )
    }
}

@Composable
fun ActionButton(
    text: String,
    enabled: Boolean = true,
    isDestructive: Boolean = false,
    onClick: () -> Unit
) {
    val buttonColors = if (isDestructive) {
        ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
    } else {
        ButtonDefaults.buttonColors()
    }
    
    Button(
        onClick = onClick,
        modifier = Modifier.fillMaxWidth(),
        enabled = enabled,
        colors = buttonColors
    ) {
        Text(text = text)
    }
}
