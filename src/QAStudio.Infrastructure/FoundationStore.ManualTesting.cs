using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;

namespace QAStudio.Infrastructure;

public sealed partial class FoundationStore
{
    public ResultPackageProject ProjectResults(string projectId) => new(Cases(projectId), AutomationMappings(projectId),
        Runs(projectId), Read<Evidence>("SELECT payload FROM evidence WHERE project=? ORDER BY run_id,id", projectId),
        ReadAssessments(projectId), ManualRuns(projectId), ReadManualReadiness(projectId));

    public IReadOnlyList<ManualRun> ManualRuns(string projectId) => db.Query("""
        SELECT id,case_id,tester,executed_at,result,actual_result,notes,attachment_path,attachment_sha256
        FROM manual_runs WHERE project=? ORDER BY executed_at,seq
        """, projectId).Select(row => new ManualRun(projectId, row[0]!, row[1]!, row[2]!,
            DateTimeOffset.ParseExact(row[3]!, "O", CultureInfo.InvariantCulture),
            Enum.Parse<ManualResult>(row[4]!, ignoreCase: true), row[5]!, row[6]!, row[7], row[8])).ToArray();

    public void RecordManualRun(ManualRun run)
    {
        run.Validate();
        Transaction(() =>
        {
            if (AutomationScenario(run.ProjectId, run.TestCaseId) is not null ||
                ReadAssessments(run.ProjectId).GetValueOrDefault(run.TestCaseId)?.Status == SuitabilityStatus.Retired)
                throw new InvalidDataException("현재 자동화 연결이 있거나 폐기된 TC는 수동 이력을 등록할 수 없습니다.");
            db.Execute("""
                INSERT INTO manual_runs(project,id,case_id,tester,executed_at,result,actual_result,notes,attachment_path,attachment_sha256)
                VALUES(?,?,?,?,?,?,?,?,?,?)
                """, run.ProjectId, run.Id, run.TestCaseId, run.Tester, run.ExecutedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                run.ResultText, run.ActualResult, run.Notes, run.AttachmentPath, run.AttachmentSha256);
        });
    }

    private IReadOnlyDictionary<string, CaseAssessment> ReadAssessments(string projectId)
    {
        return db.Query("""
            SELECT a.case_id,a.case_hash,a.payload,c.payload
            FROM case_assessments a JOIN cases c ON c.project=a.project AND c.id=a.case_id WHERE a.project=?
            """, projectId).Where(row => row[1] == PayloadHash(row[3]!))
            .ToDictionary(row => row[0]!, row => JsonSerializer.Deserialize<CaseAssessment>(row[2]!)!, StringComparer.Ordinal);
    }

    // WHAT: Add separate project-scoped assessment and immutable manual run tables in schema v6.
    // WHY: Existing automated runs, evidence, cases and mappings retain their schema and data.
    // CONTRACT: Seed reviews only for the exact audited (project, ID, source payload hash).
    // FAILURE: Any DDL/seed/constraint error rolls the migration back as one transaction.
    // TEST: A v5 backup migrates/reopens with 76 reviews and 76/17/17/5 original rows unchanged.
    private void MigrateToV6()
    {
        db.Execute("""
            CREATE TABLE case_assessments(
                project TEXT NOT NULL,case_id TEXT NOT NULL,case_hash TEXT NOT NULL,payload TEXT NOT NULL,
                PRIMARY KEY(project,case_id), FOREIGN KEY(project,case_id) REFERENCES cases(project,id))
            """);
        db.Execute("""
            CREATE TABLE manual_runs(
                seq INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT NOT NULL, id TEXT NOT NULL,
                case_id TEXT NOT NULL, tester TEXT NOT NULL CHECK(length(trim(tester))>0), executed_at TEXT NOT NULL,
                result TEXT NOT NULL CHECK(result IN ('PASS','FAIL','ERROR','BLOCKED','SKIPPED')),
                actual_result TEXT NOT NULL, notes TEXT NOT NULL, attachment_path TEXT, attachment_sha256 TEXT,
                UNIQUE(project,id), FOREIGN KEY(project,case_id) REFERENCES cases(project,id),
                CHECK(result NOT IN ('PASS','FAIL') OR length(trim(actual_result))>0),
                CHECK(length(trim(actual_result))>0 OR length(trim(notes))>0),
                CHECK((attachment_path IS NULL)=(attachment_sha256 IS NULL)))
            """);
        db.Execute("CREATE INDEX manual_runs_case ON manual_runs(project,case_id,seq)");
        db.Execute("CREATE TRIGGER manual_runs_no_update BEFORE UPDATE ON manual_runs BEGIN SELECT RAISE(ABORT,'Immutable manual run'); END");
        db.Execute("CREATE TRIGGER manual_runs_no_delete BEFORE DELETE ON manual_runs BEGIN SELECT RAISE(ABORT,'Immutable manual run'); END");
        using var resource = typeof(FoundationStore).Assembly.GetManifestResourceStream("QAStudio.SuitabilitySeed.tsv")
            ?? throw new InvalidDataException("Automation suitability seed is missing.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var original = db.Query("SELECT project,id,payload FROM cases")
            .ToDictionary(row => (row[0]!, row[1]!), row => PayloadHash(row[2]!));
        while (reader.ReadLine() is { } line)
        {
            string[] field = line.Split('\t');
            if (field.Length != 9 || !Enum.TryParse<SuitabilityStatus>(field[3], out var status) || !Enum.IsDefined(status))
                throw new InvalidDataException("Invalid automation suitability seed row.");
            if (original.GetValueOrDefault((field[0], field[1])) != field[2]) continue;
            var review = new CaseAssessment(field[0], field[1], status, field[4], field[5], field[6], field[7], field[8]);
            db.Execute("INSERT INTO case_assessments(project,case_id,case_hash,payload) VALUES(?,?,?,?)",
                review.ProjectId, review.CaseId, field[2], JsonSerializer.Serialize(review));
        }
        db.Execute("PRAGMA user_version=6");
    }

    private static string PayloadHash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
