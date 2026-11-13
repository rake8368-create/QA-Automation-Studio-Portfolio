using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QA.AutomationStudio.Desktop;

// WHAT: Show and edit the selected TC's registered automation mapping.
// WHY: Mapping review belongs beside the TC requirements in a dedicated dialog.
// CONTRACT: Only explicit save or unlink changes the mapping. The dialog never executes a TC.
// FAILURE: A failed write leaves the dialog open and reports the store error.
// TEST: Save and unlink an isolated mapping, reopen, and compare the original five bindings.
internal sealed class AutomationMappingForm : Form
{
    private sealed record Scenario(string Id, string Description, string Expected)
    {
        public override string ToString() => $"{Id} · {Description}";
    }

    private static readonly Scenario[] Scenarios =
    [
        new("MAC-NP-001", "f", "f"),
        new("MAC-NP-002", "ff", "ff"),
        new("MAC-NP-003", "OFF 무출력", "OFF · 빈 문서 유지 (명시적 무출력 검증)"),
        new("MAC-NP-004", "ON 대상 불일치", "ON · 대상 charmap / 실제 Notepad 불일치 · 무출력"),
        new("MAC-NP-005", "F3 두 번 / ff", "f 확인 → F3 → ff")
    ];

    public string? SavedScenario { get; private set; }

    public AutomationMappingForm(FoundationStore store, TestCase tc, string? currentScenario)
    {
        Name = "AutomationMappingForm";
        Text = "Automation Mapping";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 370);
        MinimumSize = new Size(560, 340);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 10);
        BackColor = Color.FromArgb(245, 247, 250);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.Controls.Add(new Label { Text = $"{tc.ProjectId} / {tc.Id}", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Name = "TcAutomationStatus",
            Text = $"현재 연결 · {currentScenario ?? "자동화 미연결"}", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft }, 0, 1);

        string requirements = $"목적: {tc.Fields.GetValueOrDefault("테스트 목적")}{Environment.NewLine}" +
            $"사전조건: {tc.Fields.GetValueOrDefault("사전조건")}{Environment.NewLine}" +
            $"기대 결과: {tc.Fields.GetValueOrDefault("기대 결과")}";
        layout.Controls.Add(new TextBox { AccessibleName = "선택 TC 원본 정보", Dock = DockStyle.Fill,
            Text = requirements, Multiline = true, ReadOnly = true, BackColor = Color.White,
            ScrollBars = ScrollBars.Vertical, TabStop = false }, 0, 2);

        var selection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selection.Controls.Add(new Label { Text = "시나리오", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var scenarios = new ComboBox { Name = "MappingScenario", AccessibleName = "자동화 매핑 시나리오",
            DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 0) };
        scenarios.Items.AddRange(Scenarios);
        scenarios.SelectedIndex = Array.FindIndex(Scenarios, scenario => scenario.Id == currentScenario);
        selection.Controls.Add(scenarios, 1, 0);
        layout.Controls.Add(selection, 0, 3);

        var save = new Button { Name = "SaveTcAutomationMapping", Text = "TC 연결 저장", AutoSize = true,
            Enabled = scenarios.SelectedIndex >= 0 };
        var unlink = new Button { Name = "RemoveTcAutomationMapping", Text = "연결 해제", AutoSize = true,
            Enabled = currentScenario is not null };
        var close = new Button { Name = "CloseAutomationMapping", Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.AddRange([close, unlink, save]);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        CancelButton = close;

        scenarios.SelectedIndexChanged += (_, _) => save.Enabled = scenarios.SelectedIndex >= 0;
        save.Click += (_, _) =>
        {
            if (scenarios.SelectedItem is not Scenario scenario) return;
            string review = $"TC: {tc.Id}{Environment.NewLine}{requirements}{Environment.NewLine}{Environment.NewLine}" +
                $"연결: {scenario.Id} · F3 → {scenario.Expected} · 2초 안정화{Environment.NewLine}" +
                "목적과 사전조건이 일치할 때만 연결하세요.";
            if (MessageBox.Show(this, review, "선택 TC 자동화 연결 확인", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                Apply(scenario);
        };
        unlink.Click += (_, _) => Apply(null);

        void Apply(Scenario? scenario)
        {
            try
            {
                if (scenario is null) store.RemoveAutomationMapping(tc.ProjectId, tc.Id);
                else store.SetAutomationMapping(tc.ProjectId, tc.Id, scenario.Id);
                SavedScenario = scenario?.Id;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ImportErrors.Expected(ex))
            {
                MessageBox.Show(this, ex.Message, scenario is null ? "자동화 연결 해제 오류" : "자동화 연결 저장 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

