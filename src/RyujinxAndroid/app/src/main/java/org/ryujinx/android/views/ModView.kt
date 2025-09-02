package org.ryujinx.android.views

import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import org.ryujinx.android.viewmodels.ModItem
import org.ryujinx.android.viewmodels.ModViewModel
import java.io.File

@Composable
fun ModView(viewModel: ModViewModel, titleId: String) {
    val modItems = remember { mutableStateListOf<ModItem>() }
    val canClose = remember { mutableStateOf(false) }
    var showFileBrowser by remember { mutableStateOf(false) }
    var currentDirectory by remember { mutableStateOf(File("/")) }
    var installFolderMode by remember { mutableStateOf(false) }
    
    LaunchedEffect(Unit) {
        viewModel.setModItems(modItems, canClose)
    }
    
    if (showFileBrowser) {
        FileBrowserDialog(
            currentPath = currentDirectory.absolutePath,
            onDismiss = { showFileBrowser = false },
            onFileSelected = { selectedPath ->
                if (installFolderMode) {
                    // 处理文件夹安装
                    val folder = File(selectedPath)
                    if (folder.exists() && folder.isDirectory) {
                        viewModel.addMod(selectedPath)
                    }
                } else {
                    // 处理压缩包安装
                    viewModel.addMod(selectedPath)
                }
                showFileBrowser = false
                installFolderMode = false
            },
            onDirectoryChanged = { newDirectory ->
                currentDirectory = File(newDirectory)
            },
            isFolderSelection = installFolderMode
        )
    }
    
    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        // Header with title and add button
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Text(
                text = "Mods for $titleId",
                style = MaterialTheme.typography.headlineSmall
            )
            
            // 添加两个按钮：压缩包安装和文件夹安装
            Row {
                // 压缩包安装按钮
                IconButton(onClick = { 
                    installFolderMode = false
                    showFileBrowser = true 
                }) {
                    Icon(Icons.Filled.Archive, contentDescription = "Install from Archive")
                }
                
                // 文件夹安装按钮
                IconButton(onClick = { 
                    installFolderMode = true
                    showFileBrowser = true 
                }) {
                    Icon(Icons.Filled.Folder, contentDescription = "Install from Folder")
                }
            }
        }
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // Mod list with enable/disable checkboxes
        if (modItems.isEmpty()) {
            Box(
                modifier = Modifier.fillMaxSize(),
                contentAlignment = Alignment.Center
            ) {
                Text("No mods installed. Click + to add one.")
            }
        } else {
            LazyColumn(modifier = Modifier.fillMaxSize()) {
                items(modItems) { modItem ->
                    ModListItem(
                        modItem = modItem,
                        onToggle = { enabled ->
                            // 处理启用/禁用状态切换
                            viewModel.toggleMod(modItem, enabled)
                        },
                        onDelete = { viewModel.remove(modItem) }
                    )
                }
            }
        }
    }
}

@Composable
fun ModListItem(modItem: ModItem, onToggle: (Boolean) -> Unit, onDelete: () -> Unit) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(8.dp)
    ) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Checkbox(
                checked = modItem.isEnabled.value,
                onCheckedChange = onToggle
            )
            
            Spacer(modifier = Modifier.width(16.dp))
            
            Icon(
                imageVector = if (modItem.isDirectory) Icons.Filled.Folder else Icons.Filled.InsertDriveFile,
                contentDescription = if (modItem.isDirectory) "Folder" else "File",
                modifier = Modifier.size(24.dp)
            )
            
            Spacer(modifier = Modifier.width(16.dp))
            
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = modItem.name,
                    style = MaterialTheme.typography.bodyLarge,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                
                Text(
                    text = modItem.size,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            
            IconButton(onClick = onDelete) {
                Icon(Icons.Filled.Delete, contentDescription = "Delete")
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FileBrowserDialog(
    currentPath: String,
    onDismiss: () -> Unit,
    onFileSelected: (String) -> Unit,
    onDirectoryChanged: (String) -> Unit,
    isFolderSelection: Boolean = false
) {
    val files = remember { mutableStateListOf<FileItem>() }
    
    LaunchedEffect(currentPath) {
        // Load files from current path
        val directory = File(currentPath)
        if (directory.exists() && directory.isDirectory) {
            files.clear()
            // Add parent directory option if not at root
            if (directory.parent != null) {
                files.add(FileItem(
                    name = "..",
                    path = directory.parent!!,
                    isDirectory = true
                ))
            }
            
            directory.listFiles()?.sortedBy { it.name }?.forEach { file ->
                files.add(FileItem(
                    name = file.name,
                    path = file.absolutePath,
                    isDirectory = file.isDirectory,
                    size = if (file.isDirectory) "" else "${file.length() / 1024} KB"
                ))
            }
        }
    }
    
    Dialog(onDismissRequest = onDismiss) {
        Surface(
            shape = MaterialTheme.shapes.large,
            modifier = Modifier
                .fillMaxWidth()
                .height(500.dp)
        ) {
            Column(modifier = Modifier.padding(16.dp)) {
                Text(
                    text = if (isFolderSelection) "Select Mod Folder" else "Select Mod File or Folder",
                    style = MaterialTheme.typography.headlineSmall,
                    modifier = Modifier.padding(bottom = 16.dp)
                )
                
                Text(
                    text = "Current: $currentPath",
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(bottom = 8.dp)
                )
                
                Divider()
                
                LazyColumn(modifier = Modifier.weight(1f)) {
                    items(files) { file ->
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clickable {
                                    if (file.isDirectory) {
                                        if (isFolderSelection && file.name != "..") {
                                            // 如果是文件夹选择模式，选择文件夹
                                            onFileSelected(file.path)
                                        } else {
                                            onDirectoryChanged(file.path)
                                        }
                                    } else {
                                        // 只允许选择特定文件类型
                                        if (file.path.endsWith(".zip") || file.path.endsWith(".rar") || 
                                            file.path.endsWith(".7z") || file.isDirectory) {
                                            onFileSelected(file.path)
                                        }
                                    }
                                }
                                .padding(16.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Icon(
                                imageVector = if (file.isDirectory) Icons.Filled.Folder else Icons.Filled.InsertDriveFile,
                                contentDescription = if (file.isDirectory) "Folder" else "File",
                                modifier = Modifier.size(24.dp)
                            )
                            
                            Spacer(modifier = Modifier.width(16.dp))
                            
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                    text = file.name,
                                    style = MaterialTheme.typography.bodyLarge,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                                
                                if (file.size.isNotEmpty()) {
                                    Text(
                                        text = file.size,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            }
                        }
                        
                        Divider()
                    }
                }
                
                Spacer(modifier = Modifier.height(16.dp))
                
                Button(
                    onClick = onDismiss,
                    modifier = Modifier.align(Alignment.End)
                ) {
                    Text("Cancel")
                }
            }
        }
    }
}

data class FileItem(
    val name: String,
    val path: String,
    val isDirectory: Boolean,
    val size: String = ""
)
