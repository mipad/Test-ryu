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
        private bool _hasEnabledCheats = false;

        private void Activate()
        {
            if (_tamperThread == null || !_tamperThread.IsAlive)
            {
                if (_hasEnabledCheats)
                {
                    Logger.Debug?.Print(LogClass.TamperMachine, "Activating TamperMachine thread");
                    
                    _tamperThread = new Thread(this.TamperRunner)
                    {
                        Name = "HLE.TamperMachine",
                    };
                    _tamperThread.Start();
                }
            }
        }

        private void Deactivate()
        {
            if (_tamperThread != null && _tamperThread.IsAlive)
            {
                Logger.Debug?.Print(LogClass.TamperMachine, "Deactivating TamperMachine thread");
                
                // 清空队列，让线程自然退出
                while (_programs.TryDequeue(out _)) { }
                
                // 等待线程安全退出
                if (!_tamperThread.Join(1000))
                {
                    Logger.Warning?.Print(LogClass.TamperMachine, "TamperMachine thread did not exit gracefully");
                }
            }
        }

        internal void InstallAtmosphereCheat(string name, string buildId, IEnumerable<string> rawInstructions, ProcessTamperInfo info, ulong exeAddress)
        {
            Logger.Debug?.Print(LogClass.TamperMachine, $"Installing Atmosphere cheat: {name} for build ID: {buildId}");
            
            if (!CanInstallOnPid(info.Process.Pid))
            {
                Logger.Warning?.Print(LogClass.TamperMachine, $"Cannot install cheat {name} on process {info.Process.Pid}");
                return;
            }

            ITamperedProcess tamperedProcess = new TamperedKProcess(info.Process);
            AtmosphereCompiler compiler = new(exeAddress, info.HeapAddress, info.AliasAddress, info.AslrAddress, tamperedProcess);
            
            ITamperProgram program = compiler.Compile(name, rawInstructions);

            if (program != null)
            {
                program.TampersCodeMemory = false;
                program.IsEnabled = false; // 默认禁用

                _programs.Enqueue(program);
                _programDictionary.TryAdd($"{buildId}-{name}", program);
                
                Logger.Info?.Print(LogClass.TamperMachine, $"Successfully installed cheat '{name}'");
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
            if (enabledCheats == null || enabledCheats.Length == 0)
            {
                // 没有启用的金手指，关闭服务
                _hasEnabledCheats = false;
                foreach (var program in _programDictionary.Values)
                {
                    program.IsEnabled = false;
                }
                Deactivate();
                Logger.Info?.Print(LogClass.TamperMachine, "No cheats enabled, TamperMachine deactivated");
                return;
            }

            bool anyEnabled = false;
            
            // 禁用所有金手指
            foreach (var program in _programDictionary.Values)
            {
                program.IsEnabled = false;
            }

            // 启用指定的金手指
            foreach (var cheat in enabledCheats)
            {
                if (_programDictionary.TryGetValue(cheat, out var program))
                {
                    program.IsEnabled = true;
                    anyEnabled = true;
                }
                else
                {
                    Logger.Warning?.Print(LogClass.TamperMachine, $"Cheat not found: {cheat}");
                }
            }

            _hasEnabledCheats = anyEnabled;
            
            if (_hasEnabledCheats)
            {
                // 重建队列，只包含启用的金手指
                while (_programs.TryDequeue(out _)) { }
                
                foreach (var program in _programDictionary.Values)
                {
                    if (program.IsEnabled)
                    {
                        _programs.Enqueue(program);
                    }
                }
                
                Activate();
                Logger.Info?.Print(LogClass.TamperMachine, $"Activated TamperMachine with {enabledCheats.Length} cheat(s)");
            }
            else
            {
                Deactivate();
                Logger.Info?.Print(LogClass.TamperMachine, "No valid cheats enabled, TamperMachine deactivated");
            }
        }

        private static bool IsProcessValid(ITamperedProcess process)
        {
            return process.State != ProcessState.Crashed && 
                   process.State != ProcessState.Exiting && 
                   process.State != ProcessState.Exited;
        }

        private void TamperRunner()
        {
            Logger.Info?.Print(LogClass.TamperMachine, "TamperMachine thread started");
            
            int executionCount = 0;

            while (true)
            {
                // 如果没有启用的金手指，退出线程
                if (!_hasEnabledCheats)
                {
                    Logger.Debug?.Print(LogClass.TamperMachine, "No enabled cheats, exiting thread");
                    break;
                }

                // 休眠以减少CPU使用
                Thread.Sleep(TamperMachineSleepMs);

                if (!AdvanceTamperingsQueue())
                {
                    // 没有更多工作要做
                    Logger.Debug?.Print(LogClass.TamperMachine, "No programs to execute, exiting thread");
                    break;
                }
                
                executionCount++;
                // 减少日志频率，每1000次执行记录一次
                if (executionCount % 1000 == 0)
                {
                    Logger.Debug?.Print(LogClass.TamperMachine, $"TamperMachine executed {executionCount} cycles");
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

            // 检查进程是否仍然有效
            if (!IsProcessValid(program.Process))
            {
                // 进程无效，不移除程序但记录
                Logger.Warning?.Print(LogClass.TamperMachine, $"Process for program {program.Name} is no longer valid");
                return true;
            }

            // 重新入队以保持循环
            _programs.Enqueue(program);

            // 跳过禁用的程序
            if (!program.IsEnabled)
            {
                return true;
            }

            try
            {
                ControllerKeys pressedKeys = (ControllerKeys)Volatile.Read(ref _pressedKeys);
                program.Execute(pressedKeys);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"Tampering program {program.Name} crashed: {ex.Message}");
            }

            return true;
        }

        public void UpdateInput(List<GamepadInput> gamepadInputs)
        {
            // 查找玩家1或手持设备的输入
            foreach (GamepadInput input in gamepadInputs)
            {
                if (input.PlayerId == PlayerIndex.Player1 || input.PlayerId == PlayerIndex.Handheld)
                {
                    long previousKeys = Volatile.Read(ref _pressedKeys);
                    Volatile.Write(ref _pressedKeys, (long)input.Buttons);
                    return;
                }
            }

            // 清除输入，因为玩家1未连接
            long oldKeys = Volatile.Read(ref _pressedKeys);
            if (oldKeys != 0)
            {
                Volatile.Write(ref _pressedKeys, 0);
            }
        }
        
        public string GetStatus()
        {
            int enabledCount = 0;
            foreach (var program in _programDictionary.Values)
            {
                if (program.IsEnabled) enabledCount++;
            }
            
            return $"Total cheats: {_programDictionary.Count}, Enabled: {enabledCount}, Thread alive: {_tamperThread?.IsAlive ?? false}";
        }
    }
}
