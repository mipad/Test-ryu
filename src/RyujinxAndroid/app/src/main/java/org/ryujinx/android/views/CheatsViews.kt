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
import org.ryujinx.android.viewmodels.CheatListItem // 添加这个导入
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
    val viewModel = remember { CheatsViewModel(titleId, gamePath, packageName, context) }
    val cheats by viewModel.cheats.collectAsState(emptyList())
    val isLoading by viewModel.isLoading.collectAsState()
    val errorMessage by viewModel.errorMessage.collectAsState()
    
    // 状态控制
    var showDeleteConfirmDialog by remember { mutableStateOf(false) }
    var showAddCheatDialog by remember { mutableStateOf(false) }
    var selectedCheatFile by remember { mutableStateOf<File?>(null) }
    var customDisplayName by remember { mutableStateOf("") }
    
    // 文件选择器 - 使用 OpenDocument 契约
    val filePickerLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument(),
        onResult = { uri ->
            if (uri != null) {
                try {
                    // 从 URI 读取文件内容
                    context.contentResolver.openInputStream(uri)?.use { inputStream ->
                        // 获取文件名
                        val fileName = getFileNameFromUri(context, uri) ?: "cheat_${System.currentTimeMillis()}.txt"
                        
                        // 检查文件扩展名
                        if (!fileName.endsWith(".txt") && !fileName.endsWith(".json")) {
                            viewModel.setErrorMessage("Only .txt and .json files are supported")
                            return@use
                        }
                        
                        // 检查文件名，不允许添加 enabled.txt
                        if (fileName.equals("enabled.txt", ignoreCase = true)) {
                            viewModel.setErrorMessage("Cannot add enabled.txt file. This is a system file.")
                            return@use
                        }
                        
                        // 创建临时文件
                        val tempFile = File(context.cacheDir, fileName)
                        tempFile.outputStream().use { outputStream ->
                            inputStream.copyTo(outputStream)
                        }
                        
                        // 设置默认显示名称（不带扩展名的文件名）
                        customDisplayName = tempFile.nameWithoutExtension
                        selectedCheatFile = tempFile
                        showAddCheatDialog = true
                    }
                } catch (e: Exception) {
                    viewModel.setErrorMessage("Failed to import cheat file: ${e.message}")
                }
            }
        }
    )

    // 添加金手指对话框
    if (showAddCheatDialog && selectedCheatFile != null) {
        AlertDialog(
            onDismissRequest = { 
                showAddCheatDialog = false
                selectedCheatFile?.delete() // 清理临时文件
                selectedCheatFile = null
            },
            title = { Text("Add Cheat File") },
            text = {
                Column {
                    Text("File: ${selectedCheatFile?.name ?: ""}")
                    Spacer(modifier = Modifier.height(16.dp))
                    OutlinedTextField(
                        value = customDisplayName,
                        onValueChange = { customDisplayName = it },
                        label = { Text("Display Name") },
                        modifier = Modifier.fillMaxWidth(),
                        placeholder = { Text("Enter a name for this cheat file") }
                    )
                }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        if (customDisplayName.isNotBlank()) {
                            selectedCheatFile?.let { file ->
                                viewModel.addCheatFile(file, customDisplayName)
                            }
                            showAddCheatDialog = false
                            selectedCheatFile = null
                        }
                    }
                ) {
                    Text("Add")
                }
            },
            dismissButton = {
                TextButton(
                    onClick = {
                        showAddCheatDialog = false
                        selectedCheatFile?.delete() // 清理临时文件
                        selectedCheatFile = null
                    }
                ) {
                    Text("Cancel")
                }
            }
        )
    }

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
            text = { 
                Column {
                    Text("Are you sure you want to delete all cheat files?")
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        "Note: The enabled.txt file (which stores cheat states) will not be deleted.",
                        style = MaterialTheme.typography.bodySmall
                    )
                }
            },
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
                    items(cheats) { item ->
                        when (item) {
                            is CheatListItem.GroupHeader -> {
                                // 显示分组标题
                                Card(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(horizontal = 16.dp, vertical = 8.dp),
                                    colors = CardDefaults.cardColors(
                                        containerColor = MaterialTheme.colorScheme.surfaceVariant
                                    )
                                ) {
                                    Text(
                                        text = item.displayName,
                                        modifier = Modifier.padding(16.dp),
                                        style = MaterialTheme.typography.titleMedium
                                    )
                                }
                            }
                            is CheatListItem.CheatItem -> {
                                // 显示金手指项
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
                                            text = item.name,
                                            style = MaterialTheme.typography.bodyLarge
                                        )
                                        Text(
                                            text = "ID: ${item.id}",
                                            style = MaterialTheme.typography.bodySmall,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                    }
                                    Switch(
                                        checked = item.enabled,
                                        onCheckedChange = { enabled ->
                                            viewModel.setCheatEnabled(item.id, enabled)
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
