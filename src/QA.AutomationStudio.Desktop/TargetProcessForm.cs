using System.Diagnostics;

namespace QA.AutomationStudio.Desktop;

public sealed record SelectedTarget(string Executable, int? ProcessId, DateTime? StartTimeUtc);

// WHAT: Native running-application picker, separate from the project/TC selector.
// WHY: Identify the actual process under test, including restarted instances.
// CONTRACT: Snapshot PID/start time/path, require an explicit selection, no target input or DB writes.
// FAILURE: Inaccessible/exited processes are omitted; refresh updates the snapshot; cancel leaves selection unchanged.
// TEST: Published picker selects D4Sequencer; adapter rechecks identity before any window assertion.
public sealed class TargetProcessForm : Form
{
    private readonly string selectionHint;
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, MultiSelect = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly Button refresh = new() { Text = "새로고침", AutoSize = true };
    private readonly Button choose = new() { Text = "선택한 프로세스 연결", AutoSize = true, Enabled = false };
    private readonly Label status = new() { AutoSize = true, Text = "메인 창이 있는 실행 중인 프로그램을 조회합니다." };
    public SelectedTarget? SelectedTarget { get; private set; }
    public TargetProcessForm(string selectionHint = "D4Sequencer 행을 선택한 뒤 연결하세요.")
    {
        this.selectionHint = selectionHint;
        Text = "자동화 테스트 대상 프로세스 선택"; Name = "TargetProcessForm";
        ClientSize = new Size(980, 480); MinimumSize = new Size(760, 380);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("맑은 고딕", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(status, 0, 0); layout.Controls.Add(grid, 0, 1);
        foreach (var (name, title, weight) in new[] { ("Id", "PID", 12f), ("Name", "프로세스", 25f), ("Title", "메인 창", 50f), ("Path", "EXE 경로", 65f) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = name, HeaderText = title, FillWeight = weight });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); buttons.Controls.Add(choose); buttons.Controls.Add(refresh); layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout); CancelButton = cancel; AcceptButton = choose;
        grid.SelectionChanged += (_, _) => choose.Enabled = refresh.Enabled && grid.CurrentRow?.DataBoundItem is ProcessRow;
        refresh.Click += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => await RefreshAsync();
        choose.Click += (_, _) =>
        {
            if (grid.CurrentRow?.DataBoundItem is not ProcessRow row) return;
            SelectedTarget = new(row.Path, row.Id, row.StartTimeUtc); DialogResult = DialogResult.OK;
        };
    }
    private async Task RefreshAsync()
    {
        refresh.Enabled = choose.Enabled = false; status.Text = "프로세스 조회 중…";
        var rows = await Task.Run(() =>
        {
            var result = new List<ProcessRow>();
            foreach (var process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.HasExited || process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    string? path = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path)) continue;
                    result.Add(new(process.Id, process.ProcessName, process.MainWindowTitle, path, process.StartTime.ToUniversalTime()));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
            return result.OrderByDescending(r => r.Name.Equals("D4Sequencer", StringComparison.OrdinalIgnoreCase)).ThenBy(r => r.Name).ToList();
        });
        if (IsDisposed) return;
        grid.DataSource = rows; grid.ClearSelection(); grid.CurrentCell = null;
        refresh.Enabled = true; choose.Enabled = false;
        status.Text = $"{rows.Count}개 프로그램 · {selectionHint} 재시작했다면 새로고침 후 다시 선택하세요.";
    }
    private sealed record ProcessRow(int Id, string Name, string Title, string Path, DateTime StartTimeUtc);
}
