// ModViews.kt
package org.ryujinx.android.views

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.navigation.NavHostController
import kotlinx.coroutines.launch
import org.ryujinx.android.viewmodels.ModModel
import org.ryujinx.android.viewmodels.ModType
import org.ryujinx.android.viewmodels.ModViewModel
import java.io.File

class ModViews {
    companion object {
        
        // 在 ModManagementScreen 中修改协程调用
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ModManagementScreen(
    viewModel: ModViewModel,
    navController: NavHostController,
    titleId: String,
    gameName: String
) {
    val context = LocalContext.current
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    
    // 状态变量
    var showDeleteAllDialog by remember { mutableStateOf(false) }
    var showDeleteDialog by remember { mutableStateOf<ModModel?>(null) }
    var showAddModDialog by remember { mutableStateOf(false) }
    var selectedModPath by remember { mutableStateOf("") }
    
    // 文件夹选择启动器
    val folderPickerLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocumentTree()
    ) { uri ->
        uri?.let {
            val folderPath = getFilePathFromUri(context, it)
            if (!folderPath.isNullOrEmpty()) {
                selectedModPath = folderPath
                showAddModDialog = true
            }
        }
    }

    // 加载Mod列表 - 修复：使用forceRefresh确保每次都重新加载
    LaunchedEffect(titleId) {
        viewModel.loadMods(titleId, forceRefresh = true)
    }

    // 显示错误消息
    viewModel.errorMessage?.let { error ->
        LaunchedEffect(error) {
            snackbarHostState.showSnackbar(error)
            viewModel.clearError()
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { 
                    Column {
                        Text(
                            text = "Mod Management",
                            style = MaterialTheme.typography.titleLarge
                        )
                        Text(
                            text = "$gameName ($titleId)",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                },
                navigationIcon = {
                    IconButton(onClick = { navController.popBackStack() }) {
                        Icon(Icons.Default.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    IconButton(
                        onClick = {
                            viewModel.getDebugInfo(titleId)
                        }
                    ) {
                        Icon(Icons.Default.Warning, contentDescription = "Debug")
                    }
                }
            )
        },
        floatingActionButton = {
            FloatingActionButton(
                onClick = {
                    folderPickerLauncher.launch(null)
                }
            ) {
                Icon(Icons.Default.Add, contentDescription = "Add Mod")
            }
        },
        snackbarHost = { SnackbarHost(hostState = snackbarHostState) }
    ) { paddingValues ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(paddingValues)
        ) {
            if (viewModel.isLoading) {
                Column(
                    modifier = Modifier.fillMaxSize(),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    CircularProgressIndicator()
                    Spacer(modifier = Modifier.height(16.dp))
                    Text("Loading mods...")
                }
            } else {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(16.dp)
                ) {
                    // 统计信息和批量操作
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = "Mods: ${viewModel.mods.size} (${viewModel.mods.count { it.enabled }} enabled)",
                            style = MaterialTheme.typography.bodyMedium
                        )
                        
                        Row {
                            OutlinedButton(
                                onClick = {
                                    scope.launch {
                                        viewModel.enableAllMods(titleId)
                                        // 重新加载列表以确保状态更新
                                        viewModel.loadMods(titleId, forceRefresh = true)
                                    }
                                },
                                modifier = Modifier.padding(end = 8.dp)
                            ) {
                                Text("Enable All")
                            }
                            
                            OutlinedButton(
                                onClick = {
                                    scope.launch {
                                        viewModel.disableAllMods(titleId)
                                        // 重新加载列表以确保状态更新
                                        viewModel.loadMods(titleId, forceRefresh = true)
                                    }
                                },
                                modifier = Modifier.padding(end = 8.dp)
                            ) {
                                Text("Disable All")
                            }
                            
                            OutlinedButton(
                                onClick = { showDeleteAllDialog = true },
                                enabled = viewModel.mods.isNotEmpty()
                            ) {
                                Text("Delete All")
                            }
                        }
                    }
                    
                    Spacer(modifier = Modifier.height(16.dp))
                    
                    // Mod列表
                    if (viewModel.mods.isEmpty()) {
                        Column(
                            modifier = Modifier.fillMaxSize(),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.Center
                        ) {
                            Text(
                                text = "📁",
                                style = MaterialTheme.typography.displayMedium
                            )
                            Spacer(modifier = Modifier.height(16.dp))
                            Text(
                                text = "No mods found",
                                style = MaterialTheme.typography.bodyLarge,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Text(
                                text = "Click the + button to add a mod",
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    } else {
                        // 使用类似DLC的列表布局
                        Surface(
                            modifier = Modifier.padding(8.dp),
                            color = MaterialTheme.colorScheme.surfaceVariant,
                            shape = MaterialTheme.shapes.medium
                        ) {
                            LazyColumn(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(400.dp) // 固定高度，确保内容可滚动
                            ) {
                                items(viewModel.mods) { mod ->
                                    ModListItem(
                                        mod = mod,
                                        onEnabledChanged = { enabled ->
                                            scope.launch {
                                                viewModel.setModEnabled(titleId, mod, enabled)
                                                // 重新加载列表以确保状态更新
                                                viewModel.loadMods(titleId, forceRefresh = true)
                                            }
                                        },
                                        onDelete = {
                                            showDeleteDialog = mod
                                        },
                                        onOpenLocation = {
                                            openFolderLocation(context, mod.path)
                                        }
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // 删除单个Mod对话框
    showDeleteDialog?.let { mod ->
        AlertDialog(
            onDismissRequest = { showDeleteDialog = null },
            title = { Text("Delete Mod") },
            text = { 
                Text("Are you sure you want to delete \"${mod.name}\"? This action cannot be undone.") 
            },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch {
                            viewModel.deleteMod(titleId, mod)
                            showDeleteDialog = null
                            // 重新加载列表
                            viewModel.loadMods(titleId, forceRefresh = true)
                        }
                    }
                ) {
                    Text("Delete")
                }
            },
            dismissButton = {
                OutlinedButton(
                    onClick = { showDeleteDialog = null }
                ) {
                    Text("Cancel")
                }
            }
        )
    }

    // 删除所有Mod对话框
    if (showDeleteAllDialog) {
        AlertDialog(
            onDismissRequest = { showDeleteAllDialog = false },
            title = { Text("Delete All Mods") },
            text = { 
                Text("Are you sure you want to delete all ${viewModel.mods.size} mods? This action cannot be undone.") 
            },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch {
                            viewModel.deleteAllMods(titleId)
                            showDeleteAllDialog = false
                            // 重新加载列表
                            viewModel.loadMods(titleId, forceRefresh = true)
                        }
                    }
                ) {
                    Text("Delete All")
                }
            },
            dismissButton = {
                OutlinedButton(
                    onClick = { showDeleteAllDialog = false }
                ) {
                    Text("Cancel")
                }
            }
        )
    }

    // 添加Mod对话框
    if (showAddModDialog) {
        AddModDialog(
            selectedPath = selectedModPath,
            onConfirm = { modName ->
                scope.launch {
                    viewModel.addMod(titleId, selectedModPath, modName)
                    showAddModDialog = false
                    selectedModPath = ""
                    // 重新加载列表
                    viewModel.loadMods(titleId, forceRefresh = true)
                }
            },
            onDismiss = {
                showAddModDialog = false
                selectedModPath = ""
            }
        )
    }

    // 调试信息对话框
    viewModel.debugInfo?.let { debugInfo ->
        AlertDialog(
            onDismissRequest = { viewModel.clearDebugInfo() },
            title = { Text("Debug Information") },
            text = { 
                Column {
                    Text(
                        text = debugInfo,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(400.dp)
                    )
                }
            },
            confirmButton = {
                Button(
                    onClick = { viewModel.clearDebugInfo() }
                ) {
                    Text("Close")
                }
            }
        )
    }
}

        @Composable
        private fun ModListItem(
            mod: ModModel,
            onEnabledChanged: (Boolean) -> Unit,
            onDelete: () -> Unit,
            onOpenLocation: () -> Unit
        ) {
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 4.dp, horizontal = 8.dp),
                shape = RoundedCornerShape(8.dp)
            ) {
                Column(
                    modifier = Modifier.padding(16.dp)
                ) {
                    // 第一行：开关、Mod名称和操作按钮
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        // 启用开关
                        Switch(
                            checked = mod.enabled,
                            onCheckedChange = onEnabledChanged
                        )
                        
                        Spacer(modifier = Modifier.width(12.dp))
                        
                        // Mod名称 - 占用剩余空间
                        Text(
                            text = mod.name,
                            style = MaterialTheme.typography.bodyLarge,
                            fontWeight = FontWeight.Medium,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f)
                        )
                        
                        // 操作按钮 - 使用图标按钮，类似DLC界面
                        Row {
                            // 打开文件夹按钮
                            IconButton(
                                onClick = onOpenLocation
                            ) {
                                Icon(Icons.Default.Folder, contentDescription = "Open Location")
                            }
                            
                            // 删除按钮
                            IconButton(
                                onClick = onDelete
                            ) {
                                Icon(Icons.Default.Delete, contentDescription = "Delete")
                            }
                        }
                    }
                    
                    Spacer(modifier = Modifier.height(8.dp))
                    
                    // 第二行：类型和存储位置信息
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text(
                            text = "Type: ${mod.type.name}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        
                        Text(
                            text = if (mod.inExternalStorage) "External Storage" else "Internal Storage",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    
                    Spacer(modifier = Modifier.height(4.dp))
                    
                    // 第三行：路径信息
                    Text(
                        text = mod.path,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
        }

        @Composable
        private fun AddModDialog(
            selectedPath: String,
            onConfirm: (String) -> Unit,
            onDismiss: () -> Unit
        ) {
            var modName by remember { mutableStateOf(File(selectedPath).name) }
            
            AlertDialog(
                onDismissRequest = onDismiss,
                title = { Text("Add Mod") },
                text = {
                    Column {
                        Text("Selected folder: $selectedPath")
                        Spacer(modifier = Modifier.height(8.dp))
                        Text("Mod name:")
                        OutlinedTextField(
                            value = modName,
                            onValueChange = { modName = it },
                            modifier = Modifier.fillMaxWidth(),
                            placeholder = { Text("Enter mod name") }
                        )
                    }
                },
                confirmButton = {
                    Button(
                        onClick = { onConfirm(modName) },
                        enabled = modName.isNotEmpty()
                    ) {
                        Text("Add Mod")
                    }
                },
                dismissButton = {
                    OutlinedButton(onClick = onDismiss) {
                        Text("Cancel")
                    }
                }
            )
        }

        private fun getFilePathFromUri(context: Context, uri: Uri): String? {
            return try {
                val contentResolver = context.contentResolver
                val takeFlags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                contentResolver.takePersistableUriPermission(uri, takeFlags)
                
                // 对于 DocumentFile，我们需要使用 DocumentsContract 来获取路径
                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.LOLLIPOP) {
                    val documentId = android.provider.DocumentsContract.getDocumentId(uri)
                    if (documentId.startsWith("primary:")) {
                        val path = documentId.substringAfter("primary:")
                        "/storage/emulated/0/$path"
                    } else {
                        // 处理其他存储设备
                        uri.path
                    }
                } else {
                    uri.path
                }
            } catch (e: Exception) {
                e.printStackTrace()
                null
            }
        }

        private fun openFolderLocation(context: Context, path: String) {
            try {
                val intent = Intent(Intent.ACTION_VIEW)
                val file = File(path)
                val uri = if (file.exists() && file.isDirectory) {
                    Uri.fromFile(file)
                } else {
                    // 如果路径不存在或者是文件，打开父目录
                    val parentDir = file.parentFile ?: file
                    Uri.fromFile(parentDir)
                }
                intent.setDataAndType(uri, "resource/folder")
                
                // 添加标志以在新任务中打开
                intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                
                // 检查是否有应用可以处理这个Intent
                if (intent.resolveActivity(context.packageManager) != null) {
                    context.startActivity(intent)
                } else {
                    // 如果没有应用可以处理，显示路径信息
                    android.widget.Toast.makeText(context, "Path: $path\nNo file manager app found", android.widget.Toast.LENGTH_LONG).show()
                }
            } catch (e: Exception) {
                // 如果无法直接打开，显示路径信息
                android.widget.Toast.makeText(context, "Path: $path\nError: ${e.message}", android.widget.Toast.LENGTH_LONG).show()
            }
        }
    }
}
