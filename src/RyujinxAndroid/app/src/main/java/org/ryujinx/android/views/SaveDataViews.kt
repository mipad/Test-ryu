package org.ryujinx.android.views

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.navigation.NavHostController
import org.ryujinx.android.RyujinxNative
import java.io.File
import java.io.FileOutputStream
import java.text.SimpleDateFormat
import java.util.*

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SaveDataViews(navController: NavHostController, titleId: String, gameName: String) {
    val context = LocalContext.current
    var saveDataInfo by remember { mutableStateOf<SaveDataInfo?>(null) }
    var showImportExportDialog by remember { mutableStateOf(false) }
    var showDeleteConfirmDialog by remember { mutableStateOf(false) }
    var operationInProgress by remember { mutableStateOf(false) }
    var operationMessage by remember { mutableStateOf("") }
    var showOperationResult by remember { mutableStateOf(false) }
    var operationSuccess by remember { mutableStateOf(false) }
    
    // 文件选择器
    val filePickerLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.GetContent()
    ) { uri ->
        uri?.let { selectedUri ->
            operationInProgress = true
            operationMessage = "Importing save data..."
            
            Thread {
                try {
                    // 将选中的文件复制到临时位置
                    val inputStream = context.contentResolver.openInputStream(selectedUri)
                    val tempFile = File.createTempFile("save_import", ".zip")
                    FileOutputStream(tempFile).use { output ->
                        inputStream?.copyTo(output)
                    }
                    
                    // 调用导入方法
                    val success = RyujinxNative.importSaveData(titleId, tempFile.absolutePath)
                    
                    // 删除临时文件
                    tempFile.delete()
                    
                    android.os.Handler(android.os.Looper.getMainLooper()).post {
                        operationInProgress = false
                        operationSuccess = success
                        operationMessage = if (success) 
                            "Save data imported successfully" 
                        else 
                            "Failed to import save data"
                        showOperationResult = true
                        
                        if (success) {
                            // 重新加载存档信息
                            loadSaveDataInfo(titleId, gameName) { info ->
                                saveDataInfo = info
                            }
                        }
                    }
                } catch (e: Exception) {
                    android.os.Handler(android.os.Looper.getMainLooper()).post {
                        operationInProgress = false
                        operationSuccess = false
                        operationMessage = "Error importing save data: ${e.message}"
                        showOperationResult = true
                    }
                }
            }.start()
        }
    }
    
    // 加载存档信息
    LaunchedEffect(titleId) {
        loadSaveDataInfo(titleId, gameName) { info ->
            saveDataInfo = info
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
                        style = MaterialTheme.typography.headlineSmall,
                        maxLines = 1,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
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
                        Icon(
                            Icons.Filled.Save,
                            contentDescription = "No Save Data",
                            modifier = Modifier.size(48.dp),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        Text(
                            "No save data found for this game",
                            color = MaterialTheme.colorScheme.onSurfaceVariant
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
                    enabled = saveDataInfo != null && !operationInProgress,
                    icon = Icons.Filled.Archive
                ) {
                    operationInProgress = true
                    operationMessage = "Exporting save data..."
                    
                    Thread {
                        // 创建导出文件名
                        val fileName = "${gameName.replace("[^a-zA-Z0-9]".toRegex(), "_")}_save_${System.currentTimeMillis()}.zip"
                        val exportDir = File(context.getExternalFilesDir(null), "exports")
                        exportDir.mkdirs()
                        val exportPath = File(exportDir, fileName).absolutePath
                        
                        val success = RyujinxNative.exportSaveData(titleId, exportPath)
                        
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
                }
                
                ActionButton(
                    text = "Import Save Data", 
                    enabled = !operationInProgress,
                    icon = Icons.Filled.Unarchive
                ) {
                    // 打开文件选择器选择ZIP文件
                    filePickerLauncher.launch("application/zip")
                }
                
                ActionButton(
                    text = "Delete Save Data",
                    enabled = saveDataInfo != null && !operationInProgress,
                    icon = Icons.Filled.Delete,
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
    
    // 删除确认对话框
    if (showDeleteConfirmDialog) {
        DeleteConfirmDialog(
            onDismiss = { showDeleteConfirmDialog = false },
            onConfirm = { 
                operationInProgress = true
                operationMessage = "Deleting save data..."
                
                Thread {
                    val success = RyujinxNative.deleteSaveData(titleId)
                    
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
    icon: androidx.compose.ui.graphics.vector.ImageVector,
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
        Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(modifier = Modifier.width(8.dp))
        Text(text = text)
    }
}

@Composable
fun DeleteConfirmDialog(
    onDismiss: () -> Unit,
    onConfirm: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Delete Save Data") },
        text = { 
            Text("Are you sure you want to delete all save data for this game? This action cannot be undone.") 
        },
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

// 加载存档信息的辅助函数
private fun loadSaveDataInfo(titleId: String, gameName: String, onResult: (SaveDataInfo?) -> Unit) {
    Thread {
        val exists = RyujinxNative.saveDataExists(titleId)
        if (exists) {
            val saveId = RyujinxNative.getSaveIdByTitleId(titleId)
            if (!saveId.isNullOrEmpty()) {
                // 这里可以加载更详细的存档信息，比如大小和修改时间
                val saveInfo = SaveDataInfo(
                    saveId = saveId,
                    titleId = titleId,
                    titleName = gameName,
                    lastModified = Date(),
                    size = 0 // 实际使用时可以计算真实大小
                )
                android.os.Handler(android.os.Looper.getMainLooper()).post {
                    onResult(saveInfo)
                }
            } else {
                android.os.Handler(android.os.Looper.getMainLooper()).post {
                    onResult(null)
                }
            }
        } else {
            android.os.Handler(android.os.Looper.getMainLooper()).post {
                onResult(null)
            }
        }
    }.start()
}
