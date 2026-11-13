using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QA.AutomationStudio.Desktop;

// WHAT: Send only the F3 trigger, or Ctrl+N to create a verified modern Notepad test tab.
// WHY: Output F must originate in MacroStudio, never in the QA input adapter.
// CONTRACT: x64 INPUT=40 bytes, ordered DOWN/UP, native counts/errors and integrity levels recorded.
// FAILURE: Partial/zero insertion throws; best-effort KEYUP cleanup is recorded, never treated as success.
// TEST: Actual GUI run records SendInput 2/2 separately from the observed text assertion.
internal static class WindowsTriggerInput
{
    internal sealed record InputCall(DateTimeOffset At, string Purpose, string[] Events,
        int InputSize, int Requested, uint Inserted, int Win32Error);
    internal sealed record SecurityLevel(int ProcessId, int IntegrityRid, bool Elevated, bool UiAccess);
    internal static void F3(Action<InputCall> record) => Send("MacroStudio F3 trigger", [(0x72, false), (0x72, true)], record);
    internal static void NewNotepadTab(Action<InputCall> record) => Send("Create new modern Notepad tab (Ctrl+N)",
        [(0x11, false), (0x4e, false), (0x4e, true), (0x11, true)], record);
    private static void Send(string purpose, (ushort Key, bool Up)[] events, Action<InputCall> record)
    {
        int size = Marshal.SizeOf<Input>();
        if (size != 40 || IntPtr.Size != 8) throw new PlatformNotSupportedException("Windows x64 INPUT layout must be 40 bytes");
        var inputs = events.Select(e => new Input { Type = 1, Data = new() { Keyboard = new()
            { VirtualKey = e.Key, ScanCode = (ushort)Native.MapVirtualKey(e.Key, 0), Flags = e.Up ? 2u : 0u, ExtraInfo = (UIntPtr)0x51415354 } } }).ToArray();
        Native.SetLastError(0);
        uint sent = Native.SendInput((uint)inputs.Length, inputs, size);
        int error = Marshal.GetLastWin32Error();
        record(new(DateTimeOffset.UtcNow, purpose, events.Select(e => $"VK 0x{e.Key:X2} {(e.Up ? "KEYUP" : "KEYDOWN")}").ToArray(), size, inputs.Length, sent, error));
        if (sent == inputs.Length) return;
        var releases = events.Select(e => e.Key).Distinct().Reverse().Select(key => new Input { Type = 1,
            Data = new() { Keyboard = new() { VirtualKey = key, Flags = 2, ExtraInfo = (UIntPtr)0x51415354 } } }).ToArray();
        Native.SetLastError(0);
        uint released = Native.SendInput((uint)releases.Length, releases, size);
        int releaseError = Marshal.GetLastWin32Error();
        record(new(DateTimeOffset.UtcNow, "Partial SendInput KEYUP cleanup", releases.Select(i => $"VK 0x{i.Data.Keyboard.VirtualKey:X2} KEYUP").ToArray(), size, releases.Length, released, releaseError));
        throw new Win32Exception(error, $"{purpose}: SendInput {sent}/{inputs.Length}; Win32Error={error}. UIPI/권한 또는 입력 데스크톱을 확인하세요.");
    }
    internal static SecurityLevel InspectSecurity(int pid)
    {
        IntPtr process = Native.OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"PID {pid} 권한 조회 실패");
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!Native.OpenProcessToken(process, 8, out token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
            int Dword(int info)
            {
                IntPtr data = Marshal.AllocHGlobal(4);
                try
                {
                    if (!Native.GetTokenInformation(token, info, data, 4, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return Marshal.ReadInt32(data);
                }
                finally { Marshal.FreeHGlobal(data); }
            }
            Native.GetTokenInformation(token, 25, IntPtr.Zero, 0, out int length);
            if (length <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "TokenIntegrityLevel size failed");
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!Native.GetTokenInformation(token, 25, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                byte count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                if (count == 0) throw new InvalidDataException("Invalid integrity SID");
                int rid = Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
                return new(pid, rid, Dword(20) != 0, Dword(26) != 0);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { if (token != IntPtr.Zero) Native.CloseHandle(token); Native.CloseHandle(process); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputData { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);
        [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("kernel32.dll")] internal static extern void SetLastError(uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(IntPtr token, int info, IntPtr data, int size, out int length);
        [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    }
}
