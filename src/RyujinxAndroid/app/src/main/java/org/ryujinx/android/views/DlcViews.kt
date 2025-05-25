package org.ryujinx.android.views

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import org.ryujinx.android.viewmodels.DlcItem
import org.ryujinx.android.viewmodels.DlcViewModel

class DlcViews {
    companion object {
        @Composable
        fun Main(
            titleId: String,
            name: String,
            openDialog: MutableState<Boolean>,
            canClose: MutableState<Boolean> = mutableStateOf(false)
        ) {
            val viewModel = remember { DlcViewModel(titleId) }
            val dlcItems = remember { SnapshotStateList<DlcItem>() }
            val refresh = remember { mutableStateOf(false) }

            // Load DLC items when viewModel or refresh changes
            if (dlcItems.isEmpty() || refresh.value) {
                dlcItems.clear()
                dlcItems.addAll(viewModel.getDlc())
                refresh.value = false
            }

            Column(modifier = Modifier.padding(16.dp)) {
                // Header with game name
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

                // DLC List Container
                Surface(
                    modifier = Modifier.padding(8.dp),
                    color = MaterialTheme.colorScheme.surfaceVariant,
                    shape = MaterialTheme.shapes.medium
                ) {
                    LazyColumn(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(400.dp)
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
                                        .weight(1f)
                                )
                                IconButton(
                                    onClick = {
                                        viewModel.remove(dlcItem)
                                        refresh.value = true
                                    }
                                ) {
                                    Icon(Icons.Filled.Delete, "Remove")
                                }
                            }
                        }
                    }
                }

                // Action Buttons
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 8.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    TextButton(
                        onClick = {
                            viewModel.add(refresh)
                            refresh.value = true
                        }
                    ) {
                        Text("Add")
                    }
                    Spacer(modifier = Modifier.width(8.dp))
                    TextButton(
                        onClick = {
                            viewModel.save(dlcItems)
                            openDialog.value = false
                            canClose.value = true
                        }
                    ) {
                        Text("Save")
                    }
                }
            }
        }
    }
}
