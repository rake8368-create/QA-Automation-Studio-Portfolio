using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QAStudio.Tests;

// WHAT: Exercise observation safety at explicit pre/post-trigger boundaries with fake adapters.
// WHY: A transient exact f must not hide later focus, document or reader faults.
// CONTRACT: Append only to independent Day8 database copies; no real input or TC creation.
// FAILURE: Verdict/JSON/SQLite disagreement, changed old rows or unexpected F3 count fails.
// TEST: --day9 <new-output-directory> <before-day9.sqlite>; existing text checks also run.
internal static class Day9Checks
{
    public static int Run(string directory, string baseline)
    {
        if (Directory.Exists(directory)) throw new IOException("Day9 output must be new; old evidence is immutable");
        Directory.CreateDirectory(directory);
        var results = new List<object>(); var scenarios = new List<object>(); int failures = 0;
        var total = Stopwatch.StartNew();
        void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new { name, status = "PASS", milliseconds = timer.Elapsed.TotalMilliseconds }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        string Serialize(object value) => JsonSerializer.Serialize(value);
        Dictionary<string, List<string?[]>> Rows(SqliteDatabase raw) => raw.Query("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
            .ToDictionary(r => r[0]!, r => raw.Query("SELECT * FROM \"" + r[0] + "\" ORDER BY rowid"));
        foreach (string fault in new[] { "pre-focus-arm", "pre-focus-read", "pre-focus-sendguard", "pre-document", "pre-reader",
            "post-focus-immediate", "post-focus-after-f", "post-document-immediate", "post-document-after-f",
            "post-reader-immediate", "post-reader-after-f", "stable-f" })
        {
            Test("Day9 boundary + SQLite preservation: " + fault, () =>
            {
                string caseDirectory = Path.Combine(directory, fault); Directory.CreateDirectory(caseDirectory);
                string db = Path.Combine(caseDirectory, "isolated.sqlite");
                using (var source = new SqliteDatabase(baseline)) source.BackupTo(db);
                using (var migrateIsolated = new FoundationStore(db)) { }
                Dictionary<string, List<string?[]>> before; string schema;
                using (var raw = new SqliteDatabase(db))
                {
                    before = Rows(raw);
                    schema = Serialize(raw.Query("SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name"));
                    Check(before["cases"].Count == 75 && before["runs"].Count == 16 && before["evidence"].Count == 16 &&
                        before["tc_automation_mappings"].Count == 4 && raw.Query("PRAGMA user_version")[0][0] == FoundationStore.SchemaVersion.ToString(), "Actual Day8 baseline");
                }
                var probe = new Probe(fault); var trigger = new Trigger(probe);
                TextExecution execution;
                using (var store = new FoundationStore(db))
                {
                    var tc = store.Cases("macro-studio").Single(c => c.Id == "MAC-QA-001");
                    Check(store.AutomationMappings("macro-studio")[tc.Id] == "MAC-NP-001", "Existing f mapping");
                    var request = new SingleTextRequest("macro-studio", "MAC-NP-001", "f", "F3", caseDirectory,
                        "DAY9 FAKE PROBE / FAKE TRIGGER; NOT A PRODUCT E2E", 200, 150, 10, LinkedTestCaseId: tc.Id);
                    execution = new SingleTextOrchestrator().ExecuteAsync(probe, request, store: store, registeredCase: tc, trigger: trigger).GetAwaiter().GetResult();
                    var expected = fault == "stable-f" ? ExecutionResult.Pass : ExecutionResult.Error;
                    Check(execution.Run.Result == expected && probe.Disposed, "ERROR required for every observation fault, PASS only for stable f");
                    Check(store.Runs("macro-studio").Single(r => r.Id == execution.Run.Id) == execution.Run, "SQLite payload equals returned run");
                    Check(store.EvidenceFor("macro-studio", execution.Run.Id).Single() == execution.Evidence, "SQLite evidence equals returned evidence");
                }
                int expectedF3 = fault.StartsWith("pre-", StringComparison.Ordinal) ? 0 : 1;
                Check(trigger.F3Count == expectedF3, "Fake F3 count at boundary");
                byte[] bytes = File.ReadAllBytes(execution.Evidence.Path);
                Check(Convert.ToHexString(SHA256.HashData(bytes)) == execution.Evidence.Sha256, "JSON hash matches saved evidence");
                using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
                string verdict = ResultText.Format(execution.Run.Result);
                Check(root.GetProperty("result").GetString() == verdict &&
                    root.GetProperty("run").GetProperty("Result").GetInt32() == (int)execution.Run.Result &&
                    root.GetProperty("triggerDiagnostics").GetProperty("fakeF3Count").GetInt32() == expectedF3, "JSON verdict / nested run / fake F3 agree");
                Check(root.GetProperty("triggerCompleted").GetBoolean() == (expectedF3 == 1), "Trigger completion cannot be inferred before dispatch");
                Check(root.GetProperty("triggerAttempted").GetBoolean() == (!fault.StartsWith("pre-", StringComparison.Ordinal) || fault == "pre-focus-sendguard"), "Attempt and dispatch distinguished");
                var post = root.GetProperty("observations").EnumerateArray().Skip(2).ToArray();
                if (fault.EndsWith("after-f", StringComparison.Ordinal))
                    Check(post.Length == 1 && post[0].GetProperty("Text").GetString() == "f" && root.GetProperty("actual").GetString() == "f", "Real transient f is retained while final verdict is ERROR");
                if (fault == "stable-f")
                    Check(post.Length >= 2 && post.All(s => s.GetProperty("Text").GetString() == "f") &&
                        (post[^1].GetProperty("At").GetDateTimeOffset() - post[0].GetProperty("At").GetDateTimeOffset()).TotalMilliseconds >= 150,
                        "Repeated identical f through stabilization");
                string reason = root.GetProperty("reason").GetString()!;
                if (fault.Contains("focus", StringComparison.Ordinal)) Check(reason.Contains("DAY9_FOCUS_LOST", StringComparison.Ordinal), "Focus error evidence");
                if (fault is "pre-document" or "pre-reader") Check(reason.Contains("트리거 직전", StringComparison.Ordinal), "Pre-trigger identity error evidence");
                if (fault.StartsWith("post-document", StringComparison.Ordinal) || fault.StartsWith("post-reader", StringComparison.Ordinal))
                    Check(reason.Contains("관찰 중 문서 또는 읽기 방식", StringComparison.Ordinal), "Post-trigger identity error evidence");
                using (var raw = new SqliteDatabase(db))
                {
                    var after = Rows(raw);
                    foreach (var (table, rows) in before)
                    {
                        if (table == "sqlite_sequence") continue;
                        int added = table is "runs" or "evidence" ? 1 : 0;
                        Check(after[table].Count == rows.Count + added && Serialize(after[table].Take(rows.Count)) == Serialize(rows), "Every original column/row unchanged: " + table);
                    }
                    Check(raw.Query("SELECT seq FROM sqlite_sequence WHERE name='runs'")[0][0] == "17", "Only sequence advances for appended run");
                    Check(raw.Query("SELECT result FROM runs WHERE id=?", execution.Run.Id)[0][0] == verdict, "SQLite result column agrees");
                    Check(schema == Serialize(raw.Query("SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name")) && raw.Query("PRAGMA user_version")[0][0] == FoundationStore.SchemaVersion.ToString(), "Schema/triggers unchanged");
                    Check(raw.Query("PRAGMA integrity_check")[0][0] == "ok" && raw.Query("PRAGMA foreign_key_check").Count == 0, "Isolated SQLite integrity");
                }
                scenarios.Add(new { fault, harnessStatus = "PASS", verdict, fakeF3Count = trigger.F3Count, trigger.FireCalls,
                    realF3Count = 0, errorCategory = fault == "stable-f" ? "NONE" : fault.Contains("focus", StringComparison.Ordinal) ? "FOCUS_LOST" : fault.Contains("document", StringComparison.Ordinal) ? "DOCUMENT_IDENTITY_CHANGED" : "READER_CHANGED",
                    reason, execution.Run.Id, evidence = execution.Evidence.Path, execution.Evidence.Sha256, isolatedDatabase = db,
                    existingRowsUnchanged = true, finalCounts = new { TC = 75, Run = 17, Evidence = 17, Mapping = 4, Schema = 4 }, trace = probe.Trace });
            });
        }
        SingleTextChecks.Run(Path.Combine(directory, "existing-text"), Test, Check);
        total.Stop();
        File.WriteAllText(Path.Combine(directory, "tests.json"), JsonSerializer.Serialize(new { total = results.Count, passed = results.Count - failures,
            failed = failures, seconds = total.Elapsed.TotalSeconds, realProductExecutions = 0, realF3Count = 0,
            scope = "QA observer safety only; synthetic results in copied databases are not product E2E", scenarios, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SUMMARY {results.Count - failures}/{results.Count}; failed={failures}; {total.Elapsed.TotalSeconds:F3}s; real F3=0");
        return failures == 0 ? 0 : 1;
    }

    // WHAT/WHY: Switch fault state only at the named boundary, then expose it on subsequent reads.
    // CONTRACT: Focus loss throws as the desktop probe does; changed samples keep their new IDs.
    // FAILURE: Faults latch; an earlier f never erases the fault.
    // TEST: Immediate and after-f variants independently assert the final ERROR and stored JSON.
    private sealed class Probe(string fault) : ISingleTextProbe
    {
        public bool Triggered { get; set; }
        public bool InSendGuard { get; set; }
        public bool Disposed { get; private set; }
        public List<object> Trace { get; } = [];
        private int postReads;
        public TextSample Arm(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fault == "pre-focus-arm") LoseFocus("Arm");
            return Sample("arm", "", "UIA.ValuePattern", "DAY9-document-A");
        }
        private void LoseFocus(string phase)
        {
            Trace.Add(new { phase, fault, error = "DAY9_FOCUS_LOST", postReads });
            throw new InvalidOperationException("DAY9_FOCUS_LOST at " + phase);
        }
        private TextSample Sample(string phase, string text, string reader, string document)
        {
            var sample = new TextSample(DateTimeOffset.UtcNow, text, reader, document);
            Trace.Add(new { phase, postReads, sample }); return sample;
        }
        public TextSample Read()
        {
            if (!Triggered)
            {
                if (fault == "pre-focus-read" || InSendGuard && fault == "pre-focus-sendguard") LoseFocus(InSendGuard ? "send-guard" : "pre-read");
                return Sample("pre-read", "", fault == "pre-reader" ? "UIA.TextPattern" : "UIA.ValuePattern", fault == "pre-document" ? "DAY9-document-B" : "DAY9-document-A");
            }
            postReads++;
            bool changed = !fault.EndsWith("after-f", StringComparison.Ordinal) || postReads >= 2;
            if (changed && fault.StartsWith("post-focus", StringComparison.Ordinal)) LoseFocus("post-read");
            return Sample("post-read", "f", changed && fault.StartsWith("post-reader", StringComparison.Ordinal) ? "UIA.TextPattern" : "UIA.ValuePattern",
                changed && fault.StartsWith("post-document", StringComparison.Ordinal) ? "DAY9-document-B" : "DAY9-document-A");
        }
        public void Dispose() { Disposed = true; Trace.Add(new { phase = "dispose" }); }
    }
    private sealed class Trigger(Probe probe) : ITextTrigger
    {
        public int FireCalls { get; private set; }
        public int F3Count { get; private set; }
        public object Diagnostics => new { method = "DAY9 FAKE ONLY; no SendInput", fireCalls = FireCalls, fakeF3Count = F3Count, realF3Count = 0, probe.Trace };
        public void Fire(CancellationToken token, Action<string> log)
        {
            token.ThrowIfCancellationRequested(); FireCalls++;
            // Mirror the desktop adapter's additional Read/HasFocus guard before dispatch.
            probe.InSendGuard = true;
            probe.Read();
            probe.InSendGuard = false;
            F3Count++; probe.Triggered = true;
            log("DAY9 simulated F3 only; no real keyboard dispatch");
        }
    }
}
