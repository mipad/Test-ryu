package org.ryujinx.android.views

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.InsertDriveFile
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import org.ryujinx.android.viewmodels.ModItem
import org.ryujinx.android.viewmodels.ModViewModel

@Composable
fun ModView(viewModel: ModViewModel, titleId: String) {
    val modItems = remember { mutableStateListOf<ModItem>() }
    val canClose = remember { mutableStateOf(false) }
    
    LaunchedEffect(Unit) {
        viewModel.setModItems(modItems, canClose)
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
            
            IconButton(onClick = { viewModel.add() }) {
                Icon(Icons.Default.Add, contentDescription = "Add Mod")
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
                imageVector = if (modItem.isDirectory) Icons.Default.Folder else Icons.Default.InsertDriveFile,
                contentDescription = if (modItem.isDirectory) "Folder" else "File"
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
                Icon(Icons.Default.Delete, contentDescription = "Delete")
            }
        }
    }
}

// File browser dialog (simplified version)
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FileBrowserDialog(
    currentPath: String,
    onDismiss: () -> Unit,
    onFileSelected: (String) -> Unit
) {
    val files = remember { mutableStateListOf<FileItem>() }
    
    LaunchedEffect(currentPath) {
        // Load files from current path
        val directory = File(currentPath)
        if (directory.exists() && directory.isDirectory) {
            files.clear()
            directory.listFiles()?.forEach { file ->
                files.add(FileItem(
                    name = file.name,
                    path = file.absolutePath,
                    isDirectory = file.isDirectory
                ))
            }
        }
    }
    
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Select File or Folder") },
        text = {
            Column {
                // Breadcrumb navigation
                // File list
                LazyColumn {
                    items(files) { file ->
                        // File item with click handler
                    }
                }
            }
        },
        confirmButton = {
            Button(onClick = { /* Handle selection */ }) {
                Text("Select")
            }
        },
        dismissButton = {
            Button(onClick = onDismiss) {
                Text("Cancel")
            }
        }
    )
}

data class FileItem(
    val name: String,
    val path: String,
    val isDirectory: Boolean
)
