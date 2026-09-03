using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WandEnhancer.View.MainWindow;

namespace WandEnhancer.Core
{
    /// <summary>
    /// Starts Wand as a debugger so every Electron process is stopped before its first user-mode
    /// instruction. The ASAR-integrity fuse is cleared at the CREATE_PROCESS debug event and the
    /// process is then allowed to run. Descendants whose image is not Wand.exe are detached at
    /// that same event, before their code executes, so games do not retain a debug port.
    /// </summary>
    internal static class FuseLauncher
    {
        private const int AsarIntegrityExitCode = -36861;

        /// <returns>False when the session ended badly enough to be worth showing the user.</returns>
        public static bool Launch(string exePath, string args, Action<string, ELogType> log = null)
        {
            long stateRva = ElectronFuse.FindStateRva(exePath);
            if (stateRva < 0)
            {
                log?.Invoke($"No Electron fuse block in {exePath}. A patched Wand will exit " +
                            $"with {AsarIntegrityExitCode}; an unpatched one is unaffected.", ELogType.Error);
                return false;
            }

            var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var commandLine = new StringBuilder(
                string.IsNullOrEmpty(args) ? $"\"{exePath}\"" : $"\"{exePath}\" {args}");

            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, DEBUG_PROCESS,
                    IntPtr.Zero, Path.GetDirectoryName(exePath), ref startupInfo, out var info))
            {
                log?.Invoke($"Could not start Wand under the fuse patcher " +
                            $"(win32 error {Marshal.GetLastWin32Error()}).", ELogType.Error);
                return false;
            }

            // A launcher failure must not terminate every process still being debugged.
            DebugSetProcessKillOnExit(false);
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            log?.Invoke($"Started {exePath} as pid {info.dwProcessId}.", ELogType.Info);

            return DriveDebugLoop(info.dwProcessId, exePath, stateRva, log);
        }

        private static bool DriveDebugLoop(
            int mainProcessId, string exePath, long stateRva, Action<string, ELogType> log)
        {
            var wandProcesses = new HashSet<int>();
            var initialBreakpoints = new HashSet<int>();
            var debugEvent = new byte[DebugEventSize];
            int created = 0;
            int cleared = 0;
            int missed = 0;
            bool healthy = true;

            while (true)
            {
                if (!WaitForDebugEvent(debugEvent, INFINITE))
                {
                    log?.Invoke($"Waiting for a Wand process failed " +
                                $"(win32 error {Marshal.GetLastWin32Error()}).", ELogType.Error);
                    healthy = false;
                    break;
                }

                int code = BitConverter.ToInt32(debugEvent, OffsetDebugEventCode);
                int processId = BitConverter.ToInt32(debugEvent, OffsetProcessId);
                int threadId = BitConverter.ToInt32(debugEvent, OffsetThreadId);
                uint status = DBG_CONTINUE;
                bool detachInsteadOfContinue = false;
                bool mainExited = false;

                switch (code)
                {
                    case CREATE_PROCESS_DEBUG_EVENT:
                        var imageFile = (IntPtr)BitConverter.ToInt64(debugEvent, OffsetCreateProcessFile);
                        var process = (IntPtr)BitConverter.ToInt64(debugEvent, OffsetCreateProcessHandle);
                        var imageBase = (IntPtr)BitConverter.ToInt64(debugEvent, OffsetCreateProcessImageBase);

                        if (IsImage(imageFile, process, exePath))
                        {
                            wandProcesses.Add(processId);
                            created++;

                            if (ElectronFuse.ClearAtImageBase(
                                    process, imageBase, stateRva, out string failure))
                            {
                                cleared++;
                                log?.Invoke($"pid {processId} started - fuse cleared before execution.",
                                    ELogType.Info);
                            }
                            else
                            {
                                missed++;
                                healthy = false;
                                log?.Invoke($"Fuse not cleared in pid {processId}: {failure}. It may exit " +
                                            $"with {AsarIntegrityExitCode}.", ELogType.Error);
                            }
                        }
                        else
                        {
                            // DEBUG_PROCESS also reports games launched by Wand. Detach this one
                            // while its create event still has every thread stopped.
                            detachInsteadOfContinue = true;
                        }

                        if (imageFile != IntPtr.Zero)
                        {
                            CloseHandle(imageFile);
                        }
                        break;

                    case LOAD_DLL_DEBUG_EVENT:
                        var loadedFile = (IntPtr)BitConverter.ToInt64(debugEvent, OffsetUnion);
                        if (loadedFile != IntPtr.Zero)
                        {
                            CloseHandle(loadedFile);
                        }
                        break;

                    case EXCEPTION_DEBUG_EVENT:
                        int exceptionCode = BitConverter.ToInt32(debugEvent, OffsetExceptionCode);
                        status = exceptionCode == EXCEPTION_BREAKPOINT && initialBreakpoints.Add(processId)
                            ? DBG_CONTINUE
                            : DBG_EXCEPTION_NOT_HANDLED;

                        if (BitConverter.ToInt32(debugEvent, OffsetExceptionFirstChance) == 0)
                        {
                            log?.Invoke($"pid {processId} hit an unhandled exception: " +
                                        $"{DescribeCode(exceptionCode)}.", ELogType.Error);
                            healthy = false;
                        }
                        break;

                    case EXIT_PROCESS_DEBUG_EVENT:
                        int exitCode = BitConverter.ToInt32(debugEvent, OffsetExitCode);
                        wandProcesses.Remove(processId);
                        initialBreakpoints.Remove(processId);

                        if (exitCode != 0)
                        {
                            log?.Invoke($"pid {processId} exited with code {DescribeCode(exitCode)}.",
                                ELogType.Error);
                            if (exitCode == AsarIntegrityExitCode || processId == mainProcessId)
                            {
                                healthy = false;
                            }
                        }

                        mainExited = processId == mainProcessId;
                        break;
                }

                if (detachInsteadOfContinue)
                {
                    if (!DebugActiveProcessStop(processId))
                    {
                        int error = Marshal.GetLastWin32Error();
                        ContinueDebugEvent(processId, threadId, status);
                        log?.Invoke($"Could not detach non-Wand child pid {processId} before execution " +
                                    $"(win32 error {error}).", ELogType.Warn);
                    }
                }
                else
                {
                    ContinueDebugEvent(processId, threadId, status);
                }

                if (mainExited)
                {
                    break;
                }
            }

            foreach (int processId in wandProcesses)
            {
                DebugActiveProcessStop(processId);
            }

            log?.Invoke($"Wand closed: fuse cleared in {cleared} of {created} processes" +
                        (missed == 0 ? "." : $", {missed} missed."),
                missed == 0 ? ELogType.Info : ELogType.Error);
            return healthy && created > 0 && missed == 0;
        }

        private static bool IsImage(IntPtr imageFile, IntPtr process, string exePath)
        {
            string path = GetImagePath(imageFile);
            if (path == null)
            {
                var buffer = new StringBuilder(MaxPathLength);
                int length = buffer.Capacity;
                if (!QueryFullProcessImageName(process, 0, buffer, ref length))
                {
                    return false;
                }

                path = buffer.ToString();
            }

            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                path = path.Substring(4);
            }

            return string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetImagePath(IntPtr imageFile)
        {
            if (imageFile == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new StringBuilder(MaxPathLength);
            uint length = GetFinalPathNameByHandle(imageFile, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                return null;
            }

            return buffer.ToString();
        }

        private static string DescribeCode(int code)
        {
            switch (code)
            {
                case 0: return "0";
                case AsarIntegrityExitCode:
                    return $"{code} (ASAR integrity check failed - the fuse was not cleared in time)";
                case unchecked((int)0x80000003): return $"0x{code:X8} (Wand aborted itself during startup)";
                case unchecked((int)0xC0000005): return $"0x{code:X8} (access violation)";
                case unchecked((int)0xC0000135): return $"0x{code:X8} (a required DLL is missing)";
                case unchecked((int)0xC0000142): return $"0x{code:X8} (a DLL failed to initialise)";
                case unchecked((int)0xC0000409): return $"0x{code:X8} (stack buffer overrun)";
                default: return $"{code} (0x{code:X8})";
            }
        }

        #region P/Invoke

        // x64 DEBUG_EVENT: three DWORDs plus padding, followed by its native union.
        private const int DebugEventSize = 192;
        private const int OffsetDebugEventCode = 0;
        private const int OffsetProcessId = 4;
        private const int OffsetThreadId = 8;
        private const int OffsetUnion = 16;
        private const int OffsetExceptionCode = OffsetUnion;
        private const int OffsetExceptionFirstChance = OffsetUnion + 152;
        private const int OffsetExitCode = OffsetUnion;
        private const int OffsetCreateProcessFile = OffsetUnion;
        private const int OffsetCreateProcessHandle = OffsetUnion + 8;
        private const int OffsetCreateProcessImageBase = OffsetUnion + 24;

        private const uint DEBUG_PROCESS = 0x00000001;
        private const uint DBG_CONTINUE = 0x00010002;
        private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
        private const int EXCEPTION_DEBUG_EVENT = 1;
        private const int CREATE_PROCESS_DEBUG_EVENT = 3;
        private const int EXIT_PROCESS_DEBUG_EVENT = 5;
        private const int LOAD_DLL_DEBUG_EVENT = 6;
        private const int EXCEPTION_BREAKPOINT = unchecked((int)0x80000003);
        private const int INFINITE = -1;
        private const int MaxPathLength = 32768;

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WaitForDebugEvent(byte[] lpDebugEvent, int dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ContinueDebugEvent(
            int dwProcessId, int dwThreadId, uint dwContinueStatus);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugActiveProcessStop(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugSetProcessKillOnExit(bool killOnExit);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(
            IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion
    }
}
