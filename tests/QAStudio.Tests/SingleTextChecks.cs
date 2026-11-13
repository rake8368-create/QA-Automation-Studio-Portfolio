using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;

namespace QAStudio.Tests;

// WHAT: Exercise real assertion/evidence code with scripted read-only observations, not product PASSes.
// WHY: Late repetition, missing data and failed persistence must never yield a saved PASS.
// CONTRACT: All files belong to the existing harness's isolated evidence directory.
// FAILURE: Any incorrect verdict/evidence or swallowed save exception fails the harness.
// TEST: Called by the existing dependency-free QAStudio.Tests runner.
internal static class SingleTextChecks
{
    public static void Run(string directory, Action<string, Action> test, Action<bool, string> check)
    {
        SingleTextRequest Request() => new("macro-studio", "MAC-NP-001", "f", "F3", directory, "SCRIPTED PROBE/TRIGGER ONLY", 100, 100, 10);
        TextExecution Execute(Probe probe, IExecutionStore? store = null, TestCase? tc = null) =>
            new SingleTextOrchestrator().ExecuteAsync(probe, Request(), store: store, registeredCase: tc, trigger: new FakeTrigger(probe)).GetAwaiter().GetResult();
        test("Text assertion requires empty baseline, exact f and stable repeated actual reads", () =>
        {
            var probe = new Probe("", ["", "f"]);
            var executed = Execute(probe);
            check(executed.Run.Result == ExecutionResult.Pass && probe.Reads > 2 && probe.Disposed && probe.TriggerCalls == 1, "Stable live sample and exactly one automatic trigger contract");
            byte[] bytes = File.ReadAllBytes(executed.Evidence.Path);
            check(Convert.ToHexString(SHA256.HashData(bytes)) == executed.Evidence.Sha256, "Evidence checksum");
            using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
            check(root.GetProperty("actual").GetString() == "f" && root.GetProperty("expected").GetString() == "f" &&
                root.GetProperty("scenarioId").GetString() == "MAC-NP-001" && root.GetProperty("mode").GetString() == "automatic" &&
                root.GetProperty("finished").GetDateTimeOffset() >= root.GetProperty("started").GetDateTimeOffset(), "Run provenance");
        });
        foreach (var (name, samples) in new (string, string[])[]
        {
            ("no output", [""]), ("wrong character", ["g"]), ("duplicate", ["ff"]),
            ("late repetition", ["f", "f", "ff"]), ("transient mismatch cannot recover to PASS", ["f", "ff", "f"]),
            ("deleted output", ["f", ""]), ("no Trim or newline normalization", ["f\r\n"]), ("case sensitive", ["F"])
        })
            test("Text assertion FAIL: " + name, () => check(Execute(new("", samples)).Run.Result == ExecutionResult.Fail, name));
        foreach (var (name, samples, verdict) in new (string, string[], ExecutionResult)[]
        {
            ("prefix completes", ["f", "f", "ff"], ExecutionResult.Pass),
            ("exact ff", ["ff"], ExecutionResult.Pass),
            ("incomplete f", ["f"], ExecutionResult.Fail),
            ("empty", [""], ExecutionResult.Fail),
            ("extra f", ["fff"], ExecutionResult.Fail),
            ("wrong then exact", ["g", "ff"], ExecutionResult.Fail),
            ("late repetition then recovery", ["f", "ff", "fff", "ff"], ExecutionResult.Fail),
            ("shrinks to prefix after completion", ["ff", "f", "ff"], ExecutionResult.Fail)
        })
            test("Two-character assertion: " + name, () =>
            {
                var probe = new Probe("", samples);
                var executed = new SingleTextOrchestrator().ExecuteAsync(probe,
                    Request() with { ScenarioId = "MAC-NP-002", Expected = "ff" }, trigger: new FakeTrigger(probe)).GetAwaiter().GetResult();
                check(executed.Run.Result == verdict && probe.TriggerCalls == 1, name);
                using var json = JsonDocument.Parse(File.ReadAllBytes(executed.Evidence.Path));
                check(json.RootElement.GetProperty("scenarioId").GetString() == "MAC-NP-002" &&
                    json.RootElement.GetProperty("expected").GetString() == "ff", "Scenario evidence");
            });
        test("Nonempty baseline stops without any post-trigger observation", () =>
        {
            var probe = new Probe("user document", ["f"]);
            check(Execute(probe).Run.Result == ExecutionResult.Error && probe.Reads == 0, "Protect user document");
        });
        test("Missing automatic trigger stops instead of waiting for physical keyboard input", () =>
        {
            var probe = new Probe("", ["f"]);
            var result = new SingleTextOrchestrator().ExecuteAsync(probe, Request()).GetAwaiter().GetResult();
            check(result.Run.Result == ExecutionResult.Error && probe.Reads == 0 && probe.TriggerCalls == 0, "No automatic adapter must be ERROR");
        });
        test("Automatic trigger insertion failure records ERROR without expected output substitution", () =>
        {
            var probe = new Probe("", ["f"]);
            var executed = new SingleTextOrchestrator().ExecuteAsync(probe, Request(), trigger: new FakeTrigger(probe, true)).GetAwaiter().GetResult();
            check(executed.Run.Result == ExecutionResult.Error && probe.TriggerCalls == 1 && !probe.Triggered, "Failed insertion must be ERROR");
        });
        test("Successful automatic trigger with no observed output records FAIL and trigger diagnostics", () =>
        {
            var probe = new Probe("", [""]); var executed = Execute(probe);
            using var saved = JsonDocument.Parse(File.ReadAllBytes(executed.Evidence.Path));
            check(executed.Run.Result == ExecutionResult.Fail && probe.TriggerCalls == 1 &&
                saved.RootElement.GetProperty("actual").GetString() == "" &&
                saved.RootElement.GetProperty("triggerDiagnostics").GetProperty("inserted").GetInt32() == 2,
                "SendInput-style success cannot imply PASS");
        });
        test("Cancellation before automatic trigger never emits it", () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var probe = new Probe("", ["f"]);
            var executed = new SingleTextOrchestrator().ExecuteAsync(probe, Request(), cancellationToken: cancellation.Token,
                trigger: new FakeTrigger(probe)).GetAwaiter().GetResult();
            check(executed.Run.Result == ExecutionResult.Cancelled && probe.TriggerCalls == 0 && probe.Disposed, "Cancelled trigger");
        });
        test("Read exception / lost focus yields ERROR, never a fabricated actual value", () =>
        {
            var result = Execute(new("", ["f"]) { Fault = true });
            check(result.Run.Result == ExecutionResult.Error, "Unreadable target");
        });
        test("Document or reader changes while observing yield ERROR", () =>
        {
            check(Execute(new("", ["f"]) { ChangedDocument = true }).Run.Result == ExecutionResult.Error, "Switched tab");
            check(Execute(new("", ["f"]) { ChangedReader = true }).Run.Result == ExecutionResult.Error, "Changed reader");
        });
        test("Evidence write failure propagates before store.RecordRun", () =>
        {
            string blocker = Path.Combine(directory, "not-a-directory"); File.WriteAllText(blocker, "block");
            var store = new RecordingStore();
            var probe = new Probe("", ["f"]);
            bool failed = false;
            try { new SingleTextOrchestrator().ExecuteAsync(probe, Request() with { EvidenceDirectory = blocker },
                store: store, registeredCase: new("macro-studio", "MAC-NP-001", new Dictionary<string, string>()), trigger: new FakeTrigger(probe)).GetAwaiter().GetResult(); }
            catch (IOException) { failed = true; }
            check(failed && store.Count == 0, "No saved PASS without evidence");
        });
        test("Existing execution store receives only this scenario's own TC and immutable evidence", () =>
        {
            var store = new RecordingStore(); var tc = new TestCase("macro-studio", "MAC-NP-001", new Dictionary<string, string>());
            Execute(new("", ["f"]), store, tc); check(store.Count == 1, "RecordRun reused");
            bool rejected = false;
            try { Execute(new("", ["f"]), store, tc with { Id = "MAC-004" }); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && store.Count == 1, "Never relabel another TC as functional PASS");
            store.Fail = true; bool failed = false;
            try { Execute(new("", ["f"]), store, tc); } catch (IOException) { failed = true; }
            check(failed && store.Count == 1, "Persistence errors must propagate");
        });
    }
    private sealed class Probe(string baseline, string[] texts) : ISingleTextProbe
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public bool Fault { get; init; }
        public bool ChangedDocument { get; init; }
        public bool ChangedReader { get; init; }
        public bool Triggered { get; set; }
        public int TriggerCalls { get; set; }
        public TextSample Arm(CancellationToken cancellationToken) => new(DateTimeOffset.UtcNow, baseline, "scripted", "isolated-document");
        public TextSample Read()
        {
            if (Fault) throw new InvalidOperationException("Simulated lost focus or unreadable text");
            if (!Triggered) return new(DateTimeOffset.UtcNow, baseline, ChangedReader ? "different" : "scripted", ChangedDocument ? "different" : "isolated-document");
            return new(DateTimeOffset.UtcNow, texts[Math.Min(Reads++, texts.Length - 1)], ChangedReader ? "different" : "scripted", ChangedDocument ? "different" : "isolated-document");
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeTrigger(Probe probe, bool fail = false) : ITextTrigger
    {
        public object Diagnostics => new { method = "SCRIPTED TRIGGER ONLY", calls = probe.TriggerCalls, inserted = fail ? 0 : 2 };
        public void Fire(CancellationToken cancellationToken, Action<string> log)
        {
            cancellationToken.ThrowIfCancellationRequested(); probe.TriggerCalls++;
            if (fail) throw new InvalidOperationException("Simulated SendInput 0/2");
            probe.Triggered = true; log("SCRIPTED trigger 2/2; actual text assertion still required");
        }
    }
    private sealed class RecordingStore : IExecutionStore
    {
        public int Count { get; private set; }
        public bool Fail { get; set; }
        public void RecordRun(TestRun run, Evidence evidence)
        {
            if (Fail) throw new IOException("Simulated store failure");
            if (!File.Exists(evidence.Path) || run.Id != evidence.RunId) throw new InvalidDataException("Missing evidence");
            run.Validate(); Count++;
        }
    }
}
