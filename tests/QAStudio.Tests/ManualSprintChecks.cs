using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using QAStudio.Adapters;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QAStudio.Tests;

// WHAT: Verify v5 preservation, manual append, evidence and unified result projection.
// WHY: Manual results must be independently countable and require actual observations for PASS/FAIL.
// CONTRACT: Run exclusively on new copies of the supplied pre-migration backup.
// FAILURE: Failed preconditions, DB rejects, or absent evidence cannot become a successful verdict.
// TEST: --manual-sprint <v5 backup> <scratch parent>, with no real SUT dispatch.
internal static class ManualSprintChecks
{
    public static int Run(string baseline, string output)
    {
        var clock = Stopwatch.StartNew();
        string scratch = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string db = Path.Combine(scratch, "main.sqlite");
        string failedDb = Path.Combine(scratch, "rollback.sqlite");
        string original = LegacySnapshot(baseline);
        File.Copy(baseline, db);
        File.Copy(baseline, failedDb);
        using (var sql = new SqliteDatabase(failedDb)) sql.Execute("CREATE TABLE manual_runs(blocker TEXT)");
        Reject(() => new FoundationStore(failedDb).Dispose());
        using (var sql = new SqliteDatabase(failedDb, readOnly: true))
        {
            Check(sql.Query("PRAGMA user_version")[0][0] == "5", "Rollback kept schema v5");
            Check(sql.Query("SELECT name FROM sqlite_master WHERE name='case_assessments'").Count == 0, "No partial v6 DDL");
        }
        Check(original == LegacySnapshot(failedDb), "Failed migration preserved all legacy data");

        const string project = "macro-studio";
        string source = Path.Combine(scratch, "verified-observation.txt");
        File.WriteAllText(source, "Isolated test attachment");
        string sourceHash = Hash(source);
        var time = DateTimeOffset.UtcNow.AddMinutes(-1);
        ManualRun saved;
        using (var store = new FoundationStore(db))
        {
            var data = store.ProjectResults(project);
            var all = store.Projects().SelectMany(p =>
            {
                var snapshot = store.ProjectResults(p.Id);
                return snapshot.Cases.Select(snapshot.AssessmentFor);
            }).ToArray();
            Check(all.Length == 76 && all.Count(a => a.Status == SuitabilityStatus.Implemented) == 5 &&
                all.Count(a => a.Status == SuitabilityStatus.Candidate) == 65 && all.Count(a => a.Status == SuitabilityStatus.Manual) == 1 &&
                all.Count(a => a.Status == SuitabilityStatus.Blocked) == 1 && all.Count(a => a.Status == SuitabilityStatus.Review) == 4 &&
                all.All(a => a.Observation.Length > 0 && a.Rationale.Length > 0), "Complete audited assessments");
            var tc = data.Cases.Single(c => c.Id == "AUD-012");
            var mapped = data.Cases.Single(c => c.Id == "MAC-QA-001");
            var recorder = new ManualExecutionRecorder(store);
            Reject(() => recorder.Record(db, tc, "Fixture", ManualResult.Pass, "", "memo", null, time));
            Reject(() => recorder.Record(db, tc, "Fixture", ManualResult.Fail, "", "", null, time));
            Reject(() => recorder.Record(db, tc, "Fixture", ManualResult.Skipped, "", "", null, time));
            Reject(() => recorder.Record(db, tc, "Fixture", ManualResult.Skipped, "", "reason", Path.Combine(scratch, "missing-file.txt"), time));
            Reject(() => recorder.Record(db, mapped, "Fixture", ManualResult.Blocked, "", "reason", source, time));
            Reject(() => recorder.Record(db, tc with { ProjectId = "qa-studio" }, "Fixture", ManualResult.Blocked, "", "reason", null, time));
            Check(store.ManualRuns(project).Count == 0 && !Directory.EnumerateFiles(Path.Combine(scratch, "evidence"), "*", SearchOption.AllDirectories).Any(),
                "Rejected saves leave no rows or copied attachments");
            saved = recorder.Record(db, tc, "Fixture", ManualResult.Skipped, "Playback not performed", "Isolated workflow", source, time);
            var automatic = data.Runs.Last(r => r.TestCaseId == "IMP-GUI-20261028-001");
            var legacy = data.Cases.Single(c => c.Id == automatic.TestCaseId);
            recorder.Record(db, legacy, "Fixture", ManualResult.Blocked, "", "Earlier fixture time", null, automatic.ExecutedAt.AddSeconds(-1));
            recorder.Record(db, legacy, "Fixture", ManualResult.Error, "", "Later fixture time", null, time);
            Check(CaseExecution.Latest(automatic, store.ManualRuns(project).First(r => r.TestCaseId == legacy.Id))?.Mode == ExecutionMode.Automated,
                "A backdated manual record does not replace the newer automated result");
        }
        using (var reopened = new FoundationStore(db))
            Check(reopened.ManualRuns(project).Single(r => r.Id == saved.Id) == saved, "Manual persistence after reopen");
        using (var sql = new SqliteDatabase(db))
        {
            Reject(() => sql.Execute("UPDATE manual_runs SET result='PASS' WHERE id=?", saved.Id));
            Reject(() => sql.Execute("DELETE FROM manual_runs WHERE id=?", saved.Id));
            Check(sql.Query("PRAGMA user_version")[0][0] == "6" && sql.Query("PRAGMA foreign_key_check").Count == 0, "v6 integrity");
        }
        Check(original == LegacySnapshot(db), "Existing table SQL, triggers, all rows and histories preserved");

        var snapshotForExport = FoundationStore.ReadResultPackage(db);
        var exporter = new ResultPackageExporter(new WorkbookAdapter());
        var receipt = exporter.Export(Path.Combine(scratch, "export"), db, snapshotForExport);
        Check(receipt.Cases == 76 && receipt.Runs == 20 && receipt.ManualRuns == 3 && receipt.EvidenceIssues == 0, "Separated execution totals");
        string resultFile = Path.Combine(receipt.Directory, ResultPackageExporter.ResultsFile);
        var latest = ReadRows(resultFile, 1);
        var history = ReadRows(resultFile, 2);
        var manual = ReadRows(resultFile, 3);
        var audit = ReadRows(resultFile, 4);
        var row = latest.Single(r => r.GetValueOrDefault("B") == "AUD-012");
        Check(row["F"] == "SKIPPED" && row["N"] == "MANUAL" && row["O"] == "0" && row["P"] == "1", "Manual latest and count");
        Check(latest.Single(r => r.GetValueOrDefault("B") == "IMP-GUI-20261028-001")["F"] == "ERROR", "Manual latest supersedes an earlier automatic result");
        Check(history.Count == 17 && history.All(r => r["I"] == "AUTOMATED") && manual.Count == 3 && audit.Count == 76, "Workbook history and review sheets");
        string folder = Path.Combine(receipt.Directory, row["J"]);
        Check(Hash(source) == sourceHash && Hash(Path.Combine(folder, Path.GetFileName(source))) == sourceHash &&
            saved.AttachmentSha256 == sourceHash, "Original and packaged manual attachment hashes");
        File.Delete(Path.Combine(scratch, saved.AttachmentPath!));
        var missing = exporter.Export(Path.Combine(scratch, "export"), db, snapshotForExport);
        Check(missing.EvidenceIssues == 1 && missing.Runs == 20, "Missing attachment surfaced without reclassifying results");
        Check(File.ReadAllText(Path.Combine(missing.Directory, row["J"], ResultPackageExporter.EvidenceIndex)).Contains("\"Missing\"", StringComparison.Ordinal),
            "Missing attachment is explicitly recorded");
        Check(original == LegacySnapshot(db), "Repeated and missing-file exports preserve original DB tables");
        int mappingChecks = 0;
        MappingChecks.Run(Path.Combine(scratch, "mapping"), (name, action) =>
        {
            action();
            mappingChecks++;
            Console.WriteLine($"PASS {name}");
        }, Check);
        Console.WriteLine($"PASS manual sprint + {mappingChecks} mapping checks ({clock.ElapsedMilliseconds} ms), isolated files: {scratch}");
        return 0;
    }

    private static string LegacySnapshot(string path)
    {
        using var db = new SqliteDatabase(path, readOnly: true);
        var names = db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('case_assessments','manual_runs') ORDER BY name");
        return JsonSerializer.Serialize(new
        {
            schema = db.Query("SELECT type,name,tbl_name,sql FROM sqlite_master WHERE tbl_name NOT IN ('case_assessments','manual_runs') ORDER BY type,name"),
            rows = names.Select(n => db.Query($"SELECT * FROM \"{n[0]}\" ORDER BY rowid"))
        });
    }
    private static List<Dictionary<string, string>> ReadRows(string path, int sheet)
    {
        using var zip = ZipFile.OpenRead(path);
        using var xml = zip.GetEntry($"xl/worksheets/sheet{sheet}.xml")!.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return XDocument.Load(xml).Descendants(ns + "row").Skip(1)
            .Select(row => row.Elements(ns + "c").ToDictionary(cell =>
                new string(cell.Attribute("r")!.Value.TakeWhile(char.IsLetter).ToArray()), cell => cell.Value)).ToList();
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return; }
        throw new InvalidOperationException("Expected the operation to fail.");
    }
}
