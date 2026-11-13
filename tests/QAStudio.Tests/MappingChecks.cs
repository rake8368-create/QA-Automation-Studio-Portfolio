using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QAStudio.Tests;

// WHAT: Verify persistent scenario links and registered-TC execution identity in isolated databases.
// WHY: A mapping must not rewrite imported requirements or fabricate a product execution.
// CONTRACT: Synthetic cases/probes only; no production DB or MacroStudio input is used.
// FAILURE: Schema, scope, preservation or history mismatch fails the existing harness.
// TEST: Migration, CRUD/reopen, allowlist/FK, import identity and existing RecordRun integration.
internal static class MappingChecks
{
    public static void Run(string directory, Action<string, Action> test, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        TestCase Fixture() => new("macro-studio", "GUI-MAPPING-FIXTURE", new Dictionary<string, string>
        {
            ["TC ID"] = "GUI-MAPPING-FIXTURE", ["우선순위"] = "P2", ["QA 기법"] = "격리 연결 검증",
            ["테스트 목적"] = "격리 테스트 전용: 선택한 f/ff 시나리오 연결 저장·변경·해제",
            ["사전조건"] = "운영 TC와 분리된 검증 DB. 실제 MacroStudio 실행용 TC가 아님",
            ["수행 절차"] = "명시 연결 → 재시작 → 변경 → 해제. 실제 F3 실행 금지",
            ["기대 결과"] = "선택한 연결 유지, 해제 후 자동화 미연결"
        });
        ImportPreview Preview(TestCase tc)
        {
            byte[] bytes = [1, 2, 3];
            return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)), [tc], [], []);
        }
        string NewPath() => Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sqlite");
        FoundationStore Seed(string path)
        {
            var store = new FoundationStore(path); store.RegisterProject(new("macro-studio", "MacroStudio · 격리 검증"));
            store.CommitNew("fixture", Preview(Fixture())); return store;
        }
        void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { rejected = true; }
            check(rejected, "Invalid mapping must be rejected");
        }
        test("Mapping v1 migration preserves original rows and immutable triggers; starts unlinked", () =>
        {
            string path = NewPath(); using (var seed = Seed(path)) { }
            using (var raw = new SqliteDatabase(path))
            {
                raw.Execute("DROP TABLE manual_runs");
                raw.Execute("DROP TABLE case_assessments");
                raw.Execute("DROP TABLE tc_automation_mappings"); raw.Execute("PRAGMA user_version=1");
            }
            string Snapshot()
            {
                using var raw = new SqliteDatabase(path);
                var names = raw.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('tc_automation_mappings','manual_runs','case_assessments') ORDER BY name");
                return JsonSerializer.Serialize(names.Select(n => new { name = n[0], rows = raw.Query("SELECT * FROM \"" + n[0] + "\" ORDER BY rowid") }));
            }
            string before = Snapshot();
            using (var migrated = new FoundationStore(path)) check(migrated.AutomationMappings("macro-studio").Count == 0, "No seed mappings");
            check(before == Snapshot(), "Every original table row unchanged");
            using var inspect = new SqliteDatabase(path);
            check(inspect.Query("PRAGMA user_version")[0][0] == FoundationStore.SchemaVersion.ToString() && inspect.Query("PRAGMA foreign_key_check").Count == 0, "Current schema compatibility");
            check(inspect.Query("SELECT name FROM sqlite_master WHERE type='trigger' AND name='runs_no_update'").Count == 1, "Immutable history trigger retained");
        });
        test("Mapping explicit save survives reopen, changes without duplication, then removes", () =>
        {
            string path = NewPath(); var tc = Fixture(); string payload;
            using (var store = Seed(path))
            {
                check(store.AutomationMappings(tc.ProjectId).Count == 0, "Initially all unlinked");
                payload = JsonSerializer.Serialize(store.Cases(tc.ProjectId));
                store.SetAutomationMapping(tc.ProjectId, tc.Id, "MAC-NP-001");
                store.SetAutomationMapping(tc.ProjectId, tc.Id, "MAC-NP-001");
            }
            using var reopened = new FoundationStore(path);
            check(reopened.AutomationScenario(tc.ProjectId, tc.Id) == "MAC-NP-001", "Reopen persistence");
            reopened.SetAutomationMapping(tc.ProjectId, tc.Id, "MAC-NP-002");
            check(reopened.AutomationMappings(tc.ProjectId).Count == 1 && reopened.AutomationScenario(tc.ProjectId, tc.Id) == "MAC-NP-002", "One row, updated scenario");
            reopened.RemoveAutomationMapping(tc.ProjectId, tc.Id);
            check(reopened.AutomationScenario(tc.ProjectId, tc.Id) is null && payload == JsonSerializer.Serialize(reopened.Cases(tc.ProjectId)), "Remove only mapping; TC unchanged");
            check(reopened.Runs(tc.ProjectId).Count == 0, "CRUD is never an execution");
        });
        test("Mapping rejects unsupported scenarios, unknown TCs and cross-project links in API and SQLite", () =>
        {
            string path = NewPath(); using var store = Seed(path); var tc = Fixture();
            Reject(() => store.SetAutomationMapping(tc.ProjectId, tc.Id, "MAC-NP-999"));
            Reject(() => store.SetAutomationMapping(tc.ProjectId, "missing", "MAC-NP-001"));
            Reject(() => store.SetAutomationMapping("qa-studio", tc.Id, "MAC-NP-001"));
            using var raw = new SqliteDatabase(path);
            Reject(() => raw.Execute("INSERT INTO tc_automation_mappings VALUES(?,?,?)", tc.ProjectId, tc.Id, "unknown"));
            Reject(() => raw.Execute("INSERT INTO tc_automation_mappings VALUES(?,?,?)", tc.ProjectId, "missing", "MAC-NP-001"));
            check(store.AutomationMappings(tc.ProjectId).Count == 0, "No invalid links persisted");
        });
        test("Mapping survives same-ID reimport and rejected native duplicate import", () =>
        {
            using var store = Seed(NewPath()); var tc = Fixture();
            store.SetAutomationMapping(tc.ProjectId, tc.Id, "MAC-NP-001");
            store.Commit("same-id", Preview(tc));
            Reject(() => store.CommitNew("native-duplicate", Preview(tc)));
            check(store.Cases(tc.ProjectId).Count == 1 && store.AutomationScenario(tc.ProjectId, tc.Id) == "MAC-NP-001", "Stable project/id relationship");
        });
        test("Linked scenario uses existing RecordRun with imported identity and separate scenario provenance", () =>
        {
            using var store = Seed(NewPath()); var tc = Fixture();
            string original = JsonSerializer.Serialize(store.Cases(tc.ProjectId));
            store.SetAutomationMapping(tc.ProjectId, tc.Id, "MAC-NP-001");
            var probe = new ScriptedProbe();
            var request = new SingleTextRequest(tc.ProjectId, "MAC-NP-001", "f", "F3", directory,
                "SYNTHETIC MAPPING CONTRACT CHECK; NOT PRODUCT E2E", 100, 100, 10, tc.Id);
            var result = new SingleTextOrchestrator().ExecuteAsync(probe, request, store: store, registeredCase: tc, trigger: probe).GetAwaiter().GetResult();
            check(store.Runs(tc.ProjectId).Single().TestCaseId == tc.Id && store.EvidenceFor(tc.ProjectId, result.Run.Id).Count == 1, "Existing atomic persistence");
            using var json = JsonDocument.Parse(File.ReadAllBytes(result.Evidence.Path));
            check(json.RootElement.GetProperty("scenarioId").GetString() == "MAC-NP-001" &&
                json.RootElement.GetProperty("request").GetProperty("LinkedTestCaseId").GetString() == tc.Id, "Scenario and TC provenance");
            check(original == JsonSerializer.Serialize(store.Cases(tc.ProjectId)) && probe.Calls == 1, "TC immutable; one scripted trigger");
        });
        test("Linked execution rejects missing store or mismatched registered TC before triggering", () =>
        {
            using var store = Seed(NewPath()); var tc = Fixture(); var probe = new ScriptedProbe();
            var request = new SingleTextRequest(tc.ProjectId, "MAC-NP-001", "f", "F3", directory, "SYNTHETIC", LinkedTestCaseId: tc.Id);
            void RejectRequest(IExecutionStore? targetStore, TestCase? registered)
            {
                bool rejected = false;
                try { new SingleTextOrchestrator().ExecuteAsync(probe, request, store: targetStore, registeredCase: registered, trigger: probe).GetAwaiter().GetResult(); }
                catch (ArgumentException) { rejected = true; }
                check(rejected && probe.Calls == 0, "Identity rejected before input");
            }
            RejectRequest(null, null); RejectRequest(store, tc with { Id = "other" });
            RejectRequest(store, tc with { ProjectId = "qa-studio" });
        });
        test("Isolated GUI mapping fixture has no links, runs or production TC data", () =>
        {
            using var fixture = Seed(Path.Combine(directory, "gui-fixture.sqlite"));
            check(fixture.Cases("macro-studio").Count == 1 && fixture.AutomationMappings("macro-studio").Count == 0 &&
                fixture.Runs("macro-studio").Count == 0, "GUI fixture only; no real scenario execution permitted");
        });
    }
    private sealed class ScriptedProbe : ISingleTextProbe, ITextTrigger
    {
        public int Calls { get; private set; }
        public object Diagnostics => new { method = "SCRIPTED ONLY; NO MACROSTUDIO EXECUTION" };
        public TextSample Arm(CancellationToken token) => Sample("");
        public TextSample Read() => Sample(Calls == 0 ? "" : "f");
        private static TextSample Sample(string value) => new(DateTimeOffset.UtcNow, value, "scripted", "isolated-fixture");
        public void Fire(CancellationToken token, Action<string> log) { token.ThrowIfCancellationRequested(); Calls++; }
        public void Dispose() { }
    }
}
