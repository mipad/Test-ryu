package org.ryujinx.android.views

import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.SettingsViewModel

class MainView {
    companion object {
        @Composable
        fun Main(mainViewModel: MainViewModel) {
            val navController = rememberNavController()
            mainViewModel.navController = navController

            NavHost(navController = navController, startDestination = "home") {
                composable("home") { HomeViews.Home(mainViewModel.homeViewModel, navController) }
                composable("user") { UserViews.Main(mainViewModel) }
                composable("game") { GameViews.Main() }
                composable("settings") {
                    // 移除 timeZone 参数
                    SettingViews.Main(
                        SettingsViewModel(
                            navController,
                            mainViewModel.activity
                        ), mainViewModel
                    )
                }
                // 添加金手指界面导航，包含 titleId 和 gamePath 参数
                composable(
                    "cheats/{titleId}?gamePath={gamePath}",
                    arguments = listOf(
                        navArgument("titleId") { type = NavType.StringType },
                        navArgument("gamePath") { 
                            type = NavType.StringType
                            defaultValue = ""
                            nullable = true
                        }
                    )
                ) { backStackEntry ->
                    val titleId = backStackEntry.arguments?.getString("titleId") ?: ""
                    val gamePath = backStackEntry.arguments?.getString("gamePath") ?: ""
                    CheatsViews(navController, titleId, gamePath)
                }
                // 添加时区选择界面导航
                composable("timezone") {
                    TimeZoneView(
                        onBack = { navController.popBackStack() },
                        onTimeZoneSelected = { selectedTimeZone ->
                            // 这里需要找到其他方式来更新时区设置
                            // 可能需要通过 ViewModel 或其他状态管理方式
                        }
                    )
                }
            }
        }
    }
}
