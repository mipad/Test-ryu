using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Tamper;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        public TamperMachine()
        {
            Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine initialized");
        }

        private void Activate()
        {
            if (_tamperThread == null || !_tamperThread.IsAlive)
            {
                Logger.Debug?.Print(LogClass.TamperMachine, "Activating TamperMachine thread");
                
                _tamperThread = new Thread(this.TamperRunner)
                {
                    Name = "HLE.TamperMachine",
                };
                _tamperThread.Start();
            }
            else
            {
                Logger.Debug?.Print(LogClass.TamperMachine, "TamperMachine thread is already active");
            }
        }

        internal void InstallAtmosphereCheat(string name, string buildId, IEnumerable<string> rawInstructions, ProcessTamperInfo info, ulong exeAddress)
        {
            Logger.Info?.Print(LogClass.TamperMachine, 
                $"[金手指安装] 开始安装: {name}, BuildId: {buildId}");
            
            if (!CanInstallOnPid(info.Process.Pid))
            {
                Logger.Warning?.Print(LogClass.TamperMachine, $"Cannot install cheat {name} on process {info.Process.Pid}");
                return;
            }

            ITamperedProcess tamperedProcess = new TamperedKProcess(info.Process);
            
            // ==== 关键修改：使用ProcessTamperInfo中的金手指基址 ====
            ulong actualExeAddress = exeAddress;
            
            // 计算偏移（使用第二个NSO的偏移判断模式）
            ulong offsetFromAslr = info.SecondNsoBase > 0 ? (info.SecondNsoBase - info.AslrAddress) : (info.MainNsoBase - info.AslrAddress);
            
            // 记录地址信息用于调试
            Logger.Info?.Print(LogClass.TamperMachine, 
                $"[地址信息] 金手指 '{name}':");
            Logger.Info?.Print(LogClass.TamperMachine, 
                $"  传入的exeAddress: 0x{exeAddress:X}");
            Logger.Info?.Print(LogClass.TamperMachine, 
                $"  info.MainNsoBase: 0x{info.MainNsoBase:X}");
            Logger.Info?.Print(LogClass.TamperMachine, 
                $"  info.SecondNsoBase: 0x{info.SecondNsoBase:X}");
            Logger.Info?.Print(LogClass.TamperMachine, 
                $"  info.CheatCompileExeAddress: 0x{info.CheatCompileExeAddress:X}");
            Logger.Info?.Print(LogClass.TamperMachine, 
                $"  info.AslrAddress: 0x{info.AslrAddress:X}");
            Logger.Info?.Print(LogClass.TamperMachine, 
                $"  偏移ASLR: 0x{offsetFromAslr:X}");
            
            // 基于偏移判断模式
            bool isNceModeByOffset = offsetFromAslr == 0xE7000;
            bool isJitModeByOffset = offsetFromAslr == 0x104000;
            
            if (isNceModeByOffset)
            {
                Logger.Info?.Print(LogClass.TamperMachine,
                    $"[模式判断] 偏移0x{offsetFromAslr:X} => NCE模式");
            }
            else if (isJitModeByOffset)
            {
                Logger.Info?.Print(LogClass.TamperMachine,
                    $"[模式判断] 偏移0x{offsetFromAslr:X} => JIT模式");
            }
            else
            {
                Logger.Warning?.Print(LogClass.TamperMachine,
                    $"[模式警告] 未知偏移: 0x{offsetFromAslr:X}, 使用info.IsLikelyNceMode");
            }
            
            // 验证exeAddress，如果为0或与预期不符，使用ProcessTamperInfo中的金手指基址
            if (exeAddress == 0)
            {
                // 如果传入0，使用ProcessTamperInfo中的金手指基址
                actualExeAddress = info.CheatCompileExeAddress;
                Logger.Info?.Print(LogClass.TamperMachine,
                    $"传入的exeAddress为0，使用ProcessTamperInfo中的金手指基址: 0x{actualExeAddress:X}");
            }
            else if (exeAddress != info.CheatCompileExeAddress)
            {
                // 检查传入的地址是否在NSO地址列表中
                if (info.CodeAddresses.Contains(exeAddress))
                {
                    // 如果在列表中，保持原地址
                    Logger.Info?.Print(LogClass.TamperMachine,
                        $"exeAddress (0x{exeAddress:X}) 在NSO地址列表中，保持原地址");
                    actualExeAddress = exeAddress;
                }
                else
                {
                    // 不在列表中，使用ProcessTamperInfo中的金手指基址
                    Logger.Warning?.Print(LogClass.TamperMachine,
                        $"exeAddress (0x{exeAddress:X}) 不在NSO地址列表中，使用ProcessTamperInfo中的金手指基址: 0x{info.CheatCompileExeAddress:X}");
                    actualExeAddress = info.CheatCompileExeAddress;
                }
            }
            
            // 最终验证
            if (actualExeAddress == 0)
            {
                Logger.Error?.Print(LogClass.TamperMachine,
                    $"无法确定有效的exeAddress，跳过金手指 '{name}'");
                return;
            }
            
            // 过滤掉注释行
            var validInstructions = new List<string>();
            int commentCount = 0;
            
            foreach (var instruction in rawInstructions)
            {
                var trimmed = instruction.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }
                
                if (trimmed.StartsWith("#"))
                {
                    commentCount++;
                    Logger.Debug?.Print(LogClass.TamperMachine, $"跳过注释行: {trimmed}");
                    continue;
                }
                
                validInstructions.Add(instruction);
            }
            
            Logger.Info?.Print(LogClass.TamperMachine,
                $"[指令过滤] 原始: {rawInstructions.Count()}, 有效: {validInstructions.Count}, 注释: {commentCount}");
            
            if (validInstructions.Count == 0)
            {
                Logger.Warning?.Print(LogClass.TamperMachine,
                    $"金手指 '{name}' 没有有效指令，跳过安装");
                return;
            }
            
            // 输出最终的地址信息
            Logger.Info?.Print(LogClass.TamperMachine,
                $"[最终决定] 金手指 '{name}' 使用exeAddress: 0x{actualExeAddress:X}");
            Logger.Info?.Print(LogClass.TamperMachine,
                $"[编译器地址] Exe=0x{actualExeAddress:X}, Heap=0x{info.HeapAddress:X}, " +
                $"Alias=0x{info.AliasAddress:X}, Aslr=0x{info.AslrAddress:X}");
            
            AtmosphereCompiler compiler = new(actualExeAddress, info.HeapAddress, info.AliasAddress, info.AslrAddress, tamperedProcess);
            
            ITamperProgram program = compiler.Compile(name, validInstructions);

            if (program != null)
            {
                program.TampersCodeMemory = false;

                _programs.Enqueue(program);
                _programDictionary.TryAdd($"{buildId}-{name}", program);
                
                Logger.Info?.Print(LogClass.TamperMachine, $"成功安装金手指 '{name}' with ID {buildId}-{name}");
                Logger.Debug?.Print(LogClass.TamperMachine, $"程序队列大小: {_programs.Count}, 字典大小: {_programDictionary.Count}");
            }
            else
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"编译金手指失败: {name}");
            }

            Activate();
        }


        private static bool CanInstallOnPid(ulong pid)
        {
            // Do not allow tampering of kernel processes.
            if (pid < KernelConstants.InitialProcessId)
            {
                Logger.Warning?.Print(LogClass.TamperMachine, $"Refusing to tamper kernel process {pid}");

                return false;
            }

            return true;
        }

        public void EnableCheats(string[] enabledCheats)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"Enabling cheats: {string.Join(", ", enabledCheats)}");
            
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
                    Logger.Debug?.Print(LogClass.TamperMachine, $"Enabled cheat: {cheat}");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.TamperMachine, $"Cheat not found: {cheat}");
                }
            }
            
            Logger.Info?.Print(LogClass.TamperMachine, $"Enabled {enabledCount} cheat(s) out of {enabledCheats.Length} requested");
        }

        private static bool IsProcessValid(ITamperedProcess process)
        {
            bool isValid = process.State != ProcessState.Crashed && 
                          process.State != ProcessState.Exiting && 
                          process.State != ProcessState.Exited;
            
            if (!isValid)
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"Process is not valid for tampering: State={process.State}");
            }
            
            return isValid;
        }

        private void TamperRunner()
        {
            Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine thread running");
            
            int sleepCounter = 0;
            int executionCount = 0;

            while (true)
            {
                // Sleep to not consume too much CPU.
                if (sleepCounter == 0)
                {
                    sleepCounter = _programs.Count;
                    Logger.Debug?.Print(LogClass.TamperMachine, $"Sleeping for {TamperMachineSleepMs}ms, programs count: {_programs.Count}");
                    Thread.Sleep(TamperMachineSleepMs);
                }
                else
                {
                    sleepCounter--;
                }

                if (!AdvanceTamperingsQueue())
                {
                    // No more work to be done.
                    Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine thread exiting");
                    return;
                }
                
                executionCount++;
                if (executionCount % 100 == 0) // Log every 100 executions
                {
                    Logger.Debug?.Print(LogClass.TamperMachine, $"TamperMachine has executed {executionCount} cycles");
                }
            }
        }

        private bool AdvanceTamperingsQueue()
        {
            if (!_programs.TryDequeue(out ITamperProgram program))
            {
                // No more programs in the queue.
                Logger.Debug?.Print(LogClass.TamperMachine, "No programs in queue, clearing dictionary");
                _programDictionary.Clear();

                return false;
            }

            // Check if the process is still suitable for running the tamper program.
            if (!IsProcessValid(program.Process))
            {
                // Exit without re-enqueuing the program because the process is no longer valid.
                Logger.Warning?.Print(LogClass.TamperMachine, $"Process for program {program.Name} is no longer valid, removing from queue");
                return true;
            }

            // Re-enqueue the tampering program because the process is still valid.
            _programs.Enqueue(program);

            // Skip execution if program is not enabled
            if (!program.IsEnabled)
            {
                Logger.Debug?.Print(LogClass.TamperMachine, $"Skipping disabled program: {program.Name}");
                return true;
            }

            Logger.Debug?.Print(LogClass.TamperMachine, $"Running tampering program {program.Name}");

            try
            {
                ControllerKeys pressedKeys = (ControllerKeys)Volatile.Read(ref _pressedKeys);
                Logger.Debug?.Print(LogClass.TamperMachine, $"Current pressed keys: {pressedKeys}");
                
                program.Process.TamperedCodeMemory = false;
                program.Execute(pressedKeys);

                // Detect the first attempt to tamper memory and log it.
                if (!program.TampersCodeMemory && program.Process.TamperedCodeMemory)
                {
                    program.TampersCodeMemory = true;
                    Logger.Warning?.Print(LogClass.TamperMachine, $"Tampering program {program.Name} modifies code memory so it may not work properly");
                }
                
                Logger.Debug?.Print(LogClass.TamperMachine, $"Successfully executed program {program.Name}");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"The tampering program {program.Name} crashed: {ex.Message}");
                Logger.Debug?.Print(LogClass.TamperMachine, $"Exception details: {ex}");

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
                        Logger.Debug?.Print(LogClass.TamperMachine, $"Input updated: {input.Buttons}");
                    }
                    
                    return;
                }
            }

            // Clear the input because player one is not connected.
            long oldKeys = Volatile.Read(ref _pressedKeys);
            if (oldKeys != 0)
            {
                Volatile.Write(ref _pressedKeys, 0);
                Logger.Debug?.Print(LogClass.TamperMachine, "Input cleared (no player one connected)");
            }
        }
        
        // Add a method to get current status information for debugging
        public string GetStatus()
        {
            return $"Programs in queue: {_programs.Count}, Dictionary entries: {_programDictionary.Count}, Thread alive: {_tamperThread?.IsAlive ?? false}";
        }
    }
}
