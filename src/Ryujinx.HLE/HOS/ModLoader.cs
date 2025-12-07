using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Tamper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ryujinx.HLE.Loaders
{
    internal class ModLoader
    {
        private readonly Switch _device;
        private readonly Dictionary<ulong, List<CheatInfo>> _cheatCache = new();

        public ModLoader(Switch device)
        {
            _device = device;
        }

        public void LoadCheats(ulong programId, ProcessTamperInfo tamperInfo, TamperMachine tamperMachine)
        {
            try
            {
                Logger.Debug?.Print(LogClass.ModLoader, $"[LoadCheats] 为程序 {programId:X16} 加载金手指");
                
                // 获取金手指
                var cheats = GetCheatsForProgram(programId);
                
                if (cheats.Count == 0)
                {
                    Logger.Debug?.Print(LogClass.ModLoader, $"程序 {programId:X16} 没有找到金手指");
                    return;
                }
                
                Logger.Info?.Print(LogClass.ModLoader, $"为程序 {programId:X16} 找到 {cheats.Count} 个金手指");
                
                // ==== 关键修改开始 ====
                // 记录地址信息，用于调试
                LogTamperInfo(tamperInfo);
                
                // 确定要使用的exeAddress
                ulong exeAddress = DetermineExeAddress(tamperInfo);
                
                Logger.Debug?.Print(LogClass.ModLoader,
                    $"[LoadCheats] 确定使用的exeAddress: 0x{exeAddress:X}");
                // ==== 关键修改结束 ====
                
                // 安装每个金手指
                int installedCount = 0;
                foreach (var cheat in cheats)
                {
                    try
                    {
                        // 获取对应的buildId
                        string buildId = GetBuildIdForCheat(cheat, tamperInfo);
                        
                        Logger.Debug?.Print(LogClass.ModLoader,
                            $"安装金手指: {cheat.Name}, BuildId: {buildId}");
                        
                        // 关键：传递正确的exeAddress
                        tamperMachine.InstallAtmosphereCheat(
                            cheat.Name,
                            buildId,
                            cheat.Instructions,
                            tamperInfo,
                            exeAddress);
                        
                        installedCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.ModLoader,
                            $"安装金手指 {cheat.Name} 失败: {ex.Message}");
                    }
                }
                
                Logger.Info?.Print(LogClass.ModLoader,
                    $"为程序 {programId:X16} 安装了 {installedCount}/{cheats.Count} 个金手指");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ModLoader,
                    $"加载程序 {programId:X16} 的金手指时发生错误: {ex.Message}");
            }
        }
        
        // ==== 新增：记录ProcessTamperInfo信息 ====
        private void LogTamperInfo(ProcessTamperInfo tamperInfo)
        {
            if (tamperInfo == null)
            {
                Logger.Warning?.Print(LogClass.ModLoader, "ProcessTamperInfo 为 null");
                return;
            }
            
            Logger.Debug?.Print(LogClass.ModLoader, "[TamperInfo] 地址信息:");
            Logger.Debug?.Print(LogClass.ModLoader, $"  PID: {tamperInfo.Process.Pid}");
            Logger.Debug?.Print(LogClass.ModLoader, $"  ASLR地址: 0x{tamperInfo.AslrAddress:X}");
            Logger.Debug?.Print(LogClass.ModLoader, $"  主NSO基址: 0x{tamperInfo.MainNsoBase:X}");
            Logger.Debug?.Print(LogClass.ModLoader, $"  堆地址: 0x{tamperInfo.HeapAddress:X}");
            Logger.Debug?.Print(LogClass.ModLoader, $"  别名地址: 0x{tamperInfo.AliasAddress:X}");
            Logger.Debug?.Print(LogClass.ModLoader, $"  固定codeStart: 0x{tamperInfo.FixedCodeStart:X}");
            
            // 计算偏移
            ulong offsetFromAslr = tamperInfo.MainNsoBase - tamperInfo.AslrAddress;
            ulong offsetFromFixed = tamperInfo.MainNsoBase - tamperInfo.FixedCodeStart;
            
            Logger.Debug?.Print(LogClass.ModLoader,
                $"  主NSO偏移ASLR: 0x{offsetFromAslr:X} " +
                $"(JIT模式预期: ~0x{0x580000:X})");
            Logger.Debug?.Print(LogClass.ModLoader,
                $"  主NSO偏移固定: 0x{offsetFromFixed:X}");
            
            // 检查是否是NCE模式
            if (tamperInfo.IsLikelyNceMode)
            {
                Logger.Info?.Print(LogClass.ModLoader,
                    "[TamperInfo] 检测到可能的NCE模式 (ASLR地址 > 4GB)");
                
                // 检查偏移是否正常
                if (offsetFromAslr < 0x500000 || offsetFromAslr > 0x600000)
                {
                    Logger.Warning?.Print(LogClass.ModLoader,
                        $"[TamperInfo] NCE模式下主NSO偏移异常: 0x{offsetFromAslr:X} " +
                        $"(预期在 0x500000-0x600000 范围内)");
                }
            }
            
            // 记录所有NSO地址
            if (tamperInfo.CodeAddresses != null)
            {
                Logger.Debug?.Print(LogClass.ModLoader, "[TamperInfo] 所有NSO地址:");
                int index = 0;
                foreach (var addr in tamperInfo.CodeAddresses)
                {
                    Logger.Debug?.Print(LogClass.ModLoader,
                        $"  NSO[{index}]: 0x{addr:X} " +
                        $"(偏移ASLR: 0x{addr - tamperInfo.AslrAddress:X})");
                    index++;
                }
            }
        }
        
        // ==== 新增：确定要使用的exeAddress ====
        private ulong DetermineExeAddress(ProcessTamperInfo tamperInfo)
        {
            // 策略：使用 tamperInfo.CheatCompileExeAddress
            // 这个属性会根据NCE模式自动返回正确的地址
            
            ulong exeAddress = tamperInfo.CheatCompileExeAddress;
            
            Logger.Debug?.Print(LogClass.ModLoader,
                $"[DetermineExeAddress] 使用 tamperInfo.CheatCompileExeAddress: 0x{exeAddress:X}");
            
            // 验证地址
            if (exeAddress == 0)
            {
                Logger.Warning?.Print(LogClass.ModLoader,
                    "[DetermineExeAddress] CheatCompileExeAddress 为 0，使用 MainNsoBase");
                exeAddress = tamperInfo.MainNsoBase;
            }
            
            if (exeAddress == 0)
            {
                Logger.Warning?.Print(LogClass.ModLoader,
                    "[DetermineExeAddress] MainNsoBase 也为 0，使用 AslrAddress");
                exeAddress = tamperInfo.AslrAddress;
            }
            
            return exeAddress;
        }
        
        // ==== 新增：获取金手指对应的buildId ====
        private string GetBuildIdForCheat(CheatInfo cheat, ProcessTamperInfo tamperInfo)
        {
            if (tamperInfo.BuildIds == null || !tamperInfo.BuildIds.Any())
                return "unknown";
            
            // 简化实现：使用第一个buildId
            // 实际应该根据cheat信息匹配正确的buildId
            return tamperInfo.BuildIds.First();
        }

        private List<CheatInfo> GetCheatsForProgram(ulong programId)
        {
            // 检查缓存
            if (_cheatCache.TryGetValue(programId, out var cachedCheats))
            {
                return cachedCheats;
            }
            
            List<CheatInfo> cheats = new List<CheatInfo>();
            
            try
            {
                // 从文件系统加载金手指
                string cheatPath = GetCheatFilePath(programId);
                
                if (File.Exists(cheatPath))
                {
                    cheats = LoadCheatsFromFile(cheatPath);
                    Logger.Debug?.Print(LogClass.ModLoader, $"从文件 {cheatPath} 加载了 {cheats.Count} 个金手指");
                }
                else
                {
                    Logger.Debug?.Print(LogClass.ModLoader, $"金手指文件不存在: {cheatPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ModLoader, $"加载程序 {programId:X16} 的金手指时发生错误: {ex.Message}");
            }
            
            // 缓存结果
            _cheatCache[programId] = cheats;
            
            return cheats;
        }
        
        private string GetCheatFilePath(ulong programId)
        {
            // 构建金手指文件路径
            // 这里根据Ryujinx的实际文件结构进行调整
            string programIdStr = programId.ToString("X16");
            string modsPath = Path.Combine(_device.Configuration.GameModsPathOrDefault, programIdStr, "cheats");
            
            // 查找最新的金手指文件
            if (Directory.Exists(modsPath))
            {
                var files = Directory.GetFiles(modsPath, "*.txt");
                if (files.Length > 0)
                {
                    // 使用第一个找到的文件
                    return files[0];
                }
            }
            
            return Path.Combine(modsPath, "cheats.txt");
        }
        
        private List<CheatInfo> LoadCheatsFromFile(string filePath)
        {
            List<CheatInfo> cheats = new List<CheatInfo>();
            
            try
            {
                string[] lines = File.ReadAllLines(filePath);
                CheatInfo currentCheat = null;
                List<string> currentInstructions = null;
                
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    
                    if (string.IsNullOrEmpty(trimmedLine))
                        continue;
                    
                    // 检测金手指名称行：[金手指名称]
                    if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                    {
                        // 保存前一个金手指
                        if (currentCheat != null && currentInstructions != null)
                        {
                            currentCheat.Instructions = currentInstructions;
                            cheats.Add(currentCheat);
                        }
                        
                        // 开始新的金手指
                        string cheatName = trimmedLine.Substring(1, trimmedLine.Length - 2);
                        currentCheat = new CheatInfo { Name = cheatName };
                        currentInstructions = new List<string>();
                    }
                    // 检测指令行（16进制数字）
                    else if (IsHexInstruction(trimmedLine))
                    {
                        if (currentInstructions != null)
                        {
                            currentInstructions.Add(trimmedLine);
                        }
                    }
                }
                
                // 保存最后一个金手指
                if (currentCheat != null && currentInstructions != null && currentInstructions.Count > 0)
                {
                    currentCheat.Instructions = currentInstructions;
                    cheats.Add(currentCheat);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.ModLoader, $"加载金手指文件 {filePath} 时发生错误: {ex.Message}");
            }
            
            return cheats;
        }
        
        private bool IsHexInstruction(string line)
        {
            // 检查是否是16进制指令（8个16进制字符）
            if (line.Length != 8)
                return false;
            
            foreach (char c in line)
            {
                if (!Uri.IsHexDigit(c))
                    return false;
            }
            
            return true;
        }
    }
    
    public class CheatInfo
    {
        public string Name { get; set; }
        public IEnumerable<string> Instructions { get; set; }
    }
}
