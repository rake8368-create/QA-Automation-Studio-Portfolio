using System.Diagnostics;
using System.Text.Json;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QA.AutomationStudio.Desktop;

// WHAT: Deterministic import acceptance against disposable copies; never the default release DB.
// CONTRACT: Explicit --verify-import paths only. GUI interaction is verified separately on the published EXE.
internal static class ImportVerification
{
    internal static int Run(string root, string output)
    {
        Directory.CreateDirectory(output); List<object> results = []; int failures = 0;
        string Fixture(string name) => Path.Combine(root, "fixtures", name);
        string Clone(string name) { string path = Path.Combine(output, name + ".sqlite"); File.Copy(Path.Combine(root, "before.sqlite"), path, false); return path; }
        var project = new Project("macro-studio", "macro-studio");
        var map = new Dictionary<string, string>();
        void Check(bool ok, string reason = "Assertion failed") { if (!ok) throw new InvalidDataException(reason); }
        void Throws(Action body) { bool threw = false; try { body(); } catch (Exception e) when (ImportErrors.Expected(e)) { threw = true; } Check(threw, "Expected import rejection"); }
        string Snapshot(string db) { using var s = new FoundationStore(db); return JsonSerializer.Serialize(s.Projects().SelectMany(p => s.Cases(p.Id))); }
        int Count(string db) { using var s = new FoundationStore(db); return s.Projects().Sum(p => s.Cases(p.Id).Count); }
        void Test(string id, Action body)
        {
            var time = Stopwatch.StartNew();
            try { body(); results.Add(new { id, status = "PASS", seconds = time.Elapsed.TotalSeconds }); }
            catch (Exception e) { failures++; results.Add(new { id, status = "FAIL", seconds = time.Elapsed.TotalSeconds, error = e.ToString() }); }
        }
        Test("TC-IMP-003 Missing required column blocks all writes", () =>
        {
            string db = Clone("missing"); string before = Snapshot(db);
            var session = new DesktopImportSession(db, project, Fixture("missing-column.xlsx"));
            Check(!session.Validate(map, false).CanSave); Throws(() => session.Save(map, false)); Check(before == Snapshot(db));
        });
        Test("TC-IMP-004 File and DB collisions block by default; explicit skip preserves originals", () =>
        {
            string db = Clone("duplicates"); string before = Snapshot(db);
            var file = new DesktopImportSession(db, project, Fixture("duplicate-file.xlsx"));
            Check(file.Validate(map, false).DuplicateRows == 2 && !file.Validate(map, true).CanSave);
            var existing = new DesktopImportSession(db, project, Fixture("duplicate-db.xlsx"));
            Check(!existing.Validate(map, false).CanSave); Throws(() => existing.Save(map, false)); Check(before == Snapshot(db));
            var receipt = existing.Save(map, true); Check(receipt.Added == 1 && receipt.Skipped == 1 && Count(db) == 71);
            using var old = new FoundationStore(Path.Combine(root, "before.sqlite")); using var now = new FoundationStore(db);
            Check(old.Cases(project.Id).All(c => JsonSerializer.Serialize(c) == JsonSerializer.Serialize(now.Cases(project.Id).Single(x => x.Id == c.Id))));
        });
        Test("TC-IMP-005 Validated save and source backup", () =>
        {
            string db = Clone("valid"); var session = new DesktopImportSession(db, project, Fixture("valid.xlsx"));
            var validation = session.Validate(map, false); Check(validation.CanSave && validation.Rows.Single().ExcelRow == 6 && Count(db) == 70);
            var receipt = session.Save(map, false); Check(receipt.Added == 1 && Count(db) == 71 && File.Exists(receipt.BackupPath));
            Check(Count(receipt.BackupPath) == 70);
            using var s = new FoundationStore(db); Check(s.Source(receipt.BatchId).SequenceEqual(File.ReadAllBytes(Fixture("valid.xlsx"))));
            Check(s.LatestResult(project.Id, receipt.CaseIds.Single()) == ExecutionResult.NotRun);
            Throws(() => session.Save(map, false)); Check(Count(db) == 71);
        });
        Test("TC-IMP-007 Reopen persists imported TC", () =>
        {
            using var store = new FoundationStore(Path.Combine(output, "valid.sqlite"));
            Check(store.Cases(project.Id).Any(c => c.Id == "IMP-GUI-20261028-001")); Check(store.Runs(project.Id).Count == 0);
        });
        Test("TC-IMP-008 Invalid XLSX never reaches commit", () =>
        {
            string db = Clone("invalid"); string before = Snapshot(db);
            Throws(() => new DesktopImportSession(db, project, Fixture("invalid.xlsx"))); Check(before == Snapshot(db));
        });
        Test("TC-IMP-009 Mid-transaction injected failure rolls back prior row and provenance", () =>
        {
            string db = Path.Combine(root, "rollback.sqlite"); string before = Snapshot(db);
            var session = new DesktopImportSession(db, project, Fixture("rollback.xlsx")); Check(session.Validate(map, false).CanSave);
            Throws(() => session.Save(map, false)); Check(Count(db) == 70 && before == Snapshot(db));
        });
        Test("TC-IMP-010 Every original TC payload remains unchanged", () =>
        {
            using var original = new FoundationStore(Path.Combine(root, "before.sqlite")); using var after = new FoundationStore(Path.Combine(output, "valid.sqlite"));
            foreach (var p in original.Projects())
            foreach (var tc in original.Cases(p.Id))
                Check(JsonSerializer.Serialize(tc) == JsonSerializer.Serialize(after.Cases(p.Id).Single(c => c.Id == tc.Id)));
            Check(original.Projects().Sum(p => original.Cases(p.Id).Count) == 70);
        });
        Test("Explicit aliases use existing ImportService validation", () =>
        {
            string db = Clone("aliases"); var s = new DesktopImportSession(db, project, Fixture("alias.xlsx"));
            Check(!s.Validate(map, false).CanSave);
            var aliases = new Dictionary<string, string> { ["Identifier"] = "TC ID", ["Purpose"] = "테스트 목적" };
            Check(s.Validate(aliases, false).CanSave); Check(s.Save(aliases, false).Added == 1);
        });
        Test("Required data and schema priorities reject without coercion", () =>
        {
            string db = Clone("schema");
            foreach (var name in new[] { "blank-required.xlsx", "bad-priority.xlsx" })
            { var s = new DesktopImportSession(db, project, Fixture(name)); Check(!s.Validate(map, false).CanSave); Throws(() => s.Save(map, false)); }
            Check(Count(db) == 70);
        });
        Test("Stale preview collision is rechecked before commit", () =>
        {
            string db = Clone("stale"); var first = new DesktopImportSession(db, project, Fixture("valid.xlsx")); var second = new DesktopImportSession(db, project, Fixture("valid.xlsx"));
            Check(first.Validate(map, false).CanSave && second.Validate(map, false).CanSave); first.Save(map, false);
            Throws(() => second.Save(map, false)); Check(Count(db) == 71);
        });
        File.WriteAllText(Path.Combine(output, "acceptance.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
