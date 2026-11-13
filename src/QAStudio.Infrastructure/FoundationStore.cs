using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;

namespace QAStudio.Infrastructure;

// WHAT: Versioned, transactional project-scoped persistence for Phase 1 entities and workbook provenance.
// WHY: Result history must outlive TC edits and cross-project IDs must never join accidentally.
// CONTRACT: Versioned migrations preserve original rows and immutable triggers.
// FAILURE: Future versions rejected; duplicate batch and FK/validation/trigger failures roll back, never partial import.
// TEST: Roundtrip/reopen, version rejection, invalid-import rollback, immutable SQL, cross-project links and retry history.
public sealed partial class FoundationStore : IFoundationStore, IExecutionStore, IManualRunStore, IDisposable
{
    private readonly SqliteDatabase db;
    public const int SchemaVersion = 6;
    private FoundationStore(SqliteDatabase database) => db = database;
    public FoundationStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new(path);
        try { Migrate(); } catch { db.Dispose(); throw; }
    }
    public void RegisterProject(Project project)
    {
        if (string.IsNullOrWhiteSpace(project.Id) || string.IsNullOrWhiteSpace(project.Name)) throw new InvalidDataException("Project ID/name required");
        db.Execute("INSERT INTO projects(id,name) VALUES(?,?) ON CONFLICT(id) DO UPDATE SET name=excluded.name", project.Id, project.Name);
    }
    public void Commit(string batchId, ImportPreview preview)
    {
        if (preview.IsProjectScopedMaster)
        {
            CommitNew(batchId, preview);
            return;
        }
        if (!preview.IsValid || preview.Cases.Count == 0 || string.IsNullOrWhiteSpace(batchId) ||
            Convert.ToHexString(SHA256.HashData(preview.Source)) != preview.SourceSha256)
            throw new InvalidDataException("Invalid import preview/provenance");
        if (preview.Cases.Select(c => c.Id).Distinct().Count() != preview.Cases.Count)
            throw new InvalidDataException("Duplicate import IDs");
        Transaction(() =>
        {
            db.Execute("INSERT INTO imports(id,sha256,source) VALUES(?,?,?)", batchId, preview.SourceSha256, Convert.ToBase64String(preview.Source));
            foreach (var tc in preview.Cases)
            {
                if (string.IsNullOrWhiteSpace(tc.Id) || tc.Fields.GetValueOrDefault("TC ID") != tc.Id ||
                    !new[] { "P0", "P1", "P2" }.Contains(tc.Fields.GetValueOrDefault("우선순위")) ||
                    new[] { "테스트 목적", "QA 기법", "수행 절차", "기대 결과" }.Any(k => string.IsNullOrWhiteSpace(tc.Fields.GetValueOrDefault(k))))
                    throw new InvalidDataException("Invalid TC fields");
                db.Execute("INSERT INTO cases(project,id,priority,payload) VALUES(?,?,?,?) ON CONFLICT(project,id) DO UPDATE SET priority=excluded.priority,payload=excluded.payload", tc.ProjectId, tc.Id, tc.Fields["우선순위"], JsonSerializer.Serialize(tc));
                db.Execute("INSERT INTO import_cases(batch,project,case_id) VALUES(?,?,?)", batchId, tc.ProjectId, tc.Id);
            }
            foreach (var design in preview.Automations)
            {
                if (design.State != AutomationState.NeedsMapping || design.Approved || design.Binding != "") throw new InvalidDataException("Imported designs are not executable");
                db.Execute("INSERT INTO automations(project,id,case_id,state,payload) VALUES(?,?,?,?,?) ON CONFLICT(project,id) DO UPDATE SET case_id=excluded.case_id,state=excluded.state,payload=excluded.payload", design.ProjectId, design.Id, design.TestCaseId, AutomationStateText.Format(design.State), JsonSerializer.Serialize(design));
            }
        });
    }
    public void BackupTo(string path) => db.BackupTo(path);
    // WHAT: Native GUI import adds only new TC IDs and preserves every existing payload/run.
    // WHY: The legacy Commit permits explicit edits; GUI import must never overwrite a collision.
    // CONTRACT: Recheck under BEGIN IMMEDIATE; plain INSERT and batch provenance commit together.
    // FAILURE: Duplicate/race/constraint/trigger failure rolls back the whole batch.
    // TEST: TC-IMP-004/005/009/010 and concurrent stale preview rejection.
    public void CommitNew(string batchId, ImportPreview preview)
    {
        if (!preview.IsValid || preview.Cases.Count == 0 || string.IsNullOrWhiteSpace(batchId) ||
            Convert.ToHexString(SHA256.HashData(preview.Source)) != preview.SourceSha256)
            throw new InvalidDataException("Invalid import preview/provenance");
        if (preview.Cases.Select(c => (c.ProjectId, c.Id)).Distinct().Count() != preview.Cases.Count)
            throw new InvalidDataException("Duplicate import IDs");
        Transaction(() =>
        {
            db.Execute("INSERT INTO imports(id,sha256,source) VALUES(?,?,?)", batchId, preview.SourceSha256, Convert.ToBase64String(preview.Source));
            foreach (var tc in preview.Cases)
            {
                if (string.IsNullOrWhiteSpace(tc.Id) || tc.Fields.GetValueOrDefault("TC ID") != tc.Id ||
                    !new[] { "P0", "P1", "P2" }.Contains(tc.Fields.GetValueOrDefault("우선순위")) ||
                    new[] { "테스트 목적", "QA 기법", "수행 절차", "기대 결과" }.Any(k => string.IsNullOrWhiteSpace(tc.Fields.GetValueOrDefault(k))))
                    throw new InvalidDataException("Invalid TC fields");
                db.Execute("INSERT INTO cases(project,id,priority,payload) VALUES(?,?,?,?)", tc.ProjectId, tc.Id, tc.Fields["우선순위"], JsonSerializer.Serialize(tc));
                db.Execute("INSERT INTO import_cases(batch,project,case_id) VALUES(?,?,?)", batchId, tc.ProjectId, tc.Id);
            }
            foreach (var design in preview.Automations)
            {
                if (design.State != AutomationState.NeedsMapping || design.Approved || design.Binding != "") throw new InvalidDataException("Imported designs are not executable");
                db.Execute("INSERT INTO automations(project,id,case_id,state,payload) VALUES(?,?,?,?,?)", design.ProjectId, design.Id, design.TestCaseId, AutomationStateText.Format(design.State), JsonSerializer.Serialize(design));
            }
        });
    }
    public IReadOnlyList<TestCase> Cases(string projectId) => Read<TestCase>("SELECT payload FROM cases WHERE project=? ORDER BY id", projectId);
    // WHAT: User-owned, one-per-TC bindings to the supported text scenarios.
    // WHY: Imported design rows are not scenario bindings and may be replaced by an explicit reimport.
    // CONTRACT: No defaults, inference or TC payload writes; project-scoped PK/FK and allowlist.
    // FAILURE: Unknown scenario/project/TC is rejected by validation or SQLite constraints.
    // TEST: Migration, duplicate upsert, reopen/change/remove, FK, reimport and data preservation.
    public IReadOnlyDictionary<string, string> AutomationMappings(string projectId) =>
        db.Query("SELECT case_id,scenario_id FROM tc_automation_mappings WHERE project=? ORDER BY case_id", projectId)
            .ToDictionary(r => r[0]!, r => r[1]!, StringComparer.Ordinal);
    public string? AutomationScenario(string projectId, string caseId) =>
        db.Query("SELECT scenario_id FROM tc_automation_mappings WHERE project=? AND case_id=?", projectId, caseId)
            .SingleOrDefault()?[0];
    public void SetAutomationMapping(string projectId, string caseId, string scenarioId)
    {
        if (projectId != "macro-studio" || string.IsNullOrWhiteSpace(caseId) ||
            scenarioId is not ("MAC-NP-001" or "MAC-NP-002" or "MAC-NP-003" or "MAC-NP-004" or "MAC-NP-005"))
            throw new InvalidDataException("MacroStudio TC에는 MAC-NP-001, MAC-NP-002, MAC-NP-003, MAC-NP-004, MAC-NP-005만 명시적으로 연결할 수 있습니다.");
        db.Execute("INSERT INTO tc_automation_mappings(project,case_id,scenario_id) VALUES(?,?,?) ON CONFLICT(project,case_id) DO UPDATE SET scenario_id=excluded.scenario_id",
            projectId, caseId, scenarioId);
    }
    public void RemoveAutomationMapping(string projectId, string caseId) =>
        db.Execute("DELETE FROM tc_automation_mappings WHERE project=? AND case_id=?", projectId, caseId);
    public IReadOnlyList<Project> Projects() => db.Query("SELECT id,name FROM projects ORDER BY name,id")
        .Select(row => new Project(row[0]!, row[1]!)).ToArray();

    // WHAT: Read the registered projects, cases, explicit mappings and immutable history together.
    // WHY: Each workbook and evidence folder must reflect the same committed DB state.
    // CONTRACT: Open read-only, bypass startup migration, and pin a single SQLite snapshot.
    // FAILURE: An unsupported schema or failed query stops export before any file is published.
    // TEST: Export twice and compare the complete operating DB hash and all exported rows.
    public static IReadOnlyList<ResultPackageProject> ReadResultPackage(string path)
    {
        using var store = new FoundationStore(new SqliteDatabase(path, readOnly: true));
        store.db.Execute("BEGIN");
        if (store.db.Query("PRAGMA user_version")[0][0] != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidDataException($"결과 패키지는 SQLite schema v{SchemaVersion}에서 내보낼 수 있습니다.");
        var snapshot = store.Projects().Select(project => store.ProjectResults(project.Id)).ToArray();
        store.db.Execute("COMMIT");
        return snapshot;
    }
    public IReadOnlyList<AutomationDefinition> Automations(string projectId) => Read<AutomationDefinition>("SELECT payload FROM automations WHERE project=? ORDER BY id", projectId);
    public IReadOnlyList<TestRun> Runs(string projectId) => Read<TestRun>("SELECT payload FROM runs WHERE project=? ORDER BY seq", projectId);
    public IReadOnlyList<TestPlan> Plans(string projectId) => Read<TestPlan>("SELECT payload FROM plans WHERE project=? ORDER BY id", projectId);
    public IReadOnlyList<Defect> Defects(string projectId) => Read<Defect>("SELECT payload FROM defects WHERE project=? ORDER BY id", projectId);
    public IReadOnlyList<AIProposal> Proposals(string projectId) => Read<AIProposal>("SELECT payload FROM proposals WHERE project=? ORDER BY id", projectId);
    public IReadOnlyList<Evidence> EvidenceFor(string projectId, string runId) => Read<Evidence>("SELECT payload FROM evidence WHERE project=? AND run_id=? ORDER BY id", projectId, runId);
    public AutomationDefinition TransitionAutomation(string projectId, string id, AutomationState next, string? binding = null, bool? approved = null)
    {
        AutomationDefinition? changed = null;
        Transaction(() =>
        {
            var current = Read<AutomationDefinition>("SELECT payload FROM automations WHERE project=? AND id=?", projectId, id).Single();
            changed = (current with { Binding = binding ?? current.Binding, Approved = approved ?? current.Approved }).Transition(next);
            db.Execute("UPDATE automations SET state=?,payload=? WHERE project=? AND id=?", AutomationStateText.Format(changed.State), JsonSerializer.Serialize(changed), projectId, id);
        });
        return changed!;
    }
    public IReadOnlyList<TestCase> BatchCases(string batchId) => Read<TestCase>("SELECT c.payload FROM cases c JOIN import_cases i ON c.project=i.project AND c.id=i.case_id WHERE i.batch=? ORDER BY c.id", batchId);
    public byte[] Source(string batchId)
    {
        var row = db.Query("SELECT source,sha256 FROM imports WHERE id=?", batchId).Single();
        byte[] bytes = Convert.FromBase64String(row[0]!);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != row[1]) throw new InvalidDataException("Stored workbook checksum mismatch");
        return bytes;
    }
    public ExecutionResult LatestResult(string projectId, string caseId)
    {
        var values = Read<TestRun>("SELECT payload FROM runs WHERE project=? AND case_id=? ORDER BY seq DESC LIMIT 1", projectId, caseId);
        return values.Count == 0 ? ExecutionResult.NotRun : values[0].Result;
    }
    // WHAT: Append the existing immutable run and its evidence together.
    // FAILURE: A persistence/constraint error rolls both records back; callers must not display a saved PASS.
    public void RecordRun(TestRun run, Evidence evidence) => Transaction(() =>
    {
        if (evidence.ProjectId != run.ProjectId || evidence.RunId != run.Id) throw new InvalidDataException("Run evidence scope mismatch");
        AppendRun(run); AppendEvidence(evidence);
    });
    public void AppendRun(TestRun run)
    {
        run.Validate();
        db.Execute("INSERT INTO runs(project,id,case_id,result,assertion,evidence,previous_id,payload) VALUES(?,?,?,?,?,?,?,?)",
            run.ProjectId, run.Id, run.TestCaseId, ResultText.Format(run.Result), run.Assertion, run.Evidence, run.PreviousRunId, JsonSerializer.Serialize(run));
    }
    public void AppendEvidence(Evidence evidence) => db.Execute("INSERT INTO evidence(project,id,run_id,payload) VALUES(?,?,?,?)", evidence.ProjectId, evidence.Id, evidence.RunId, JsonSerializer.Serialize(evidence));
    public void SavePlan(TestPlan plan) => Transaction(() =>
    {
        db.Execute("INSERT INTO plans(project,id,payload) VALUES(?,?,?)", plan.ProjectId, plan.Id, JsonSerializer.Serialize(plan));
        foreach (var id in plan.CaseIds) db.Execute("INSERT INTO plan_cases(project,plan_id,case_id) VALUES(?,?,?)", plan.ProjectId, plan.Id, id);
    });
    public void SaveDefect(Defect defect) => db.Execute("INSERT INTO defects(project,id,run_id,retest_id,payload) VALUES(?,?,?,?,?)", defect.ProjectId, defect.Id, defect.RunId, defect.RetestRunId, JsonSerializer.Serialize(defect));
    public void AppendProposal(AIProposal proposal)
    {
        if (new[] { proposal.Source, proposal.Model, proposal.PromptVersion, proposal.ReviewStatus, proposal.Evidence }.Any(string.IsNullOrWhiteSpace) ||
            !new[] { "PENDING", "APPROVED", "REJECTED", "EDITED" }.Contains(proposal.ReviewStatus) ||
            proposal.ReviewStatus != "PENDING" && string.IsNullOrWhiteSpace(proposal.Reviewer))
            throw new InvalidDataException("AI source/model/prompt/review provenance required");
        db.Execute("INSERT INTO proposals(project,id,case_id,payload) VALUES(?,?,?,?)", proposal.ProjectId, proposal.Id, proposal.TestCaseId, JsonSerializer.Serialize(proposal));
    }
    private List<T> Read<T>(string sql, params string?[] parameters) => db.Query(sql, parameters).Select(x => JsonSerializer.Deserialize<T>(x[0]!)!).ToList();
    private void Transaction(Action body)
    {
        db.Execute("BEGIN IMMEDIATE");
        try { body(); db.Execute("COMMIT"); }
        catch { db.Execute("ROLLBACK"); throw; }
    }
    private void Migrate()
    {
        int ReadVersion() => int.Parse(db.Query("PRAGMA user_version")[0][0]!, System.Globalization.CultureInfo.InvariantCulture);
        int version = ReadVersion();
        if (version > SchemaVersion) throw new InvalidDataException($"Unsupported future schema: {version}");
        if (version == SchemaVersion) return;
        Transaction(() =>
        {
            version = ReadVersion();
            if (version > SchemaVersion) throw new InvalidDataException($"Unsupported future schema: {version}");
            if (version < 5) MigrateToV5(version);
            if (version < 6) MigrateToV6();
        });
    }

    // WHAT/WHY: SQLite CHECK expansion requires a transactional table rebuild, not writable_schema.
    // CONTRACT: Copy every mapping; retain attached explicit indexes/triggers and all other tables.
    // FAILURE: Any copy/DDL/constraint failure rolls back schema, data and user_version together.
    // TEST: Day11 v4 snapshot migration/reopen, attached-trigger retention and rollback.
    private void MigrateToV5(int version)
    {
        if (version == 0) foreach (var statement in Schema) db.Execute(statement);
        if (version < 2)
            db.Execute("CREATE TABLE tc_automation_mappings(project TEXT NOT NULL CHECK(project='macro-studio'),case_id TEXT NOT NULL,scenario_id TEXT NOT NULL CHECK(scenario_id IN ('MAC-NP-001','MAC-NP-002')),PRIMARY KEY(project,case_id),FOREIGN KEY(project,case_id) REFERENCES cases(project,id))");
        var attached = db.Query("SELECT sql FROM sqlite_master WHERE tbl_name='tc_automation_mappings' AND type IN ('index','trigger') AND sql IS NOT NULL ORDER BY type,name");
        db.Execute("CREATE TABLE tc_automation_mappings_v5(project TEXT NOT NULL CHECK(project='macro-studio'),case_id TEXT NOT NULL,scenario_id TEXT NOT NULL CHECK(scenario_id IN ('MAC-NP-001','MAC-NP-002','MAC-NP-003','MAC-NP-004','MAC-NP-005')),PRIMARY KEY(project,case_id),FOREIGN KEY(project,case_id) REFERENCES cases(project,id))");
        db.Execute("INSERT INTO tc_automation_mappings_v5 SELECT project,case_id,scenario_id FROM tc_automation_mappings");
        db.Execute("DROP TABLE tc_automation_mappings");
        db.Execute("ALTER TABLE tc_automation_mappings_v5 RENAME TO tc_automation_mappings");
        foreach (var row in attached) db.Execute(row[0]!);
        db.Execute("PRAGMA user_version=5");
    }
    private static readonly string[] Schema = [
        "CREATE TABLE projects(id TEXT PRIMARY KEY NOT NULL,name TEXT NOT NULL)",
        "CREATE TABLE imports(id TEXT PRIMARY KEY NOT NULL,sha256 TEXT NOT NULL,source TEXT NOT NULL)",
        "CREATE TABLE cases(project TEXT NOT NULL,id TEXT NOT NULL,priority TEXT NOT NULL CHECK(priority IN ('P0','P1','P2')),payload TEXT NOT NULL,PRIMARY KEY(project,id),FOREIGN KEY(project) REFERENCES projects(id))",
        "CREATE TABLE import_cases(batch TEXT NOT NULL,project TEXT NOT NULL,case_id TEXT NOT NULL,PRIMARY KEY(batch,project,case_id),FOREIGN KEY(batch) REFERENCES imports(id),FOREIGN KEY(project,case_id) REFERENCES cases(project,id))",
        "CREATE TABLE automations(project TEXT NOT NULL,id TEXT NOT NULL,case_id TEXT NOT NULL,state TEXT NOT NULL CHECK(state IN ('DRAFT','NEEDS_MAPPING','READY','EXECUTING','FINISHED')),payload TEXT NOT NULL,PRIMARY KEY(project,id),FOREIGN KEY(project,case_id) REFERENCES cases(project,id))",
        "CREATE TABLE runs(seq INTEGER PRIMARY KEY AUTOINCREMENT,project TEXT NOT NULL,id TEXT NOT NULL,case_id TEXT NOT NULL,result TEXT NOT NULL CHECK(result IN ('PASS','FAIL','ERROR','BLOCKED','CANCELLED')),assertion TEXT NOT NULL,evidence TEXT NOT NULL,previous_id TEXT,payload TEXT NOT NULL,UNIQUE(project,id),UNIQUE(project,case_id,id),CHECK(result NOT IN ('PASS','FAIL') OR (length(trim(assertion))>0 AND length(trim(evidence))>0)),FOREIGN KEY(project,case_id) REFERENCES cases(project,id),FOREIGN KEY(project,case_id,previous_id) REFERENCES runs(project,case_id,id))",
        "CREATE TABLE evidence(project TEXT NOT NULL,id TEXT NOT NULL,run_id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(project,id),FOREIGN KEY(project,run_id) REFERENCES runs(project,id))",
        "CREATE TABLE plans(project TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(project,id),FOREIGN KEY(project) REFERENCES projects(id))",
        "CREATE TABLE plan_cases(project TEXT NOT NULL,plan_id TEXT NOT NULL,case_id TEXT NOT NULL,PRIMARY KEY(project,plan_id,case_id),FOREIGN KEY(project,plan_id) REFERENCES plans(project,id),FOREIGN KEY(project,case_id) REFERENCES cases(project,id))",
        "CREATE TABLE defects(project TEXT NOT NULL,id TEXT NOT NULL,run_id TEXT NOT NULL,retest_id TEXT,payload TEXT NOT NULL,PRIMARY KEY(project,id),FOREIGN KEY(project,run_id) REFERENCES runs(project,id),FOREIGN KEY(project,retest_id) REFERENCES runs(project,id))",
        "CREATE TABLE proposals(project TEXT NOT NULL,id TEXT NOT NULL,case_id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(project,id),FOREIGN KEY(project,case_id) REFERENCES cases(project,id))",
        "CREATE INDEX runs_case ON runs(project,case_id,seq)",
        "CREATE TRIGGER runs_no_update BEFORE UPDATE ON runs BEGIN SELECT RAISE(ABORT,'Immutable run'); END",
        "CREATE TRIGGER runs_no_delete BEFORE DELETE ON runs BEGIN SELECT RAISE(ABORT,'Immutable run'); END",
        "CREATE TRIGGER evidence_no_update BEFORE UPDATE ON evidence BEGIN SELECT RAISE(ABORT,'Immutable evidence'); END",
        "CREATE TRIGGER evidence_no_delete BEFORE DELETE ON evidence BEGIN SELECT RAISE(ABORT,'Immutable evidence'); END",
        "CREATE TRIGGER proposals_no_update BEFORE UPDATE ON proposals BEGIN SELECT RAISE(ABORT,'Append a new proposal review'); END",
        "CREATE TRIGGER proposals_no_delete BEFORE DELETE ON proposals BEGIN SELECT RAISE(ABORT,'Immutable proposal review'); END",
        "CREATE TRIGGER imports_no_update BEFORE UPDATE ON imports BEGIN SELECT RAISE(ABORT,'Immutable workbook source'); END",
        "CREATE TRIGGER imports_no_delete BEFORE DELETE ON imports BEGIN SELECT RAISE(ABORT,'Immutable workbook source'); END"
    ];
    public void Dispose() => db.Dispose();
}
