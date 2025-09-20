package org.ryujinx.android.views

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import java.util.Calendar

@Composable
fun CustomTimeDialog(
    currentYear: Int,
    currentMonth: Int,
    currentDay: Int,
    currentHour: Int,
    currentMinute: Int,
    currentSecond: Int,
    onDismiss: () -> Unit,
    onTimeSet: (Int, Int, Int, Int, Int, Int) -> Unit
) {
    var year by remember { mutableStateOf(currentYear) }
    var month by remember { mutableStateOf(currentMonth) }
    var day by remember { mutableStateOf(currentDay) }
    var hour by remember { mutableStateOf(currentHour) }
    var minute by remember { mutableStateOf(currentMinute) }
    var second by remember { mutableStateOf(currentSecond) }
    
    Dialog(onDismissRequest = onDismiss) {
        Surface(
            modifier = Modifier
                .width(340.dp)
                .wrapContentHeight(),
            shape = RoundedCornerShape(8.dp),
            color = MaterialTheme.colorScheme.surface
        ) {
            Column(
                modifier = Modifier.padding(16.dp)
            ) {
                // 标题
                Text(
                    text = "Set Custom Time",
                    fontSize = 20.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.padding(bottom = 16.dp)
                )
                
                // 时间设置表单
                Column(
                    modifier = Modifier.fillMaxWidth(),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    // 年
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("Year:", modifier = Modifier.weight(1f))
                        NumberPicker(
                            value = year,
                            onValueChange = { year = it },
                            range = 2000..2100
                        )
                    }
                    
                    // 月
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("Month:", modifier = Modifier.weight(1f))
                        NumberPicker(
                            value = month,
                            onValueChange = { month = it },
                            range = 1..12
                        )
                    }
                    
                    // 日
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("Day:", modifier = Modifier.weight(1f))
                        NumberPicker(
                            value = day,
                            onValueChange = { day = it },
                            range = 1..31
                        )
                    }
                    
                    // 时
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("Hour:", modifier = Modifier.weight(1f))
                        NumberPicker(
                            value = hour,
                            onValueChange = { hour = it },
                            range = 0..23
                        )
                    }
                    
                    // 分
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("Minute:", modifier = Modifier.weight(1f))
                        NumberPicker(
                            value = minute,
                            onValueChange = { minute = it },
                            range = 0..59
                        )
                    }
                    
                    // 秒
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("Second:", modifier = Modifier.weight(1f))
                        NumberPicker(
                            value = second,
                            onValueChange = { second = it },
                            range = 0..59
                        )
                    }
                }
                
                Spacer(modifier = Modifier.height(16.dp))
                
                // 显示设置的时间
                Text(
                    text = "Set time: ${year.toString().padStart(4, '0')}-${month.toString().padStart(2, '0')}-${day.toString().padStart(2, '0')} ${hour.toString().padStart(2, '0')}:${minute.toString().padStart(2, '0')}:${second.toString().padStart(2, '0')}",
                    fontWeight = FontWeight.Bold
                )
                
                // 按钮行
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 16.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    TextButton(onClick = onDismiss) {
                        Text("Cancel")
                    }
                    Spacer(modifier = Modifier.width(8.dp))
                    Button(
                        onClick = {
                            onTimeSet(year, month, day, hour, minute, second)
                        }
                    ) {
                        Text("Set")
                    }
                }
            }
        }
    }
}

@Composable
fun NumberPicker(
    value: Int,
    onValueChange: (Int) -> Unit,
    range: IntRange
) {
    Row(
        verticalAlignment = Alignment.CenterVertically
    ) {
        IconButton(
            onClick = {
                if (value > range.first) {
                    onValueChange(value - 1)
                }
            },
            modifier = Modifier.size(32.dp)
        ) {
            Icon(
                imageVector = Icons.Filled.ArrowDropDown,
                contentDescription = "Decrease",
                modifier = Modifier.rotate(90f)
            )
        }
        
        // 使用OutlinedTextField替代Text，允许用户直接输入
        OutlinedTextField(
            value = value.toString(),
            onValueChange = { 
                val newValue = it.toIntOrNull()
                if (newValue != null && newValue in range) {
                    onValueChange(newValue)
                }
            },
            modifier = Modifier.width(60.dp),
            singleLine = true
        )
        
        IconButton(
            onClick = {
                if (value < range.last) {
                    onValueChange(value + 1)
                }
            },
            modifier = Modifier.size(32.dp)
        ) {
            Icon(
                imageVector = Icons.Filled.ArrowDropDown,
                contentDescription = "Increase",
                modifier = Modifier.rotate(270f)
            )
        }
    }
}
