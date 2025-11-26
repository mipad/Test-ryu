// MainView.kt
package org.ryujinx.android.views

import androidx.compose.runtime.Composable
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import org.ryujinx.android.MainActivity
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.SettingsViewModel

class MainView {
    companion object {
        @Composable
        fun Main(mainViewModel: MainViewModel) {
            val navController = rememberNavController()
            mainViewModel.navController = navController

            NavHost(navController = navController, startDestination = "home") {
                composable("home") { 
                    HomeViews.Home(mainViewModel.homeViewModel, navController) 
                }
                composable("user") { 
                    UserViews.Main(mainViewModel) 
                }
                composable("game") { 
                    // 确保游戏继续运行
                    if (MainActivity.mainViewModel?.activity?.isGameRunning == true) {
                        GameViews.Main()
                    } else {
                        // 如果游戏没有运行，返回首页
                        navController.navigate("home") {
                            popUpTo("home") { inclusive = true }
                        }
                    }
                }
                composable("settings") {
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
                // 添加存档管理界面导航，包含 titleId 和 gameName 参数
                composable(
                    "savedata/{titleId}?gameName={gameName}",
                    arguments = listOf(
                        navArgument("titleId") { type = NavType.StringType },
                        navArgument("gameName") { 
                            type = NavType.StringType
                            defaultValue = ""
                            nullable = true
                        }
                    )
                ) { backStackEntry ->
                    val titleId = backStackEntry.arguments?.getString("titleId") ?: ""
                    val gameName = backStackEntry.arguments?.getString("gameName") ?: ""
                    SaveDataViews(navController, titleId, gameName)
                }
                // 注意：Mod管理已改为对话框方式，移除了导航路由
            }
        }
    }
}
