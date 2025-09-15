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
            Logger.Debug?.Print(LogClass.TamperMachine, $"Installing Atmosphere cheat: {name} for build ID: {buildId}");
            
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

                _programs.Enqueue(program);
                _programDictionary.TryAdd($"{buildId}-{name}", program);
                
                Logger.Info?.Print(LogClass.TamperMachine, $"Successfully installed cheat '{name}' with ID {buildId}-{name}");
                Logger.Debug?.Print(LogClass.TamperMachine, $"Program queue size: {_programs.Count}, Dictionary size: {_programDictionary.Count}");
            }
            else
            {
                Logger.Error?.Print(LogClass.TamperMachine, $"Failed to compile cheat {name}");
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
        
        // 添加一个方法来获取当前状态信息，用于调试
        public string GetStatus()
        {
            return $"Programs in queue: {_programs.Count}, Dictionary entries: {_programDictionary.Count}, Thread alive: {_tamperThread?.IsAlive ?? false}";
        }
    }
}
