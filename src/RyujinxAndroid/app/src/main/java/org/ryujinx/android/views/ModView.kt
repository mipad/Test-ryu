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
import org.ryujinx.android.viewmodels.ModItem
import org.ryujinx.android.viewmodels.ModViewModel
import java.io.File

@Composable
fun ModView(viewModel: ModViewModel, titleId: String) {
    val modItems = remember { mutableStateListOf<ModItem>() }
    val canClose = remember { mutableStateOf(false) }
    var showFileBrowser by remember { mutableStateOf(false) }
    var currentDirectory by remember { mutableStateOf(File("/")) }
    
    LaunchedEffect(Unit) {
        viewModel.setModItems(modItems, canClose)
    }
    
    if (showFileBrowser) {
        FileBrowserDialog(
            currentPath = currentDirectory.absolutePath,
            onDismiss = { showFileBrowser = false },
            onFileSelected = { selectedPath ->
                viewModel.addMod(selectedPath)
                showFileBrowser = false
            },
            onDirectoryChanged = { newDirectory ->
                currentDirectory = File(newDirectory)
            }
        )
    }
    
    Column(modifier = Modifier.fillMaxSize()) {
        // Header with title and add button
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Text(
                text = "Mods for $titleId",
                style = MaterialTheme.typography.headlineSmall
            )
            
            IconButton(onClick = { showFileBrowser = true }) {
                Icon(Icons.Filled.Add, contentDescription = "Add Mod")
            }
        }
        
        // Mod list
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
                        onDelete = { viewModel.remove(modItem) }
                    )
                }
            }
        }
    }
}

@Composable
fun ModListItem(modItem: ModItem, onDelete: () -> Unit) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(8.dp)
    ) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
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
    onDirectoryChanged: (String) -> Unit
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
    
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Select Mod File or Folder") },
        text = {
            Column(modifier = Modifier.height(400.dp)) {
                Text(
                    text = "Current: $currentPath",
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(bottom = 8.dp)
                )
                
                Divider()
                
                LazyColumn(modifier = Modifier.fillMaxWidth()) {
                    items(files) { file ->
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clickable {
                                    if (file.isDirectory) {
                                        onDirectoryChanged(file.path)
                                    } else {
                                        // Only allow selecting certain file types
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
            }
        },
        confirmButton = {
            Button(onClick = onDismiss) {
                Text("Cancel")
            }
        }
    )
}

data class FileItem(
    val name: String,
    val path: String,
    val isDirectory: Boolean,
    val size: String = ""
)
