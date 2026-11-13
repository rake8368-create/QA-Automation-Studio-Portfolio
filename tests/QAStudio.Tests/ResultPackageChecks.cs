using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using QAStudio.Adapters;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QAStudio.Tests;

// WHAT: Cover package behavior that cannot be exercised with the current operating history.
// WHY: All operating evidence exists, and the current latest TC outcomes are PASS or NOT RUN.
// CONTRACT: Use a disposable DB and files; exercise the actual adapter, exporter and import store.
// FAILURE: A mismatch throws and exits nonzero without involving the SUT.
// TEST: Six statuses, project-scoped collisions, source hashes, images and partial-export cleanup.
internal static class ResultPackageChecks
{
    public static int Run(string output)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string scratch = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string db = Path.Combine(scratch, "fixture.sqlite");
        var project = new Project("macro-studio", "MacroStudio");
        var other = new Project("fixture", "Fixture");
        var tc = Case(project.Id, "TC-1", new() { ["프로젝트"] = "원본", ["옵션"] = "", ["원문"] = "가\r\n나 _x000D_  " });
        var blank = Case(other.Id, tc.Id, new());
        var statuses = new[] { ExecutionResult.Pass, ExecutionResult.Fail, ExecutionResult.Error,
            ExecutionResult.Blocked, ExecutionResult.Cancelled };
        var statusCases = statuses.Skip(1).Select((_, i) => Case(project.Id, $"TC-{i + 2}", new())).ToArray();
        string json = Path.Combine(scratch, "run.json");
        File.WriteAllText(json, """{"screenshotPaths":["explicit.jpg","missing.jpg"],"screenshot":"Not captured"}""");
        foreach (string name in new[] { "run.png", "explicit.jpg", "unrelated.png" })
            File.WriteAllBytes(Path.Combine(scratch, name), [137, 80, 78, 71]);
        string hash = Hash(json);
        byte[] source = [1, 2, 3];
        using (var store = new FoundationStore(db))
        {
            store.RegisterProject(project);
            store.RegisterProject(other);
            store.CommitNew("fixture", new(source, Convert.ToHexString(SHA256.HashData(source)),
                [tc, blank, .. statusCases], [], []));
            store.SetAutomationMapping(project.Id, tc.Id, "MAC-NP-001");
            AddRun(store, tc, "history", ExecutionResult.Error, "missing-primary.json", hash);
            AddRun(store, tc, "latest", ExecutionResult.Pass, "run.json", hash);
            for (int i = 0; i < statusCases.Length; i++)
                AddRun(store, statusCases[i], "run-" + i, statuses[i + 1], "run.json", i == 1 ? "BAD_HASH" : hash);
        }

        string dbHash = Hash(db);
        var snapshot = FoundationStore.ReadResultPackage(db);
        var adapter = new WorkbookAdapter();
        var exporter = new ResultPackageExporter(adapter);
        string destination = Path.Combine(scratch, "output");
        var first = exporter.Export(destination, db, snapshot);
        var second = exporter.Export(destination, db, FoundationStore.ReadResultPackage(db));
        Check(first.Cases == 6 && first.Runs == 6 && first.EvidenceIssues == 6, "Receipt");
        Check(first.Directory != second.Directory && Directory.Exists(first.Directory), "Repeat package");

        var master = File.ReadAllBytes(Path.Combine(first.Directory, ResultPackageExporter.MasterFile));
        var preview = adapter.Preview(master, new ImportOptions(new Dictionary<string, string>()));
        Check(preview.IsValid && preview.IsProjectScopedMaster && preview.Cases.Count == 6 && preview.Automations.Count == 0, "Master preview");
        foreach (var original in snapshot.SelectMany(p => p.Cases))
        {
            var loaded = preview.Cases.Single(t => t.ProjectId == original.ProjectId && t.Id == original.Id);
            Check(original.Fields.Count == loaded.Fields.Count &&
                original.Fields.All(pair => loaded.Fields.GetValueOrDefault(pair.Key) == pair.Value), "Exact source fields");
        }
        using (var store = new FoundationStore(db))
        {
            bool duplicateRejected = false;
            try { store.Commit("duplicate", preview); }
            catch (InvalidDataException) { duplicateRejected = true; }
            Check(duplicateRejected, "Existing projects reject master duplicates atomically");
        }
        Check(dbHash == Hash(db), "Operating fixture DB bytes");
        Check(Hash(json) == hash, "Original JSON");
        using var results = ZipFile.OpenRead(Path.Combine(first.Directory, ResultPackageExporter.ResultsFile));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var resultsSheet = results.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var rows = XDocument.Load(resultsSheet).Descendants(ns + "row").Skip(1).ToArray();
        string Cell(XElement row, string column) => row.Elements(ns + "c")
            .FirstOrDefault(cell => (string?)cell.Attribute("r") == column + (string?)row.Attribute("r"))?.Value ?? "";
        var latest = rows.Single(r => Cell(r, "A") == project.Id && Cell(r, "B") == tc.Id);
        var noRun = rows.Single(r => Cell(r, "A") == other.Id);
        Check(rows.Select(r => Cell(r, "F")).ToHashSet().SetEquals(statuses.Select(ResultText.Format).Append("NOT RUN")), "Result vocabulary");
        Check(Cell(latest, "H") == "latest" && Cell(latest, "K") == "2" && Cell(latest, "E") == "예", "Latest result and count");
        Check(Cell(noRun, "H") == "" && Cell(noRun, "K") == "0" && Cell(noRun, "E") == "아니요", "Unrun project-scoped TC");
        string runFolder = Path.Combine(first.Directory, Cell(latest, "J"));
        Check(new[] { "run.json", "explicit.jpg", "run.png" }.All(name =>
            Hash(Path.Combine(runFolder, name)) == Hash(Path.Combine(scratch, name))), "Associated copies match originals");
        Check(!File.Exists(Path.Combine(runFolder, "missing.jpg")) && !File.Exists(Path.Combine(runFolder, "unrelated.png")), "No replacement files");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runFolder, ResultPackageExporter.EvidenceIndex)));
        Check(manifest.RootElement.GetProperty("files").EnumerateArray().Any(e =>
            e.GetProperty("status").GetString() == "Missing"), "Missing listed");
        var indexes = Directory.GetFiles(Path.Combine(first.Directory, ResultPackageExporter.EvidenceDirectory),
            ResultPackageExporter.EvidenceIndex, SearchOption.AllDirectories);
        Check(indexes.Length == 6 && indexes.Any(path => File.ReadAllText(path).Contains("RecordedHashMismatch", StringComparison.Ordinal)),
            "All runs indexed and recorded hash mismatch surfaced");

        string failure = Path.Combine(scratch, "output-failure");
        bool failed = false;
        try { new ResultPackageExporter(new FailingResultsWorkbook(adapter)).Export(failure, db, snapshot); }
        catch (IOException ex) when (ex.Message == "Simulated workbook write failure") { failed = true; }
        Check(failed && !Directory.EnumerateFileSystemEntries(failure).Any() && dbHash == Hash(db), "Failed package does not publish or write DB");
        timer.Stop();
        Console.WriteLine($"PASS result package fixture ({timer.ElapsedMilliseconds} ms), evidence: {first.Directory}");
        return 0;
    }

    private static TestCase Case(string project, string id, Dictionary<string, string> fields)
    {
        fields.Add("TC ID", id);
        fields.Add("테스트 목적", "목적");
        fields.Add("QA 기법", "기법");
        fields.Add("우선순위", "P1");
        fields.Add("수행 절차", "절차");
        fields.Add("기대 결과", "결과");
        return new(project, id, fields);
    }

    private static void AddRun(FoundationStore store, TestCase tc, string id, ExecutionResult result, string evidence, string hash)
    {
        store.RecordRun(new(tc.ProjectId, id, tc.Id, result, "fixture assertion", evidence,
            DateTimeOffset.UtcNow, "isolated", "fixture", "fixture", "{}"),
            new(tc.ProjectId, "EV-" + id, id, evidence, hash));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private sealed class FailingResultsWorkbook(WorkbookAdapter adapter) : IResultPackageWorkbook
    {
        public byte[] ExportMaster(IReadOnlyList<ResultPackageCase> cases) => adapter.ExportMaster(cases);
        public byte[] ExportResults(IReadOnlyList<ResultPackageCase> cases) => throw new IOException("Simulated workbook write failure");
    }
}
