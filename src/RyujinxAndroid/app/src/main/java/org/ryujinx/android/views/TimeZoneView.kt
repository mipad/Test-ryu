package org.ryujinx.android.views

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import org.ryujinx.android.RyujinxNative

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TimeZoneView(onBack: () -> Unit, onTimeZoneSelected: (String) -> Unit) {
    val timeZones = remember { mutableStateOf(RyujinxNative.getTimeZoneList()) }
    val currentTimeZone = remember { mutableStateOf(RyujinxNative.getCurrentTimeZone()) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Select Time Zone") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                }
            )
        }
    ) { paddingValues ->
        Column(modifier = Modifier.padding(paddingValues)) {
            LazyColumn(modifier = Modifier.fillMaxSize()) {
                items(timeZones.value) { timeZone ->
                    Text(
                        text = timeZone,
                        modifier = Modifier
                            .clickable {
                                RyujinxNative.setTimeZone(timeZone)
                                onTimeZoneSelected(timeZone)
                                onBack()
                            }
                            .padding(16.dp)
                    )
                }
            }
        }
    }
}
