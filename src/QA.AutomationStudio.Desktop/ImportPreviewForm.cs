using System.ComponentModel;
using QAStudio.Adapters;

namespace QA.AutomationStudio.Desktop;

// WHAT: Native preview, explicit column mapping and duplicate policy before persistence.
// CONTRACT: Only Save writes; background work owns its DB connection, controls freeze while saving.
// FAILURE: Close/cancel during validation never commits; save errors keep the preview for correction.
// TEST: Real EXE file dialog/preview/save/cancel/reopen plus isolated service rollback tests.
public sealed class ImportPreviewForm : Form
{
    private readonly DesktopImportSession session;
    private readonly DataGridView mapping = Grid("ColumnMappingGrid", "컬럼 매핑");
    private readonly DataGridView rows = Grid("ImportPreviewGrid", "가져오기 미리보기");
    private readonly Label summary = new() { Name = "ImportSummary", AccessibleName = "검증 결과 요약", Dock = DockStyle.Fill, AutoSize = false };
    private readonly TextBox issues = new() { Name = "ImportIssues", AccessibleName = "검증 오류", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox skip = new() { Name = "SkipDuplicates", AccessibleName = "중복 행 건너뛰기", Text = "중복 행 건너뛰기 (같은 프로젝트의 파일 중복 ID는 모두 제외)", AutoSize = true };
    private readonly Button validate = new() { Name = "ValidateImport", AccessibleName = "다시 검증", Text = "매핑 적용 / 다시 검증", AutoSize = true };
    private readonly Button save = new() { Name = "CommitImport", AccessibleName = "검증된 TC 가져오기", Text = "검증된 TC 가져오기", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Name = "CancelImport", AccessibleName = "가져오기 취소", Text = "취소", AutoSize = true };
    private bool working, saving, dirty;
    private long generation;
    public DesktopImportSession.Receipt? Receipt { get; private set; }

    public ImportPreviewForm(DesktopImportSession session)
    {
        this.session = session;
        Text = "Excel TC 가져오기 — 미리보기"; Name = "ImportPreviewForm"; AccessibleName = "Excel TC 가져오기 미리보기";
        Font = new Font("맑은 고딕", 10); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1240, 820); MinimumSize = new Size(1000, 720); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        foreach (var height in new[] { 36f, 52f, 30f, 158f, 34f }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        bool master = session.Inspection.IsProjectScopedMaster;
        string destination = master ? "마스터에 명시된 프로젝트" : session.Project.Name;
        layout.Controls.Add(new Label { Text = $"파일: {session.FileName}   |   대상 프로젝트: {destination}", Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
        layout.Controls.Add(summary, 0, 1);
        layout.Controls.Add(new Label { Text = master ? "마스터 Excel · 행별 프로젝트와 원본 TC 필드를 사용합니다."
            : "컬럼 매핑 · 원본 컬럼명을 표준 항목으로 연결한 후 다시 검증하세요. 필수: TC ID / 테스트 목적 / QA 기법 / 우선순위 / 수행 절차 / 기대 결과", Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 2);
        mapping.ReadOnly = master; mapping.AutoGenerateColumns = false;
        mapping.Columns.Add(new DataGridViewTextBoxColumn { Name = "SourceHeader", HeaderText = "원본 Excel 컬럼", ReadOnly = true, Width = 350 });
        var targets = WorkbookAdapter.CaseColumns.Concat(["대분류", "중분류"]).Distinct().ToArray();
        var target = new DataGridViewComboBoxColumn { Name = "TargetHeader", HeaderText = "표준 항목 (매핑된 컬럼)", Width = 450, FlatStyle = FlatStyle.Flat };
        target.Items.Add("(원본 유지)"); target.Items.AddRange(targets.Cast<object>().ToArray()); mapping.Columns.Add(target);
        foreach (var header in session.Inspection.Headers) mapping.Rows.Add(header, targets.Contains(header) ? header : "(원본 유지)");
        layout.Controls.Add(mapping, 0, 3);
        layout.Controls.Add(new Label { Text = "우선순위: P0 / P1 / P2 · 원본 값은 자동 변환하지 않습니다. 오류는 빨강, 중복은 노랑입니다.", Dock = DockStyle.Fill }, 0, 4);
        rows.AutoGenerateColumns = false;
        var columns = new[] { ("ExcelRow","Excel 행",75), ("ProjectId","프로젝트",125), ("Id","TC ID",160), ("Major","대분류",110), ("Minor","중분류",110),
            ("Purpose","테스트 목적",210), ("Preconditions","사전조건",180), ("Steps","테스트 절차",240), ("Expected","기대 결과",220),
            ("Priority","우선순위",85), ("Status","검증 상태",95), ("Reason","오류 사유",500) };
        foreach (var (property, title, width) in columns.Where(c => master || c.Item1 != "ProjectId"))
            rows.Columns.Add(new DataGridViewTextBoxColumn { Name = property, DataPropertyName = property, HeaderText = title, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        layout.Controls.Add(rows, 0, 5); layout.Controls.Add(issues, 0, 6);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.AddRange([skip, validate, save, cancel]); layout.Controls.Add(actions, 0, 7); Controls.Add(layout);
        mapping.CurrentCellDirtyStateChanged += (_, _) => { if (mapping.IsCurrentCellDirty) mapping.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        mapping.CellValueChanged += (_, _) => InvalidatePreview(); skip.CheckedChanged += (_, _) => InvalidatePreview();
        validate.Click += async (_, _) => await ValidateAsync(); save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        FormClosing += (_, e) => { if (saving) e.Cancel = true; else generation++; };
        Shown += async (_, _) => await ValidateAsync();
        CancelButton = cancel;
    }
    private void InvalidatePreview()
    {
        dirty = true; save.Enabled = false;
        if (!working) summary.Text = "매핑 또는 중복 정책이 변경되었습니다. 다시 검증하세요.";
    }
    private Dictionary<string, string> ReadMap() => mapping.Rows.Cast<DataGridViewRow>()
        .Where(r => r.Cells[1].Value?.ToString() != "(원본 유지)")
        .ToDictionary(r => r.Cells[0].Value!.ToString()!, r => r.Cells[1].Value!.ToString()!, StringComparer.Ordinal);
    private void Busy(bool value)
    {
        working = value; mapping.Enabled = !value; skip.Enabled = !value; validate.Enabled = !value;
        save.Enabled = false; cancel.Enabled = !saving; UseWaitCursor = value;
    }
    private async Task ValidateAsync()
    {
        if (working) return;
        var map = ReadMap(); bool skipRows = skip.Checked; long token = ++generation;
        Busy(true); summary.Text = "검증 중입니다. DB에는 저장하지 않습니다.";
        try
        {
            var result = await Task.Run(() => session.Validate(map, skipRows));
            if (IsDisposed || token != generation) return;
            rows.DataSource = new BindingList<DesktopImportSession.PreviewRow>(result.Rows.ToList());
            foreach (DataGridViewRow row in rows.Rows)
            {
                string state = row.Cells["Status"].Value?.ToString() ?? "";
                row.DefaultCellStyle.BackColor = state == "오류" ? Color.MistyRose : state == "중복" ? Color.LightGoldenrodYellow : Color.White;
            }
            string status = result.CanSave ? $"저장 예정 {result.Import.Cases.Count}개 · 중복 건너뛰기 {(skipRows ? "선택됨" : "선택 안 함")}"
                : result.DuplicateRows == result.Rows.Count && result.DuplicateRows > 0 && result.BlockingIssues.Count == 0
                    ? "모든 행이 중복되어 저장할 TC가 없습니다." : "가져오기 차단: 오류와 중복 정책을 확인하세요.";
            summary.Text = $"총 TC {result.Rows.Count}개  |  유효 {result.ValidRows}개  |  오류 {result.ErrorRows}개  |  중복 {result.DuplicateRows}개\n" + status;
            issues.Text = string.Join(Environment.NewLine, result.BlockingIssues.Select(i => $"{i.Sheet} 행 {i.Row}, {i.Field}: {i.Message}"));
            dirty = false; Busy(false); save.Enabled = result.CanSave;
        }
        catch (Exception ex) when (ImportErrors.Expected(ex))
        {
            if (!IsDisposed) { issues.Text = ex.Message; summary.Text = "검증 실패 · 저장하지 않았습니다."; }
        }
        finally { if (!IsDisposed && token == generation && working) Busy(false); }
    }
    private async Task SaveAsync()
    {
        if (working || dirty || !save.Enabled) return;
        var map = ReadMap(); bool skipRows = skip.Checked;
        saving = true; Busy(true); summary.Text = "DB 백업 후 트랜잭션으로 저장 중입니다. 완료될 때까지 기다려 주세요.";
        try
        {
            Receipt = await Task.Run(() => session.Save(map, skipRows));
            saving = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) when (ImportErrors.Expected(ex))
        {
            saving = false; Busy(false); dirty = true;
            summary.Text = "저장 실패 · 트랜잭션은 Rollback됩니다. 다시 검증 후 재시도하세요.";
            issues.Text = ex.Message;
            MessageBox.Show(this, "가져오기 실패: " + ex.Message, "Excel 가져오기 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private static DataGridView Grid(string name, string accessible) => new()
    {
        Name = name, AccessibleName = accessible, Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, ColumnHeadersHeight = 32
    };
}
internal static class ImportErrors
{
    public static bool Expected(Exception ex) => ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
        or System.Xml.XmlException or ArgumentException or FormatException or KeyNotFoundException or IndexOutOfRangeException
        or System.Text.Json.JsonException or NotSupportedException;
}

