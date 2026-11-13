using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QAStudio.Tests;

// WHAT: Focused OFF-negative and v2->v3 preservation checks; reuse f/ff assertion regression checks.
// WHY: Silence is valid only after verified OFF and a successful trigger, with reliable >=2s reads.
// CONTRACT: Fake input/observation adapters and disposable DBs only; no production writes or UI input.
// FAILURE: Incorrect verdict, persistence publication or migration preservation fails this command.
// TEST: --day7 <output-directory> <read-only-baseline-backup>.
internal static class Day7Checks
{
    public static int Run(string directory, string baseline)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>(); int failed = 0; var total = Stopwatch.StartNew();
        void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new { name, status = "PASS", milliseconds = watch.Elapsed.TotalMilliseconds }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        SingleTextRequest Request() => new("macro-studio", "MAC-NP-003", "", "F3", directory, "FAKE OFF PROBE / FAKE TRIGGER ONLY",
            LinkedTestCaseId: "MAC-QA-003", Expectation: TextExpectation.NoOutputWhileMacroOff);
        TestCase Case() => new("macro-studio", "MAC-QA-003", new Dictionary<string, string>
        { ["TC ID"] = "MAC-QA-003", ["우선순위"] = "P1", ["QA 기법"] = "Negative", ["테스트 목적"] = "isolated OFF fixture", ["수행 절차"] = "fake trigger", ["기대 결과"] = "no output" });
        TextExecution Execute(Probe probe, RecordingStore? store = null) => new SingleTextOrchestrator().ExecuteAsync(probe, Request(),
            store: store ?? new RecordingStore(), registeredCase: Case(), trigger: probe).GetAwaiter().GetResult();
        Test("OFF absence requires successful trigger, >=2000ms sample span and real SQLite append", () =>
        {
            using var db = new FoundationStore(Path.Combine(directory, "negative.sqlite"));
            db.RegisterProject(new("macro-studio", "isolated")); byte[] source = [7];
            db.CommitNew("isolated", new(source, Convert.ToHexString(SHA256.HashData(source)), [Case()], [], []));
            db.SetAutomationMapping("macro-studio", "MAC-QA-003", "MAC-NP-003");
            var probe = new Probe();
            var result = new SingleTextOrchestrator().ExecuteAsync(probe, Request(), store: db, registeredCase: Case(), trigger: probe).GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(File.ReadAllBytes(result.Evidence.Path)); var root = json.RootElement;
            var samples = root.GetProperty("observations").EnumerateArray().Skip(2).ToArray();
            Check(result.Run.Result == ExecutionResult.Pass && probe.Calls == 1 && probe.OffChecks > 2, "Verified OFF and exactly one fake trigger");
            Check(root.GetProperty("expectation").GetString() == "NoOutputWhileMacroOff" && root.GetProperty("triggerCompleted").GetBoolean(), "Explicit negative evidence");
            Check(samples.Length > 2 && samples.All(s => s.GetProperty("Text").GetString() == "") &&
                (samples[^1].GetProperty("At").GetDateTimeOffset() - samples[0].GetProperty("At").GetDateTimeOffset()).TotalMilliseconds >= 2000, ">=2s actual sample span");
            Check(db.Runs("macro-studio").Count == 1 && db.EvidenceFor("macro-studio", result.Run.Id).Count == 1, "Atomic SQLite append");
        });
        Test("OFF any character immediately FAILs even if it would disappear", () => Check(Execute(new() { Output = "f" }).Run.Result == ExecutionResult.Fail, "Any character FAIL"));
        Test("OFF late output within observation FAILs", () => Check(Execute(new() { Output = "x", OutputAt = 1800 }).Run.Result == ExecutionResult.Fail, "Late output FAIL"));
        foreach (string fault in new[] { "off", "profile", "baseline", "trigger", "read", "document", "reader" })
            Test("OFF ERROR: " + fault, () =>
            {
                var probe = new Probe { Fault = fault }; var result = Execute(probe);
                Check(result.Run.Result == ExecutionResult.Error, "No silence PASS for " + fault);
                if (fault is "off" or "profile" or "baseline") Check(probe.Calls == 0, "Precondition failure emits no trigger");
            });
        Test("Empty exact-text remains invalid and cannot bypass negative mode", () =>
        {
            bool rejected = false; try { new SingleTextOrchestrator().ExecuteAsync(new Probe(), Request() with { Expectation = TextExpectation.ExactText }).GetAwaiter().GetResult(); }
            catch (ArgumentException) { rejected = true; } Check(rejected, "Exact empty rejected");
        });
        Test("Negative observation shorter than 2s is rejected", () =>
        {
            bool rejected = false; try { new SingleTextOrchestrator().ExecuteAsync(new Probe(), Request() with { StabilizationMilliseconds = 100 }).GetAwaiter().GetResult(); }
            catch (ArgumentException) { rejected = true; } Check(rejected, ">=2s enforced");
        });
        Test("Missing OFF verifier is ERROR without trigger", () =>
        {
            var result = new SingleTextOrchestrator().ExecuteAsync(new PlainProbe(), Request(), store: new RecordingStore(), registeredCase: Case(), trigger: new Probe()).GetAwaiter().GetResult();
            Check(result.Run.Result == ExecutionResult.Error, "Verifier required");
        });
        Test("OFF evidence failure cannot reach RecordRun", () =>
        {
            string blocker = Path.Combine(directory, "blocked-output"); File.WriteAllText(blocker, "file"); var store = new RecordingStore(); var probe = new Probe { Output = "f" };
            bool rejected = false; try { new SingleTextOrchestrator().ExecuteAsync(probe, Request() with { EvidenceDirectory = blocker }, store: store, registeredCase: Case(), trigger: probe).GetAwaiter().GetResult(); }
            catch (IOException) { rejected = true; } Check(rejected && store.Count == 0, "Evidence required");
        });
        Test("OFF SQLite save failure leaves ERROR evidence and never publishes PASS", () =>
        {
            var store = new RecordingStore { Fail = true }; var existing = Directory.GetFiles(directory, "TEXT-*.json").ToHashSet(); bool rejected = false;
            try { Execute(new Probe(), store); } catch (IOException) { rejected = true; }
            string path = Directory.GetFiles(directory, "TEXT-*.json").Single(p => !existing.Contains(p));
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            Check(rejected && store.Count == 0 && json.RootElement.GetProperty("result").GetString() == "ERROR", "No orphan PASS after DB error");
        });
        string Snapshot(SqliteDatabase raw) => JsonSerializer.Serialize(raw.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('manual_runs','case_assessments') ORDER BY name")
            .Select(r => new { table = r[0], rows = raw.Query("SELECT * FROM \"" + r[0] + "\" ORDER BY rowid") }));
        string Copy(string name)
        {
            string path = Path.Combine(directory, name + ".sqlite");
            using var original = new SqliteDatabase(baseline); original.BackupTo(path); return path;
        }
        Test("v2 production snapshot migration preserves every row and original trigger SQL", () =>
        {
            string path = Copy("migration"); string before, triggers;
            using (var raw = new SqliteDatabase(path)) { before = Snapshot(raw); triggers = JsonSerializer.Serialize(raw.Query("SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name!='manual_runs' ORDER BY name")); }
            using (var migrated = new FoundationStore(path)) Check(migrated.AutomationMappings("macro-studio").Count == 2, "Both mappings retained");
            using var inspect = new SqliteDatabase(path);
            Check(before == Snapshot(inspect) && triggers == JsonSerializer.Serialize(inspect.Query("SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name!='manual_runs' ORDER BY name")), "Full row/trigger preservation");
            Check(inspect.Query("PRAGMA user_version")[0][0] == FoundationStore.SchemaVersion.ToString() && inspect.Query("PRAGMA integrity_check")[0][0] == "ok" && inspect.Query("PRAGMA foreign_key_check").Count == 0, "Current schema integrity");
            inspect.Execute("INSERT INTO tc_automation_mappings VALUES('macro-studio','MAC-001','MAC-NP-003')");
        });
        Test("v2 mapping-attached trigger and index survive schema expansion", () =>
        {
            string path = Copy("attached");
            using (var raw = new SqliteDatabase(path))
            {
                raw.Execute("CREATE TRIGGER mapping_fixture_guard BEFORE DELETE ON tc_automation_mappings BEGIN SELECT RAISE(ABORT,'fixture immutable'); END");
                raw.Execute("CREATE INDEX mapping_fixture_index ON tc_automation_mappings(scenario_id)");
            }
            using (var migrated = new FoundationStore(path)) { }
            using var inspect = new SqliteDatabase(path);
            Check(inspect.Query("SELECT name FROM sqlite_master WHERE name IN ('mapping_fixture_guard','mapping_fixture_index')").Count == 2, "Attached objects retained");
            bool rejected = false; try { inspect.Execute("DELETE FROM tc_automation_mappings"); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && inspect.Query("SELECT * FROM tc_automation_mappings").Count == 2, "Retained trigger still enforces");
        });
        Test("Migration failure rolls back table rebuild and version", () =>
        {
            string path = Copy("rollback");
            using (var raw = new SqliteDatabase(path)) { raw.Execute("CREATE TABLE tc_automation_mappings_v5(blocker TEXT)"); }
            bool rejected = false; try { using var migrated = new FoundationStore(path); } catch (InvalidDataException) { rejected = true; }
            using var inspect = new SqliteDatabase(path);
            Check(rejected && inspect.Query("PRAGMA user_version")[0][0] == "2" && inspect.Query("SELECT * FROM tc_automation_mappings").Count == 2, "No partial migration");
        });
        SingleTextChecks.Run(Path.Combine(directory, "existing-text"), Test, Check);
        MappingChecks.Run(Path.Combine(directory, "existing-mapping"), Test, Check);
        total.Stop();
        File.WriteAllText(Path.Combine(directory, "tests.json"), JsonSerializer.Serialize(new { total = results.Count, passed = results.Count - failed, failed, seconds = total.Elapsed.TotalSeconds, realProductExecutions = 0, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SUMMARY {results.Count-failed}/{results.Count}; failures={failed}; {total.Elapsed.TotalSeconds:F3}s; real product executions=0");
        return failed == 0 ? 0 : 1;
    }
    private sealed class Probe : IMacroOffTextProbe, ITextTrigger
    {
        public string Fault { get; init; } = "";
        public string Output { get; init; } = "";
        public int OutputAt { get; init; }
        public int Calls { get; private set; }
        public int OffChecks { get; private set; }
        private readonly Stopwatch watch = new();
        public void VerifyMacroOff() { OffChecks++; if (Fault == "off") throw new InvalidOperationException("OFF not verified"); }
        public TextSample Arm(CancellationToken token)
        {
            if (Fault == "profile") throw new InvalidOperationException("Wrong profile");
            return new(DateTimeOffset.UtcNow, Fault == "baseline" ? "user text" : "", "fake UIA", "isolated document");
        }
        public TextSample Read()
        {
            if (Calls > 0 && Fault == "read") throw new InvalidOperationException("Focus/observation unavailable");
            return new(DateTimeOffset.UtcNow, Calls > 0 && watch.ElapsedMilliseconds >= OutputAt ? Output : "",
                Calls > 0 && Fault == "reader" ? "changed" : "fake UIA", Calls > 0 && Fault == "document" ? "changed" : "isolated document");
        }
        public void Fire(CancellationToken token, Action<string> log) { Calls++; if (Fault == "trigger") throw new InvalidOperationException("SendInput failed"); watch.Start(); }
        public object Diagnostics => new { method = "FAKE INPUT ONLY", calls = Calls, inserted = Fault == "trigger" ? 0 : 2 };
        public void Dispose() { }
    }
    private sealed class PlainProbe : ISingleTextProbe
    {
        public TextSample Arm(CancellationToken token) => new(DateTimeOffset.UtcNow, "", "fake", "doc");
        public TextSample Read() => new(DateTimeOffset.UtcNow, "", "fake", "doc");
        public void Dispose() { }
    }
    private sealed class RecordingStore : IExecutionStore
    {
        public int Count { get; private set; }
        public bool Fail { get; init; }
        public void RecordRun(TestRun run, Evidence evidence) { if (Fail) throw new IOException("fake DB failure"); Count++; }
    }
}
