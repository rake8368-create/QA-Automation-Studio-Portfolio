using QAStudio.Application;
using QAStudio.Domain;

namespace QA.AutomationStudio.Desktop;

// WHAT: Two temporary f/ff scenarios reuse the same native observation workflow.
// WHY: One execution click must prepare focus, emit F3 and assert the real output without human input.
// CONTRACT: Automatic safe-document preparation; only F3 triggers the macro, never direct output F.
// FAILURE: No saved result is displayed until evidence has been flushed successfully.
// TEST: Published form -> native SendInput -> actual UIA observation -> JSON evidence.
internal sealed class NotepadVerificationForm : Form
{
    private readonly SelectedTarget source;
    private readonly string evidenceDirectory;
    private readonly string scenarioId, expected;
    private readonly bool requireMacroOff;
    private readonly bool requireTargetMismatch;
    private readonly bool twoSingleFTriggers;
    private readonly TestCase? linkedCase;
    private readonly IExecutionStore? executionStore;
    private SelectedTarget? notepad;
    private readonly Button choose = new() { Text = "메모장 프로세스 선택", AutoSize = true };
    private readonly Button start = new() { Text = "자동 테스트 다시 실행", AutoSize = true, Name = "StartNotepadObservation" };
    private readonly Button cancel = new() { Text = "닫기", AutoSize = true };
    private readonly Label selected = new() { Text = "실행 중인 Notepad 자동 탐색", AutoSize = true };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Name = "NotepadObservationLog" };
    private CancellationTokenSource? cancellation;
    public TextExecution? Completed { get; private set; }
    public NotepadVerificationForm(SelectedTarget source, string evidenceDirectory, bool doubleOutput = false,
        TestCase? linkedCase = null, IExecutionStore? executionStore = null, bool requireMacroOff = false, bool requireTargetMismatch = false, bool twoSingleFTriggers = false)
    {
        this.source = source; this.evidenceDirectory = evidenceDirectory;
        this.requireMacroOff = requireMacroOff;
        this.requireTargetMismatch = requireTargetMismatch;
        this.twoSingleFTriggers = twoSingleFTriggers;
        if (twoSingleFTriggers && (doubleOutput || requireMacroOff || requireTargetMismatch)) throw new ArgumentException("두 F3 검증은 ON 단일 F 프로필입니다.");
        if (requireTargetMismatch && (requireMacroOff || doubleOutput)) throw new ArgumentException("대상 불일치는 별도의 ON 단일 F 검증입니다.");
        bool noOutput = requireMacroOff || requireTargetMismatch;
        if (requireMacroOff && doubleOutput) throw new ArgumentException("OFF 검증은 단일 F 프로필만 지원합니다.");
        if ((linkedCase is null) != (executionStore is null)) throw new ArgumentException("TC와 실행 이력 저장소를 함께 지정해야 합니다.");
        this.linkedCase = linkedCase; this.executionStore = executionStore;
        scenarioId = twoSingleFTriggers ? "MAC-NP-005" : requireTargetMismatch ? "MAC-NP-004" : requireMacroOff ? "MAC-NP-003" : doubleOutput ? "MAC-NP-002" : "MAC-NP-001";
        expected = noOutput ? "" : doubleOutput || twoSingleFTriggers ? "ff" : "f";
        Text = $"{(linkedCase is null ? scenarioId : linkedCase.Id + " → " + scenarioId)} · MacroStudio → Notepad · 자동 실행";
        Name = "NotepadVerificationForm"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(960, 550); MinimumSize = new Size(960, 550); Font = new Font("맑은 고딕", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
        foreach (int height in new[] { 86, 42, 40, 52 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 44));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = twoSingleFTriggers
            ? "두 단계 자동 실행: 빈 Notepad → 첫 F3 → 실제 f 확인 → 두 번째 F3 → 실제 ff → 2초 이상 안정화 관찰\n단일 F 프로필을 유지합니다. 문서·Reader·포커스 오류는 ERROR, 불일치/시간 초과는 FAIL입니다.\n직접 F 입력이나 자동 프로필 전환은 하지 않습니다."
            : requireTargetMismatch
            ? "대상 제한 자동 실행: ON·단일 F·대상 charmap·제한 활성화 확인 → 빈 Notepad → 실제 F3 1회 → 2초 이상 무출력 관찰\n기대 조건: charmap과 실제 포그라운드 Notepad 불일치, 같은 문서/읽기 방식 유지. 문자 입력은 FAIL, 사전조건/관찰 문제는 ERROR.\n실행 중 키보드/마우스에 손대지 마세요. 기존 문서를 보존하며 F를 직접 입력하지 않습니다."
            : requireMacroOff
            ? "OFF 전용 자동 실행: OFF·단일 F 프로필 확인 → 안전한 빈 문서 → SendInput F3 1회 → 2초 이상 무출력 관찰\n기대 조건: 같은 문서/읽기 방식에서 빈 문서 유지. 문자 입력은 FAIL, 관찰/포커스 문제는 ERROR.\n실행 중 키보드/마우스에 손대지 마세요. 기존 문서는 지우거나 닫지 않으며 F를 직접 입력하지 않습니다."
            : $"자동 실행: 안전한 빈 메모장 문서/포커스 준비 → SendInput F3 1회 → 실제 텍스트 비교 → 증적 저장\n기대값: 정확히 {expected} · 10초 출력 대기 + 기대값 완성 후 2초 안정화 관찰\n실행 중 키보드/마우스에 손대지 마세요. 기존 문서는 지우거나 닫지 않습니다. QA Studio는 F를 직접 입력하지 않습니다." }, 0, 0);
        var picker = new FlowLayoutPanel { Dock = DockStyle.Fill }; picker.Controls.Add(choose); picker.Controls.Add(selected); layout.Controls.Add(picker, 0, 1);
        layout.Controls.Add(new Label { Text = noOutput
            ? "자동 트리거: F3 KEYDOWN → KEYUP (Windows SendInput) · 무출력 관찰 · 문자 입력은 FAIL · 전송/관찰 실패는 ERROR"
            : "자동 트리거: F3 KEYDOWN → KEYUP (Windows SendInput) · 실제 입력이 없으면 FAIL", Dock = DockStyle.Fill }, 0, 2);
        layout.Controls.Add(new Label { Text = requireTargetMismatch ? "MacroStudio: ON · F3 · 단일 F · 대상 게임 창에서만 ON · charmap 실행 중 · 실제 입력/관찰 대상 Notepad."
            : requireMacroOff ? "MacroStudio: OFF · QA 단일 F 프로필 · F3 · 단축키마다 1회 실행 · 대상 Notepad. 상태/프로필은 사람이 준비합니다."
            : $"MacroStudio: ON · F3 · 단축키마다 1회 실행 · 대상 Notepad 확인. 준비할 출력: {(twoSingleFTriggers ? "F3마다 f 1회, 총 두 번" : expected)}", Dock = DockStyle.Fill }, 0, 3); layout.Controls.Add(log, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(cancel); buttons.Controls.Add(start); layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        Append(linkedCase is null ? "임시 시나리오: JSON 실행 증적만 저장합니다. SQLite TC/기존 실행 결과는 변경하지 않습니다."
            : $"연결 TC: {linkedCase.Id} → {scenarioId}. 원본 TC는 유지하고 실제 실행 이력과 JSON 증적을 저장합니다.");
        choose.Click += (_, _) => { using var pickerForm = new TargetProcessForm("Notepad 행을 선택한 뒤 연결하세요."); if (pickerForm.ShowDialog(this) == DialogResult.OK && pickerForm.SelectedTarget is { } target) { notepad = target; selected.Text = $"{Path.GetFileName(target.Executable)} · PID {target.ProcessId}"; } UpdateStart(); };
        start.Click += async (_, _) => await ExecuteAsync();
        Shown += async (_, _) => await ExecuteAsync();
        cancel.Click += (_, _) => { if (cancellation is not null) cancellation.Cancel(); else Close(); };
        FormClosing += (_, e) => { if (cancellation is not null) { cancellation.Cancel(); e.Cancel = true; } };
    }
    private void UpdateStart()
    {
        start.Enabled = cancellation is null;
    }
    private async Task ExecuteAsync()
    {
        if (!start.Enabled) return;
        cancellation = new(); start.Enabled = choose.Enabled = false;
        cancel.Text = "관찰 취소"; Completed = null;
        try
        {
            var progress = new Progress<string>(Append);
            var adapter = new NotepadTextProbe(source, notepad, expected == "ff" && !twoSingleFTriggers, requireMacroOff, requireTargetMismatch, twoSingleFTriggers);
            Completed = await new SingleTextOrchestrator().ExecuteAsync(adapter,
                new("macro-studio", scenarioId, expected, "F3", evidenceDirectory,
                    $"source={source}; observer={(notepad is null ? "auto-discover" : notepad.ToString())}", LinkedTestCaseId: linkedCase?.Id,
                    Expectation: twoSingleFTriggers ? TextExpectation.TwoSingleFTriggers : requireTargetMismatch ? TextExpectation.NoOutputWhileTargetMismatch : requireMacroOff ? TextExpectation.NoOutputWhileMacroOff : TextExpectation.ExactText),
                message => ((IProgress<string>)progress).Report(message), cancellation.Token,
                store: executionStore, registeredCase: linkedCase, trigger: adapter);
            Append($"{ResultText.Format(Completed.Run.Result)} · JSON 저장 완료 · RunID {Completed.Run.Id}");
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Completed.Run.Evidence));
            Append("판정 사유: " + saved.RootElement.GetProperty("reason").GetString());
            Append($"기대값: {expected} · 실제 관찰: " + System.Text.Json.JsonSerializer.Serialize(saved.RootElement.GetProperty("actual").GetString()));
            Append(Completed.Run.Evidence);
        }
        catch (Exception ex) { Append("ERROR · 실행/증적 저장 오류: " + ex.Message + " · 저장된 PASS로 표시하지 않습니다."); }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            choose.Enabled = true; cancel.Text = "닫기"; UpdateStart();
        }
    }
    private void Append(string message) => log.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}");
}
