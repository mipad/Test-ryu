// HomeViews.kt
package org.ryujinx.android.views 

import android.content.res.Configuration
import android.content.res.Resources
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.basicMarquee
import androidx.compose.foundation.border
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SearchBar
import androidx.compose.material3.SearchBarDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
import androidx.navigation.NavHostController
import com.anggrayudi.storage.extension.launchOnUiThread
import org.ryujinx.android.R
import org.ryujinx.android.viewmodels.FileType
import org.ryujinx.android.viewmodels.GameModel
import org.ryujinx.android.viewmodels.HomeViewModel
import org.ryujinx.android.viewmodels.QuickSettings
import java.util.Base64
import java.util.Locale
import kotlin.concurrent.thread
import kotlin.math.roundToInt
import kotlinx.coroutines.launch
import androidx.compose.foundation.clickable
import android.content.Context
import androidx.compose.ui.platform.LocalContext
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.OutlinedTextField
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.foundation.gestures.detectTapGestures
import android.content.Intent
import android.provider.MediaStore
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.statusBarsPadding
import android.util.Log
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.unit.DpOffset
import androidx.compose.foundation.background
import java.io.File
import java.io.FileOutputStream
import androidx.compose.runtime.rememberCoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import androidx.compose.foundation.Canvas

class HomeViews {
    companion object {
        const val ListImageSize = 150
        const val GridImageSize = 300

        @Composable
        fun NotAvailableIcon(modifier: Modifier = Modifier) {
            Icon(
                Icons.Filled.Add,
                contentDescription = "N/A",
                modifier = modifier
            )
        }

        @Composable
        fun NROIcon(modifier: Modifier = Modifier) {
            Image(
                painter = painterResource(id = R.drawable.icon_nro),
                contentDescription = "NRO",
                modifier = modifier
            )
        }

        @OptIn(ExperimentalFoundationApi::class)
        @Composable
        fun ListGameItem(
            gameModel: GameModel,
            viewModel: HomeViewModel,
            showAppActions: MutableState<Boolean>,
            showLoading: MutableState<Boolean>,
            selectedModel: MutableState<GameModel?>,
            showError: MutableState<String>
        ) {
            remember {
                selectedModel
            }
            val isSelected = selectedModel.value == gameModel

            val decoder = Base64.getDecoder()
            Surface(
                shape = MaterialTheme.shapes.medium,
                color = MaterialTheme.colorScheme.surface,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(8.dp)
                    .combinedClickable(
                        onClick = {
                            if (viewModel.mainViewModel?.selected != null) {
                                showAppActions.value = false
                                viewModel.mainViewModel?.apply {
                                    selected = null
                                }
                                selectedModel.value = null
                            } else if (gameModel.titleId.isNullOrEmpty() || gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro) {
                                thread {
                                    showLoading.value = true
                                    val success =
                                        viewModel.mainViewModel?.loadGame(gameModel) ?: 0
                                    if (success == 1) {
                                        launchOnUiThread {
                                            viewModel.mainViewModel?.navigateToGame()
                                        }
                                    } else {
                                        if (success == -2)
                                            showError.value =
                                                "Error loading update. Please re-add update file"
                                        gameModel.close()
                                    }
                                    showLoading.value = false
                                }
                            }
                        },
                        onLongClick = {
                            viewModel.mainViewModel?.selected = gameModel
                            showAppActions.value = true
                            selectedModel.value = gameModel
                        }
                    )
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(8.dp),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Row {
                        if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                            Box(
                                modifier = if (isSelected) {
                                    Modifier
                                        .padding(end = 8.dp)
                                        .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.5f), CircleShape)
                                } else {
                                    Modifier.padding(end = 8.dp)
                                }
                            ) {
                                if (gameModel.icon?.isNotEmpty() == true) {
                                    val pic = decoder.decode(gameModel.icon)
                                    val size =
                                        ListImageSize / Resources.getSystem().displayMetrics.density
                                    Image(
                                        bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                            .asImageBitmap(),
                                        contentDescription = gameModel.getDisplayName() + " icon",
                                        modifier = Modifier
                                            .width(size.roundToInt().dp)
                                            .height(size.roundToInt().dp)
                                    )
                                } else if (gameModel.type == FileType.Nro)
                                    NROIcon()
                                else NotAvailableIcon()
                            }
                        } else NotAvailableIcon()
                        Column {
                            Text(text = gameModel.getDisplayName())
                            Text(text = gameModel.developer ?: "")
                            Text(text = gameModel.titleId ?: "")
                        }
                    }
                    Column {
                        Text(text = gameModel.version ?: "")
                        Text(text = String.format("%.3f", gameModel.fileSize))
                    }
                }
            }
        }

        @OptIn(ExperimentalFoundationApi::class)
        @Composable
        fun GridGameItem(
            gameModel: GameModel,
            viewModel: HomeViewModel,
            showAppActions: MutableState<Boolean>,
            showLoading: MutableState<Boolean>,
            selectedModel: MutableState<GameModel?>,
            showError: MutableState<String>
        ) {
            remember {
                selectedModel
            }
            val isSelected = selectedModel.value == gameModel

            val decoder = Base64.getDecoder()
            Surface(
                shape = MaterialTheme.shapes.medium,
                color = MaterialTheme.colorScheme.surface,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(4.dp)
                    .combinedClickable(
                        onClick = {
                            if (viewModel.mainViewModel?.selected != null) {
                                showAppActions.value = false
                                viewModel.mainViewModel?.apply {
                                    selected = null
                                }
                                selectedModel.value = null
                            } else if (gameModel.titleId.isNullOrEmpty() || gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro) {
                                thread {
                                    showLoading.value = true
                                    val success =
                                        viewModel.mainViewModel?.loadGame(gameModel) ?: 0
                                    if (success == 1) {
                                        launchOnUiThread {
                                            viewModel.mainViewModel?.navigateToGame()
                                        }
                                    } else {
                                        if (success == -2)
                                            showError.value =
                                                "Error loading update. Please re-add update file"
                                        gameModel.close()
                                    }
                                    showLoading.value = false
                                }
                            }
                        },
                        onLongClick = {
                            viewModel.mainViewModel?.selected = gameModel
                            showAppActions.value = true
                            selectedModel.value = gameModel
                        }
                    )
            ) {
                Column(
                    modifier = Modifier
                        .padding(4.dp)
                        .fillMaxWidth(),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                        Box(
                            modifier = if (isSelected) {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                                    .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.5f), RoundedCornerShape(16.dp))
                            } else {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                            }
                        ) {
                            if (gameModel.icon?.isNotEmpty() == true) {
                                val pic = decoder.decode(gameModel.icon)
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(16.dp))
                                ) {
                                    Image(
                                        bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                            .asImageBitmap(),
                                        contentDescription = gameModel.getDisplayName() + " icon",
                                        contentScale = ContentScale.Crop,
                                        modifier = Modifier.fillMaxSize()
                                    )
                                }
                            } else if (gameModel.type == FileType.Nro) {
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(16.dp))
                                ) {
                                    NROIcon(
                                        modifier = Modifier
                                            .fillMaxSize(0.8f)
                                            .align(Alignment.Center)
                                    )
                                }
                            } else {
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(16.dp))
                                ) {
                                    NotAvailableIcon(
                                        modifier = Modifier
                                        .fillMaxSize(0.8f)
                                        .align(Alignment.Center)
                                    )
                                }
                            }
                        }
                    } else {
                        Box(
                            modifier = if (isSelected) {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                                    .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.5f), RoundedCornerShape(16.dp))
                            } else {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                            }
                        ) {
                            NotAvailableIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.8f)
                                    .align(Alignment.Center)
                            )
                        }
                    }
                    Text(
                        text = gameModel.getDisplayName(),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier
                            .padding(vertical = 4.dp)
                            .basicMarquee()
                    )
                }
            }
        }

        @OptIn(ExperimentalFoundationApi::class)
        @Composable
        fun LandscapeGameCarouselItem(
            gameModel: GameModel?,
            viewModel: HomeViewModel,
            showAppActions: MutableState<Boolean>,
            showLoading: MutableState<Boolean>,
            selectedModel: MutableState<GameModel?>,
            showError: MutableState<String>,
            isCentered: Boolean = false,
            onItemClick: () -> Unit = {}
        ) {
            if (gameModel == null) {
                // 空项目
                Box(
                    modifier = Modifier
                        .size(100.dp)
                        .clip(RoundedCornerShape(16.dp))
                ) {
                    // 空项目不显示任何内容
                }
                return
            }

            remember {
                selectedModel
            }
            val isSelected = selectedModel.value == gameModel
            val decoder = Base64.getDecoder()
            
            // 根据主题确定边框颜色
            val backgroundColor = MaterialTheme.colorScheme.background
            val luminance = 0.299 * backgroundColor.red + 0.587 * backgroundColor.green + 0.114 * backgroundColor.blue
            val borderColor = if (luminance > 0.5) Color.Black else Color.White

            if (isCentered) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth(0.6f)
                        .aspectRatio(1.3f)
                        .offset(y = (-23).dp)
                        .combinedClickable(
                            onClick = {
                                if (viewModel.mainViewModel?.selected != null) {
                                    showAppActions.value = false
                                    viewModel.mainViewModel?.apply {
                                        selected = null
                                    }
                                    selectedModel.value = null
                                } else if (gameModel.titleId.isNullOrEmpty() || gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro) {
                                    thread {
                                        showLoading.value = true
                                        val success =
                                            viewModel.mainViewModel?.loadGame(gameModel) ?: 0
                                        if (success == 1) {
                                            launchOnUiThread {
                                                viewModel.mainViewModel?.navigateToGame()
                                            }
                                        } else {
                                            if (success == -2)
                                                showError.value =
                                                    "Error loading update. Please re-add update file"
                                            gameModel.close()
                                        }
                                        showLoading.value = false
                                    }
                                }
                            },
                            onLongClick = {
                                viewModel.mainViewModel?.selected = gameModel
                                showAppActions.value = true
                                selectedModel.value = gameModel
                            }
                        )
                ) {
                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .border(1.dp, borderColor, RoundedCornerShape(12.dp))
                            .then(
                                if (isSelected) {
                                    Modifier.border(
                                        2.dp,
                                        MaterialTheme.colorScheme.primary.copy(alpha = 0.5f),
                                        RoundedCornerShape(12.dp)
                                    )
                                } else {
                                    Modifier
                                }
                            )
                    ) {
                        if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                            if (gameModel.icon?.isNotEmpty() == true) {
                                val pic = decoder.decode(gameModel.icon)
                                Image(
                                    bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                        .asImageBitmap(),
                                    contentDescription = gameModel.getDisplayName() + " icon",
                                    contentScale = ContentScale.FillBounds,
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(12.dp))
                                )
                            } else if (gameModel.type == FileType.Nro) {
                                NROIcon(
                                    modifier = Modifier
                                        .fillMaxSize(0.8f)
                                        .align(Alignment.Center)
                                )
                            } else {
                                NotAvailableIcon(
                                    modifier = Modifier
                                        .fillMaxSize(0.8f)
                                        .align(Alignment.Center)
                                )
                            }
                        } else {
                            NotAvailableIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.8f)
                                    .align(Alignment.Center)
                            )
                        }
                    }
                }
            } else {
                Box(
                    modifier = Modifier
                        .size(80.dp)
                        .clip(RoundedCornerShape(12.dp))
                        .clickable(onClick = onItemClick)
                ) {
                    if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                        if (gameModel.icon?.isNotEmpty() == true) {
                            val pic = decoder.decode(gameModel.icon)
                            Image(
                                bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                    .asImageBitmap(),
                                contentDescription = gameModel.getDisplayName() + " icon",
                                contentScale = ContentScale.Crop,
                                modifier = Modifier.fillMaxSize()
                            )
                        } else if (gameModel.type == FileType.Nro) {
                            NROIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.8f)
                                    .align(Alignment.Center)
                            )
                        } else {
                            NotAvailableIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.8f)
                                    .align(Alignment.Center)
                            )
                        }
                    } else {
                        NotAvailableIcon(
                            modifier = Modifier
                                .fillMaxSize(0.8f)
                                .align(Alignment.Center)
                        )
                    }
                }
            }
        }

        @OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
        @Composable
        fun Home(
            viewModel: HomeViewModel = HomeViewModel(),
            navController: NavHostController? = null,
            isPreview: Boolean = false
        ) {
            viewModel.ensureReloadIfNecessary()
            val showAppActions = remember { mutableStateOf(false) }
            val showLoading = remember { mutableStateOf(false) }
            val openTitleUpdateDialog = remember { mutableStateOf(false) }
            val canClose = remember { mutableStateOf(true) }
            val openDlcDialog = remember { mutableStateOf(false) }
            var openAppBarExtra by remember { mutableStateOf(false) }
            val showError = remember {
                mutableStateOf("")
            }

            val selectedModel = remember {
                mutableStateOf(viewModel.mainViewModel?.selected)
            }
            var query by remember {
                mutableStateOf("")
            }
            var refreshUser by remember {
                mutableStateOf(true)
            }

            var isFabVisible by remember {
                mutableStateOf(true)
            }

            // 添加重命名功能的状态变量
            var showRenameDialog by remember { mutableStateOf(false) }
            var newGameName by remember { mutableStateOf("") }
            val focusRequester = remember { FocusRequester() }

            // 自定义背景相关状态
            var customBackgroundBitmap by remember { mutableStateOf<Bitmap?>(null) }
            val context = LocalContext.current
            val coroutineScope = rememberCoroutineScope()

            // 从SharedPreferences加载保存的自定义背景
            LaunchedEffect(Unit) {
                val sharedPreferences = context.getSharedPreferences("app_preferences", Context.MODE_PRIVATE)
                val backgroundPath = sharedPreferences.getString("custom_background_path", null)
                backgroundPath?.let { path ->
                    try {
                        val file = File(path)
                        if (file.exists()) {
                            val bitmap = BitmapFactory.decodeFile(path)
                            if (bitmap != null) {
                                customBackgroundBitmap = bitmap
                            }
                        }
                    } catch (e: Exception) {
                        Log.e("HomeViews", "Error loading background", e)
                    }
                }
            }

            // 图片选择器
            val imagePicker = rememberLauncherForActivityResult(
                contract = ActivityResultContracts.GetContent(),
                onResult = { uri ->
                    uri?.let {
                        coroutineScope.launch {
                            withContext(Dispatchers.IO) {
                                try {
                                    val inputStream = context.contentResolver.openInputStream(it)
                                    inputStream?.use { stream ->
                                        val bitmap = BitmapFactory.decodeStream(stream)
                                        if (bitmap != null) {
                                            // 保存到应用私有目录
                                            val backgroundDir = File(context.filesDir, "backgrounds")
                                            if (!backgroundDir.exists()) {
                                                backgroundDir.mkdirs()
                                            }
                                            val outputFile = File(backgroundDir, "custom_background.jpg")
                                            val outputStream = FileOutputStream(outputFile)
                                            bitmap.compress(Bitmap.CompressFormat.JPEG, 90, outputStream)
                                            outputStream.close()
                                            
                                            // 保存路径到SharedPreferences
                                            val sharedPreferences = context.getSharedPreferences("app_preferences", Context.MODE_PRIVATE)
                                            sharedPreferences.edit()
                                                .putString("custom_background_path", outputFile.absolutePath)
                                                .apply()
                                            
                                            // 更新UI
                                            withContext(Dispatchers.Main) {
                                                customBackgroundBitmap = bitmap
                                            }
                                        }
                                    }
                                } catch (e: Exception) {
                                    Log.e("HomeViews", "Error saving background", e)
                                }
                            }
                        }
                    }
                }
            )

            val nestedScrollConnection = remember {
                object : NestedScrollConnection {
                    override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                        if (available.y < -1) {
                            isFabVisible = false
                        }
                        if (available.y > 1) {
                            isFabVisible = true
                        }
                        return Offset.Zero
                    }
                }
            }

            // 获取屏幕方向
            val configuration = LocalConfiguration.current
            val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE

            // 添加屏幕方向改变时的处理
            LaunchedEffect(configuration.orientation) {
                showAppActions.value = false
                selectedModel.value = null
                viewModel.mainViewModel?.selected = null
            }

            // 横屏模式下跟踪当前中央项
            var centeredIndex by remember { mutableStateOf(0) }

            // 使用正确的ModalBottomSheet状态
            val sheetState = rememberModalBottomSheetState()

            BoxWithConstraints(
                modifier = Modifier.fillMaxSize()
            ) {
                // 自定义背景层
                if (customBackgroundBitmap != null) {
                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .zIndex(-1f)
                    ) {
                        Image(
                            bitmap = customBackgroundBitmap!!.asImageBitmap(),
                            contentDescription = "Custom Background",
                            contentScale = ContentScale.Crop,
                            modifier = Modifier.fillMaxSize()
                        )
                    }
                }

                Scaffold(
                    modifier = Modifier
                        .fillMaxSize()
                        .zIndex(1f),
                    topBar = {
                        if (!isLandscape) {
                            SearchBar(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                shape = SearchBarDefaults.inputFieldShape,
                                query = query,
                                onQueryChange = {
                                    query = it
                                },
                                onSearch = {},
                                active = false,
                                onActiveChange = {},
                                leadingIcon = {
                                    Icon(
                                        Icons.Filled.Search,
                                        contentDescription = "Search Games"
                                    )
                                },
                                placeholder = {
                                    Text(text = "Ryujinx")
                                },
                                trailingIcon = {
                                    IconButton(onClick = {
                                        openAppBarExtra = !openAppBarExtra
                                    }) {
                                        if (!refreshUser) {
                                            refreshUser = true
                                        }
                                        if (refreshUser)
                                            if (viewModel.mainViewModel?.userViewModel?.openedUser?.userPicture?.isNotEmpty() == true) {
                                                val pic =
                                                    viewModel.mainViewModel!!.userViewModel.openedUser.userPicture
                                                Image(
                                                    bitmap = BitmapFactory.decodeByteArray(
                                                        pic,
                                                        0,
                                                        pic?.size ?: 0
                                                    )
                                                        .asImageBitmap(),
                                                    contentDescription = "user image",
                                                    contentScale = ContentScale.Crop,
                                                    modifier = Modifier
                                                        .padding(4.dp)
                                                        .size(40.dp)
                                                        .clip(RoundedCornerShape(12.dp))
                                                        .border(1.dp, Color.Gray, RoundedCornerShape(12.dp))
                                                )
                                            } else {
                                                Box(
                                                    modifier = Modifier
                                                        .size(40.dp)
                                                        .clip(RoundedCornerShape(12.dp))
                                                        .border(1.dp, Color.Gray, RoundedCornerShape(12.dp)),
                                                    contentAlignment = Alignment.Center
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Person,
                                                        contentDescription = "user",
                                                        modifier = Modifier.size(24.dp)
                                                    )
                                                }
                                            }
                                    }
                                }
                            ) {

                            }
                        } else {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.Start
                            ) {
                                SearchBar(
                                    modifier = Modifier
                                        .width(185.dp)
                                        .height(55.dp),
                                    shape = SearchBarDefaults.inputFieldShape,
                                    query = query,
                                    onQueryChange = {
                                        query = it
                                    },
                                    onSearch = {},
                                    active = false,
                                    onActiveChange = {},
                                    leadingIcon = {
                                        Icon(
                                            Icons.Filled.Search,
                                            contentDescription = "Search Games"
                                        )
                                    },
                                    placeholder = {
                                        Text(text = "Search Games")
                                    },
                                    trailingIcon = {
                                        IconButton(
                                            onClick = {
                                                openAppBarExtra = !openAppBarExtra
                                            },
                                            modifier = Modifier.size(32.dp)
                                        ) {
                                            if (!refreshUser) {
                                            refreshUser = true
                                        }
                                        if (refreshUser)
                                            if (viewModel.mainViewModel?.userViewModel?.openedUser?.userPicture?.isNotEmpty() == true) {
                                                val pic =
                                                    viewModel.mainViewModel!!.userViewModel.openedUser.userPicture
                                                Image(
                                                    bitmap = BitmapFactory.decodeByteArray(
                                                        pic,
                                                        0,
                                                        pic?.size ?: 0
                                                    )
                                                        .asImageBitmap(),
                                                    contentDescription = "user image",
                                                    contentScale = ContentScale.Crop,
                                                    modifier = Modifier
                                                        .size(32.dp)
                                                        .clip(RoundedCornerShape(12.dp))
                                                        .border(1.dp, Color.Gray, RoundedCornerShape(12.dp))
                                                )
                                            } else {
                                                Box(
                                                    modifier = Modifier
                                                        .size(32.dp)
                                                        .clip(RoundedCornerShape(12.dp))
                                                        .border(1.dp, Color.Gray, RoundedCornerShape(12.dp)),
                                                    contentAlignment = Alignment.Center
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Person,
                                                        contentDescription = "user",
                                                        modifier = Modifier.size(24.dp)
                                                    )
                                                }
                                            }
                                    }
                                    }
                                ) {}
                            }
                        }
                    },
                    floatingActionButton = {
                        AnimatedVisibility(
                            visible = isFabVisible,
                            enter = slideInVertically(initialOffsetY = { it * 2 }),
                            exit = slideOutVertically(targetOffsetY = { it * 2 })
                        ) {
                            FloatingActionButton(
                                onClick = {
                                    viewModel.requestReload()
                                    viewModel.ensureReloadIfNecessary()
                                },
                                shape = MaterialTheme.shapes.small
                            ) {
                                Icon(Icons.Default.Refresh, contentDescription = "refresh")
                            }
                        }
                    }

                ) { contentPadding ->
                    Box(modifier = Modifier.fillMaxSize()) {
                        Box(
                            modifier = Modifier
                                .fillMaxSize()
                                .padding(contentPadding)
                                .zIndex(1f)
                                .pointerInput(Unit) {
                                    detectTapGestures(
                                        onLongPress = {
                                            imagePicker.launch("image/*")
                                        }
                                    )
                                }
                        ) {
                            val list = remember {
                                viewModel.gameList
                            }
                            val isLoading = remember {
                                viewModel.isLoading
                            }
                            viewModel.filter(query)

                            if (!isPreview) {
                                val settings = QuickSettings(viewModel.activity!!)

                                if (isLoading.value) {
                                    Box(modifier = Modifier.fillMaxSize())
                                    {
                                        CircularProgressIndicator(
                                            modifier = Modifier
                                                .width(64.dp)
                                                .align(Alignment.Center),
                                            color = MaterialTheme.colorScheme.secondary,
                                            trackColor = MaterialTheme.colorScheme.surfaceVariant
                                        )
                                    }
                                } else {
                                    if (isLandscape) {
                                        val filteredList = list.filter {
                                            it.getDisplayName().isNotEmpty() && 
                                            (query.trim().isEmpty() || 
                                             it.getDisplayName().lowercase(Locale.getDefault()).contains(query))
                                        }
                                        
                                        if (filteredList.isEmpty()) {
                                            Box(
                                                modifier = Modifier.fillMaxSize(),
                                                contentAlignment = Alignment.Center
                                            ) {
                                                Text(
                                                    text = "No games found",
                                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                                )
                                            }
                                        } else {
                                            if (centeredIndex >= filteredList.size) {
                                                centeredIndex = 0
                                            }
                                            
                                            val leftIndex = if (centeredIndex == 0) filteredList.size - 1 else centeredIndex - 1
                                            val rightIndex = if (centeredIndex == filteredList.size - 1) 0 else centeredIndex + 1
                                            
                                            Box(
                                                modifier = Modifier
                                                    .fillMaxSize()
                                                    .nestedScroll(nestedScrollConnection)
                                                    .pointerInput(Unit) {
                                                        detectHorizontalDragGestures { change, dragAmount ->
                                                            if (dragAmount > 50) {
                                                                centeredIndex = if (centeredIndex == 0) filteredList.size - 1 else centeredIndex - 1
                                                            } else if (dragAmount < -50) {
                                                                centeredIndex = if (centeredIndex == filteredList.size - 1) 0 else centeredIndex + 1
                                                            }
                                                        }
                                                    }
                                            ) {
                                                Row(
                                                    modifier = Modifier
                                                        .fillMaxSize()
                                                        .padding(horizontal = 8.dp),
                                                    horizontalArrangement = Arrangement.SpaceEvenly,
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    LandscapeGameCarouselItem(
                                                        gameModel = filteredList.getOrNull(leftIndex),
                                                        viewModel = viewModel,
                                                        showAppActions = showAppActions,
                                                        showLoading = showLoading,
                                                        selectedModel = selectedModel,
                                                        showError = showError,
                                                        isCentered = false,
                                                        onItemClick = {
                                                            centeredIndex = leftIndex
                                                        }
                                                    )
                                                    
                                                    LandscapeGameCarouselItem(
                                                        gameModel = filteredList.getOrNull(centeredIndex),
                                                        viewModel = viewModel,
                                                        showAppActions = showAppActions,
                                                        showLoading = showLoading,
                                                        selectedModel = selectedModel,
                                                        showError = showError,
                                                        isCentered = true
                                                    )
                                                    
                                                    LandscapeGameCarouselItem(
                                                        gameModel = filteredList.getOrNull(rightIndex),
                                                        viewModel = viewModel,
                                                        showAppActions = showAppActions,
                                                        showLoading = showLoading,
                                                        selectedModel = selectedModel,
                                                        showError = showError,
                                                        isCentered = false,
                                                        onItemClick = {
                                                            centeredIndex = rightIndex
                                                        }
                                                    )
                                                }
                                            }
                                        }
                                    } else if (settings.isGrid) {
                                        LazyVerticalGrid(
                                            columns = GridCells.Fixed(2),
                                            modifier = Modifier
                                                .fillMaxSize()
                                                .padding(horizontal = 8.dp)
                                                .nestedScroll(nestedScrollConnection),
                                            horizontalArrangement = Arrangement.spacedBy(4.dp),
                                            verticalArrangement = Arrangement.spacedBy(8.dp)
                                        ) {
                                            items(list) {
                                                if (it.getDisplayName().isNotEmpty() && (query.trim()
                                                        .isEmpty() || it.getDisplayName().lowercase(Locale.getDefault())
                                                        .contains(query))
                                                ) {
                                                    GridGameItem(
                                                        gameModel = it,
                                                        viewModel = viewModel,
                                                        showAppActions = showAppActions,
                                                        showLoading = showLoading,
                                                        selectedModel = selectedModel,
                                                        showError = showError
                                                    )
                                                }
                                            }
                                        }
                                    } else {
                                        LazyColumn(
                                            modifier = Modifier.fillMaxSize()
                                        ) {
                                            items(list) {
                                                if (it.getDisplayName().isNotEmpty() && (query.trim()
                                                        .isEmpty() || it.getDisplayName().lowercase(
                                                        Locale.getDefault()
                                                    )
                                                        .contains(query))
                                                ) {
                                                    Box(modifier = Modifier) {
                                                        ListGameItem(
                                                            gameModel = it,
                                                            viewModel = viewModel,
                                                            showAppActions = showAppActions,
                                                            showLoading = showLoading,
                                                            selectedModel = selectedModel,
                                                            showError = showError
                                                        )
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // 用户卡片
                        Column(
                            modifier = Modifier
                                .padding(contentPadding)
                                .zIndex(2f)
                        ) {
                            val iconSize = 52.dp
                            AnimatedVisibility(
                                visible = openAppBarExtra,
                            ) {
                                Card(
                                    modifier = Modifier
                                        .padding(vertical = 8.dp, horizontal = 16.dp)
                                        .fillMaxWidth(),
                                    shape = MaterialTheme.shapes.medium
                                ) {
                                    Column(modifier = Modifier.padding(8.dp)) {
                                        Row(
                                            modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(12.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.SpaceBetween
                                ) {
                                if (refreshUser) {
                                    Box(
                                        modifier = Modifier
                                            .border(
                                                width = 2.dp,
                                                color = Color(0xFF14bf00),
                                                shape = RoundedCornerShape(12.dp)
                                            )
                                            .size(iconSize)
                                            .padding(2.dp),
                                        contentAlignment = Alignment.Center
                                    ) {
                                        if (viewModel.mainViewModel?.userViewModel?.openedUser?.userPicture?.isNotEmpty() == true) {
                                            val pic =
                                                viewModel.mainViewModel!!.userViewModel.openedUser.userPicture
                                            Image(
                                                bitmap = BitmapFactory.decodeByteArray(
                                                    pic,
                                                    0,
                                                    pic?.size ?: 0
                                                )
                                                    .asImageBitmap(),
                                                contentDescription = "user image",
                                                contentScale = ContentScale.Crop,
                                                modifier = Modifier
                                                    .padding(4.dp)
                                                    .size(iconSize)
                                                    .clip(RoundedCornerShape(12.dp))
                                            )
                                        } else {
                                            Icon(
                                                Icons.Filled.Person,
                                                contentDescription = "user",
                                                modifier = Modifier.size(32.dp)
                                            )
                                        }
                                    }
                                }
                                Card(
                                    modifier = Modifier
                                        .padding(horizontal = 4.dp)
                                        .fillMaxWidth(0.7f),
                                    shape = MaterialTheme.shapes.small,
                                ) {
                                    LazyRow {
                                        if (viewModel.mainViewModel?.userViewModel?.userList?.isNotEmpty() == true) {
                                            items(viewModel.mainViewModel!!.userViewModel.userList) { user ->
                                                if (user.id != viewModel.mainViewModel!!.userViewModel.openedUser.id) {
                                                    Image(
                                                        bitmap = BitmapFactory.decodeByteArray(
                                                            user.userPicture,
                                                            0,
                                                            user.userPicture?.size ?: 0
                                                        )
                                                            .asImageBitmap(),
                                                        contentDescription = "selected image",
                                                        contentScale = ContentScale.Crop,
                                                        modifier = Modifier
                                                            .padding(4.dp)
                                                            .size(iconSize)
                                                            .clip(RoundedCornerShape(12.dp))
                                                            .combinedClickable(
                                                                onClick = {
                                                                    viewModel.mainViewModel!!.userViewModel.openUser(
                                                                        user
                                                                    )
                                                                    refreshUser =
                                                                        false
                                                                })
                                                    )
                                                }
                                            }
                                        }
                                    }
                                }
                                Box(
                                    modifier = Modifier
                                        .size(iconSize)
                                ) {
                                    IconButton(
                                        modifier = Modifier.fillMaxSize(),
                                        onClick = {
                                            openAppBarExtra = false
                                            navController?.navigate("user")
                                        }) {
                                        Icon(
                                            Icons.Filled.Add,
                                            contentDescription = "N/A"
                                        )
                                    }
                                }
                            }
                        }
                        TextButton(
                            modifier = Modifier.fillMaxWidth(),
                            onClick = {
                                navController?.navigate("settings")
                            }
                        ) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Icon(
                                    Icons.Filled.Settings,
                                    contentDescription = "Settings"
                                )
                                Text(
                                    text = "Settings",
                                    modifier = Modifier.padding(start = 8.dp)
                                )
                            }
                        }
                    }
                }
            }
        }

            if (showLoading.value) {
                BasicAlertDialog(onDismissRequest = { }) {
                    Card(
                        modifier = Modifier
                            .padding(16.dp)
                            .fillMaxWidth(),
                        shape = MaterialTheme.shapes.medium
                    ) {
                        Column(
                            modifier = Modifier
                                .padding(16.dp)
                                .fillMaxWidth()
                        ) {
                            Text(text = "Loading")
                            LinearProgressIndicator(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(top = 16.dp)
                            )
                        }

                    }
                }
            }
            if (openTitleUpdateDialog.value) {
                BasicAlertDialog(onDismissRequest = {
                    openTitleUpdateDialog.value = false
                }) {
                    Surface(
                        modifier = Modifier
                            .wrapContentWidth()
                            .wrapContentHeight(),
                        shape = MaterialTheme.shapes.large,
                        tonalElevation = AlertDialogDefaults.TonalElevation
                    ) {
                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                        val name = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                        TitleUpdateViews.Main(titleId, name, openTitleUpdateDialog, canClose)
                    }

                }
            }
            if (openDlcDialog.value) {
                BasicAlertDialog(onDismissRequest = {
                    openDlcDialog.value = false
                }) {
                    Surface(
                        modifier = Modifier
                            .wrapContentWidth()
                            .wrapContentHeight(),
                        shape = MaterialTheme.shapes.large,
                        tonalElevation = AlertDialogDefaults.TonalElevation
                    ) {
                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                        val name = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                        DlcViews.Main(titleId, name, openDlcDialog, canClose)
                    }

                }
            }

            // 底部操作菜单
            if (showAppActions.value) {
                val configuration = LocalConfiguration.current
                val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
                
                var showAppMenu by remember { mutableStateOf(false) }
                
                ModalBottomSheet(
                    onDismissRequest = {
                        showAppActions.value = false
                        selectedModel.value = null
                        showAppMenu = false
                    },
                    sheetState = sheetState,
                    modifier = Modifier
                        .fillMaxWidth()
                        .wrapContentHeight()
                        .systemBarsPadding()
                ) {
                    LazyColumn(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        item {
                            selectedModel.value?.let { game ->
                                Column(
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    Text(
                                        text = game.getDisplayName(),
                                        fontSize = 20.sp,
                                        fontWeight = FontWeight.Bold,
                                        modifier = Modifier.align(Alignment.CenterHorizontally),
                                        textAlign = TextAlign.Center
                                    )
                                    if (!game.version.isNullOrEmpty()) {
                                        Text(
                                            text = "v${game.version}",
                                            fontSize = 16.sp,
                                            modifier = Modifier.align(Alignment.CenterHorizontally),
                                            textAlign = TextAlign.Center
                                        )
                                    }
                                    Spacer(modifier = Modifier.height(16.dp))
                                }
                            }
                        }
                        
                        item {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceEvenly
                            ) {
                                IconButton(onClick = {
                                    if (viewModel.mainViewModel?.selected != null) {
                                        thread {
                                            showLoading.value = true
                                            val success =
                                                viewModel.mainViewModel!!.loadGame(viewModel.mainViewModel!!.selected!!)
                                            if (success == 1) {
                                                launchOnUiThread {
                                                    viewModel.mainViewModel!!.navigateToGame()
                                                }
                                            } else {
                                                if (success == -2)
                                                    showError.value =
                                                        "Error loading update. Please re-add update file"
                                                viewModel.mainViewModel!!.selected!!.close()
                                            }
                                            showLoading.value = false
                                        }
                                    }
                                }) {
                                    Icon(
                                        Icons.Filled.PlayArrow,
                                        contentDescription = "Run",
                                        modifier = Modifier.size(32.dp)
                                    )
                                }
                                
                                // 修复菜单圆角问题
                                Box {
                                    var buttonHeight by remember { mutableStateOf(0) }
                                    val density = LocalDensity.current
                                    
                                    Box(
                                        modifier = Modifier
                                            .size(48.dp)
                                            .onGloballyPositioned { coordinates ->
                                                buttonHeight = coordinates.size.height
                                            }
                                    ) {
                                        IconButton(
                                            modifier = Modifier.fillMaxSize(),
                                            onClick = {
                                                showAppMenu = !showAppMenu
                                            }
                                        ) {
                                            Icon(
                                                Icons.Filled.Menu,
                                                contentDescription = "Menu",
                                                modifier = Modifier.size(32.dp)
                                            )
                                        }
                                    }
                                    
                                    // 修复：使用Material3的DropdownMenu并设置圆角
                                    DropdownMenu(
                                        expanded = showAppMenu,
                                        onDismissRequest = { showAppMenu = false },
                                        modifier = Modifier
                                            .width(200.dp)
                                            .heightIn(max = configuration.screenHeightDp.dp * 0.6f)
                                            .clip(RoundedCornerShape(16.dp)),
                                        offset = DpOffset(
                                            x = (-175).dp, // 调整到合适位置
                                            y = with(density) { -buttonHeight.toDp() - 180.dp }
                                        )
                                    ) {
                                        Column {
                                            DropdownMenuItem(
                                                text = { Text("重命名游戏") },
                                                onClick = {
                                                    showAppMenu = false
                                                    selectedModel.value?.let { game ->
                                                        newGameName = game.getDisplayName()
                                                        showRenameDialog = true
                                                    }
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("清除 PPTC 缓存") },
                                                onClick = {
                                                    showAppMenu = false
                                                    viewModel.mainViewModel?.clearPptcCache(
                                                        viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    )
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("清除着色器缓存") },
                                                onClick = {
                                                    showAppMenu = false
                                                    viewModel.mainViewModel?.purgeShaderCache(
                                                        viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    )
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("删除所有缓存") },
                                                onClick = {
                                                    showAppMenu = false
                                                    viewModel.mainViewModel?.deleteCache(
                                                        viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    )
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理更新") },
                                                onClick = {
                                                    showAppMenu = false
                                                    openTitleUpdateDialog.value = true
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理 DLC") },
                                                onClick = {
                                                    showAppMenu = false
                                                    openDlcDialog.value = true
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理金手指") },
                                                onClick = {
                                                    showAppMenu = false
                                                    val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    val gamePath = viewModel.mainViewModel?.selected?.path ?: ""
                                                    navController?.navigate("cheats/$titleId?gamePath=${android.net.Uri.encode(gamePath)}")
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理存档数据") },
                                                onClick = {
                                                    showAppMenu = false
                                                    val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    val gameName = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                                                    navController?.navigate("savedata/$titleId?gameName=${android.net.Uri.encode(gameName)}")
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理 Mods") },
                                                onClick = {
                                                    showAppMenu = false
                                                    val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    val gameName = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                                                    navController?.navigate("mods/$titleId?gameName=${android.net.Uri.encode(gameName)}")
                                                }
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 重命名对话框
            if (showRenameDialog) {
                AlertDialog(
                    onDismissRequest = { showRenameDialog = false },
                    title = { Text(text = "Rename Game") },
                    text = {
                        OutlinedTextField(
                            value = newGameName,
                            onValueChange = { newGameName = it },
                            label = { Text("Game Name") },
                            modifier = Modifier.focusRequester(focusRequester)
                        )
                    },
                    confirmButton = {
                        TextButton(
                            onClick = {
                                selectedModel.value?.customName = newGameName
                                showRenameDialog = false
                                viewModel.filter(query)
                            }
                        ) {
                            Text("OK")
                        }
                    },
                    dismissButton = {
                        TextButton(
                            onClick = { showRenameDialog = false }
                        ) {
                            Text("Cancel")
                        }
                    }
                )
            }
        }

        @Preview
        @Composable
        fun HomePreview() {
            Home(isPreview = true)
        }
    }
}}}
