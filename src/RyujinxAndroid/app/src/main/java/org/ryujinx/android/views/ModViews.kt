// ModViews.kt
package org.ryujinx.android.views

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.DocumentsContract
import android.util.Log
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
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
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.ryujinx.android.viewmodels.ModModel
import org.ryujinx.android.viewmodels.ModType
import org.ryujinx.android.viewmodels.ModViewModel
import java.io.File

class ModViews {
    companion object {
        
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
            var isAddingMod by remember { mutableStateOf(false) }
            
            // 添加一个状态来跟踪是否已经显示了mod列表
            var modsLoaded by remember { mutableStateOf(false) }
            
            // 使用OpenDocumentTree来选择文件夹而不是文件
            val folderPickerLauncher = rememberLauncherForActivityResult(
                ActivityResultContracts.OpenDocumentTree()
            ) { uri ->
                uri?.let {
                    try {
                        Log.d("ModViews", "Selected URI: $uri")
                        val folderPath = getFolderPathFromUri(context, it)
                        Log.d("ModViews", "Extracted folder path: $folderPath")
                        if (!folderPath.isNullOrEmpty()) {
                            selectedModPath = folderPath
                            showAddModDialog = true
                        } else {
                            // 如果无法获取路径，显示错误
                            scope.launch {
                                snackbarHostState.showSnackbar("无法获取文件夹路径，请确保选择了有效的文件夹")
                            }
                        }
                    } catch (e: Exception) {
                        Log.e("ModViews", "Error processing selected folder", e)
                        scope.launch {
                            snackbarHostState.showSnackbar("处理文件夹时出错: ${e.message}")
                        }
                    }
                }
            }

            // 加载Mod列表 - 使用延迟加载避免闪烁
            LaunchedEffect(titleId) {
                // 重置加载状态，确保每次都重新加载
                viewModel.resetLoadedState()
                // 延迟一小段时间再加载，避免UI闪烁
                delay(300)
                viewModel.loadMods(titleId)
                modsLoaded = true
            }

            // 显示错误消息
            viewModel.errorMessage?.let { error ->
                LaunchedEffect(error) {
                    Log.e("ModViews", "Error from ViewModel: $error")
                    snackbarHostState.showSnackbar(error)
                    viewModel.clearError()
                }
            }

            // 监控添加mod的状态
            LaunchedEffect(isAddingMod) {
                if (isAddingMod) {
                    Log.d("ModViews", "Adding mod in progress")
                }
            }

            Scaffold(
                topBar = {
                    TopAppBar(
                        title = { 
                            Text(
                                text = "Mod Management - $gameName ($titleId)",
                                style = MaterialTheme.typography.titleLarge,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                        },
                        navigationIcon = {
                            IconButton(onClick = { navController.popBackStack() }) {
                                Icon(Icons.Default.ArrowBack, contentDescription = "Back")
                            }
                        },
                        actions = {
                            // 添加刷新按钮
                            IconButton(
                                onClick = {
                                    scope.launch {
                                        viewModel.resetLoadedState()
                                        viewModel.loadMods(titleId)
                                        snackbarHostState.showSnackbar("刷新完成")
                                    }
                                }
                            ) {
                                Icon(Icons.Default.Refresh, contentDescription = "Refresh")
                            }
                        }
                    )
                },
                floatingActionButton = {
                    FloatingActionButton(
                        onClick = {
                            // 启动文件夹选择器，选择整个文件夹
                            folderPickerLauncher.launch(null)
                        },
                        enabled = !isAddingMod
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
                    if (viewModel.isLoading && !modsLoaded) {
                        Column(
                            modifier = Modifier.fillMaxSize(),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.Center
                        ) {
                            CircularProgressIndicator()
                            Spacer(modifier = Modifier.height(12.dp))
                            Text("Loading mods...")
                        }
                    } else {
                        // 使用可滚动的Column
                        Column(
                            modifier = Modifier
                                .fillMaxSize()
                                .padding(8.dp)
                                .verticalScroll(rememberScrollState())
                        ) {
                            // 统计信息和删除所有按钮 - 放在左侧
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(
                                    text = "Mods: ${viewModel.mods.size} (${viewModel.mods.count { it.enabled }} enabled)",
                                    style = MaterialTheme.typography.bodyMedium
                                )
                                
                                OutlinedButton(
                                    onClick = { showDeleteAllDialog = true },
                                    enabled = viewModel.mods.isNotEmpty() && !isAddingMod
                                ) {
                                    Text("Delete All")
                                }
                            }
                            
                            Spacer(modifier = Modifier.height(12.dp))
                            
                            // Mod列表
                            if (viewModel.mods.isEmpty()) {
                                Column(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .height(200.dp),
                                    horizontalAlignment = Alignment.CenterHorizontally,
                                    verticalArrangement = Arrangement.Center
                                ) {
                                    Text(
                                        text = "📁",
                                        style = MaterialTheme.typography.displayMedium
                                    )
                                    Spacer(modifier = Modifier.height(8.dp))
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
                                    Spacer(modifier = Modifier.height(8.dp))
                                    // 添加手动刷新按钮
                                    OutlinedButton(
                                        onClick = {
                                            scope.launch {
                                                viewModel.resetLoadedState()
                                                viewModel.loadMods(titleId)
                                            }
                                        },
                                        enabled = !isAddingMod
                                    ) {
                                        Icon(Icons.Default.Refresh, contentDescription = "Refresh", modifier = Modifier.size(16.dp))
                                        Spacer(modifier = Modifier.width(6.dp))
                                        Text("Refresh List")
                                    }
                                }
                            } else {
                                // 使用类似DLC的列表布局，移除固定高度
                                Surface(
                                    modifier = Modifier.padding(4.dp),
                                    color = MaterialTheme.colorScheme.surfaceVariant,
                                    shape = MaterialTheme.shapes.medium
                                ) {
                                    LazyColumn(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                    ) {
                                        items(viewModel.mods) { mod ->
                                            // 修复这里：移除enabled参数，因为原始ModListItem函数没有这个参数
                                            ModListItem(
                                                mod = mod,
                                                onEnabledChanged = { enabled ->
                                                    scope.launch {
                                                        viewModel.setModEnabled(titleId, mod, enabled)
                                                    }
                                                },
                                                onDelete = {
                                                    if (!isAddingMod) {
                                                        showDeleteDialog = mod
                                                    }
                                                }
                                            )
                                        }
                                    }
                                }
                            }
                            
                            // 添加底部间距，确保内容不会被FAB遮挡
                            Spacer(modifier = Modifier.height(60.dp))
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
                            try {
                                isAddingMod = true
                                
                                // 检查路径是否是文件夹
                                val sourceFile = File(selectedModPath)
                                if (!sourceFile.exists()) {
                                    snackbarHostState.showSnackbar("错误：文件夹不存在")
                                    isAddingMod = false
                                    return@launch
                                }
                                
                                if (!sourceFile.isDirectory) {
                                    snackbarHostState.showSnackbar("错误：请选择文件夹而不是文件")
                                    isAddingMod = false
                                    return@launch
                                }
                                
                                // 显示正在添加的消息
                                val snackbarResult = snackbarHostState.showSnackbar(
                                    message = "正在添加Mod: $modName...",
                                    withDismissAction = false
                                )
                                
                                Log.d("ModViews", "开始添加Mod: $modName, 路径: $selectedModPath")
                                
                                // 添加mod
                                viewModel.addMod(titleId, selectedModPath, modName)
                                
                                // 等待一小段时间确保操作完成
                                delay(1000)
                                
                                // 重新加载列表
                                viewModel.resetLoadedState()
                                viewModel.loadMods(titleId)
                                
                                // 等待加载完成
                                while (viewModel.isLoading) {
                                    delay(100)
                                }
                                
                                // 显示成功消息
                                snackbarHostState.showSnackbar("Mod添加成功: $modName")
                                
                            } catch (e: Exception) {
                                Log.e("ModViews", "添加Mod时出错", e)
                                snackbarHostState.showSnackbar("添加Mod失败: ${e.message}")
                            } finally {
                                isAddingMod = false
                                showAddModDialog = false
                                selectedModPath = ""
                            }
                        }
                    },
                    onDismiss = {
                        if (!isAddingMod) {
                            showAddModDialog = false
                            selectedModPath = ""
                        }
                    },
                    isAdding = isAddingMod
                )
            }

            // 显示添加mod的进度指示器
            if (isAddingMod) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(16.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center
                    ) {
                        CircularProgressIndicator()
                        Spacer(modifier = Modifier.height(12.dp))
                        Text("正在添加Mod...")
                        Spacer(modifier = Modifier.height(4.dp))
                        Text("请稍候", style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }

        @Composable
        private fun ModListItem(
            mod: ModModel,
            onEnabledChanged: (Boolean) -> Unit,
            onDelete: () -> Unit
        ) {
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 3.dp, horizontal = 6.dp),
                shape = RoundedCornerShape(6.dp)
            ) {
                Column(
                    modifier = Modifier.padding(12.dp)
                ) {
                    // 第一行：开关、Mod名称和删除按钮
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        // 启用开关 - 使用Switch而不是Checkbox
                        Switch(
                            checked = mod.enabled,
                            onCheckedChange = onEnabledChanged
                        )
                        
                        Spacer(modifier = Modifier.width(8.dp))
                        
                        // Mod名称 - 占用剩余空间
                        Column(
                            modifier = Modifier.weight(1f)
                        ) {
                            Text(
                                text = mod.name,
                                style = MaterialTheme.typography.bodyLarge,
                                fontWeight = FontWeight.Medium,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                            
                            // 类型信息
                            Text(
                                text = "Type: ${mod.type.name}",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        
                        // 删除按钮
                        IconButton(
                            onClick = onDelete
                        ) {
                            Icon(Icons.Default.Delete, contentDescription = "Delete")
                        }
                    }
                    
                    Spacer(modifier = Modifier.height(6.dp))
                    
                    // 存储位置信息
                    Text(
                        text = if (mod.inExternalStorage) "External Storage" else "Internal Storage",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    
                    Spacer(modifier = Modifier.height(3.dp))
                    
                    // 路径信息 - 允许更多行显示
                    Text(
                        text = mod.path,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 3,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.fillMaxWidth()
                    )
                }
            }
        }

        @Composable
        private fun AddModDialog(
            selectedPath: String,
            onConfirm: (String) -> Unit,
            onDismiss: () -> Unit,
            isAdding: Boolean = false
        ) {
            var modName by remember { mutableStateOf("") }
            val folderName = File(selectedPath).name
            
            // 如果modName为空，设置默认值
            if (modName.isEmpty()) {
                modName = folderName
            }
            
            AlertDialog(
                onDismissRequest = {
                    if (!isAdding) {
                        onDismiss()
                    }
                },
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
                            placeholder = { Text("Enter mod name") },
                            enabled = !isAdding
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        Text(
                            text = "This will copy the entire folder contents to the game's mod directory.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        if (isAdding) {
                            Spacer(modifier = Modifier.height(8.dp))
                            Text(
                                text = "Adding mod in progress...",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.primary
                            )
                        }
                    }
                },
                confirmButton = {
                    Button(
                        onClick = { onConfirm(modName) },
                        enabled = modName.isNotEmpty() && !isAdding
                    ) {
                        Text("Add Mod")
                    }
                },
                dismissButton = {
                    OutlinedButton(
                        onClick = onDismiss,
                        enabled = !isAdding
                    ) {
                        Text("Cancel")
                    }
                }
            )
        }

        private fun getFolderPathFromUri(context: Context, uri: Uri): String? {
            return try {
                Log.d("ModViews", "getFolderPathFromUri called with URI: $uri")
                
                // 获取持久化权限
                val contentResolver = context.contentResolver
                val takeFlags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                contentResolver.takePersistableUriPermission(uri, takeFlags)
                
                // 对于 DocumentTree URI，我们需要特殊处理
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                    val documentId = DocumentsContract.getTreeDocumentId(uri)
                    Log.d("ModViews", "Document ID: $documentId")
                    
                    // 处理不同的存储类型
                    if (documentId.startsWith("primary:")) {
                        // 主存储
                        val path = documentId.substringAfter("primary:")
                        val fullPath = "/storage/emulated/0/$path"
                        Log.d("ModViews", "Primary storage path: $fullPath")
                        
                        // 验证路径是否存在
                        val file = File(fullPath)
                        if (file.exists() && file.isDirectory) {
                            return fullPath
                        } else {
                            Log.w("ModViews", "Path does not exist or is not a directory: $fullPath")
                        }
                    } else if (documentId.contains(":")) {
                        // 可能是SD卡或其他外部存储
                        // 尝试直接使用URI路径
                        val uriPath = uri.toString()
                        Log.d("ModViews", "Non-primary storage URI: $uriPath")
                        
                        // 对于外部存储，我们可能无法获取文件系统路径
                        // 返回一个标识符，让用户知道选择了什么
                        return "external:$documentId"
                    }
                }
                
                // 回退方案：使用URI的路径部分
                val fallbackPath = uri.path
                Log.d("ModViews", "Using fallback path: $fallbackPath")
                fallbackPath
                
            } catch (e: Exception) {
                Log.e("ModViews", "Error getting folder path from URI", e)
                null
            }
        }
    }
}