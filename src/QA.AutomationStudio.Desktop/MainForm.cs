using System.ComponentModel;
using QAStudio.Domain;
using QAStudio.Application;
using QAStudio.Adapters;
using QAStudio.Infrastructure;

namespace QA.AutomationStudio.Desktop;

// WHAT: Native TC viewer, importer and entry points for registered TC execution.
// CONTRACT: Only the selected binding or the existing Smoke Test can execute.
// FAILURE: Unsupported TCs stay unavailable; failed persistence never appears as a saved result.
// TEST: Published EXE button -> new PID/main window -> SQLite -> refreshed grid/reopen.
public sealed class MainForm : Form
{
    private readonly ComboBox projects = new()
    {
        Name = "ProjectSelector", AccessibleName = "프로젝트 선택", DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill, DisplayMember = nameof(Project.Name), ValueMember = nameof(Project.Id)
    };
    private readonly DataGridView cases = new()
    {
        Name = "TestCaseGrid", AccessibleName = "TC 목록", Dock = DockStyle.Fill, ReadOnly = true,
        AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        ColumnHeadersHeight = 36
    };
    private readonly TextBox caseSearch = new() { Name = "CaseIdSearch", AccessibleName = "TC ID 검색",
        PlaceholderText = "TC ID", Dock = DockStyle.Fill };
    private readonly Label searchStatus = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button run = new()
    {
        Name = "RunTestsButton", AccessibleName = "테스트 실행", Text = "테스트 실행", Enabled = false,
        Dock = DockStyle.Fill, Margin = new Padding(16, 0, 0, 0)
    };
    private readonly TextBox log = new()
    {
        Name = "ExecutionLog", AccessibleName = "실행 로그", Multiline = true, ReadOnly = true,
        Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
        Font = new Font("Consolas", 10), WordWrap = true
    };
    private readonly Label pass = CountLabel("PassCount", "PASS", Color.FromArgb(24, 100, 60));
    private readonly Label fail = CountLabel("FailCount", "FAIL", Color.FromArgb(166, 44, 44));
    private readonly Label notRun = CountLabel("NotRunCount", "NOT RUN", Color.FromArgb(60, 75, 95));
    private readonly Label other = CountLabel("OtherCount", "기타 결과", Color.FromArgb(60, 75, 95));
    private FoundationStore? store;
    private string databasePath = "";
    private bool importing;
    private bool executing;
    private bool exporting;
    private SelectedTarget? target;
    private readonly Label targetLabel = new() { Name = "SelectedTarget", Text = "미지정 — 테스트할 D4 시퀀서를 선택하세요", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button selectProcess = new() { Name = "SelectTargetProcess", Text = "실행 중인 프로세스 선택", Dock = DockStyle.Fill };
    private readonly Button selectExe = new() { Name = "SelectTargetExe", Text = "EXE 선택 / 새 실행", Dock = DockStyle.Fill };
    private readonly Button import = new() { Name = "ImportExcelButton", AccessibleName = "Excel TC 가져오기", Text = "Excel TC 가져오기", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button exportPackage = new() { Name = "ExportResultPackageButton", AccessibleName = "결과 패키지 내보내기",
        Text = "결과 패키지 내보내기", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button manualRun = new() { Name = "ManualRunButton", AccessibleName = "수동 결과 등록",
        Text = "수동 결과 등록", Dock = DockStyle.Fill, Margin = new Padding(4, 6, 0, 9), Enabled = false };
    private readonly Label total = CountLabel("TotalCount", "총 TC", Color.FromArgb(40, 65, 95));
    private readonly Button manageMapping = new()
    {
        Name = "AutomationMappingButton", Text = "Automation Mapping", AccessibleName = "Automation Mapping",
        Dock = DockStyle.Fill, Margin = new Padding(0, 6, 3, 17), Enabled = false
    };

    public MainForm()
    {
        Text = "QA Automation Studio — Desktop";
        Name = "MainForm";
        AccessibleName = "QA Automation Studio 메인 창";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 760);
        MinimumSize = new Size(860, 620);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 10);
        BackColor = Color.FromArgb(245, 247, 250);
        other.Visible = false;
        BuildLayout();
        FormClosing += (_, e) => { if (importing || executing || exporting) e.Cancel = true; };
        selectProcess.Click += (_, _) => SelectRunningTarget();
        selectExe.Click += (_, _) => SelectExecutableTarget();
        run.Click += async (_, _) => await ExecuteSelectedAsync();
        manageMapping.Click += (_, _) => ManageSelectedMapping();
        cases.CurrentCellChanged += (_, _) =>
        {
            searchStatus.Text = "";
            UpdateRunAvailability();
        };
        import.Click += async (_, _) => await ImportExcelAsync();
        exportPackage.Click += async (_, _) => await ExportResultPackageAsync();
        manualRun.Click += (_, _) => RegisterManualResult();
        caseSearch.TextChanged += (_, _) => searchStatus.Text = "";
        caseSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            FindCase();
        };
        projects.SelectedIndexChanged += (_, _) =>
        {
            searchStatus.Text = "";
            LoadSelectedProject();
        };
        Shown += (_, _) => LoadRegisteredData();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
        foreach (var size in new[] { 64f, 42f, 48f, 58f, 34f }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        header.Controls.Add(new Label { Text = "QA Automation Studio", Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 23, FontStyle.Bold), ForeColor = Color.FromArgb(28, 44, 64) }, 0, 0);
        header.Controls.Add(manageMapping, 1, 0);
        layout.Controls.Add(header, 0, 0);

        var selection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        selection.Controls.Add(new Label { Text = "프로젝트", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        selection.Controls.Add(projects, 1, 0); selection.Controls.Add(run, 2, 0); selection.Controls.Add(import, 3, 0);
        selection.Controls.Add(exportPackage, 4, 0);
        layout.Controls.Add(selection, 0, 1);

        var targetBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        targetBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        targetBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));
        targetBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        targetBar.Controls.Add(new Label { Text = "테스트 대상", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        targetBar.Controls.Add(targetLabel, 1, 0); targetBar.Controls.Add(selectProcess, 2, 0); targetBar.Controls.Add(selectExe, 3, 0);
        layout.Controls.Add(targetBar, 0, 2);

        var counts = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
        foreach (var label in new[] { total, pass, fail, notRun, other }) counts.Controls.Add(label);
        var overview = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        overview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        overview.Controls.Add(counts, 0, 0);
        overview.Controls.Add(manualRun, 1, 0);
        layout.Controls.Add(overview, 0, 3);
        var searchBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var find = new Button { Text = "TC 찾기", Name = "FindCaseButton", Dock = DockStyle.Fill };
        find.Click += (_, _) => FindCase();
        searchBar.Controls.Add(caseSearch, 0, 0);
        searchBar.Controls.Add(find, 1, 0);
        searchBar.Controls.Add(searchStatus, 2, 0);
        layout.Controls.Add(searchBar, 0, 4);
        foreach (var (property, title, weight, min) in new[] { ("Id", "TC ID", 18f, 105), ("Purpose", "테스트 목적", 48f, 135),
                     ("Technique", "QA 기법", 17f, 65), ("Priority", "우선순위", 10f, 77), ("Suitability", "자동화 분류", 18f, 108),
                     ("Mode", "실행 방식", 15f, 105), ("Result", "실행 결과", 14f, 83), ("Automation", "자동화 연결", 18f, 95) })
            cases.Columns.Add(new DataGridViewTextBoxColumn { Name = property, DataPropertyName = property, HeaderText = title, FillWeight = weight, MinimumWidth = min });
        cases.RowTemplate.Height = 30;
        cases.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
        cases.DefaultCellStyle.SelectionBackColor = Color.FromArgb(218, 232, 247);
        cases.DefaultCellStyle.SelectionForeColor = Color.Black;
        layout.Controls.Add(cases, 0, 5);
        layout.Controls.Add(new Label { Text = "실행 로그", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 6);
        layout.Controls.Add(log, 0, 7);
        Controls.Add(layout);
    }

    // WHAT: Select the exact ID in the current project's grid.
    // WHY: The manual workbook identifies cases by project and TC ID.
    // CONTRACT: Search changes selection only. The existing run and registration guards still apply.
    // FAILURE: A missing ID leaves the selection intact and displays a message.
    // TEST: Select a manual case and verify that the mapped case still enables automated execution.
    private void FindCase()
    {
        if (importing || executing) return;
        string id = caseSearch.Text.Trim();
        if (id.Length == 0) return;
        foreach (DataGridViewRow row in cases.Rows)
        {
            if (row.DataBoundItem is not CaseRow tc || !string.Equals(tc.Id, id, StringComparison.Ordinal)) continue;
            cases.CurrentCell = row.Cells[0];
            cases.FirstDisplayedScrollingRowIndex = row.Index;
            searchStatus.Text = tc.Id + " 선택됨";
            return;
        }
        searchStatus.Text = "현재 프로젝트에 해당 TC ID가 없습니다.";
    }

    private void LoadRegisteredData()
    {
        WriteLog("Windows 네이티브 창 시작. 기존 Smoke Test와 명시적으로 연결한 TC를 실행할 수 있습니다.");
        string[] args = Environment.GetCommandLineArgs();
        string path = Path.Combine(AppContext.BaseDirectory, "data", "qa-studio.sqlite");
        if (args.Length == 3 && args[1] == "--database") path = Path.GetFullPath(args[2]);
        databasePath = path;
        if (!File.Exists(path))
        {
            projects.Enabled = false;
            WriteLog("등록된 TC 데이터베이스가 없습니다. TC 0개, 실행 기록 0개.");
            WriteLog("데이터베이스 경로: " + path);
            return;
        }
        try
        {
            store = new FoundationStore(path);
            projects.DataSource = store.Projects().ToList();
            projects.Enabled = projects.Items.Count > 0; import.Enabled = projects.Enabled;
            UpdateRunAvailability();
            WriteLog($"기존 SQLite 조회 연결: {path}");
            WriteLog($"등록 프로젝트 {projects.Items.Count}개. 테스트는 실행하지 않았습니다.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            store?.Dispose(); store = null; projects.DataSource = null; projects.Enabled = false;
            exportPackage.Enabled = false;
            cases.DataSource = null; SetCounts([]);
            WriteLog("데이터베이스 조회 실패: " + ex.Message);
        }
    }

    private void LoadSelectedProject()
    {
        if (store is null || projects.SelectedItem is not Project project) return;
        string? selectedId = (cases.CurrentRow?.DataBoundItem as CaseRow)?.Id;
        try
        {
            var data = store.ProjectResults(project.Id);
            var automatic = data.Runs.ToLookup(run => run.TestCaseId, StringComparer.Ordinal);
            var manual = data.ManualRuns.ToLookup(run => run.TestCaseId, StringComparer.Ordinal);
            var rows = data.Cases.Select(tc =>
            {
                var latest = CaseExecution.Latest(automatic[tc.Id].LastOrDefault(), manual[tc.Id].LastOrDefault());
                var assessment = data.AssessmentFor(tc);
                string mode = latest?.ModeText ?? (assessment.Status switch
                {
                    SuitabilityStatus.Implemented => "AUTOMATED",
                    SuitabilityStatus.Manual => "MANUAL",
                    _ => ""
                });
                return new CaseRow(tc.Id, tc.Fields.GetValueOrDefault("테스트 목적", ""), tc.Fields.GetValueOrDefault("QA 기법", ""),
                    tc.Fields.GetValueOrDefault("우선순위", ""), latest?.Result ?? ResultText.Format(ExecutionResult.NotRun),
                    data.Mappings.GetValueOrDefault(tc.Id) ?? (LaunchSmokeOrchestrator.Supports(tc) ? "기존 Smoke Test" : "자동화 미연결"),
                    assessment.StatusText, mode);
            }).ToList();
            cases.DataSource = new BindingList<CaseRow>(rows);
            SetCounts(rows);
            string? preferred = rows.Any(r => r.Id == selectedId) ? selectedId : LaunchSmokeOrchestrator.CaseId;
            int index = rows.FindIndex(r => r.Id == preferred);
            if (index >= 0) { cases.CurrentCell = cases.Rows[index].Cells[0]; cases.FirstDisplayedScrollingRowIndex = index; }
            UpdateRunAvailability();
            WriteLog($"프로젝트 [{project.Name}]: TC {rows.Count}개 조회. 실행 명령은 전송하지 않았습니다.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            cases.DataSource = null; SetCounts([]);
            WriteLog("TC 조회 실패: " + ex.Message);
        }
    }

    // WHAT: Select a workbook, preview without writes, then refresh the same project after commit.
    // CONTRACT: Cancelled dialogs never save; the importer button and project picker are guarded.
    // FAILURE: Native errors are shown and logged; no execution result is inferred from imported text.
    // TEST: TC-IMP-001/002/005/006/007/008/010 in the published EXE.
    private async Task ImportExcelAsync()
    {
        if (importing || executing || store is null || projects.SelectedItem is not Project project) return;
        using var dialog = new OpenFileDialog { Title = "Excel TC 파일 선택", Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) { WriteLog("Excel 파일 선택 취소 · DB 변경 없음."); return; }
        importing = true; selectProcess.Enabled = selectExe.Enabled = false; run.Enabled = false; import.Enabled = false; exportPackage.Enabled = false; manualRun.Enabled = manageMapping.Enabled = false; projects.Enabled = false; UseWaitCursor = true;
        try
        {
            var session = await Task.Run(() => new DesktopImportSession(databasePath, project, dialog.FileName));
            UseWaitCursor = false;
            using var preview = new ImportPreviewForm(session);
            if (preview.ShowDialog(this) == DialogResult.OK && preview.Receipt is { } receipt)
            {
                LoadSelectedProject();
                WriteLog($"Excel 가져오기 성공: {receipt.Added}개 저장, {receipt.Skipped}개 중복 건너뜀, 실패 0개. 배치: {receipt.BatchId}");
                WriteLog("Import 전 DB 백업: " + receipt.BackupPath);
                MessageBox.Show(this, $"가져오기 성공: {receipt.Added}개\n중복 건너뜀: {receipt.Skipped}개\n실패: 0개\nTC 목록을 갱신했습니다.", "Excel 가져오기 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else WriteLog("Excel 가져오기 미리보기 취소 · DB 변경 없음.");
        }
        catch (Exception ex) when (ImportErrors.Expected(ex))
        {
            WriteLog("Excel 가져오기 실패 · DB에 저장하지 않았습니다: " + ex.Message);
            MessageBox.Show(this, "파일을 가져올 수 없습니다.\n" + ex.Message, "Excel 가져오기 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { importing = false; selectProcess.Enabled = selectExe.Enabled = true; UseWaitCursor = false; import.Enabled = store is not null && projects.Items.Count > 0; projects.Enabled = projects.Items.Count > 0; UpdateRunAvailability(); }
    }

    // WHAT: Export the full registered portfolio to one user-selected output directory.
    // WHY: The package is generated independently of the selected TC or SUT.
    // CONTRACT: Use a separate read-only connection and keep file work off the UI thread.
    // FAILURE: Surface the actual export error or flag any evidence issues in the receipt.
    // TEST: Click the published button, choose a folder and inspect the resulting package.
    private async Task ExportResultPackageAsync()
    {
        if (!exportPackage.Enabled || exporting || importing || executing || store is null) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "전체 TC와 실행 이력을 저장할 상위 폴더를 선택하세요.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string folder = dialog.SelectedPath;
        exporting = true;
        exportPackage.Enabled = false;
        exportPackage.Text = "내보내는 중…";
        try
        {
            var receipt = await Task.Run(() => new ResultPackageExporter(new WorkbookAdapter())
                .Export(folder, databasePath, FoundationStore.ReadResultPackage(databasePath)));
            string summary = $"TC {receipt.Cases}건 · 자동 실행 {receipt.Runs - receipt.ManualRuns}건 · 수동 실행 {receipt.ManualRuns}건 · 증적 확인 필요 {receipt.EvidenceIssues}건";
            WriteLog($"결과 패키지 내보내기 완료 · {summary} · {receipt.Directory}");
            MessageBox.Show(this, $"{summary}\n\n{receipt.Directory}", "결과 패키지 내보내기 완료",
                MessageBoxButtons.OK, receipt.EvidenceIssues == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ImportErrors.Expected(ex))
        {
            WriteLog("결과 패키지 내보내기 실패: " + ex.Message);
            MessageBox.Show(this, ex.Message, "결과 패키지 내보내기 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            exporting = false;
            exportPackage.Text = "결과 패키지 내보내기";
            UpdateRunAvailability();
        }
    }
    // WHAT: Select the application under test independently of the TC project.
    // WHY: An already running D4 Sequencer must be attached rather than launched again.
    // CONTRACT: Selection alone never runs a TC or changes SQLite; PID identity is checked at execution.
    // FAILURE: Cancel keeps the prior selection; exited/reused PID returns ERROR and requires reselection.
    // TEST: Native picker -> selected D4 PID -> Run -> real window assertion -> saved result.
    private void SelectRunningTarget()
    {
        if (importing || executing) return;
        using var picker = new TargetProcessForm();
        if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedTarget is { } chosen)
            SetTarget(chosen);
    }
    private void SelectExecutableTarget()
    {
        if (importing || executing) return;
        using var picker = new OpenFileDialog { Title = "테스트할 D4 시퀀서 EXE 선택 (새 실행)", Filter = "Windows 실행 파일 (*.exe)|*.exe", CheckFileExists = true, Multiselect = false, RestoreDirectory = true };
        if (picker.ShowDialog(this) == DialogResult.OK) SetTarget(new(picker.FileName, null, null));
    }
    private void SetTarget(SelectedTarget chosen)
    {
        target = chosen;
        targetLabel.Text = chosen.ProcessId is int pid ? $"연결: {Path.GetFileName(chosen.Executable)} · PID {pid}" : $"새 실행: {chosen.Executable}";
        WriteLog($"테스트 대상 지정: {chosen.Executable}" + (chosen.ProcessId is int id ? $" · PID {id} · 시작 {chosen.StartTimeUtc:O}" : " · 새 실행"));
        UpdateRunAvailability();
    }
    private void UpdateRunAvailability()
    {
        exportPackage.Enabled = store is not null && !importing && !executing && !exporting;
        string? scenario = SelectedMapping();
        manualRun.Enabled = store is not null && !importing && !executing && scenario is null &&
            cases.CurrentRow?.DataBoundItem is CaseRow selected && selected.Suitability != "RETIRED";
        run.Enabled = target is not null && !executing && !importing && store is not null &&
            projects.SelectedItem is Project p && p.Id == LaunchSmokeOrchestrator.ProjectId &&
            cases.CurrentRow?.DataBoundItem is CaseRow row &&
            (scenario is not null && target.ProcessId is not null || row.Id == LaunchSmokeOrchestrator.CaseId);
        manageMapping.Enabled = store is not null && !executing && !importing &&
            projects.SelectedItem is Project mp && mp.Id == LaunchSmokeOrchestrator.ProjectId &&
            cases.CurrentRow?.DataBoundItem is CaseRow mr && mr.Id != LaunchSmokeOrchestrator.CaseId;
    }

    private void RegisterManualResult()
    {
        if (!manualRun.Enabled || store is null || projects.SelectedItem is not Project project ||
            cases.CurrentRow?.DataBoundItem is not CaseRow row) return;
        try
        {
            var data = store.ProjectResults(project.Id);
            var tc = data.Cases.Single(testCase => testCase.Id == row.Id);
            var assessment = data.AssessmentFor(tc);
            if (assessment.Status is SuitabilityStatus.Implemented or SuitabilityStatus.Retired) return;
            using var dialog = new ManualRunForm(databasePath, tc, assessment, data.ReadinessFor(tc), data.ManualRuns.Where(run => run.TestCaseId == tc.Id).ToArray());
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Saved is { } saved)
            {
                LoadSelectedProject();
                WriteLog($"[{saved.TestCaseId}] {saved.ResultText} · 수동 결과 저장 · {saved.Id}");
            }
        }
        catch (Exception ex) when (ImportErrors.Expected(ex))
        {
            MessageBox.Show(this, ex.Message, "수동 결과 등록 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private string? SelectedMapping() => store is not null && projects.SelectedItem is Project p &&
        cases.CurrentRow?.DataBoundItem is CaseRow row ? store.AutomationScenario(p.Id, row.Id) : null;
    private void ManageSelectedMapping()
    {
        if (!manageMapping.Enabled || importing || executing || store is null ||
            projects.SelectedItem is not Project project || cases.CurrentRow?.DataBoundItem is not CaseRow row) return;
        try
        {
            var tc = store.Cases(project.Id).Single(c => c.Id == row.Id);
            using var dialog = new AutomationMappingForm(store, tc, SelectedMapping());
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            LoadSelectedProject();
            WriteLog(dialog.SavedScenario is { } scenario
                ? $"[{tc.Id}] 사용자 명시 연결 저장: {scenario}"
                : $"[{tc.Id}] 자동화 연결 해제 · 원본 TC와 실행 이력 유지");
        }
        catch (Exception ex) when (ImportErrors.Expected(ex))
        {
            MessageBox.Show(this, ex.Message, "자동화 연결 조회 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void ExecuteNotepadScenario(bool doubleOutput, TestCase? linkedCase = null, bool requireMacroOff = false, bool requireTargetMismatch = false, bool twoSingleFTriggers = false)
    {
        if (target is null || store is null || executing || importing) return;
        executing = true;
        try
        {
            using var dialog = new NotepadVerificationForm(target, Path.Combine(Path.GetDirectoryName(databasePath)!, "evidence", "notepad-text"),
                doubleOutput, linkedCase, linkedCase is null ? null : store, requireMacroOff, requireTargetMismatch, twoSingleFTriggers);
            dialog.ShowDialog(this);
            if (dialog.Completed is { } completed)
            {
                if (linkedCase is not null) LoadSelectedProject();
                WriteLog($"[{completed.Run.TestCaseId}] {ResultText.Format(completed.Run.Result)} · 자동 F3 실행 · {completed.Run.Id} · JSON 증적 저장 · 원본 TC 변경 없음");
                WriteLog(completed.Run.Evidence);
            }
        }
        finally { executing = false; UpdateRunAvailability(); }
    }
    private async Task ExecuteSelectedAsync()
    {
        if (executing || importing || !run.Enabled || store is null || projects.SelectedItem is not Project project ||
            cases.CurrentRow?.DataBoundItem is not CaseRow selected) return;
        var tc = store.Cases(project.Id).Single(c => c.Id == selected.Id);
        string? scenario = store.AutomationScenario(project.Id, tc.Id);
        if (scenario is not null) { ExecuteNotepadScenario(scenario == "MAC-NP-002", tc, scenario == "MAC-NP-003", scenario == "MAC-NP-004", scenario == "MAC-NP-005"); return; }
        if (!LaunchSmokeOrchestrator.Supports(tc)) { WriteLog($"[{tc.Id}] 자동화 미연결 · 실행 차단"); return; }
        var selectedTarget = target!;
        executing = true; selectProcess.Enabled = selectExe.Enabled = false; run.Enabled = false; import.Enabled = false; exportPackage.Enabled = false; manualRun.Enabled = manageMapping.Enabled = false; projects.Enabled = false; cases.Enabled = false;
        WriteLog($"[{tc.Id}] 실제 실행 시작: {selectedTarget.Executable} · {(selectedTarget.ProcessId is int pid ? $"연결 PID {pid}" : "새 실행")}");
        WriteLog("검증 범위: 지정한 D4 시퀀서 프로세스 + 표시되는 응답 가능한 메인 창. 게임 입력 및 다른 기능은 검증하지 않습니다.");
        try
        {
            var completed = await Task.Run(async () =>
            {
                using var executionStore = new FoundationStore(databasePath);
                var orchestrator = new LaunchSmokeOrchestrator(new ProcessWindowAdapter(), executionStore);
                return await orchestrator.ExecuteAsync(tc, new(selectedTarget.Executable, "매크로 스튜디오 6.4",
                    Path.Combine(Path.GetDirectoryName(databasePath)!, "evidence", "launch-smoke"), ExistingProcessId: selectedTarget.ProcessId, ExpectedStartTimeUtc: selectedTarget.StartTimeUtc));
            });
            LoadSelectedProject();
            WriteLog($"[{tc.Id}] {ResultText.Format(completed.Result)} · SQLite 저장 완료 · {completed.Id}");
            WriteLog("실행 증거: " + completed.Evidence);
            using var evidence = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(completed.Evidence));
            WriteLog("판정 사유: " + evidence.RootElement.GetProperty("reason").GetString());
        }
        catch (Exception ex) when (ImportErrors.Expected(ex) || ex is System.ComponentModel.Win32Exception)
        {
            WriteLog("실행 또는 결과 저장 오류: " + ex.Message);
            WriteLog("DB에 저장하지 못한 결과를 PASS로 표시하지 않습니다.");
            MessageBox.Show(this, ex.Message, "테스트 실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            executing = false; selectProcess.Enabled = selectExe.Enabled = true; import.Enabled = store is not null && projects.Items.Count > 0;
            projects.Enabled = projects.Items.Count > 0; cases.Enabled = true; UpdateRunAvailability();
        }
    }
    private void SetCounts(IReadOnlyCollection<CaseRow> rows)
    {
        total.Text = "총 TC  " + rows.Count;
        pass.Text = "PASS  " + rows.Count(x => x.Result == "PASS");
        fail.Text = "FAIL  " + rows.Count(x => x.Result == "FAIL");
        notRun.Text = "NOT RUN  " + rows.Count(x => x.Result == "NOT RUN");
        int remaining = rows.Count(x => x.Result is not ("PASS" or "FAIL" or "NOT RUN"));
        other.Text = "기타 결과  " + remaining; other.Visible = remaining > 0;
    }
    private void WriteLog(string message) => log.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    private static Label CountLabel(string name, string text, Color color) => new()
    {
        Name = name, AccessibleName = text + " 건수", Text = text + "  0", AutoSize = true,
        Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = color, Margin = new Padding(0, 0, 32, 0)
    };
    protected override void Dispose(bool disposing)
    {
        if (disposing) store?.Dispose();
        base.Dispose(disposing);
    }
    private sealed record CaseRow(string Id, string Purpose, string Technique, string Priority, string Result,
        string Automation, string Suitability, string Mode);
}
