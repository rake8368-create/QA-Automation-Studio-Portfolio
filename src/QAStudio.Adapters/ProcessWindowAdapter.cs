using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;

namespace QAStudio.Adapters;

// WHAT: Launch a chosen EXE or inspect the explicitly selected running process.
// WHY: Project selection does not identify the executable or process under test.
// CONTRACT: Attach verifies PID, start time and path; attached processes are never closed or killed.
// WHY: A surviving process, old instance, duplicate dialog or hidden startup-check is not a visible main window.
// CONTRACT: Exact executable path, own PID, unowned WinForms main window, matching title, visible/non-minimized,
//          positive bounds and responding WM_NULL must remain true for at least 600 ms. No input is dispatched.
// FAILURE: Existing target/launch fault => ERROR; exit/no valid window within deadline => FAIL.
// TEST: Actual published MacroStudio window; missing executable ERROR; real console process FAIL.
public sealed class ProcessWindowAdapter : IProcessWindowAdapter
{
    public Task<LaunchObservation> ExecuteAsync(string runId, LaunchSmokeRequest request) => Task.Run(() => Execute(runId, request));
    private static LaunchObservation Execute(string runId, LaunchSmokeRequest request)
    {
        var timer = Stopwatch.StartNew(); Process? owned = null;
        bool attached = request.ExistingProcessId.HasValue;
        string assertion = (attached ? "Explicitly selected target PID owns" : "New target PID owns") + " a visible, non-minimized, responsive top-level WinForms main window with the expected title and positive bounds for >=600ms";
        string path = Path.GetFullPath(request.Executable);
        var observations = new List<object>();
        ExecutionResult result = ExecutionResult.Error; string reason = ""; string hash = ""; int? pid = null, exit = null;
        IntPtr stableHandle = IntPtr.Zero; long stableSince = 0; long confirmedWindowMilliseconds = 0;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows native window inspection required");
            if (request.TimeoutMilliseconds < 1000 || request.TimeoutMilliseconds > 60000) throw new ArgumentOutOfRangeException(nameof(request.TimeoutMilliseconds));
            if (!File.Exists(path)) throw new FileNotFoundException("대상 EXE를 찾을 수 없습니다.", path);
            if (!attached) foreach (var existing in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            using (existing)
            {
                if (!existing.HasExited && string.Equals(existing.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"대상 프로그램이 이미 실행 중입니다 (PID {existing.Id}). 실행 중인 프로세스 선택으로 연결하세요.");
            }
            using (var file = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(file));
            if (attached)
            {
                if (request.ExpectedStartTimeUtc is null) throw new InvalidOperationException("프로세스 식별 정보가 없습니다. 대상을 다시 선택하세요.");
                owned = Process.GetProcessById(request.ExistingProcessId!.Value);
                if (owned.HasExited || owned.StartTime.ToUniversalTime() != request.ExpectedStartTimeUtc.Value.ToUniversalTime())
                    throw new InvalidOperationException("선택한 프로세스가 종료되거나 변경되었습니다. 대상을 다시 선택하세요.");
                if (!string.Equals(owned.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("선택한 프로세스의 EXE 경로가 다릅니다. 대상을 다시 선택하세요.");
            }
            else
            {
                var info = new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Normal };
                foreach (var arg in request.Arguments ?? []) info.ArgumentList.Add(arg);
                owned = Process.Start(info) ?? throw new InvalidOperationException("Process.Start did not return a process");
            }
            pid = owned.Id; result = ExecutionResult.Fail;
            long lastObservation = -1000;
            while (timer.ElapsedMilliseconds < request.TimeoutMilliseconds)
            {
                owned.Refresh();
                if (owned.HasExited) { exit = owned.ExitCode; reason = "메인 창 검증 전에 대상 프로세스가 종료되었습니다."; break; }
                string actual = owned.MainModule?.FileName ?? "";
                if (!string.Equals(actual, path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Started process executable path mismatch");
                IntPtr match = IntPtr.Zero;
                Native.EnumWindows((handle, parameter) =>
                {
                    Native.GetWindowThreadProcessId(handle, out uint ownerPid);
                    if (ownerPid != pid || Native.GetWindow(handle, 4) != IntPtr.Zero) return true;
                    var title = new StringBuilder(1024); Native.GetWindowText(handle, title, title.Capacity);
                    var className = new StringBuilder(256); Native.GetClassName(handle, className, className.Capacity);
                    Native.GetWindowRect(handle, out var rect);
                    bool visible = Native.IsWindowVisible(handle), minimized = Native.IsIconic(handle);
                    int dwmResult = Native.DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int));
                    bool transparent = Native.GetLayeredWindowAttributes(handle, out _, out byte alpha, out uint flags) && (flags & 2) != 0 && alpha == 0;
                    bool onMonitor = Native.MonitorFromWindow(handle, 0) != IntPtr.Zero;
                    bool candidate = visible && !minimized && dwmResult == 0 && cloaked == 0 && !transparent && onMonitor && rect.Right > rect.Left && rect.Bottom > rect.Top &&
                        title.ToString().Contains(request.TitleFragment, StringComparison.Ordinal) &&
                        className.ToString().StartsWith("WindowsForms10.Window", StringComparison.Ordinal);
                    bool responsive = candidate && Native.SendMessageTimeout(handle, 0, IntPtr.Zero, IntPtr.Zero, 2, 300, out _) != IntPtr.Zero;
                    if (timer.ElapsedMilliseconds - lastObservation >= 400)
                        observations.Add(new { milliseconds = timer.ElapsedMilliseconds, pid = ownerPid, hwnd = handle.ToInt64(), title = title.ToString(), className = className.ToString(), visible, minimized, cloaked, transparent, onMonitor, responsive, rect.Left, rect.Top, rect.Right, rect.Bottom });
                    if (responsive) { match = handle; return false; }
                    return true;
                }, IntPtr.Zero);
                if (timer.ElapsedMilliseconds - lastObservation >= 400) lastObservation = timer.ElapsedMilliseconds;
                if (match != IntPtr.Zero)
                {
                    if (match != stableHandle) { stableHandle = match; stableSince = timer.ElapsedMilliseconds; }
                    else if (timer.ElapsedMilliseconds - stableSince >= 600)
                    { confirmedWindowMilliseconds = timer.ElapsedMilliseconds - stableSince; result = ExecutionResult.Pass; reason = "지정한 대상 프로세스의 실제 메인 창 표시와 응답을 확인했습니다."; break; }
                }
                else { stableHandle = IntPtr.Zero; stableSince = 0; }
                Thread.Sleep(100);
            }
            if (reason == "") reason = "제한 시간 안에 표시되는 실제 메인 창을 확인하지 못했습니다.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            result = ExecutionResult.Error; reason = ex.GetType().Name + ": " + ex.Message;
            if (!attached && owned is not null && owned.HasExited)
            { result = ExecutionResult.Fail; exit = owned.ExitCode; reason = "메인 창 검증 전에 대상 프로세스가 종료되었습니다. " + reason; }
        }
        finally
        {
            // Leave a successful target visible for the user; only our failed attempt may be closed.
            if (owned is not null)
            {
                try { if (!attached && result != ExecutionResult.Pass && !owned.HasExited) { owned.CloseMainWindow(); if (!owned.WaitForExit(1500)) owned.Kill(true); } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { observations.Add(new { cleanupError = ex.Message }); }
                owned.Dispose();
            }
        }
        Directory.CreateDirectory(request.EvidenceDirectory);
        string evidence = Path.Combine(request.EvidenceDirectory, runId + ".json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { runId, result = ResultText.Format(result), assertion, reason, executable = path,
            mode = attached ? "attach" : "launch", selectedProcessId = request.ExistingProcessId, expectedStartTimeUtc = request.ExpectedStartTimeUtc, executableSha256 = hash, pid, exitCode = exit, elapsedMilliseconds = timer.ElapsedMilliseconds, stableWindowHwnd = stableHandle.ToInt64(), confirmedWindowMilliseconds,
            expectedTitleFragment = request.TitleFragment, arguments = request.Arguments ?? [], observations }, new JsonSerializerOptions { WriteIndented = true });
        using (var file = new FileStream(evidence, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(true); }
        return new(result, assertion, evidence, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static class Native
    {
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}


