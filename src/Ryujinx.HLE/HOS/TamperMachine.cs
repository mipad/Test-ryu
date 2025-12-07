using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Tamper;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS
{
    public class TamperMachine
    {
        // Atmosphere specifies a delay of 83 milliseconds between the execution of the last
        // cheat and the re-execution of the first one.
        private const int TamperMachineSleepMs = 1000 / 12;

        private Thread _tamperThread = null;
        private readonly ConcurrentQueue<ITamperProgram> _programs = new();
        private long _pressedKeys = 0;
        private readonly Dictionary<string, ITamperProgram> _programDictionary = new();

        private void Activate()
        {
            if (_tamperThread == null || !_tamperThread.IsAlive)
            {
                Logger.Debug?.Print(LogClass.TamperMachine, "激活TamperMachine线程");
                
                _tamperThread = new Thread(this.TamperRunner)
                {
                    Name = "HLE.TamperMachine",
                };
                _tamperThread.Start();
            }
            else
            {
                Logger.Debug?.Print(LogClass.TamperMachine, "TamperMachine线程已经在运行");
            }
        }

        internal void InstallAtmosphereCheat(string name, string buildId, IEnumerable<string> rawInstructions, ProcessTamperInfo info, ulong exeAddress)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"安装Atmosphere金手指: {name}，构建ID: {buildId}");
            
            if (!CanInstallOnPid(info.Process.Pid))
            {
                Logger.Warning?.Print(LogClass.TamperMachine, $"不能在进程 {info.Process.Pid} 上安装金手指 {name}");
                return;
            }

            ITamperedProcess tamperedProcess = new TamperedKProcess(info.Process);
            
            // ==== 关键修改开始 ====
            // 验证和修正exeAddress
            ulong actualExeAddress = ValidateAndCorrectExeAddress(info, exeAddress, name);
            
            // 调试信息：记录所有地址信息
            LogAddressInfo(info, actualExeAddress, name);
            
            AtmosphereCompiler compiler = new(actualExeAddress, info.HeapAddress, info.AliasAddress, info.AslrAddress, tamperedProcess);
            
            Logger.Debug?.Print(LogClass.TamperMachine, 
                $"编译金手指 {name} 使用地址: " +
                $"Exe=0x{actualExeAddress:X}, Heap=0x{info.HeapAddress:X}, " +
                $"Alias=0x{info.AliasAddress:X}, Aslr=0x{info.AslrAddress:X}");
            // ==== 关键修改结束 ====
            
            ITamperProgram program = compiler.Compile(name, rawInstructions);

            if (program != null)
            {
                program.TampersCodeMemory = false;

                _programs.Enqueue(program);
                _programDictionary.TryAdd($"{buildId}-{name}", program);
                
                Logger.Info?.Print(LogClass.TamperMachine, $"成功安装金手指 '{name}'，ID: {buildId}-{name}");
                Logger.Debug?.Print(LogClass.TamperMachine, $"程序队列大小: {_programs.Count}, 字典大小: {_programDictionary.Count}");
                
                // 记录金手指使用的地址信息
                Logger.Debug?.Print(LogClass.TamperMachine,
                    $"[金手指地址] '{name}' 使用 exeAddress=0x{actualExeAddress:X}, " +
                    $"ASLR基址=0x{info.AslrAddress:X}, " +
                    $"主NSO基址=0x{info.MainNsoBase:X}");
            }
            else
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"编译金手指失败: {name}");
            }

            Activate();
        }
        
        // ==== 新增：验证和修正exeAddress ====
        private ulong ValidateAndCorrectExeAddress(ProcessTamperInfo info, ulong exeAddress, string cheatName)
        {
            // 检查传入的exeAddress是否与info.MainNsoBase匹配
            if (exeAddress == info.MainNsoBase)
            {
                Logger.Debug?.Print(LogClass.TamperMachine,
                    $"[地址验证] 金手指 '{cheatName}' 使用正确的exeAddress: 0x{exeAddress:X}");
                return exeAddress;
            }
            
            // 检查传入的exeAddress是否与info.CheatCompileExeAddress匹配
            if (exeAddress == info.CheatCompileExeAddress)
            {
                Logger.Debug?.Print(LogClass.TamperMachine,
                    $"[地址验证] 金手指 '{cheatName}' 使用金手指编译地址: 0x{exeAddress:X}");
                return exeAddress;
            }
            
            // 检查传入的exeAddress是否是ASLR地址
            if (exeAddress == info.AslrAddress)
            {
                Logger.Warning?.Print(LogClass.TamperMachine,
                    $"[地址验证] 金手指 '{cheatName}' 错误地使用了ASLR地址作为exeAddress！ " +
                    $"使用 info.MainNsoBase (0x{info.MainNsoBase:X}) 替代");
                return info.MainNsoBase;
            }
            
            // 检查传入的exeAddress是否可能是固定codeStart
            if (exeAddress == info.FixedCodeStart)
            {
                Logger.Warning?.Print(LogClass.TamperMachine,
                    $"[地址验证] 金手指 '{cheatName}' 使用了固定codeStart作为exeAddress！ " +
                    $"在NCE模式下这可能不正确。使用 info.MainNsoBase (0x{info.MainNsoBase:X}) 替代");
                return info.MainNsoBase;
            }
            
            // 默认情况下，使用info.MainNsoBase
            Logger.Warning?.Print(LogClass.TamperMachine,
                $"[地址验证] 金手指 '{cheatName}' 使用不明确的exeAddress: 0x{exeAddress:X}。 " +
                $"使用 info.MainNsoBase (0x{info.MainNsoBase:X}) 替代");
            
            return info.MainNsoBase;
        }
        
        // ==== 新增：记录地址信息 ====
        private void LogAddressInfo(ProcessTamperInfo info, ulong exeAddress, string cheatName)
        {
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"[地址信息] 金手指 '{cheatName}':");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  PID: {info.Process.Pid}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  ASLR地址: 0x{info.AslrAddress:X}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  主NSO基址: 0x{info.MainNsoBase:X}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  堆地址: 0x{info.HeapAddress:X}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  别名地址: 0x{info.AliasAddress:X}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  固定codeStart: 0x{info.FixedCodeStart:X}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  主NSO偏移ASLR: 0x{info.MainNsoOffsetFromAslr:X}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  主NSO偏移固定: 0x{info.MainNsoOffsetFromFixed:X}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  金手指编译地址: 0x{info.CheatCompileExeAddress:X}");
            Logger.Debug?.Print(LogClass.TamperMachine,
                $"  实际使用的exeAddress: 0x{exeAddress:X}");
            
            // 检查是否是NCE模式
            if (info.IsLikelyNceMode)
            {
                Logger.Info?.Print(LogClass.TamperMachine,
                    $"[地址信息] NCE模式检测：ASLR地址显著大于4GB");
                
                // JIT模式下，主NSO基址通常是 ASLR地址 + 0x580000
                ulong expectedJitAddress = info.AslrAddress + 0x580000UL;
                ulong difference = info.MainNsoBase > expectedJitAddress ? 
                    info.MainNsoBase - expectedJitAddress : 
                    expectedJitAddress - info.MainNsoBase;
                
                if (difference > 0x10000)
                {
                    Logger.Warning?.Print(LogClass.TamperMachine,
                        $"[地址信息] NCE模式下主NSO基址异常！ " +
                        $"实际: 0x{info.MainNsoBase:X}, " +
                        $"JIT预期: 0x{expectedJitAddress:X}, " +
                        $"差异: 0x{difference:X}");
                }
            }
        }

        private static bool CanInstallOnPid(ulong pid)
        {
            // Do not allow tampering of kernel processes.
            if (pid < KernelConstants.InitialProcessId)
            {
                Logger.Warning?.Print(LogClass.TamperMachine, $"拒绝修改内核进程 {pid}");

                return false;
            }

            return true;
        }

        public void EnableCheats(string[] enabledCheats)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"启用金手指: {string.Join(", ", enabledCheats)}");
            
            foreach (var program in _programDictionary.Values)
            {
                program.IsEnabled = false;
            }

            int enabledCount = 0;
            foreach (var cheat in enabledCheats)
            {
                if (_programDictionary.TryGetValue(cheat, out var program))
                {
                    program.IsEnabled = true;
                    enabledCount++;
                    Logger.Debug?.Print(LogClass.TamperMachine, $"已启用金手指: {cheat}");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.TamperMachine, $"未找到金手指: {cheat}");
                }
            }
            
            Logger.Info?.Print(LogClass.TamperMachine, $"已启用 {enabledCount} 个金手指（共请求 {enabledCheats.Length} 个）");
        }

        private static bool IsProcessValid(ITamperedProcess process)
        {
            bool isValid = process.State != ProcessState.Crashed && 
                          process.State != ProcessState.Exiting && 
                          process.State != ProcessState.Exited;
            
            if (!isValid)
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"进程状态不适合修改: 状态={process.State}");
            }
            
            return isValid;
        }

        private void TamperRunner()
        {
            Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine线程运行中");
            
            int sleepCounter = 0;
            int executionCount = 0;

            while (true)
            {
                // Sleep to not consume too much CPU.
                if (sleepCounter == 0)
                {
                    sleepCounter = _programs.Count;
                    Logger.Debug?.Print(LogClass.TamperMachine, $"睡眠 {TamperMachineSleepMs}ms，程序数量: {_programs.Count}");
                    Thread.Sleep(TamperMachineSleepMs);
                }
                else
                {
                    sleepCounter--;
                }

                if (!AdvanceTamperingsQueue())
                {
                    // No more work to be done.
                    Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine线程退出");
                    return;
                }
                
                executionCount++;
                if (executionCount % 100 == 0) // 每执行100次记录一次日志
                {
                    Logger.Debug?.Print(LogClass.TamperMachine, $"TamperMachine已执行 {executionCount} 个周期");
                }
            }
        }

        private bool AdvanceTamperingsQueue()
        {
            if (!_programs.TryDequeue(out ITamperProgram program))
            {
                // No more programs in the queue.
                Logger.Debug?.Print(LogClass.TamperMachine, "队列中没有程序，清空字典");
                _programDictionary.Clear();

                return false;
            }

            // Check if the process is still suitable for running the tamper program.
            if (!IsProcessValid(program.Process))
            {
                // Exit without re-enqueuing the program because the process is no longer valid.
                Logger.Warning?.Print(LogClass.TamperMachine, $"程序 {program.Name} 的进程不再有效，从队列中移除");
                return true;
            }

            // Re-enqueue the tampering program because the process is still valid.
            _programs.Enqueue(program);

            // Skip execution if program is not enabled
            if (!program.IsEnabled)
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"跳过已禁用的程序: {program.Name}");
                return true;
            }

            Logger.Debug?.Print(LogClass.TamperMachine, $"运行修改程序 {program.Name}");

            try
            {
                ControllerKeys pressedKeys = (ControllerKeys)Volatile.Read(ref _pressedKeys);
                Logger.Debug?.Print(LogClass.TamperMachine, $"当前按下的按键: {pressedKeys}");
                
                program.Process.TamperedCodeMemory = false;
                program.Execute(pressedKeys);

                // Detect the first attempt to tamper memory and log it.
                if (!program.TampersCodeMemory && program.Process.TamperedCodeMemory)
                {
                    program.TampersCodeMemory = true;
                    Logger.Warning?.Print(LogClass.TamperMachine, $"修改程序 {program.Name} 修改了代码内存，可能无法正常工作");
                }
                
                Logger.Debug?.Print(LogClass.TamperMachine, $"成功执行程序 {program.Name}");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"修改程序 {program.Name} 崩溃: {ex.Message}");
                Logger.Debug?.Print(LogClass.TamperMachine, $"异常详情: {ex}");

                // Re-enqueue the program even if it crashed, as it might be a temporary issue
                _programs.Enqueue(program);
            }

            return true;
        }

        public void UpdateInput(List<GamepadInput> gamepadInputs)
        {
            // Look for the input of the player one or the handheld.
            foreach (GamepadInput input in gamepadInputs)
            {
                if (input.PlayerId == PlayerIndex.Player1 || input.PlayerId == PlayerIndex.Handheld)
                {
                    long previousKeys = Volatile.Read(ref _pressedKeys);
                    Volatile.Write(ref _pressedKeys, (long)input.Buttons);
                    
                    if (previousKeys != (long)input.Buttons)
                    {
                        Logger.Debug?.Print(LogClass.TamperMachine, $"输入更新: {input.Buttons}");
                    }
                    
                    return;
                }
            }

            // Clear the input because player one is not connected.
            long oldKeys = Volatile.Read(ref _pressedKeys);
            if (oldKeys != 0)
            {
                Volatile.Write(ref _pressedKeys, 0);
                Logger.Debug?.Print(LogClass.TamperMachine, "输入已清除（玩家一未连接）");
            }
        }
        
        // 添加一个方法来获取当前状态信息，用于调试
        public string GetStatus()
        {
            return $"队列中的程序: {_programs.Count}, 字典条目: {_programDictionary.Count}, 线程存活: {_tamperThread?.IsAlive ?? false}";
        }
    }
}
