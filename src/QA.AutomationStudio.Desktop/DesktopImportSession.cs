using QAStudio.Adapters;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QA.AutomationStudio.Desktop;

// WHAT: Coordinate the existing importer for a frozen workbook and a selected project.
// WHY: Preview is read-only; the user must explicitly resolve duplicates before saving.
// CONTRACT: Original bytes/fields survive, new IDs only, backup before atomic CommitNew.
// FAILURE: Non-duplicate validation errors block the whole batch; stale DB collisions rollback.
// TEST: TC-IMP-003/004/005/008/009/010; aliases, skip policy, frozen source, concurrent collision.
public sealed class DesktopImportSession
{
    public string DatabasePath { get; }
    public Project Project { get; }
    public string FileName { get; }
    public byte[] Source { get; }
    public WorkbookAdapter.SheetInspection Inspection { get; }
    private readonly WorkbookAdapter adapter = new();
    public DesktopImportSession(string databasePath, Project project, string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("XLSX 파일만 지원합니다. CSV 가져오기는 기존 ImportService에서 지원하지 않습니다.");
        var file = new FileInfo(path);
        if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("파일 크기는 32 MiB 이하여야 합니다.");
        DatabasePath = databasePath; Project = project; FileName = file.Name;
        Source = File.ReadAllBytes(path); Inspection = adapter.Inspect(Source);
    }
    public sealed record PreviewRow(int ExcelRow, string ProjectId, string Id, string Major, string Minor, string Purpose,
        string Preconditions, string Steps, string Expected, string Priority, string Status, string Reason);
    public sealed record Validation(ImportPreview Import, IReadOnlyList<PreviewRow> Rows, IReadOnlyList<ImportIssue> BlockingIssues,
        int DuplicateRows, int ErrorRows, bool SkipDuplicates)
    {
        public int ValidRows => Rows.Count(r => r.Status == "유효");
        public bool CanSave => Import.Cases.Count > 0 && BlockingIssues.Count == 0 && (SkipDuplicates || DuplicateRows == 0);
    }
    public sealed record Receipt(string BatchId, int Added, int Skipped, string BackupPath, IReadOnlyList<string> CaseIds);

    public Validation Validate(IReadOnlyDictionary<string, string> map, bool skipDuplicates)
    {
        string Canonical(string header) => map.GetValueOrDefault(header) ?? header;
        string Id(WorkbookAdapter.SheetRow row) => row.Fields.FirstOrDefault(x => Canonical(x.Key) == "TC ID").Value ?? "";
        var assignments = Inspection.IsProjectScopedMaster ? new Dictionary<string, string>()
            : Inspection.Rows.Select(Id).Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => Project.Id, StringComparer.Ordinal);
        var options = new ImportOptions(assignments, map, TestCasesOnly: true);
        using var store = new FoundationStore(DatabasePath);
        var service = new ImportService(adapter, store);
        var preview = service.Preview(Source, options);
        var registeredProjects = store.Projects().Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var existing = preview.Cases.Select(c => c.ProjectId).Distinct(StringComparer.Ordinal)
            .SelectMany(store.Cases).Select(c => (c.ProjectId, c.Id)).ToHashSet();
        var fileDuplicates = preview.Cases.GroupBy(c => (c.ProjectId, c.Id)).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet();
        var nonDuplicateIssues = preview.Issues.Where(i => i.Message != "Duplicate TC ID").ToList();
        for (int i = 0; i < preview.Cases.Count; i++)
            if (!registeredProjects.Contains(preview.Cases[i].ProjectId))
                nonDuplicateIssues.Add(new("TC_Master", preview.CaseRows![i], "프로젝트", "DB에 등록된 프로젝트 ID가 필요합니다."));
        var dataRows = (preview.CaseRows ?? []).ToHashSet();
        var global = nonDuplicateIssues.Where(i => i.Sheet != "TC_Master" || !dataRows.Contains(i.Row)).ToArray();
        List<PreviewRow> rows = [];
        var conflicts = new HashSet<(string ProjectId, string Id)>();
        for (int i = 0; i < preview.Cases.Count; i++)
        {
            var c = preview.Cases[i]; var f = c.Fields; int row = preview.CaseRows![i];
            var reasons = nonDuplicateIssues.Where(x => x.Sheet == "TC_Master" && x.Row == row).Concat(global)
                .Select(x => $"{x.Sheet} {x.Field}: {x.Message}").Distinct().ToList();
            var key = (c.ProjectId, c.Id);
            bool duplicate = fileDuplicates.Contains(key) || existing.Contains(key);
            if (duplicate) conflicts.Add(key);
            string status = reasons.Count > 0 ? "오류" : duplicate ? "중복" : "유효";
            if (fileDuplicates.Contains(key)) reasons.Add("파일에서 같은 프로젝트에 중복된 TC ID의 모든 행을 건너뜁니다.");
            if (existing.Contains(key)) reasons.Add("해당 프로젝트에 이미 등록된 TC ID입니다. 덮어쓰지 않습니다.");
            rows.Add(new(row, c.ProjectId, c.Id, f.GetValueOrDefault("대분류", f.GetValueOrDefault("영역", "")),
                f.GetValueOrDefault("중분류", ""), f.GetValueOrDefault("테스트 목적", ""), f.GetValueOrDefault("사전조건", ""),
                f.GetValueOrDefault("수행 절차", ""), f.GetValueOrDefault("기대 결과", ""), f.GetValueOrDefault("우선순위", ""), status, string.Join("; ", reasons)));
        }
        var eligible = preview.Cases.Where(c => !conflicts.Contains((c.ProjectId, c.Id))).ToArray();
        var designs = preview.Automations.Where(a => eligible.Any(c => c.ProjectId == a.ProjectId && c.Id == a.TestCaseId)).ToArray();
        var validated = preview with { Cases = eligible, Automations = designs, Issues = nonDuplicateIssues,
            CaseRows = rows.Where(r => !conflicts.Contains((r.ProjectId, r.Id))).Select(r => r.ExcelRow).ToArray() };
        return new(validated, rows, nonDuplicateIssues, rows.Count(r => conflicts.Contains((r.ProjectId, r.Id))),
            rows.Count(r => r.Status == "오류"), skipDuplicates);
    }
    public Receipt Save(IReadOnlyDictionary<string, string> map, bool skipDuplicates)
    {
        var current = Validate(map, skipDuplicates);
        if (!current.CanSave) throw new InvalidDataException("검증 오류 또는 중복이 남아 있습니다. 가져오기가 차단되었습니다.");
        string batch = "GUI-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "backups");
        Directory.CreateDirectory(directory);
        string backup = Path.Combine(directory, batch + ".sqlite");
        using var store = new FoundationStore(DatabasePath);
        store.BackupTo(backup);
        store.CommitNew(batch, current.Import);
        // The immutable imports/import_cases records are the authoritative successful-import history.
        return new(batch, current.Import.Cases.Count, current.DuplicateRows, backup, current.Import.Cases.Select(c => c.Id).ToArray());
    }
}
