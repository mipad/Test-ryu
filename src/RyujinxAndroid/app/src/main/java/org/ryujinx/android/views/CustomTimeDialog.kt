package org.ryujinx.android.views

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
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
    
    // 根据年月计算最大天数
    val maxDaysInMonth = remember(year, month) {
        getMaxDaysInMonth(year, month)
    }
    
    // 确保当前天数不超过最大天数
    if (day > maxDaysInMonth) {
        day = maxDaysInMonth
    }
    
    Dialog(onDismissRequest = onDismiss) {
        Surface(
            modifier = Modifier
                .width(360.dp)
                .heightIn(max = 560.dp),
            shape = RoundedCornerShape(16.dp),
            color = MaterialTheme.colorScheme.surface,
            tonalElevation = 8.dp
        ) {
            Column(
                modifier = Modifier
                    .padding(20.dp)
                    .verticalScroll(rememberScrollState())
            ) {
                // 标题
                Text(
                    text = "Set Custom Time",
                    fontSize = 22.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(bottom = 20.dp),
                    textAlign = TextAlign.Center
                )
                
                // 时间设置表单
                Column(
                    modifier = Modifier.fillMaxWidth(),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    // 年
                    TimePickerRow(
                        label = "Year:",
                        value = year,
                        onValueChange = { year = it },
                        range = 2000..2100
                    )
                    
                    // 月
                    TimePickerRow(
                        label = "Month:",
                        value = month,
                        onValueChange = { month = it },
                        range = 1..12
                    )
                    
                    // 日
                    TimePickerRow(
                        label = "Day:",
                        value = day,
                        onValueChange = { day = it },
                        range = 1..maxDaysInMonth
                    )
                    
                    // 时
                    TimePickerRow(
                        label = "Hour:",
                        value = hour,
                        onValueChange = { hour = it },
                        range = 0..23
                    )
                    
                    // 分
                    TimePickerRow(
                        label = "Minute:",
                        value = minute,
                        onValueChange = { minute = it },
                        range = 0..59
                    )
                    
                    // 秒
                    TimePickerRow(
                        label = "Second:",
                        value = second,
                        onValueChange = { second = it },
                        range = 0..59
                    )
                }
                
                Spacer(modifier = Modifier.height(20.dp))
                
                // 显示设置的时间
                Surface(
                    shape = RoundedCornerShape(8.dp),
                    color = MaterialTheme.colorScheme.primaryContainer,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        text = "${year.toString().padStart(4, '0')}-${month.toString().padStart(2, '0')}-${day.toString().padStart(2, '0')} ${hour.toString().padStart(2, '0')}:${minute.toString().padStart(2, '0')}:${second.toString().padStart(2, '0')}",
                        fontWeight = FontWeight.Medium,
                        fontSize = 16.sp,
                        color = MaterialTheme.colorScheme.onPrimaryContainer,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(12.dp),
                        textAlign = TextAlign.Center
                    )
                }
                
                // 按钮行
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 20.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    TextButton(
                        onClick = onDismiss,
                        colors = ButtonDefaults.textButtonColors(
                            contentColor = MaterialTheme.colorScheme.onSurface
                        )
                    ) {
                        Text("Cancel", fontWeight = FontWeight.Medium)
                    }
                    Spacer(modifier = Modifier.width(12.dp))
                    Button(
                        onClick = {
                            onTimeSet(year, month, day, hour, minute, second)
                        },
                        colors = ButtonDefaults.buttonColors(
                            containerColor = MaterialTheme.colorScheme.primary
                        )
                    ) {
                        Text("Set", fontWeight = FontWeight.Medium)
                    }
                }
            }
        }
    }
}

@Composable
private fun TimePickerRow(
    label: String,
    value: Int,
    onValueChange: (Int) -> Unit,
    range: IntRange
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = label,
            modifier = Modifier.weight(1f),
            fontWeight = FontWeight.Medium,
            fontSize = 16.sp,
            color = MaterialTheme.colorScheme.onSurface
        )
        NumberPicker(
            value = value,
            onValueChange = onValueChange,
            range = range
        )
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
            modifier = Modifier.size(36.dp),
            colors = IconButtonDefaults.iconButtonColors(
                contentColor = MaterialTheme.colorScheme.primary
            )
        ) {
            Icon(
                imageVector = Icons.Filled.ArrowDropDown,
                contentDescription = "Decrease",
                modifier = Modifier.rotate(90f)
            )
        }
        
        // 使用OutlinedTextField，调整宽度确保数字显示完整
        OutlinedTextField(
            value = value.toString(),
            onValueChange = { 
                val newValue = it.toIntOrNull()
                if (newValue != null && newValue in range) {
                    onValueChange(newValue)
                }
            },
            modifier = Modifier.width(80.dp),
            singleLine = true,
            textStyle = LocalTextStyle.current.copy(
                textAlign = TextAlign.Center,
                fontSize = 16.sp
            ),
            colors = TextFieldDefaults.colors(
                focusedContainerColor = MaterialTheme.colorScheme.surface,
                unfocusedContainerColor = MaterialTheme.colorScheme.surface,
                disabledContainerColor = MaterialTheme.colorScheme.surface,
            )
        )
        
        IconButton(
            onClick = {
                if (value < range.last) {
                    onValueChange(value + 1)
                }
            },
            modifier = Modifier.size(36.dp),
            colors = IconButtonDefaults.iconButtonColors(
                contentColor = MaterialTheme.colorScheme.primary
            )
        ) {
            Icon(
                imageVector = Icons.Filled.ArrowDropDown,
                contentDescription = "Increase",
                modifier = Modifier.rotate(270f)
            )
        }
    }
}

/**
 * 根据年份和月份获取该月的最大天数
 */
private fun getMaxDaysInMonth(year: Int, month: Int): Int {
    val calendar = Calendar.getInstance()
    calendar.set(Calendar.YEAR, year)
    calendar.set(Calendar.MONTH, month - 1) // Calendar月份从0开始
    return calendar.getActualMaximum(Calendar.DAY_OF_MONTH)
}
