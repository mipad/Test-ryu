package org.ryujinx.android.views

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.navigation.NavHostController
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.viewmodels.MainViewModel
import java.io.File
import java.text.SimpleDateFormat
import java.util.*

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SaveDataViews(navController: NavHostController, titleId: String, gameName: String) {
    var saveDataInfo by remember { mutableStateOf<SaveDataInfo?>(null) }
    var showImportExportDialog by remember { mutableStateOf(false) }
    var showDeleteConfirmDialog by remember { mutableStateOf(false) }
    var operationInProgress by remember { mutableStateOf(false) }
    var operationMessage by remember { mutableStateOf("") }
    var showOperationResult by remember { mutableStateOf(false) }
    var operationSuccess by remember { mutableStateOf(false) }
    
    LaunchedEffect(titleId) {
        // 检查存档是否存在
        val exists = RyujinxNative.saveDataExists(titleId)
        if (exists) {
            val saveId = RyujinxNative.getSaveIdByTitleId(titleId)
            // 这里可以加载更详细的存档信息
            saveDataInfo = SaveDataInfo(
                saveId = saveId,
                titleId = titleId,
                titleName = gameName,
                lastModified = Date(),
                size = 0 // 实际使用时可以计算真实大小
            )
        }
    }
    
    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text("Save Data Management") },
                navigationIcon = {
                    IconButton(onClick = { navController.popBackStack() }) {
                        Icon(Icons.Filled.ArrowBack, contentDescription = "Back")
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
                    Text(
                        text = gameName,
                        style = MaterialTheme.typography.headlineSmall
                    )
                    Text(
                        text = "Title ID: $titleId",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(16.dp))
            
            // 存档信息
            if (saveDataInfo != null) {
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
                        InfoRow("Save ID:", saveDataInfo!!.saveId)
                        InfoRow("Last Modified:", formatDate(saveDataInfo!!.lastModified))
                        InfoRow("Size:", formatFileSize(saveDataInfo!!.size))
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
                        Text("No save data found for this game")
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(24.dp))
            
            // 操作按钮 - 使用文字代替图标
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                ActionButton(
                    text = "Export Save Data",
                    enabled = saveDataInfo != null && !operationInProgress
                ) {
                    showImportExportDialog = true
                }
                
                ActionButton(
                    text = "Import Save Data", 
                    enabled = !operationInProgress
                ) {
                    showImportExportDialog = true
                }
                
                ActionButton(
                    text = "Delete Save Data",
                    enabled = saveDataInfo != null && !operationInProgress,
                    isDestructive = true
                ) {
                    showDeleteConfirmDialog = true
                }
            }
        }
    }
    
    // 操作进度对话框
    if (operationInProgress) {
        AlertDialog(
            onDismissRequest = { /* 不允许取消 */ },
            title = { Text("Processing") },
            text = { 
                Column {
                    Text(operationMessage)
                    Spacer(modifier = Modifier.height(16.dp))
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                }
            },
            confirmButton = {}
        )
    }
    
    // 操作结果对话框
    if (showOperationResult) {
        AlertDialog(
            onDismissRequest = { showOperationResult = false },
            title = { 
                Text(if (operationSuccess) "Success" else "Error") 
            },
            text = { Text(operationMessage) },
            confirmButton = {
                TextButton(onClick = { showOperationResult = false }) {
                    Text("OK")
                }
            }
        )
    }
    
    // 导入/导出对话框
    if (showImportExportDialog) {
        ImportExportDialog(
            onDismiss = { showImportExportDialog = false },
            onExport = { 
                operationInProgress = true
                operationMessage = "Exporting save data..."
                
                // 在实际应用中，这里应该让用户选择导出路径
                val exportPath = "/sdcard/Download/${gameName}_save_${System.currentTimeMillis()}.zip"
                
                // 在后台线程执行导出操作
                Thread {
                    val success = RyujinxNative.exportSaveData(titleId, exportPath)
                    
                    // 回到主线程更新UI
                    android.os.Handler(android.os.Looper.getMainLooper()).post {
                        operationInProgress = false
                        operationSuccess = success
                        operationMessage = if (success) 
                            "Save data exported to: $exportPath" 
                        else 
                            "Failed to export save data"
                        showOperationResult = true
                    }
                }.start()
            },
            onImport = { 
                // 在实际应用中，这里应该打开文件选择器选择ZIP文件
                // 这里只是示例，需要实现文件选择功能
                operationMessage = "Please select a save data ZIP file to import"
                showOperationResult = true
            }
        )
    }
    
    // 删除确认对话框
    if (showDeleteConfirmDialog) {
        DeleteConfirmDialog(
            onDismiss = { showDeleteConfirmDialog = false },
            onConfirm = { 
                operationInProgress = true
                operationMessage = "Deleting save data..."
                
                // 在后台线程执行删除操作
                Thread {
                    val success = RyujinxNative.deleteSaveData(titleId)
                    
                    // 回到主线程更新UI
                    android.os.Handler(android.os.Looper.getMainLooper()).post {
                        operationInProgress = false
                        operationSuccess = success
                        operationMessage = if (success) 
                            "Save data deleted successfully" 
                        else 
                            "Failed to delete save data"
                        showOperationResult = true
                        
                        if (success) {
                            // 更新UI状态
                            saveDataInfo = null
                            // 延迟关闭对话框并返回
                            android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({
                                navController.popBackStack()
                            }, 1500)
                        }
                    }
                }.start()
            }
        )
    }
}

@Composable
fun InfoRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(text = label, style = MaterialTheme.typography.bodyMedium)
        Text(text = value, style = MaterialTheme.typography.bodyMedium)
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

@Composable
fun ImportExportDialog(
    onDismiss: () -> Unit,
    onExport: () -> Unit,
    onImport: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Save Data Operations") },
        text = { Text("Choose an operation for save data management") },
        confirmButton = {
            Column {
                Button(
                    onClick = {
                        onExport()
                        onDismiss()
                    },
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("Export to ZIP")
                }
                Spacer(modifier = Modifier.height(8.dp))
                Button(
                    onClick = {
                        onImport()
                        onDismiss()
                    },
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("Import from ZIP")
                }
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Cancel")
            }
        }
    )
}

@Composable
fun DeleteConfirmDialog(
    onDismiss: () -> Unit,
    onConfirm: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Delete Save Data") },
        text = { Text("Are you sure you want to delete all save data for this game? This action cannot be undone.") },
        confirmButton = {
            TextButton(
                onClick = {
                    onConfirm()
                    onDismiss()
                },
                colors = ButtonDefaults.textButtonColors(
                    contentColor = MaterialTheme.colorScheme.error
                )
            ) {
                Text("Delete")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Cancel")
            }
        }
    )
}

// 数据类
data class SaveDataInfo(
    val saveId: String,
    val titleId: String,
    val titleName: String,
    val lastModified: Date,
    val size: Long
)

// 工具函数
private fun formatDate(date: Date): String {
    val formatter = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault())
    return formatter.format(date)
}

private fun formatFileSize(size: Long): String {
    return when {
        size < 1024 -> "$size B"
        size < 1024 * 1024 -> "${size / 1024} KB"
        else -> "${size / (1024 * 1024)} MB"
    }
}
