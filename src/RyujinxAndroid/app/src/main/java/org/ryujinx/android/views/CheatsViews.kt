package org.ryujinx.android.views

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.navigation.NavController
import org.ryujinx.android.viewmodels.CheatsViewModel
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalContext
import java.io.File

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CheatsViews(
    navController: NavController,
    titleId: String,
    gamePath: String
) {
    val context = LocalContext.current
    val packageName = context.packageName // 动态获取包名
    
    // 传递包名给 ViewModel
    val viewModel = remember { CheatsViewModel(titleId, gamePath, packageName) }
    val cheats by viewModel.cheats.collectAsState(emptyList())
    val isLoading by viewModel.isLoading.collectAsState()
    val errorMessage by viewModel.errorMessage.collectAsState()
    
    // 状态控制
    var showDeleteConfirmDialog by remember { mutableStateOf(false) }
    
    // 文件选择器 - 使用 OpenDocument 契约
    val filePickerLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument(),
        onResult = { uri ->
            if (uri != null) {
                try {
                    // 从 URI 读取文件内容并复制到金手指目录
                    context.contentResolver.openInputStream(uri)?.use { inputStream ->
                        // 获取文件名
                        val fileName = getFileNameFromUri(context, uri) ?: "cheat_${System.currentTimeMillis()}.txt"
                        
                        // 检查文件扩展名
                        if (!fileName.endsWith(".txt") && !fileName.endsWith(".json")) {
                            viewModel.setErrorMessage("Only .txt and .json files are supported")
                            return@use
                        }
                        
                        // 创建临时文件
                        val tempFile = File(context.cacheDir, fileName)
                        tempFile.outputStream().use { outputStream ->
                            inputStream.copyTo(outputStream)
                        }
                        
                        // 传递给 ViewModel 处理
                        viewModel.addCheatFile(tempFile)
                        
                        // 删除临时文件
                        tempFile.delete()
                    }
                } catch (e: Exception) {
                    viewModel.setErrorMessage("Failed to import cheat file: ${e.message}")
                }
            }
        }
    )

    // 显示错误对话框
    if (errorMessage != null) {
        AlertDialog(
            onDismissRequest = { viewModel.clearError() },
            title = { Text("Error") },
            text = { Text(errorMessage!!) },
            confirmButton = {
                TextButton(
                    onClick = { viewModel.clearError() }
                ) {
                    Text("OK")
                }
            }
        )
    }
    
    // 删除确认对话框
    if (showDeleteConfirmDialog) {
        AlertDialog(
            onDismissRequest = { showDeleteConfirmDialog = false },
            title = { Text("Confirm Delete") },
            text = { Text("Are you sure you want to delete all cheat files? This action cannot be undone.") },
            confirmButton = {
                TextButton(
                    onClick = {
                        viewModel.deleteAllCheats()
                        showDeleteConfirmDialog = false
                    }
                ) {
                    Text("Delete")
                }
            },
            dismissButton = {
                TextButton(
                    onClick = { showDeleteConfirmDialog = false }
                ) {
                    Text("Cancel")
                }
            }
        )
    }
    
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Manage Cheats") },
                navigationIcon = {
                    IconButton(onClick = { navController.popBackStack() }) {
                        Icon(Icons.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    TextButton(
                        onClick = { viewModel.saveCheats() },
                        enabled = !isLoading
                    ) {
                        Text("Save")
                    }
                }
            )
        },
        bottomBar = {
            // 在底部添加操作按钮
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(16.dp),
                horizontalArrangement = Arrangement.SpaceEvenly
            ) {
                Button(
                    onClick = { 
                        // 启动文件选择器，显示 txt 和 json 文件
                        filePickerLauncher.launch(arrayOf("text/plain", "application/json"))
                    },
                    enabled = !isLoading
                ) {
                    Text("Add Cheats")
                }
                
                Button(
                    onClick = { showDeleteConfirmDialog = true },
                    enabled = !isLoading && viewModel.getCheatFileCount() > 0,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.error
                    )
                ) {
                    Text("Delete All")
                }
            }
        }
    ) { innerPadding ->
        Column(
            modifier = Modifier
                .padding(innerPadding)
                .fillMaxSize()
        ) {
            // 显示金手指文件统计信息
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(16.dp)
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Text("Cheat Files:")
                    Text("${viewModel.getCheatFileCount()} files")
                }
            }
            
            // 显示金手指目录路径（用于调试）
            if (cheats.isEmpty() && !isLoading) {
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceVariant
                    )
                ) {
                    Text(
                        text = "Cheats directory:\n/storage/emulated/0/Android/data/$packageName/files/mods/contents/$titleId/cheats/",
                        modifier = Modifier.padding(16.dp),
                        style = MaterialTheme.typography.bodySmall
                    )
                }
            }
            
            if (isLoading) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .weight(1f),
                    contentAlignment = Alignment.Center
                ) {
                    CircularProgressIndicator()
                }
            } else if (cheats.isEmpty()) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .weight(1f),
                    contentAlignment = Alignment.Center
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text("No cheats found for this game")
                        Spacer(modifier = Modifier.height(8.dp))
                        Text(
                            text = "Click 'Add Cheats' below to import .txt or .json files",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            } else {
                LazyColumn(
                    modifier = Modifier.weight(1f)
                ) {
                    items(cheats) { cheat ->
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(16.dp),
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            Column(
                                modifier = Modifier.weight(1f)
                            ) {
                                Text(
                                    text = cheat.name,
                                    style = MaterialTheme.typography.bodyLarge
                                )
                                Text(
                                    text = "ID: ${cheat.id}",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                            Switch(
                                checked = cheat.enabled,
                                onCheckedChange = { enabled ->
                                    viewModel.setCheatEnabled(cheat.id, enabled)
                                }
                            )
                        }
                        Divider()
                    }
                }
            }
        }
    }
}

// 辅助函数：从 URI 获取文件名
private fun getFileNameFromUri(context: android.content.Context, uri: android.net.Uri): String? {
    return context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) {
            val displayNameIndex = cursor.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
            if (displayNameIndex != -1) {
                cursor.getString(displayNameIndex)
            } else {
                null
            }
        } else {
            null
        }
    }
}
