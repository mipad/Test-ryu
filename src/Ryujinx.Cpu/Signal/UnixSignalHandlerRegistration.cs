using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.Signal
{
    static partial class UnixSignalHandlerRegistration
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public unsafe struct SigSet
        {
            fixed long sa_mask[16];
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct SigAction
        {
            public IntPtr sa_handler;
            public SigSet sa_mask;
            public int sa_flags;
            public IntPtr sa_restorer;
        }

        [SupportedOSPlatform("android"), StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct SigActionBionic
        {
            public int sa_flags;
            public IntPtr sa_handler;
            public SigSet sa_mask;
            public IntPtr sa_restorer;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct Stack
        {
            public IntPtr ss_sp;
            public int ss_flags;
            public IntPtr ss_size;
        }

        private const int SIGSEGV = 11;
        private const int SIGBUS = 10;
        private const int SA_SIGINFO = 0x00000004;
        private const int SA_ONSTACK = 0x08000000;
        private const int SS_DISABLE = 2;
        private const int SS_AUTODISARM = 1 << 31;

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigaction(int signum, ref SigAction sigAction, out SigAction oldAction);

        [SupportedOSPlatform("android"), LibraryImport("libc", SetLastError = true)]
        private static partial int sigaction(int signum, ref SigActionBionic sigAction, out SigActionBionic oldAction);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigaction(int signum, IntPtr sigAction, out SigAction oldAction);

        [SupportedOSPlatform("android"), LibraryImport("libc", SetLastError = true)]
        private static partial int sigaction(int signum, IntPtr sigAction, out SigActionBionic oldAction);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigemptyset(ref SigSet set);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigaltstack(ref Stack ss, out Stack oldSs);

        [UnconditionalSuppressMessage("", "CA1416")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SigAction ConvertBionicToSigAction(in SigActionBionic bionicAction)
        {
            return new SigAction
            {
                sa_handler = bionicAction.sa_handler,
                sa_mask = bionicAction.sa_mask,
                sa_flags = bionicAction.sa_flags,
                sa_restorer = bionicAction.sa_restorer
            };
        }

        [UnconditionalSuppressMessage("", "CA1416")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SigActionBionic ConvertSigActionToBionic(in SigAction action)
        {
            return new SigActionBionic
            {
                sa_handler = action.sa_handler,
                sa_mask = action.sa_mask,
                sa_flags = action.sa_flags,
                sa_restorer = action.sa_restorer
            };
        }

        public static SigAction GetSegfaultExceptionHandler()
        {
            int result;
            SigAction old;

            if (Ryujinx.Common.PlatformInfo.IsBionic)
            {
#pragma warning disable CA1416
                result = sigaction(SIGSEGV, IntPtr.Zero, out SigActionBionic tmp);
                old = ConvertBionicToSigAction(tmp);
#pragma warning restore CA1416
            }
            else
            {
                result = sigaction(SIGSEGV, IntPtr.Zero, out old);
            }

            if (result != 0)
            {
                throw new SystemException($"Could not get SIGSEGV sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
            }

            return old;
        }

        public static SigAction RegisterExceptionHandler(IntPtr action)
        {
            int result;
            SigAction old;

            if (Ryujinx.Common.PlatformInfo.IsBionic)
            {
#pragma warning disable CA1416
                SigActionBionic sig = new()
                {
                    sa_handler = action,
                    sa_flags = SA_SIGINFO | SA_ONSTACK,
                };

                sigemptyset(ref sig.sa_mask);

                result = sigaction(SIGSEGV, ref sig, out SigActionBionic tmp);
                old = ConvertBionicToSigAction(tmp);
#pragma warning restore CA1416
            }
            else
            {
                SigAction sig = new SigAction
                {
                    sa_handler = action,
                    sa_flags = SA_SIGINFO,
                };

                sigemptyset(ref sig.sa_mask);

                result = sigaction(SIGSEGV, ref sig, out old);

                if (result != 0)
                {
                    throw new SystemException($"Could not register SIGSEGV sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
                }

                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    result = sigaction(SIGBUS, ref sig, out _);

                    if (result != 0)
                    {
                        throw new SystemException($"Could not register SIGBUS sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
                    }
                }
            }

            return old;
        }

        public static void RegisterAlternateStack(IntPtr stackPtr, ulong stackSize)
        {
            Stack stack = new()
            {
                ss_sp = stackPtr,
                ss_flags = SS_AUTODISARM,
                ss_size = (IntPtr)stackSize
            };

            int result = sigaltstack(ref stack, out _);

            if (result != 0)
            {
                throw new SystemException($"Could not set alternate stack. Error: {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }

        public static void UnregisterAlternateStack()
        {
            Stack stack = new()
            {
                ss_flags = SS_DISABLE
            };

            int result = sigaltstack(ref stack, out _);

            if (result != 0)
            {
                throw new SystemException($"Could not remove alternate stack. Error: {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }

        public static void RegisterExceptionHandler(int sigNum, IntPtr action)
        {
            int result;

            if (Ryujinx.Common.PlatformInfo.IsBionic)
            {
#pragma warning disable CA1416
                SigActionBionic sig = new()
                {
                    sa_handler = action,
                    sa_flags = SA_SIGINFO | SA_ONSTACK
                };

                sigemptyset(ref sig.sa_mask);

                result = sigaction(sigNum, ref sig, out SigActionBionic oldu);

                if (OperatingSystem.IsAndroid() && oldu.sa_handler != IntPtr.Zero)
                {
                    throw new InvalidOperationException($"SIG{sigNum} is already in use.");
                }
#pragma warning restore CA1416
            }
            else
            {
                SigAction sig = new()
                {
                    sa_handler = action,
                    sa_flags = SA_SIGINFO | SA_ONSTACK,
                };

                sigemptyset(ref sig.sa_mask);

                result = sigaction(sigNum, ref sig, out SigAction oldu);

                if (oldu.sa_handler != IntPtr.Zero)
                {
                    throw new InvalidOperationException($"SIG{sigNum} is already in use.");
                }
            }

            if (result != 0)
            {
                throw new SystemException($"Could not register SIG{sigNum} sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }

        public static bool RestoreExceptionHandler(SigAction oldAction)
        {
            if (Ryujinx.Common.PlatformInfo.IsBionic)
            {
#pragma warning disable CA1416
                if (!OperatingSystem.IsAndroid())
                {
                    return false;
                }

                var tmp = ConvertSigActionToBionic(oldAction);
                return sigaction(SIGSEGV, ref tmp, out SigActionBionic _) == 0;
#pragma warning restore CA1416
            }
            else
            {
                bool success = sigaction(SIGSEGV, ref oldAction, out SigAction _) == 0;

                if (success && (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()))
                {
                    success = sigaction(SIGBUS, ref oldAction, out SigAction _) == 0;
                }

                return success;
            }
        }
    }
}
