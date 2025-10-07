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
        
        @OptIn(ExperimentalMaterial3Api::class)
        @Composable
        fun ModManagementScreen(
            viewModel: ModViewModel,
            navController: NavHostController,
            titleId: String,
            gameName: String
        ) {
            val context = LocalContext.current
            val coroutineScope = rememberCoroutineScope()
            val snackbarHostState = remember { SnackbarHostState() }
            
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

            // 加载Mod列表
            LaunchedEffect(titleId) {
                viewModel.loadMods(titleId)
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
                                    text = "Mods: ${viewModel.mods.size} (${viewModel.selectedMods.size} enabled)",
                                    style = MaterialTheme.typography.bodyMedium
                                )
                                
                                Row {
                                    // 使用文字按钮代替图标按钮
                                    OutlinedButton(
                                        onClick = {
                                            coroutineScope.launch {
                                                viewModel.enableAllMods(titleId)
                                            }
                                        },
                                        modifier = Modifier.padding(end = 8.dp)
                                    ) {
                                        Text("Enable All")
                                    }
                                    
                                    OutlinedButton(
                                        onClick = {
                                            coroutineScope.launch {
                                                viewModel.disableAllMods(titleId)
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
                                LazyColumn(
                                    modifier = Modifier.fillMaxSize()
                                ) {
                                    items(viewModel.mods) { mod ->
                                        ModListItem(
                                            mod = mod,
                                            onEnabledChanged = { enabled ->
                                                coroutineScope.launch {
                                                    viewModel.setModEnabled(titleId, mod, enabled)
                                                }
                                            },
                                            onDelete = {
                                                showDeleteDialog = mod
                                            },
                                            onOpenLocation = {
                                                // 在Android上打开文件夹位置
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
                                coroutineScope.launch {
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
                                coroutineScope.launch {
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
                        coroutineScope.launch {
                            viewModel.addMod(titleId, selectedModPath, modName)
                            showAddModDialog = false
                            selectedModPath = ""
                        }
                    },
                    onDismiss = {
                        showAddModDialog = false
                        selectedModPath = ""
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
                    .padding(vertical = 4.dp),
                shape = RoundedCornerShape(8.dp)
            ) {
                Column(
                    modifier = Modifier.padding(16.dp)
                ) {
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
                        
                        // Mod信息
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
                        
                        // 操作按钮 - 使用文字按钮
                        Row {
                            OutlinedButton(
                                onClick = onOpenLocation,
                                modifier = Modifier.padding(end = 8.dp)
                            ) {
                                Text("Open")
                            }
                            
                            OutlinedButton(
                                onClick = onDelete
                            ) {
                                Text("Delete")
                            }
                        }
                    }
                    
                    // 路径信息（可选的，因为可能很长）
                    Text(
                        text = mod.path,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 2,
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
                val uri = Uri.parse("file://$path")
                intent.setDataAndType(uri, "resource/folder")
                context.startActivity(intent)
            } catch (e: Exception) {
                // 如果无法直接打开，显示路径信息
                android.widget.Toast.makeText(context, "Path: $path", android.widget.Toast.LENGTH_LONG).show()
            }
        }
    }
}
