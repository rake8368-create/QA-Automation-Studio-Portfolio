using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QAStudio.Tests;

// WHAT: Exercise the two dedicated trigger phases, evidence/store agreement and v4->v5 preservation.
// WHY: Two sends without an intervening exact f cannot establish per-trigger correctness.
// CONTRACT: Fake input only, disposable copies; real GUI run is a separate single attempt.
// FAILURE: Wrong order/count/verdict, lost existing rows or save errors fail the harness.
// TEST: --day11 <new-output> <before-day11.sqlite>; existing text and mapping regressions included.
internal static class Day11Checks
{
    public static int Run(string directory, string baseline)
    {
        if (Directory.Exists(directory)) throw new IOException("New output required");
        Directory.CreateDirectory(directory); var tests = new List<object>(); int failed = 0; var total = Stopwatch.StartNew();
        void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
        void Test(string name, Action action)
        {
            try { action(); tests.Add(new { name, status = "PASS" }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; tests.Add(new { name, status = "FAIL", error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        TestCase Case() => new("macro-studio", "MAC-QA-005", new Dictionary<string, string> { ["TC ID"] = "MAC-QA-005", ["우선순위"] = "P1", ["QA 기법"] = "반복 트리거", ["테스트 목적"] = "isolated", ["수행 절차"] = "fake", ["기대 결과"] = "ff" });
        SingleTextRequest Request() => new("macro-studio", "MAC-NP-005", "ff", "F3", directory, "FAKE ONLY", 120, 2000, 10, "MAC-QA-005", TextExpectation.TwoSingleFTriggers);
        TextExecution Execute(Probe probe, IExecutionStore? store = null) => new SingleTextOrchestrator().ExecuteAsync(probe, Request(), store: store ?? new Store(), registeredCase: Case(), trigger: probe).GetAwaiter().GetResult();
        foreach (var (fault, expected, count) in new (string, ExecutionResult, int)[] {
            ("normal",ExecutionResult.Pass,2), ("first-timeout",ExecutionResult.Fail,1), ("first-wrong",ExecutionResult.Fail,1),
            ("first-duplicate",ExecutionResult.Fail,1), ("first-case",ExecutionResult.Fail,1), ("first-newline",ExecutionResult.Fail,1),
            ("pre-focus",ExecutionResult.Error,0), ("baseline",ExecutionResult.Error,0), ("trigger1",ExecutionResult.Error,1),
            ("first-focus",ExecutionResult.Error,1), ("first-document",ExecutionResult.Error,1), ("first-reader",ExecutionResult.Error,1),
            ("guard-change",ExecutionResult.Fail,1), ("guard-focus",ExecutionResult.Error,1), ("trigger2",ExecutionResult.Error,2),
            ("second-timeout",ExecutionResult.Fail,2), ("second-extra",ExecutionResult.Fail,2), ("second-wrong",ExecutionResult.Fail,2),
            ("second-delete",ExecutionResult.Fail,2), ("late-extra-recovery",ExecutionResult.Fail,2),
            ("second-focus",ExecutionResult.Error,2), ("after-ff-focus",ExecutionResult.Error,2),
            ("after-ff-document",ExecutionResult.Error,2), ("after-ff-reader",ExecutionResult.Error,2) })
        {
            Test("Two F3: " + fault, () =>
            {
                var probe = new Probe(fault); var result = Execute(probe);
                Check(result.Run.Result == expected && probe.Calls == count && probe.Disposed, "Verdict/count/disposal");
                using var json = JsonDocument.Parse(File.ReadAllBytes(result.Evidence.Path)); var r = json.RootElement;
                Check(r.GetProperty("result").GetString() == ResultText.Format(expected) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.Evidence.Path))) == result.Evidence.Sha256, "JSON/hash agreement");
                Check(r.GetProperty("triggerAttempts").GetInt32() == count, "Attempt count");
                if (count == 2) Check(probe.FirstFReturnedBeforeSecond, "Second trigger only after actual first f");
                if (fault.StartsWith("after-ff", StringComparison.Ordinal)) Check(r.GetProperty("observations").EnumerateArray().Any(s => s.GetProperty("Text").GetString() == "ff"), "Exact ff recorded before observation fault");
                if (fault == "normal")
                {
                    var stages = r.GetProperty("stages").EnumerateArray().ToArray();
                    Check(stages.Select(s => s.GetProperty("eventName").GetString()).SequenceEqual(new[] { "trigger-attempt", "trigger-completed", "exact-f-observed", "trigger-attempt", "trigger-completed", "exact-ff-observed" }), "Stage order");
                    Check(r.GetProperty("stabilizedMilliseconds").GetInt64() >= 2000 && r.GetProperty("completedTriggers").GetInt32() == 2, ">=2s stable and two successful sends");
                }
            });
        }
        Test("Two F3 store failure propagates and leaves ERROR JSON", () =>
        {
            var before = Directory.GetFiles(directory, "TEXT-*.json").ToHashSet(); bool thrown = false;
            try { Execute(new("normal"), new Store { Fail = true }); } catch (IOException) { thrown = true; }
            using var json = JsonDocument.Parse(File.ReadAllBytes(Directory.GetFiles(directory, "TEXT-*.json").Single(p => !before.Contains(p))));
            Check(thrown && json.RootElement.GetProperty("result").GetString() == "ERROR", "No publishable PASS on save failure");
        });
        Test("Scenario/timing/store contracts reject invalid two-trigger requests", () =>
        {
            foreach (var request in new[] { Request() with { ScenarioId = "MAC-NP-002" }, Request() with { Expected = "f" }, Request() with { StabilizationMilliseconds = 100 }, Request() with { LinkedTestCaseId = null } })
            {
                bool rejected = false;
                try { new SingleTextOrchestrator().ExecuteAsync(new Probe("normal"), request, store: new Store(), registeredCase: Case(), trigger: new Probe("normal")).GetAwaiter().GetResult(); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid request rejected");
            }
        });
        string Copy(string name) { string p = Path.Combine(directory, name + ".sqlite"); using var raw = new SqliteDatabase(baseline); raw.BackupTo(p); return p; }
        string Rows(SqliteDatabase db) => JsonSerializer.Serialize(db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('manual_runs','case_assessments') ORDER BY name").Select(r => new { name = r[0], rows = db.Query("SELECT * FROM \"" + r[0] + "\" ORDER BY rowid") }));
        Test("v4->v5 every row/trigger preserved and reopen idempotent", () =>
        {
            string p = Copy("migration"), rows, triggers;
            using (var raw = new SqliteDatabase(p)) { Check(raw.Query("PRAGMA user_version")[0][0] == "4", "Actual v4 baseline"); rows = Rows(raw); triggers = JsonSerializer.Serialize(raw.Query("SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name!='manual_runs' ORDER BY name")); }
            for (int i = 0; i < 2; i++) { using var store = new FoundationStore(p); Check(store.AutomationMappings("macro-studio").Count == 4, "Original mappings"); }
            using var inspect = new SqliteDatabase(p);
            Check(Rows(inspect) == rows && JsonSerializer.Serialize(inspect.Query("SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name!='manual_runs' ORDER BY name")) == triggers, "Rows/triggers unchanged");
            Check(inspect.Query("PRAGMA user_version")[0][0] == FoundationStore.SchemaVersion.ToString() && inspect.Query("PRAGMA integrity_check")[0][0] == "ok" && inspect.Query("PRAGMA foreign_key_check").Count == 0, "Current schema integrity");
        });
        Test("v5 retains mapping-attached objects", () =>
        {
            string p = Copy("attached");
            using (var raw = new SqliteDatabase(p)) { raw.Execute("CREATE TRIGGER day11_guard BEFORE DELETE ON tc_automation_mappings BEGIN SELECT RAISE(ABORT,'guard'); END"); raw.Execute("CREATE INDEX day11_index ON tc_automation_mappings(scenario_id)"); }
            using (var store = new FoundationStore(p)) { }
            using var inspect = new SqliteDatabase(p); bool blocked = false;
            try { inspect.Execute("DELETE FROM tc_automation_mappings"); } catch (InvalidDataException) { blocked = true; }
            Check(blocked && inspect.Query("SELECT name FROM sqlite_master WHERE name IN ('day11_guard','day11_index')").Count == 2, "Attached objects preserved");
        });
        Test("v5 migration rollback on DDL failure", () =>
        {
            string p = Copy("rollback"); string before;
            using (var raw = new SqliteDatabase(p)) { raw.Execute("CREATE TABLE tc_automation_mappings_v5(blocker TEXT)"); before = Rows(raw); }
            bool thrown = false; try { using var store = new FoundationStore(p); } catch (InvalidDataException) { thrown = true; }
            using var inspect = new SqliteDatabase(p); Check(thrown && Rows(inspect) == before && inspect.Query("PRAGMA user_version")[0][0] == "4", "Atomic rollback");
        });
        Test("New TC mapping + successful two-stage run use existing SQLite transaction", () =>
        {
            string p = Copy("persistence"); using var store = new FoundationStore(p); byte[] source = [11];
            store.CommitNew("day11-fixture", new(source, Convert.ToHexString(SHA256.HashData(source)), [Case()], [], []));
            store.SetAutomationMapping("macro-studio", "MAC-QA-005", "MAC-NP-005");
            var result = Execute(new("normal"), store);
            Check(store.Cases("macro-studio").Count == 71 && store.AutomationMappings("macro-studio").Count == 5 && store.Runs("macro-studio").Single(r => r.Id == result.Run.Id) == result.Run && store.EvidenceFor("macro-studio", result.Run.Id).Single() == result.Evidence, "Append and saved evidence");
        });
        SingleTextChecks.Run(Path.Combine(directory, "existing-text"), Test, Check);
        MappingChecks.Run(Path.Combine(directory, "existing-mapping"), Test, Check);
        total.Stop(); File.WriteAllText(Path.Combine(directory, "tests.json"), JsonSerializer.Serialize(new { total = tests.Count, passed = tests.Count - failed, failed, seconds = total.Elapsed.TotalSeconds, realF3 = 0, tests }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SUMMARY {tests.Count - failed}/{tests.Count}; failed={failed}; {total.Elapsed.TotalSeconds:F3}s; real F3=0"); return failed == 0 ? 0 : 1;
    }
    private sealed class Store : IExecutionStore { public bool Fail { get; init; } public void RecordRun(TestRun run, Evidence evidence) { if (Fail) throw new IOException("Fake SQLite failure"); } }
    private sealed class Probe(string fault) : ISingleTextProbe, ITextTrigger
    {
        public int Calls { get; private set; } public bool Disposed { get; private set; } public bool FirstFReturnedBeforeSecond { get; private set; }
        private bool returnedF; private int firstReads, secondReads; private readonly Stopwatch second = new();
        public TextSample Arm(CancellationToken token) => new(DateTimeOffset.UtcNow, fault == "baseline" ? "existing" : "", "fake-reader", "fake-doc");
        public TextSample Read()
        {
            if (fault == "pre-focus" || Calls == 1 && fault == "first-focus" || Calls == 1 && firstReads > 0 && fault == "guard-focus" || Calls == 2 && fault == "second-focus" || Calls == 2 && secondReads > 2 && fault == "after-ff-focus") throw new InvalidOperationException("Fake focus lost");
            string text = "", reader = "fake-reader", doc = "fake-doc";
            if (Calls == 1)
            {
                text = fault switch { "first-timeout" => "", "first-wrong" => "g", "first-duplicate" => "ff", "first-case" => "F", "first-newline" => "f\r\n", "guard-change" when firstReads > 0 => "ff", _ => "f" };
                if (fault == "first-document") doc = "changed"; if (fault == "first-reader") reader = "changed";
                firstReads++; if (text == "f") returnedF = true;
            }
            if (Calls == 2)
            {
                secondReads++; text = fault switch { "second-timeout" => "f", "second-extra" => "fff", "second-wrong" => "g", "second-delete" => "", "late-extra-recovery" when second.ElapsedMilliseconds is >= 1700 and < 1850 => "fff", _ => secondReads == 1 ? "f" : "ff" };
                if (secondReads > 2 && fault == "after-ff-document") doc = "changed"; if (secondReads > 2 && fault == "after-ff-reader") reader = "changed";
            }
            return new(DateTimeOffset.UtcNow, text, reader, doc);
        }
        public void Fire(CancellationToken token, Action<string> log)
        {
            Calls++; if (Calls == 2) { FirstFReturnedBeforeSecond = returnedF; second.Start(); }
            if (Calls == 1 && fault == "trigger1" || Calls == 2 && fault == "trigger2") throw new InvalidOperationException("Fake SendInput failure");
        }
        public object Diagnostics => new { method = "FAKE INPUT ONLY", Calls }; public void Dispose() => Disposed = true;
    }
}
