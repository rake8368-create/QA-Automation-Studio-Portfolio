using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QA.AutomationStudio.Desktop;

// WHAT: Capture an explicitly selected manual verdict and the tester's observation for one TC.
// WHY: Operators need the original instructions and prior manual records while entering results.
// CONTRACT: No default verdict, no SUT action, and no update to TC or automated history.
// FAILURE: Keep the form open on validation or save failure; publish a result only after commit.
// TEST: Real WinForms registration on a cloned DB, reopen, and match the exported Manual_History.
internal sealed class ManualRunForm : Form
{
    private readonly TextBox tester = new() { Name = "ManualTester", AccessibleName = "실행자", MaxLength = 120, Dock = DockStyle.Fill };
    private readonly DateTimePicker executedAt = new() { Name = "ManualExecutedAt", AccessibleName = "실행 시각",
        Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Dock = DockStyle.Fill };
    private readonly ComboBox result = new() { Name = "ManualResult", AccessibleName = "수동 실행 결과",
        DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox actual = Multiline("ManualActual", "실제 결과", 4000);
    private readonly TextBox notes = Multiline("ManualNotes", "메모", 4000);
    private readonly TextBox attachment = new() { Name = "ManualAttachmentPath", AccessibleName = "첨부 증적 경로",
        ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Button add = new() { Name = "ManualAddAttachment", Text = "파일 선택", AutoSize = true };
    private readonly Button clear = new() { Name = "ManualClearAttachment", Text = "첨부 해제", AutoSize = true };
    private readonly Button save = new() { Name = "ManualSave", Text = "수동 결과 저장", AutoSize = true };
    private readonly Button cancel = new() { Name = "ManualCancel", Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
    private bool saving;
    public ManualRun? Saved { get; private set; }

    public ManualRunForm(string databasePath, TestCase tc, CaseAssessment assessment, ManualReadiness? readiness, IReadOnlyList<ManualRun> history)
    {
        Text = $"수동 테스트 결과 · {tc.Id}";
        Name = "ManualRunForm";
        AccessibleName = "수동 테스트 결과 등록";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1010, 718);
        MinimumSize = new Size(860, 650);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, AutoEllipsis = true,
            Text = $"{tc.ProjectId} / {tc.Id} · {assessment.StatusText} · {tc.Fields.GetValueOrDefault("테스트 목적", "")}" }, 0, 0);

        layout.Controls.Add(new Label { Name = "ManualReadiness", Dock = DockStyle.Fill, AutoEllipsis = true,
            Text = readiness is null ? "" : $"수동 실행 준비도 · {readiness.StatusText}\n{readiness.Reason}" }, 0, 1);

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var originals = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = new Padding(0, 0, 10, 0) };
        foreach (var (caption, field) in new[] { ("원본 사전조건", "사전조건"), ("원본 수행 절차", "수행 절차"), ("원본 기대 결과", "기대 결과") })
        {
            var input = Multiline(field, caption, 0);
            input.ReadOnly = true;
            input.Text = tc.Fields.GetValueOrDefault(field, "");
            originals.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            originals.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            originals.Controls.Add(Caption(caption));
            originals.Controls.Add(input);
        }
        columns.Controls.Add(originals, 0, 0);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Margin = Padding.Empty };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 3; row++) editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        result.Items.AddRange(Enum.GetNames<ManualResult>().Select(text => text.ToUpperInvariant()).Cast<object>().ToArray());
        executedAt.Value = DateTime.Now;
        var files = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        files.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        files.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        files.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        files.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        files.Controls.Add(attachment, 0, 0);
        files.Controls.Add(add, 1, 0);
        files.Controls.Add(clear, 2, 0);
        var fields = new (string Caption, Control Field)[] { ("실행자", tester), ("실행 시각", executedAt), ("실행 결과", result),
            ("실제 결과", actual), ("메모", notes), ("첨부 증적", files) };
        for (int row = 0; row < fields.Length; row++)
        {
            editor.Controls.Add(Caption(fields[row].Caption), 0, row);
            editor.Controls.Add(fields[row].Field, 1, row);
        }
        columns.Controls.Add(editor, 1, 0);
        layout.Controls.Add(columns, 0, 2);
        layout.Controls.Add(Caption($"이전 수동 실행 · {history.Count}건"), 0, 3);
        layout.Controls.Add(History(history), 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.AddRange([cancel, save]);
        layout.Controls.Add(actions, 0, 5);
        Controls.Add(layout);

        CancelButton = cancel;
        add.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "수동 실행 증적 파일 선택", Filter = "모든 파일 (*.*)|*.*",
                CheckFileExists = true, Multiselect = false, RestoreDirectory = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) attachment.Text = dialog.FileName;
        };
        clear.Click += (_, _) => attachment.Clear();
        FormClosing += (_, e) => { if (saving) e.Cancel = true; };
        save.Click += async (_, _) =>
        {
            if (saving) return;
            if (result.SelectedItem is not string selected || !Enum.TryParse<ManualResult>(selected, ignoreCase: true, out var verdict))
            {
                MessageBox.Show(this, "실행 결과를 선택하세요.", "수동 결과 등록", MessageBoxButtons.OK, MessageBoxIcon.Information);
                result.Focus();
                return;
            }
            string name = tester.Text, observation = actual.Text, memo = notes.Text, path = attachment.Text;
            var time = new DateTimeOffset(executedAt.Value);
            saving = true;
            editor.Enabled = save.Enabled = cancel.Enabled = false;
            try
            {
                Saved = await Task.Run(() =>
                {
                    using var store = new FoundationStore(databasePath);
                    return new ManualExecutionRecorder(store).Record(databasePath, tc, name, verdict, observation, memo, path, time);
                });
            }
            catch (Exception ex) when (ImportErrors.Expected(ex))
            {
                MessageBox.Show(this, ex.Message, "수동 결과 저장 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { saving = false; editor.Enabled = save.Enabled = cancel.Enabled = true; }
            if (Saved is not null) { DialogResult = DialogResult.OK; Close(); }
        };
    }

    private static Label Caption(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private static TextBox Multiline(string name, string caption, int length) => new()
    {
        Name = name, AccessibleName = caption, MaxLength = length, Multiline = true,
        ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill
    };
    private static DataGridView History(IReadOnlyList<ManualRun> runs)
    {
        var grid = new DataGridView { Name = "ManualHistoryGrid", AccessibleName = "이전 수동 실행 이력",
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White };
        foreach (var (name, width) in new[] { ("실행 시각", 170), ("결과", 95), ("실행자", 125), ("실제 결과", 230), ("메모", 190), ("첨부", 145) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = width });
        foreach (var run in runs.Reverse())
            grid.Rows.Add(run.ExecutedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), run.ResultText, run.Tester,
                run.ActualResult, run.Notes, Path.GetFileName(run.AttachmentPath) ?? "");
        return grid;
    }
}
