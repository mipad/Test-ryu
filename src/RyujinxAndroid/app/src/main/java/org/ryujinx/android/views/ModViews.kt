// ModViews.kt
package org.ryujinx.android.views

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
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
import androidx.compose.ui.platform.LocalConfiguration
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
import androidx.compose.material3.TextButton

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
            
            // 添加一个状态来跟踪是否已经显示了mod列表
            var modsLoaded by remember { mutableStateOf(false) }
            
            // 使用OpenDocumentTree来选择文件夹而不是文件
            val folderPickerLauncher = rememberLauncherForActivityResult(
                ActivityResultContracts.OpenDocumentTree()
            ) { uri ->
                uri?.let {
                    // 获取文件夹路径
                    val folderPath = getFolderPathFromUri(context, it)
                    if (!folderPath.isNullOrEmpty()) {
                        selectedModPath = folderPath
                        showAddModDialog = true
                    } else {
                        // 如果无法获取路径，显示错误
                        scope.launch {
                            snackbarHostState.showSnackbar("无法获取文件夹路径")
                        }
                    }
                }
            }

            // 加载Mod列表 - 使用延迟加载避免闪烁
            LaunchedEffect(titleId) {
                // 重置加载状态，确保每次都重新加载
                viewModel.resetLoadedState()
                // 延迟一小段时间再加载，避免UI闪烁
                kotlinx.coroutines.delay(300)
                viewModel.loadMods(titleId)
                modsLoaded = true
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
                            // 移除Column包装，直接在一行显示标题和游戏信息
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
                                    }
                                }
                            ) {
                                Icon(Icons.Default.Refresh, contentDescription = "Refresh")
                            }
                        }
                    )
                },
                floatingActionButton = {
                    // 修复：用小型正方形包裹+按钮
                    Box(
                        modifier = Modifier
                            .size(56.dp)
                            .padding(4.dp)
                    ) {
                        Surface(
                            modifier = Modifier
                                .fillMaxSize()
                                .clip(RoundedCornerShape(12.dp)),
                            color = MaterialTheme.colorScheme.primary,
                            contentColor = MaterialTheme.colorScheme.onPrimary,
                            shadowElevation = 6.dp
                        ) {
                            IconButton(
                                onClick = {
                                    // 启动文件夹选择器，选择整个文件夹
                                    folderPickerLauncher.launch(null)
                                },
                                modifier = Modifier.fillMaxSize()
                            ) {
                                Icon(Icons.Default.Add, contentDescription = "Add Mod")
                            }
                        }
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
                            Spacer(modifier = Modifier.height(12.dp)) // 减少间距
                            Text("Loading mods...")
                        }
                    } else {
                        // 使用可滚动的Column
                        Column(
                            modifier = Modifier
                                .fillMaxSize()
                                .padding(8.dp) // 减少内边距
                                .verticalScroll(rememberScrollState()) // 添加垂直滚动
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
                                    enabled = viewModel.mods.isNotEmpty()
                                ) {
                                    Text("Delete All")
                                }
                            }
                            
                            Spacer(modifier = Modifier.height(12.dp)) // 减少间距
                            
                            // Mod列表
                            if (viewModel.mods.isEmpty()) {
                                Column(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .height(200.dp), // 减少高度
                                    horizontalAlignment = Alignment.CenterHorizontally,
                                    verticalArrangement = Arrangement.Center
                                ) {
                                    Text(
                                        text = "📁",
                                        style = MaterialTheme.typography.displayMedium
                                    )
                                    Spacer(modifier = Modifier.height(8.dp)) // 减少间距
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
                                    Spacer(modifier = Modifier.height(8.dp)) // 减少间距
                                    // 添加手动刷新按钮
                                    OutlinedButton(
                                        onClick = {
                                            scope.launch {
                                                viewModel.resetLoadedState()
                                                viewModel.loadMods(titleId)
                                            }
                                        }
                                    ) {
                                        Icon(Icons.Default.Refresh, contentDescription = "Refresh", modifier = Modifier.size(16.dp))
                                        Spacer(modifier = Modifier.width(6.dp)) // 减少间距
                                        Text("Refresh List")
                                    }
                                }
                            } else {
                                // 使用类似DLC的列表布局，移除固定高度
                                Surface(
                                    modifier = Modifier.padding(4.dp), // 减少内边距
                                    color = MaterialTheme.colorScheme.surfaceVariant,
                                    shape = MaterialTheme.shapes.medium
                                ) {
                                    Column(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                    ) {
                                        viewModel.mods.forEach { mod ->
                                            ModListItem(
                                                mod = mod,
                                                onEnabledChanged = { enabled ->
                                                    scope.launch {
                                                        viewModel.setModEnabled(titleId, mod, enabled)
                                                        // 不重新加载列表，避免闪烁
                                                    }
                                                },
                                                onDelete = {
                                                    showDeleteDialog = mod
                                                }
                                            )
                                        }
                                    }
                                }
                            }
                            
                            // 添加底部间距，确保内容不会被FAB遮挡
                            Spacer(modifier = Modifier.height(60.dp)) // 减少底部间距
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
                                    viewModel.loadMods(titleId)
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
                                    viewModel.loadMods(titleId)
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
                            // 检查路径是否是文件夹
                            val sourceFile = File(selectedModPath)
                            if (!sourceFile.exists() || !sourceFile.isDirectory) {
                                snackbarHostState.showSnackbar("请选择一个有效的文件夹")
                                return@launch
                            }
                            
                            viewModel.addMod(titleId, selectedModPath, modName)
                            showAddModDialog = false
                            selectedModPath = ""
                            // 重新加载列表
                            viewModel.loadMods(titleId)
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
        fun ModManagementDialog(
            titleId: String,
            gameName: String,
            onDismiss: () -> Unit
        ) {
            val modViewModel = remember { ModViewModel() }
            val context = LocalContext.current
            val configuration = LocalConfiguration.current
            val isLandscape = configuration.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE
            val scope = rememberCoroutineScope()
            
            // 状态变量
            var showDeleteAllDialog by remember { mutableStateOf(false) }
            var showDeleteDialog by remember { mutableStateOf<ModModel?>(null) }
            var showAddModDialog by remember { mutableStateOf(false) }
            var selectedModPath by remember { mutableStateOf("") }
            
            // 使用OpenDocumentTree来选择文件夹
            val folderPickerLauncher = rememberLauncherForActivityResult(
                ActivityResultContracts.OpenDocumentTree()
            ) { uri ->
                uri?.let {
                    val folderPath = getFolderPathFromUri(context, it)
                    if (!folderPath.isNullOrEmpty()) {
                        selectedModPath = folderPath
                        showAddModDialog = true
                    }
                }
            }

            // 加载Mod列表
            LaunchedEffect(titleId) {
                modViewModel.resetLoadedState()
                modViewModel.loadMods(titleId)
            }

            // 显示错误消息
            modViewModel.errorMessage?.let { error ->
                LaunchedEffect(error) {
                    modViewModel.clearError()
                }
            }

            AlertDialog(
                onDismissRequest = onDismiss,
                title = { 
                    Text(
                        text = "Mod Management - $gameName",
                        style = MaterialTheme.typography.titleLarge
                    )
                },
                text = {
                    // 根据屏幕方向选择布局
                    if (isLandscape) {
                        // 横屏：左右布局
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(400.dp) // 固定高度，允许滚动
                        ) {
                            // 左侧：Mod列表（可滚动）
                            Column(
                                modifier = Modifier
                                    .weight(1f)
                                    .fillMaxHeight()
                                    .verticalScroll(rememberScrollState())
                            ) {
                                // 统计信息和删除所有按钮
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(
                                        text = "Mods: ${modViewModel.mods.size} (${modViewModel.mods.count { it.enabled }} enabled)",
                                        style = MaterialTheme.typography.bodyMedium
                                    )
                                    
                                    TextButton(
                                        onClick = { showDeleteAllDialog = true },
                                        enabled = modViewModel.mods.isNotEmpty()
                                    ) {
                                        Text("Delete All")
                                    }
                                }
                                
                                Spacer(modifier = Modifier.height(12.dp))
                                
                                // Mod列表
                                if (modViewModel.mods.isEmpty()) {
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
                                            text = "Click + button to add a mod",
                                            style = MaterialTheme.typography.bodyMedium,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                    }
                                } else {
                                    Column(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .verticalScroll(rememberScrollState())
                                    ) {
                                        modViewModel.mods.forEach { mod ->
                                            ModListItem(
                                                mod = mod,
                                                onEnabledChanged = { enabled ->
                                                    scope.launch {
                                                        modViewModel.setModEnabled(titleId, mod, enabled)
                                                    }
                                                },
                                                onDelete = {
                                                    showDeleteDialog = mod
                                                }
                                            )
                                        }
                                    }
                                }
                            }
                            
                            // 右侧：操作按钮和信息
                            Column(
                                modifier = Modifier
                                    .weight(0.4f)
                                    .fillMaxHeight()
                                    .padding(start = 16.dp),
                                verticalArrangement = Arrangement.spacedBy(12.dp)
                            ) {
                                Text(
                                    text = "Actions",
                                    style = MaterialTheme.typography.titleMedium,
                                    fontWeight = FontWeight.Bold
                                )
                                
                                // 修复：用小型正方形包裹+按钮
                                Box(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .height(48.dp)
                                ) {
                                    Surface(
                                        modifier = Modifier
                                            .size(48.dp)
                                            .align(Alignment.Center)
                                            .clip(RoundedCornerShape(8.dp)),
                                        color = MaterialTheme.colorScheme.primary,
                                        contentColor = MaterialTheme.colorScheme.onPrimary,
                                        shadowElevation = 4.dp
                                    ) {
                                        IconButton(
                                            onClick = {
                                                folderPickerLauncher.launch(null)
                                            },
                                            modifier = Modifier.fillMaxSize()
                                        ) {
                                            Icon(Icons.Default.Add, contentDescription = "Add Mod")
                                        }
                                    }
                                }
                                
                                Text(
                                    text = "Add Mod",
                                    style = MaterialTheme.typography.bodySmall,
                                    modifier = Modifier.align(Alignment.CenterHorizontally)
                                )
                                
                                OutlinedButton(
                                    onClick = {
                                        scope.launch {
                                            modViewModel.resetLoadedState()
                                            modViewModel.loadMods(titleId)
                                        }
                                    },
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    Icon(Icons.Default.Refresh, contentDescription = "Refresh", modifier = Modifier.size(16.dp))
                                    Spacer(modifier = Modifier.width(6.dp))
                                    Text("Refresh List")
                                }
                                
                                Spacer(modifier = Modifier.height(16.dp))
                                
                                Text(
                                    text = "Info",
                                    style = MaterialTheme.typography.titleMedium,
                                    fontWeight = FontWeight.Bold
                                )
                                
                                Text(
                                    text = "• Mods are stored in game's mod directory",
                                    style = MaterialTheme.typography.bodySmall
                                )
                                Text(
                                    text = "• Enable/disable mods using the switch",
                                    style = MaterialTheme.typography.bodySmall
                                )
                                Text(
                                    text = "• Supported types: RomFs, ExeFs",
                                    style = MaterialTheme.typography.bodySmall
                                )
                            }
                        }
                    } else {
                        // 竖屏：上下布局
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(500.dp) // 固定高度，允许滚动
                                .verticalScroll(rememberScrollState())
                        ) {
                            // 操作按钮区域
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(
                                    text = "Mods: ${modViewModel.mods.size} (${modViewModel.mods.count { it.enabled }} enabled)",
                                    style = MaterialTheme.typography.bodyMedium
                                )
                                
                                Row {
                                    TextButton(
                                        onClick = { showDeleteAllDialog = true },
                                        enabled = modViewModel.mods.isNotEmpty()
                                    ) {
                                        Text("Delete All")
                                    }
                                    
                                    // 修复：用小型正方形包裹+按钮
                                    Box(
                                        modifier = Modifier.size(48.dp)
                                    ) {
                                        Surface(
                                            modifier = Modifier
                                                .fillMaxSize()
                                                .clip(RoundedCornerShape(8.dp)),
                                            color = MaterialTheme.colorScheme.primary,
                                            contentColor = MaterialTheme.colorScheme.onPrimary,
                                            shadowElevation = 4.dp
                                        ) {
                                            IconButton(
                                                onClick = {
                                                    folderPickerLauncher.launch(null)
                                                },
                                                modifier = Modifier.fillMaxSize()
                                            ) {
                                                Icon(Icons.Default.Add, contentDescription = "Add Mod")
                                            }
                                        }
                                    }
                                }
                            }
                            
                            Spacer(modifier = Modifier.height(12.dp))
                            
                            // Mod列表
                            if (modViewModel.mods.isEmpty()) {
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
                                        text = "Click + button to add a mod",
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            } else {
                                Column {
                                    modViewModel.mods.forEach { mod ->
                                        ModListItem(
                                            mod = mod,
                                            onEnabledChanged = { enabled ->
                                                scope.launch {
                                                    modViewModel.setModEnabled(titleId, mod, enabled)
                                                }
                                            },
                                            onDelete = {
                                                showDeleteDialog = mod
                                            }
                                        )
                                    }
                                }
                            }
                            
                            // 刷新按钮
                            OutlinedButton(
                                onClick = {
                                    scope.launch {
                                        modViewModel.resetLoadedState()
                                        modViewModel.loadMods(titleId)
                                    }
                                },
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(top = 16.dp)
                            ) {
                                Icon(Icons.Default.Refresh, contentDescription = "Refresh", modifier = Modifier.size(16.dp))
                                Spacer(modifier = Modifier.width(6.dp))
                                Text("Refresh List")
                            }
                        }
                    }
                },
                confirmButton = {
                    TextButton(onClick = onDismiss) {
                        Text("Close")
                    }
                }
            )

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
                                    modViewModel.deleteMod(titleId, mod)
                                    showDeleteDialog = null
                                }
                            }
                        ) {
                            Text("Delete")
                        }
                    },
                    dismissButton = {
                        TextButton(
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
                        Text("Are you sure you want to delete all ${modViewModel.mods.size} mods? This action cannot be undone.") 
                    },
                    confirmButton = {
                        Button(
                            onClick = {
                                scope.launch {
                                    modViewModel.deleteAllMods(titleId)
                                    showDeleteAllDialog = false
                                }
                            }
                        ) {
                            Text("Delete All")
                        }
                    },
                    dismissButton = {
                        TextButton(
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
                            val sourceFile = File(selectedModPath)
                            if (!sourceFile.exists() || !sourceFile.isDirectory) {
                                return@launch
                            }
                            
                            modViewModel.addMod(titleId, selectedModPath, modName)
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
        fun ModListItem(
            mod: ModModel,
            onEnabledChanged: (Boolean) -> Unit,
            onDelete: () -> Unit
        ) {
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 4.dp),
                shape = RoundedCornerShape(8.dp)
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(12.dp),
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
                            fontWeight = FontWeight.Medium
                        )
                        
                        Text(
                            text = "Type: ${mod.type.name} • ${if (mod.inExternalStorage) "External" else "Internal"}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        
                        Text(
                            text = mod.path,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    
                    Spacer(modifier = Modifier.width(12.dp))
                    
                    // 删除按钮
                    IconButton(onClick = onDelete) {
                        Icon(Icons.Filled.Delete, contentDescription = "Delete")
                    }
                }
            }
        }

        @Composable
        fun AddModDialog(
            selectedPath: String,
            onConfirm: (String) -> Unit,
            onDismiss: () -> Unit
        ) {
            var modName by remember { mutableStateOf("") }
            val folderName = File(selectedPath).name
            
            if (modName.isEmpty()) {
                modName = folderName
            }
            
            AlertDialog(
                onDismissRequest = onDismiss,
                title = { Text("Add Mod") },
                text = {
                    Column {
                        Text("Selected folder: $selectedPath")
                        Spacer(modifier = Modifier.height(12.dp))
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
                    TextButton(onClick = onDismiss) {
                        Text("Cancel")
                    }
                }
            )
        }

        private fun getFolderPathFromUri(context: Context, uri: Uri): String? {
            return try {
                val contentResolver = context.contentResolver
                val takeFlags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                contentResolver.takePersistableUriPermission(uri, takeFlags)
                
                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.LOLLIPOP) {
                    val documentId = android.provider.DocumentsContract.getTreeDocumentId(uri)
                    if (documentId.startsWith("primary:")) {
                        val path = documentId.substringAfter("primary:")
                        "/storage/emulated/0/$path"
                    } else {
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
    }
}