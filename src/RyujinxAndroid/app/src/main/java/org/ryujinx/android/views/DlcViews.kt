package org.ryujinx.android.views

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import org.ryujinx.android.viewmodels.DlcItem
import org.ryujinx.android.viewmodels.DlcViewModel

class DlcViews {
    companion object {
        @Composable
        fun Main(titleId: String, name: String, openDialog: MutableState<Boolean>, canClose: MutableState<Boolean>) {
            val viewModel = DlcViewModel(titleId)
            val dlcItems = remember { SnapshotStateList<DlcItem>() }
            val configuration = LocalConfiguration.current
            val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
            
            // 根据屏幕方向调整高度
            val contentHeight = if (isLandscape) 250.dp else 400.dp

            Column(modifier = Modifier.padding(16.dp)) {
                // 标题区域 
                Column {
                    Row(
                        modifier = Modifier
                            .padding(8.dp)
                            .fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text(
                            text = "DLC for $name",
                            textAlign = TextAlign.Center,
                            modifier = Modifier.align(Alignment.CenterVertically)
                        )
                    }
                    
                    // DLC列表区域 
                    Surface(
                        modifier = Modifier.padding(8.dp),
                        color = MaterialTheme.colorScheme.surfaceVariant,
                        shape = MaterialTheme.shapes.medium
                    ) {
                        viewModel.setDlcItems(dlcItems, canClose)
                        
                        LazyColumn(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(contentHeight) // 使用动态高度
                        ) {
                            items(dlcItems) { dlcItem ->
                                Row(
                                    modifier = Modifier
                                        .padding(8.dp)
                                        .fillMaxWidth()
                                ) {
                                    Checkbox(
                                        checked = dlcItem.isEnabled.value,
                                        onCheckedChange = { dlcItem.isEnabled.value = it }
                                    )
                                    Text(
                                        text = dlcItem.name,
                                        modifier = Modifier
                                            .align(Alignment.CenterVertically)
                                            .wrapContentWidth(Alignment.Start)
                                            .fillMaxWidth(0.9f)
                                    )
                                    IconButton(
                                        onClick = {
                                            viewModel.remove(dlcItem)
                                        }
                                    ) {
                                        Icon(
                                            Icons.Filled.Delete,
                                            contentDescription = "remove"
                                        )
                                    }
                                }
                            }
                        }
                    }
                }
                
                Spacer(modifier = Modifier.height(8.dp))
                
                // 底部按钮区域 
                Row(modifier = Modifier.align(Alignment.End)) {
                    // 添加按钮 
                    IconButton(
                        modifier = Modifier.padding(4.dp),
                        onClick = {
                            viewModel.add()
                        }
                    ) {
                        Icon(
                            Icons.Filled.Add,
                            contentDescription = "Add"
                        )
                    }
                    
                    // 保存按钮 
                    TextButton(
                        modifier = Modifier.padding(4.dp),
                        onClick = {
                            canClose.value = true
                            viewModel.save(openDialog)
                        }
                    ) {
                        Text("Save")
                    }
                }
            }
        }
    }
}
