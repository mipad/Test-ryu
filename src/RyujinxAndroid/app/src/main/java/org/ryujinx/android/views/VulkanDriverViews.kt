package org.kenjinx.android.views

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import org.kenjinx.android.MainActivity
import org.kenjinx.android.viewmodels.VulkanDriverViewModel

class VulkanDriverViews {
    companion object {
        @Composable
        fun Main(activity: MainActivity, openDialog: MutableState<Boolean>) {
            var driverViewModel = VulkanDriverViewModel(activity)
            var isChanged = remember { mutableStateOf(false) }
            var refresh = remember { mutableStateOf(false) }
            var drivers = driverViewModel.getAvailableDrivers()
            var selectedDriver = remember { mutableStateOf(0) }

            if (refresh.value) {
                isChanged.value = true
                refresh.value = false
            }

            if (!isChanged.value) {
                selectedDriver.value =
                    drivers.indexOfFirst { it.driverPath == driverViewModel.selected } + 1
                isChanged.value = true
            }
            Column(modifier = Modifier.padding(16.dp)) {
                Row(modifier = Modifier.align(Alignment.Start)) {
                    IconButton(
                        onClick = {
                            driverViewModel.removeSelected()
                            refresh.value = true
                        }
                    ) {
                        Icon(
                            Icons.Filled.Delete,
                            contentDescription = "Remove"
                        )
                    }
                    IconButton(
                        onClick = {
                            driverViewModel.add(refresh)
                            refresh.value = true
                            selectedDriver.value = 0
                            driverViewModel.selected = ""
                        }
                    ) {
                        Icon(
                            Icons.Filled.Add,
                            contentDescription = "Add"
                        )
                    }
                    IconButton(
                        onClick = {
                            driverViewModel.saveSelected()
                            openDialog.value = false
                        },
                    ) {
                        Icon(
                            Icons.Filled.Edit,
                            contentDescription = "Save"
                        )
                    }
                }
                Spacer(modifier = Modifier.height(8.dp))
                Column {
                    Text(text = "Driver List", textAlign = TextAlign.Center)
                    Box(
                        modifier = Modifier
                            .padding(8.dp)
                            .height(150.dp)
                    ) {
                        val scrollState = rememberScrollState()

                        val needsScrollbar by remember {
                            derivedStateOf {
                                scrollState.maxValue > 0
                            }
                        }
                        Surface(
                            modifier = Modifier
                                .fillMaxWidth()
                                .fillMaxHeight()
                                .padding(end = if (needsScrollbar) 16.dp else 0.dp),
                            color = MaterialTheme.colorScheme.surfaceVariant,
                            shape = MaterialTheme.shapes.medium
                        ) {
                            Column(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .verticalScroll(
                                        scrollState
                                    )
                                    .padding(8.dp)
                            ) {
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(vertical = 4.dp)
                                        .clickable {
                                            selectedDriver.value = 0
                                            isChanged.value = true
                                            driverViewModel.selected = ""
                                        },
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    RadioButton(
                                        selected = selectedDriver.value == 0 || driverViewModel.selected.isEmpty(),
                                        onClick = {
                                            selectedDriver.value = 0
                                            isChanged.value = true
                                            driverViewModel.selected = ""
                                        }
                                    )
                                    Text(
                                        text = "Default",
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(start = 8.dp)
                                    )
                                }
                                var driverIndex = 1
                                for (driver in drivers) {
                                    var ind = driverIndex
                                    Row(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(
                                                vertical = 4.dp
                                            )
                                            .clickable {
                                                selectedDriver.value = ind
                                                isChanged.value = true
                                                driverViewModel.selected = driver.driverPath
                                            },
                                        verticalAlignment = Alignment.CenterVertically
                                    ) {
                                        RadioButton(
                                            selected = selectedDriver.value == ind,
                                            onClick = {
                                                selectedDriver.value = ind
                                                isChanged.value = true
                                                driverViewModel.selected = driver.driverPath
                                            }
                                        )
                                        Column(
                                            modifier = Modifier
                                                .weight(1f)
                                                .padding(
                                                    start = 8.dp
                                                )
                                        ) {
                                            Text(text = driver.libraryName)
                                            Text(text = driver.driverVersion)
                                            Text(text = driver.description)
                                        }
                                    }
                                    driverIndex++
                                }

                                Spacer(
                                    modifier = Modifier.height(
                                        8.dp
                                    )
                                )
                            }
                        }

                        if (needsScrollbar) {
                            Box(
                                modifier = Modifier
                                    .align(Alignment.CenterEnd)
                                    .width(6.dp)
                                    .fillMaxHeight()
                                    .background(Color.LightGray.copy(alpha = 0.3f))
                            ) {
                                val thumbRatio = 150.dp.value / (150.dp.value + scrollState.maxValue)
                                val thumbSize = (150.dp.value * thumbRatio).coerceAtLeast(40f)
                                val scrollRatio = scrollState.value.toFloat() / scrollState.maxValue.toFloat()
                                val maxOffset = 150.dp.value - (thumbSize * 0.7f)
                                val thumbOffset = maxOffset * scrollRatio

                                Box(
                                    modifier = Modifier
                                        .width(6.dp)
                                        .height((thumbSize * 0.7f).dp)
                                        .offset(y = thumbOffset.dp)
                                        .background(
                                            color = MaterialTheme.colorScheme.tertiary,
                                            shape = MaterialTheme.shapes.small
                                        )
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
