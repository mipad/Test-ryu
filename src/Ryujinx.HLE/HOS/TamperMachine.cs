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
        private bool _isRunning = false;
        private readonly object _threadLock = new object();

        private void ActivateIfNeeded()
        {
            lock (_threadLock)
            {
                // 检查是否有至少一个启用的金手指
                bool hasEnabledCheat = false;
                foreach (var program in _programDictionary.Values)
                {
                    if (program.IsEnabled)
                    {
                        hasEnabledCheat = true;
                        break;
                    }
                }

                // 如果有启用的金手指且线程未运行，则启动线程
                if (hasEnabledCheat && (!_isRunning || _tamperThread == null || !_tamperThread.IsAlive))
                {
                    Logger.Debug?.Print(LogClass.TamperMachine, "Activating TamperMachine thread");
                    
                    _isRunning = true;
                    _tamperThread = new Thread(this.TamperRunner)
                    {
                        Name = "HLE.TamperMachine",
                        IsBackground = true
                    };
                    _tamperThread.Start();
                }
                else if (!hasEnabledCheat && _isRunning)
                {
                    // 如果没有启用的金手指且线程正在运行，则停止线程
                    _isRunning = false;
                    Logger.Debug?.Print(LogClass.TamperMachine, "No enabled cheats, TamperMachine will stop");
                }
            }
        }

        internal void InstallAtmosphereCheat(string name, string buildId, IEnumerable<string> rawInstructions, ProcessTamperInfo info, ulong exeAddress)
        {
            Logger.Info?.Print(LogClass.TamperMachine, $"Installing Atmosphere cheat: {name} for build ID: {buildId}");
            
            if (!CanInstallOnPid(info.Process.Pid))
            {
                Logger.Warning?.Print(LogClass.TamperMachine, $"Cannot install cheat {name} on process {info.Process.Pid}");
                return;
            }

            ITamperedProcess tamperedProcess = new TamperedKProcess(info.Process);
            AtmosphereCompiler compiler = new(exeAddress, info.HeapAddress, info.AliasAddress, info.AslrAddress, tamperedProcess);
            
            Logger.Debug?.Print(LogClass.TamperMachine, $"Compiling cheat {name} with addresses: Exe=0x{exeAddress:X}, Heap=0x{info.HeapAddress:X}, Alias=0x{info.AliasAddress:X}, Aslr=0x{info.AslrAddress:X}");
            
            ITamperProgram program = compiler.Compile(name, rawInstructions);

            if (program != null)
            {
                program.TampersCodeMemory = false;
                // 默认禁用新安装的金手指
                program.IsEnabled = false;

                _programs.Enqueue(program);
                _programDictionary.TryAdd($"{buildId}-{name}", program);
                
                Logger.Info?.Print(LogClass.TamperMachine, $"Successfully installed cheat '{name}' with ID {buildId}-{name}");
            }
            else
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"Failed to compile cheat {name}");
            }
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
            Logger.Info?.Print(LogClass.TamperMachine, $"Enabling {enabledCheats.Length} cheat(s)");
            
            // 首先禁用所有金手指
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
                }
                else
                {
                    Logger.Warning?.Print(LogClass.TamperMachine, $"Cheat not found: {cheat}");
                }
            }
            
            Logger.Info?.Print(LogClass.TamperMachine, $"Enabled {enabledCount} cheat(s)");
            
            // 根据启用状态管理服务线程
            ActivateIfNeeded();
        }

        private static bool IsProcessValid(ITamperedProcess process)
        {
            bool isValid = process.State != ProcessState.Crashed && 
                          process.State != ProcessState.Exiting && 
                          process.State != ProcessState.Exited;
            
            return isValid;
        }

        private void TamperRunner()
        {
            Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine thread started");
            
            int sleepCounter = 0;

            while (_isRunning)
            {
                // 检查是否还有启用的金手指
                bool hasEnabledCheat = false;
                foreach (var program in _programDictionary.Values)
                {
                    if (program.IsEnabled)
                    {
                        hasEnabledCheat = true;
                        break;
                    }
                }

                // 如果没有启用的金手指，退出线程
                if (!hasEnabledCheat)
                {
                    Logger.Info?.Print(LogClass.TamperMachine, "No enabled cheats, stopping TamperMachine thread");
                    _isRunning = false;
                    break;
                }

                // 睡眠以降低CPU使用率
                if (sleepCounter == 0)
                {
                    sleepCounter = _programs.Count;
                    Thread.Sleep(TamperMachineSleepMs);
                }
                else
                {
                    sleepCounter--;
                }

                if (!AdvanceTamperingsQueue())
                {
                    // 没有更多工作要做
                    Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine thread exiting");
                    _isRunning = false;
                    break;
                }
            }
            
            Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine thread stopped");
        }

        private bool AdvanceTamperingsQueue()
        {
            if (!_programs.TryDequeue(out ITamperProgram program))
            {
                // 队列中没有程序
                return false;
            }

            // 检查进程是否仍然适合运行金手指程序
            if (!IsProcessValid(program.Process))
            {
                // 进程不再有效，从字典中移除
                Logger.Warning?.Print(LogClass.TamperMachine, $"Process for program {program.Name} is no longer valid, removing");
                RemoveProgramFromDictionary(program);
                return true;
            }

            // 重新入队金手指程序
            _programs.Enqueue(program);

            // 如果程序未启用，跳过执行
            if (!program.IsEnabled)
            {
                return true;
            }

            try
            {
                ControllerKeys pressedKeys = (ControllerKeys)Volatile.Read(ref _pressedKeys);
                
                program.Process.TamperedCodeMemory = false;
                program.Execute(pressedKeys);

                // 检测首次尝试篡改内存并记录
                if (!program.TampersCodeMemory && program.Process.TamperedCodeMemory)
                {
                    program.TampersCodeMemory = true;
                    Logger.Warning?.Print(LogClass.TamperMachine, $"Tampering program {program.Name} modifies code memory so it may not work properly");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"The tampering program {program.Name} crashed: {ex.Message}");
                
                // 即使崩溃也重新入队程序，因为可能是临时问题
                _programs.Enqueue(program);
            }

            return true;
        }

        private void RemoveProgramFromDictionary(ITamperProgram program)
        {
            string keyToRemove = null;
            foreach (var kvp in _programDictionary)
            {
                if (kvp.Value == program)
                {
                    keyToRemove = kvp.Key;
                    break;
                }
            }
            
            if (keyToRemove != null)
            {
                _programDictionary.Remove(keyToRemove);
            }
        }

        public void UpdateInput(List<GamepadInput> gamepadInputs)
        {
            // 查找玩家一或手持设备的输入
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

            // 清除输入，因为玩家一未连接
            long oldKeys = Volatile.Read(ref _pressedKeys);
            if (oldKeys != 0)
            {
                Volatile.Write(ref _pressedKeys, 0);
                Logger.Debug?.Print(LogClass.TamperMachine, "Input cleared (no player one connected)");
            }
        }
        
        // 添加一个方法来获取当前状态信息，用于调试
        public string GetStatus()
        {
            int enabledCount = 0;
            foreach (var program in _programDictionary.Values)
            {
                if (program.IsEnabled)
                {
                    enabledCount++;
                }
            }
            
            return $"Programs installed: {_programDictionary.Count}, Enabled: {enabledCount}, Thread running: {_isRunning}";
        }
        
        // 添加一个方法来停止所有金手指服务
        public void StopAllCheats()
        {
            Logger.Info?.Print(LogClass.TamperMachine, "Stopping all cheats");
            
            foreach (var program in _programDictionary.Values)
            {
                program.IsEnabled = false;
            }
            
            _isRunning = false;
            Logger.Info?.Print(LogClass.TamperMachine, "All cheats disabled");
        }
    }
}
