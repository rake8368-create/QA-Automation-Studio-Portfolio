using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using QAStudio.Application;

namespace QA.AutomationStudio.Desktop;

// WHAT: Prepare a safe Notepad document, focus it, emit F3, and read its actual UIA text.
// WHY: Titles/screenshots/old MacroStudio logs cannot prove actual text delivery.
// CONTRACT: Never types F or clears/closes documents; a new modern tab is verified before F3.
// FAILURE: Ambiguous editor, stale PID/document, lost focus, IME uncertainty or read fault => ERROR.
// TEST: Real Notepad UIA observation plus runner failure tests; no OCR/clipboard dependency.
internal sealed class NotepadTextProbe(SelectedTarget source, SelectedTarget? selectedNotepad = null, bool doubleOutput = false,
    bool requireMacroOff = false, bool requireTargetMismatch = false, bool twoSingleFTriggers = false) : IMacroOffTextProbe, ITargetMismatchTextProbe, ITextTrigger
{
    private SelectedTarget? notepad = selectedNotepad;
    private SelectedTarget? restrictionTarget;
    private string configuredRestriction = "";
    private AutomationElement? root, editor;
    private IntPtr hwnd;
    private string identity = "", reader = "";
    private readonly Dictionary<string, object?> diagnostics = new() { ["method"] = "Windows.SendInput F3 KEYDOWN -> KEYUP", ["directOutputKeyDispatched"] = false };
    private readonly List<WindowsTriggerInput.InputCall> calls = [];
    public object Diagnostics => diagnostics;
    // WHAT/WHY: Negative runs must prove OFF independently of observing an empty editor.
    // CONTRACT: Read the designated QA process's explicit OFF control; never toggle its state.
    // FAILURE: ON, ambiguous state, wrong process or double-output mode is ERROR.
    // TEST: Day7 real OFF preconditions plus scripted verifier failures in the orchestrator.
    public void VerifyMacroOff()
    {
        if (!requireMacroOff || doubleOutput) throw new InvalidOperationException("OFF 전용 검증 모드가 아닙니다.");
        CheckSource();
        using var process = VerifyProcess(source);
        var window = AutomationElement.FromHandle(process.MainWindowHandle);
        bool HasButton(string name) => window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name))) is not null;
        if (!HasButton("매크로 켜기") || HasButton("매크로 끄기"))
            throw new InvalidOperationException("MacroStudio OFF 상태를 확인하지 못했습니다. 상태를 변경하지 않고 중단합니다.");
        diagnostics["macroOffVerified"] = true;
        diagnostics["macroOffLastVerifiedAt"] = DateTimeOffset.UtcNow;
    }
    // WHAT: Verify ON, enabled restriction and a live safe target different from focused Notepad.
    // WHY: Silence with OFF/disabled restriction/missing target does not prove target blocking.
    // CONTRACT: Read configuration only; charmap is a restriction reference and receives no input.
    // FAILURE: Any uncertain source, setting, target identity or foreground mismatch is ERROR.
    // TEST: Day8 state-negative fixtures; real QA/charmap/Notepad identity evidence around F3.
    public void VerifyTargetMismatch()
    {
        if (!requireTargetMismatch || requireMacroOff || doubleOutput)
            throw new InvalidOperationException("대상 불일치 전용 단일 F 검증 모드가 아닙니다.");
        CheckSource(); InspectSourceConfiguration();
        var selected = restrictionTarget ?? throw new InvalidOperationException("제한 대상 프로세스가 없습니다.");
        using var restricted = VerifyProcess(selected);
        if (restricted.MainWindowHandle == IntPtr.Zero || !Native.IsWindowVisible(restricted.MainWindowHandle))
            throw new InvalidOperationException("실행 중인 문자표의 실제 창을 확인하지 못했습니다.");
        if (notepad is null || hwnd == IntPtr.Zero || !HasFocus())
            throw new InvalidOperationException("대상 불일치 검사 중 Notepad 포커스를 확인하지 못했습니다.");
        IntPtr foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        using var focusedProcess = VerifyProcess(notepad);
        string actualExe = focusedProcess.MainModule?.FileName ?? throw new InvalidOperationException("포그라운드 EXE 조회 실패");
        if (foregroundPid != focusedProcess.Id || !focusedProcess.ProcessName.Equals("Notepad", StringComparison.OrdinalIgnoreCase) ||
            focusedProcess.ProcessName.Equals(Path.GetFileNameWithoutExtension(configuredRestriction.Trim()), StringComparison.OrdinalIgnoreCase) ||
            actualExe.Equals(selected.Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("설정 대상과 실제 포그라운드 Notepad의 불일치 조건을 확인하지 못했습니다.");
        if (!diagnostics.TryGetValue("targetRestrictionObservations", out var values))
            diagnostics["targetRestrictionObservations"] = values = new List<object>();
        ((List<object>)values!).Add(new { at = DateTimeOffset.UtcNow, macroOn = true, restrictionEnabled = true,
            configuredTarget = configuredRestriction, restrictedTarget = selected, restrictedWindow = restricted.MainWindowHandle.ToInt64(),
            foregroundWindow = foreground.ToInt64(), foregroundPid, foregroundProcessName = focusedProcess.ProcessName,
            foregroundExecutable = actualExe, notepadFocused = true, targetMismatch = true,
            comparison = "ProcessName vs Path.GetFileNameWithoutExtension(configuredTarget.Trim()), OrdinalIgnoreCase" });
        diagnostics["targetRestrictionVerified"] = true;
    }
    public TextSample Arm(CancellationToken cancellationToken)
    {
        diagnostics["inputCalls"] = calls;
        CheckSource();
        InspectSourceConfiguration();
        notepad ??= DiscoverNotepad();
        diagnostics["source"] = source; diagnostics["notepad"] = notepad;
        var security = new[] { WindowsTriggerInput.InspectSecurity(Environment.ProcessId),
            WindowsTriggerInput.InspectSecurity(source.ProcessId!.Value), WindowsTriggerInput.InspectSecurity(notepad.ProcessId!.Value) };
        diagnostics["securityLevels"] = security;
        if (!security[0].UiAccess && security.Skip(1).Any(level => level.IntegrityRid > security[0].IntegrityRid))
            throw new InvalidOperationException("대상 프로세스의 무결성/권한 수준이 QA Studio보다 높습니다. UIPI 준비 실패로 F3를 보내지 않습니다.");
        using var process = VerifyProcess(notepad);
        if (!process.ProcessName.Equals("Notepad", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(notepad.Executable).Equals("Notepad.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("선택 대상이 Windows 메모장 프로세스가 아닙니다.");
        hwnd = process.MainWindowHandle;
        if (Native.IsIconic(hwnd)) Native.ShowWindowAsync(hwnd, 9);
        CheckWindow();
        root = AutomationElement.FromHandle(hwnd);
        FindEditor();
        FocusEditor(cancellationToken);
        CheckEnglishAndCaps();
        string original = ReadText();
        diagnostics["originalDocumentLength"] = original.Length;
        diagnostics["originalDocumentSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
        if (original.Length > 0) CreateSafeTestTab(cancellationToken);
        else diagnostics["documentPreparation"] = "Reused a verified empty document; no content changed";
        if (ReadText() != "") throw new InvalidOperationException("안전한 빈 테스트 문서를 준비하지 못했습니다. 기존 문서는 변경하지 않았습니다.");
        FocusEditor(cancellationToken);
        identity = $"pid={process.Id};start={notepad.StartTimeUtc:O};hwnd={hwnd};runtime={string.Join('.', editor!.GetRuntimeId())}";
        return Read();
    }
    private bool FindEditor(bool allowPending = false)
    {
        if (root is null) throw new InvalidOperationException("메모장 창이 없습니다.");
        var found = root.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        var candidates = found.Cast<AutomationElement>().Where(e =>
        {
            var c = e.Current;
            bool editorClass = c.ClassName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                c.ClassName.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
            return editorClass && c.IsEnabled && !c.IsOffscreen && c.IsKeyboardFocusable &&
                (e.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly ||
                 e.TryGetCurrentPattern(TextPattern.Pattern, out _));
        }).ToArray();
        if (allowPending && candidates.Length == 0) return false;
        if (candidates.Length != 1) throw new InvalidOperationException($"메모장 입력 영역을 유일하게 식별하지 못했습니다 ({candidates.Length}개). 검색/설정 창을 닫고 빈 문서를 선택하세요.");
        editor = candidates[0];
        reader = editor.TryGetCurrentPattern(ValuePattern.Pattern, out _) ? "UIA.ValuePattern" : "UIA.TextPattern";
        return true;
    }
    private void FocusEditor(CancellationToken cancellationToken)
    {
        CheckWindow();
        CaptureFocus("before activation");
        bool activated = Native.SetForegroundWindow(hwnd);
        diagnostics["setForegroundWindowReturned"] = activated;
        editor!.SetFocus();
        CaptureFocus("after UIA SetFocus");
        var deadline = Stopwatch.StartNew();
        while (!HasFocus())
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckSource(); CheckWindow();
            if (deadline.ElapsedMilliseconds >= 5000) { CaptureFocus("focus timeout"); throw new InvalidOperationException("메모장 입력 포커스를 자동으로 준비하지 못했습니다."); }
            cancellationToken.WaitHandle.WaitOne(50);
        }
        CaptureFocus("focus verified");
    }
    private void CaptureFocus(string phase)
    {
        IntPtr foreground = Native.GetForegroundWindow();
        uint thread = Native.GetWindowThreadProcessId(hwnd, out uint pid);
        Native.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        var gui = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
        bool guiRead = Native.GetGUIThreadInfo(thread, ref gui);
        var focused = AutomationElement.FocusedElement;
        if (!diagnostics.TryGetValue("focusObservations", out var existing)) diagnostics["focusObservations"] = existing = new List<object>();
        ((List<object>)existing!).Add(new { phase, at = DateTimeOffset.UtcNow, pid, hwnd = hwnd.ToInt64(), minimized = Native.IsIconic(hwnd),
            foregroundHwnd = foreground.ToInt64(), foregroundPid, guiRead, nativeFocusHwnd = gui.Focus.ToInt64(),
            editorHwnd = editor!.Current.NativeWindowHandle, editorClass = editor.Current.ClassName,
            editorHasKeyboardFocus = editor.Current.HasKeyboardFocus, editorFocusable = editor.Current.IsKeyboardFocusable,
            editorOffscreen = editor.Current.IsOffscreen, editorRuntimeId = editor.GetRuntimeId(),
            focusedPid = focused?.Current.ProcessId, focusedClass = focused?.Current.ClassName,
            focusedHwnd = focused?.Current.NativeWindowHandle, focusedRuntimeId = focused?.GetRuntimeId(),
            uiaSameEditor = focused is not null && Automation.Compare(focused, editor) });
    }
    private List<(string Id, string Name, bool Selected)> Tabs() => root!.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Cast<AutomationElement>()
        .Select(e => (Id: string.Join('.', e.GetRuntimeId()), Name: e.Current.Name,
            Selected: e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) && ((SelectionItemPattern)pattern).Current.IsSelected)).ToList();
    private void CreateSafeTestTab(CancellationToken cancellationToken)
    {
        var before = Tabs();
        diagnostics["tabsBefore"] = before.ToDictionary(t => t.Id, t => t.Selected);
        diagnostics["tabNamesBefore"] = before.Select(t => t.Name).ToArray();
        if (before.Count == 0 || before.Count(t => t.Selected) != 1)
            throw new InvalidOperationException("작성 중인 문서가 있습니다. 모던 메모장의 기존 탭을 신뢰성 있게 식별할 수 없어 새 테스트 탭을 만들지 않고 중단합니다.");
        if (!HasFocus()) throw new InvalidOperationException("새 테스트 탭 생성 전 포커스가 바뀌었습니다.");
        WindowsTriggerInput.NewNotepadTab(call => calls.Add(call));
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            cancellationToken.ThrowIfCancellationRequested(); CheckWindow();
            root = AutomationElement.FromHandle(hwnd);
            // WHAT/WHY: Ctrl+N temporarily exposes no editor while the new tab is being created.
            // CONTRACT: Poll only readiness within the existing deadline; never resend Ctrl+N/F3.
            // FAILURE: Ambiguity still throws; missing editor times out without dispatching F3.
            // TEST: Repeated E2E must preserve previous tabs and observe a new empty baseline.
            if (!FindEditor(allowPending: true))
            {
                diagnostics["newTabEditorPendingPolls"] = diagnostics.TryGetValue("newTabEditorPendingPolls", out var count) ? (int)count! + 1 : 1;
                cancellationToken.WaitHandle.WaitOne(100);
                continue;
            }
            var after = Tabs();
            // WHAT/WHY: Notepad can recreate an old TabItem provider when its tab strip overflows.
            // CONTRACT: Preserve the entire ordered name sequence (including duplicates), plus one
            // newly selected trailing tab and an empty editor; runtime IDs remain diagnostics only
            // for old tabs, not permanent document identities. Never clear, close or retype content.
            // FAILURE: Any count/order/name/selection/empty-editor mismatch still times out as ERROR.
            // TEST: Overflow reproduction changed the first old ID despite a new focused empty tab;
            // cross-run f -> ff -> f -> ff must retain all old tabs and use independent documents.
            bool originalTabsPreserved = after.Count == before.Count + 1 &&
                after.Take(before.Count).Select(t => t.Name).SequenceEqual(before.Select(t => t.Name), StringComparer.Ordinal);
            bool newTabSelected = after.Count == before.Count + 1 && after.Count(t => t.Selected) == 1 &&
                after[^1].Selected && before.All(t => t.Id != after[^1].Id);
            bool emptyEditor = ReadText() == "";
            diagnostics["tabPreparationLastObservation"] = new { at = DateTimeOffset.UtcNow, tabs = after.ToDictionary(t => t.Id, t => t.Selected),
                tabNames = after.Select(t => t.Name).ToArray(), originalTabsPreserved, newTabSelected, emptyEditor,
                beforeCount = before.Count, afterCount = after.Count,
                missingBeforeIds = before.Where(t => after.All(a => a.Id != t.Id)).Select(t => t.Id).ToArray(),
                newIds = after.Where(t => before.All(b => b.Id != t.Id)).Select(t => t.Id).ToArray(),
                selectedIds = after.Where(t => t.Selected).Select(t => t.Id).ToArray(),
                textLength = ReadText().Length, editorHwnd = editor!.Current.NativeWindowHandle,
                editorRuntimeId = editor.GetRuntimeId(), editorHasKeyboardFocus = editor.Current.HasKeyboardFocus,
                foregroundHwnd = Native.GetForegroundWindow().ToInt64(), topHwnd = hwnd.ToInt64(), pid = notepad!.ProcessId };
            if (originalTabsPreserved && newTabSelected && emptyEditor)
            {
                diagnostics["tabsAfter"] = after.ToDictionary(t => t.Id, t => t.Selected);
                diagnostics["documentPreparation"] = "Created a new empty trailing test tab via Ctrl+N; original tab count/order/names preserved; no document cleared or closed";
                return;
            }
            cancellationToken.WaitHandle.WaitOne(100);
        }
        throw new InvalidOperationException("기존 탭 보존과 새 빈 테스트 탭을 확인하지 못했습니다. F3를 보내지 않고 중단합니다.");
    }
    public void Fire(CancellationToken cancellationToken, Action<string> log)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // WHAT/WHY: Day11 reuses F3 but requires existing f before its second dispatch.
        // CONTRACT: At most two calls, fresh single-F ON configuration and unchanged focused editor.
        // FAILURE: Wrong phase/content/configuration stops before SendInput; no profile changes.
        // TEST: Staged fake checks and one registered MAC-NP-005 GUI execution.
        int f3Count = calls.Count(c => c.Purpose == "MacroStudio F3 trigger");
        string requiredText = twoSingleFTriggers && f3Count == 1 ? "f" : "";
        if (twoSingleFTriggers && f3Count >= 2) throw new InvalidOperationException("두 단계 F3 전송 횟수를 초과했습니다.");
        if (Read().Text != requiredText) throw new InvalidOperationException("F3 전송 직전 문서 내용이 단계별 사전조건과 다릅니다.");
        if (twoSingleFTriggers) InspectSourceConfiguration();
        if (requireTargetMismatch) VerifyTargetMismatch();
        log("SendInput F3 KEYDOWN -> KEYUP 1회 호출. 직접 F 입력 없음. 호출 성공과 실제 출력 성공은 별개입니다.");
        if (!HasFocus()) throw new InvalidOperationException("SendInput 직전 메모장 포커스가 변경되었습니다.");
        WindowsTriggerInput.F3(call =>
        {
            calls.Add(call);
            log($"{call.Purpose}: SendInput {call.Inserted}/{call.Requested}, INPUT={call.InputSize}, Win32Error={call.Win32Error}, at={call.At:O}");
        });
        if (requireMacroOff || requireTargetMismatch)
            log($"{(requireMacroOff ? "OFF" : "ON / 대상 불일치")} 전용 검증: 실제 F3 전송 후 2초 이상 무출력을 관찰합니다. 포커스/문서 관찰 문제는 ERROR입니다.");
        else
        {
            diagnostics["compatibilityNote"] = "Inspected MacroStudio v6.4 source GlobalTriggerListener.ProcessKeyboardEvent returns immediately for injected events (LLKHF_INJECTED); no supported external run command was found. Only actual text can establish PASS.";
            log("호환성 참고: 확인한 MacroStudio 소스는 주입된 키 이벤트를 무시합니다. 실제 출력이 없으면 FAIL로 기록합니다.");
        }
    }
    public TextSample Read()
    {
        CheckSource();
        var target = notepad ?? throw new InvalidOperationException("메모장 프로세스가 없습니다.");
        using var process = VerifyProcess(target);
        if (process.MainWindowHandle != hwnd) throw new InvalidOperationException("메모장 창이 변경되었습니다.");
        CheckWindow();
        if (!HasFocus()) throw new InvalidOperationException("메모장 입력 포커스를 잃었습니다.");
        CheckEnglishAndCaps();
        string currentIdentity = $"pid={process.Id};start={target.StartTimeUtc:O};hwnd={hwnd};runtime={string.Join('.', editor!.GetRuntimeId())}";
        return new(DateTimeOffset.UtcNow, ReadText(), reader, currentIdentity);
    }
    private string ReadText()
    {
        if (editor is null) throw new InvalidOperationException("메모장 입력 영역이 없습니다.");
        if (reader == "UIA.ValuePattern")
        {
            var value = (ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern);
            if (value.Current.IsReadOnly) throw new InvalidOperationException("메모장이 읽기 전용입니다.");
            return value.Current.Value;
        }
        var text = ((TextPattern)editor.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(65537);
        if (text.Length > 65536) throw new InvalidOperationException("관찰 가능한 텍스트 길이를 초과했습니다.");
        return text; // No Trim/line-ending normalization: exact content is the assertion.
    }
    private bool HasFocus()
    {
        if (editor is null || editor.Current.IsOffscreen) return false;
        // Windows 11 Notepad may foreground the identified RichEdit HWND after UIA SetFocus.
        // Keep exact editor identity + PID + UIA keyboard focus checks; do not accept other windows.
        IntPtr foreground = Native.GetForegroundWindow();
        IntPtr editorHwnd = (IntPtr)editor.Current.NativeWindowHandle;
        if (foreground != hwnd && (editorHwnd == IntPtr.Zero || foreground != editorHwnd)) return false;
        Native.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        if (foregroundPid != notepad?.ProcessId) return false;
        var focused = AutomationElement.FocusedElement;
        return focused is not null && Automation.Compare(focused, editor) && editor.Current.HasKeyboardFocus;
    }
    private void CheckSource()
    {
        using var process = VerifyProcess(source);
        // Permit only the explicitly designated v6.3 QA binary; PID/start/path were verified above.
        string? designatedQaPath = Environment.GetEnvironmentVariable("QASTUDIO_MACROSTUDIO_QA_EXE");
        bool designatedQa63 = !string.IsNullOrWhiteSpace(designatedQaPath)
            && Path.IsPathFullyQualified(designatedQaPath)
            && string.Equals(Path.GetFileName(designatedQaPath), "MacroStudio.v6.3.QA.F3.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFullPath(source.Executable), Path.GetFullPath(designatedQaPath), StringComparison.OrdinalIgnoreCase)
            && process.MainWindowTitle.Contains("매크로 스튜디오 6.3", StringComparison.Ordinal);
        if ((requireMacroOff || requireTargetMismatch || twoSingleFTriggers) && !designatedQa63)
            throw new InvalidOperationException("무출력 검증은 지정된 MacroStudio v6.3 QA 프로세스만 지원합니다.");
        if (!(process.MainWindowTitle.Contains("매크로 스튜디오 6.4", StringComparison.Ordinal) || designatedQa63) ||
            process.MainWindowHandle == IntPtr.Zero || !Native.IsWindowVisible(process.MainWindowHandle))
            throw new InvalidOperationException("MacroStudio v6.4 또는 지정된 v6.3 QA EXE의 실제 창을 찾을 수 없습니다.");
    }
    private SelectedTarget DiscoverNotepad()
    {
        var candidates = new List<SelectedTarget>();
        foreach (var process in Process.GetProcessesByName("Notepad"))
        using (process)
        {
            if (process.HasExited || process.MainWindowHandle == IntPtr.Zero) continue;
            string executable = process.MainModule?.FileName ?? throw new InvalidOperationException("메모장 EXE 경로 조회 실패");
            candidates.Add(new(executable, process.Id, process.StartTime.ToUniversalTime()));
        }
        diagnostics["notepadCandidates"] = candidates;
        if (candidates.Count != 1) throw new InvalidOperationException($"실행 중인 메모장을 유일하게 찾을 수 없습니다 ({candidates.Count}개). 대상을 선택한 뒤 다시 실행하세요.");
        return candidates[0];
    }
    private void InspectSourceConfiguration()
    {
        if (twoSingleFTriggers && (doubleOutput || requireMacroOff || requireTargetMismatch))
            throw new InvalidOperationException("두 F3 검증은 별도의 ON 단일 F 모드입니다.");
        using var process = VerifyProcess(source);
        var window = AutomationElement.FromHandle(process.MainWindowHandle);
        string Value(AutomationElement element)
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
            if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection))
                return string.Join(";", ((SelectionPattern)selection).Current.GetSelection().Select(e => e.Current.Name));
            return element.Current.Name;
        }
        var buttons = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().Select(e => e.Current.Name).ToArray();
        var choices = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox))
            .Cast<AutomationElement>().Select(Value).ToArray();
        var targets = window.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.NameProperty, "대상 EXE"))).Cast<AutomationElement>().Select(Value).ToArray();
        if (requireTargetMismatch)
        {
            if (requireMacroOff || doubleOutput || !buttons.Contains("매크로 끄기") || buttons.Contains("매크로 켜기"))
                throw new InvalidOperationException("대상 불일치 검증은 MacroStudio ON과 단일 F 모드가 필요합니다.");
            var restriction = window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                new PropertyCondition(AutomationElement.NameProperty, "대상 게임 창에서만")));
            if (restriction is null || !restriction.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle) ||
                ((TogglePattern)toggle).Current.ToggleState != ToggleState.On)
                throw new InvalidOperationException("대상 프로그램 제한이 활성화되지 않았습니다.");
            if (targets.Length != 1 || !Path.GetFileNameWithoutExtension(targets[0].Trim()).Equals("charmap", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Day8 제한 대상은 실행 중인 안전한 문자표(charmap)여야 합니다.");
            configuredRestriction = targets[0];
            if (restrictionTarget is null)
            {
                var candidates = new List<SelectedTarget>();
                foreach (var candidate in Process.GetProcessesByName("charmap"))
                using (candidate)
                {
                    string? path = candidate.MainModule?.FileName;
                    if (candidate.HasExited || candidate.MainWindowHandle == IntPtr.Zero || path is null ||
                        !path.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "charmap.exe"), StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add(new(path, candidate.Id, candidate.StartTime.ToUniversalTime()));
                }
                if (candidates.Count != 1) throw new InvalidOperationException("실행 중인 Windows 문자표 프로세스를 유일하게 확인하지 못했습니다.");
                restrictionTarget = candidates[0];
            }
            using var verifiedTarget = VerifyProcess(restrictionTarget);
            diagnostics["restrictedTarget"] = restrictionTarget;
        }
        bool outputReady = buttons.Contains("F");
        if (doubleOutput)
        {
            // WHAT/WHY: Custom-sequence mode hides the repeat-key button; inspect its live grid instead.
            // CONTRACT: MAC-NP-002 requires exactly two paired F strokes and the separate QA sequence.
            // FAILURE: Unreadable/mismatched rows stop preparation before any F3 is emitted.
            // TEST: Real QA profile grid -> one F3 -> observed f/ff -> stable ff and JSON.
            var gridElement = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SequenceGrid"));
            if (gridElement is null || !gridElement.Current.IsEnabled ||
                !gridElement.TryGetCurrentPattern(GridPattern.Pattern, out var pattern))
                throw new InvalidOperationException("F 2회 자유 시퀀스의 실제 표를 읽을 수 없습니다.");
            var grid = (GridPattern)pattern;
            if (grid.Current.RowCount != 8 || grid.Current.ColumnCount != 4)
                throw new InvalidOperationException("F 2회 QA 시퀀스의 8개 이벤트를 확인하지 못했습니다.");
            string Cell(int row, int column)
            {
                var cell = grid.GetItem(row, column);
                if (!cell.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                    throw new InvalidOperationException("시퀀스 셀의 실제 값을 읽을 수 없습니다.");
                return ((ValuePattern)value).Current.Value;
            }
            var rows = Enumerable.Range(0, 8).Select(i => new { action = Cell(i, 1), key = Cell(i, 2), duration = Cell(i, 3) }).ToArray();
            diagnostics["sourceSequenceRows"] = rows;
            string[] actions = ["키 누름", "대기", "키 뗌", "대기", "키 누름", "대기", "키 뗌", "대기"];
            outputReady = choices.Contains("QA F3 x2  [F3]") && rows.Select(r => r.action).SequenceEqual(actions) &&
                new[] { 0, 2, 4, 6 }.All(i => rows[i].key == "F") &&
                rows[1].duration == "30" && rows[3].duration == "150" && rows[5].duration == "30" && rows[7].duration == "20";
        }
        diagnostics["macroConfiguration"] = new { trigger = "F3", expectedOutputKey = "F", outputCount = doubleOutput ? 2 : 1, outputReady, comboValues = choices, targetValues = targets,
            masterOn = buttons.Contains("매크로 끄기"), hasF3 = buttons.Contains("F3"), hasF = buttons.Contains("F") };
        bool masterReady = requireMacroOff ? buttons.Contains("매크로 켜기") && !buttons.Contains("매크로 끄기") : buttons.Contains("매크로 끄기");
        if (twoSingleFTriggers && !choices.Any(v => v.Replace(" ", "", StringComparison.Ordinal) == "QAF3[F3]"))
            throw new InvalidOperationException("두 F3 검증의 QA 단일 F 프로필을 확인하지 못했습니다.");
        if (requireMacroOff && (doubleOutput || !choices.Any(v => v.Replace(" ", "", StringComparison.Ordinal) == "QAF3[F3]")))
            throw new InvalidOperationException("OFF 검증의 QA 단일 F 출력 프로필을 확인하지 못했습니다.");
        if (!masterReady || !buttons.Contains("F3") || !outputReady ||
            !choices.Contains("단축키마다 1회 실행") || !choices.Any(v => v.Contains("[F3]", StringComparison.Ordinal)) ||
            targets.Length != 1 || !Path.GetFileNameWithoutExtension(targets[0].Trim()).Equals(requireTargetMismatch ? "charmap" : "Notepad", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MacroStudio의 {(requireMacroOff ? "OFF" : "ON")} · 선택된 F3 · 해당 F 출력 설정 · 단축키마다 1회 · 대상 {(requireTargetMismatch ? "charmap" : "Notepad")} 설정을 확인하지 못했습니다. 설정을 변경하지 않고 중단합니다.");
    }
    private static Process VerifyProcess(SelectedTarget selected)
    {
        if (selected.ProcessId is not int pid || selected.StartTimeUtc is null)
            throw new InvalidOperationException("실행 중인 프로세스를 선택하세요. 새 EXE 실행 모드는 지원하지 않습니다.");
        var process = Process.GetProcessById(pid);
        try
        {
            if (process.HasExited || process.StartTime.ToUniversalTime() != selected.StartTimeUtc.Value.ToUniversalTime() ||
                !string.Equals(process.MainModule?.FileName, selected.Executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("선택한 프로세스의 PID/시작 시각/경로가 변경되었습니다. 다시 선택하세요.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    private void CheckWindow()
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) ||
            Native.SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 2, 300, out _) == IntPtr.Zero)
            throw new InvalidOperationException("표시되고 응답하는 메모장 창을 확인할 수 없습니다.");
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != notepad?.ProcessId) throw new InvalidOperationException("메모장 창 소유 프로세스가 변경되었습니다.");
    }
    private void CheckEnglishAndCaps()
    {
        if ((Native.GetKeyState(0x14) & 1) != 0) throw new InvalidOperationException("CapsLock을 끄고 다시 실행하세요.");
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c })
            if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("Shift/Ctrl/Alt/Windows 키를 놓고 다시 실행하세요.");
        uint thread = Native.GetWindowThreadProcessId(hwnd, out _);
        int language = (int)(Native.GetKeyboardLayout(thread).ToInt64() & 0xffff);
        if ((language & 0x3ff) == 9) return; // English keyboard layout.
        IntPtr focus = (IntPtr)editor!.Current.NativeWindowHandle;
        IntPtr ime = Native.ImmGetDefaultIMEWnd(focus == IntPtr.Zero ? hwnd : focus);
        if ((language & 0x3ff) != 0x12 || ime == IntPtr.Zero ||
            Native.SendMessageTimeout(ime, 0x0283, (IntPtr)5, IntPtr.Zero, 2, 300, out var open) == IntPtr.Zero ||
            Native.SendMessageTimeout(ime, 0x0283, (IntPtr)1, IntPtr.Zero, 2, 300, out var conversion) == IntPtr.Zero ||
            (open != IntPtr.Zero && (conversion.ToInt64() & 1) != 0))
            throw new InvalidOperationException("영문 입력 상태를 확인할 수 없습니다. 영문 키보드/IME 영문 모드로 설정하세요.");
    }
    public void Dispose() { editor = root = null; }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
        { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("imm32.dll")] internal static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
