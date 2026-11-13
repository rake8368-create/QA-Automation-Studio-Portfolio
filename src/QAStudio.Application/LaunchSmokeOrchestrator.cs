using System.Text.Json;
using QAStudio.Domain;

namespace QAStudio.Application;

// WHAT: Run one explicitly bound startup smoke and persist its observed terminal result.
// WHY: Imported prose and previous executions are never execution evidence.
// CONTRACT: Existing case remains unchanged; only this binding can execute; result/evidence append atomically.
// FAILURE: Adapter launch errors are ERROR, unmet window assertions are FAIL, persistence failure propagates.
// TEST: Real GUI launch -> native PID/window evidence -> SQLite; isolated ERROR/FAIL storage checks.
public sealed record LaunchSmokeRequest(string Executable, string TitleFragment, string EvidenceDirectory,
    int TimeoutMilliseconds = 20000, IReadOnlyList<string>? Arguments = null, int? ExistingProcessId = null, DateTime? ExpectedStartTimeUtc = null);
public sealed record LaunchObservation(ExecutionResult Result, string Assertion, string EvidencePath, string EvidenceSha256);
public interface IProcessWindowAdapter
{
    Task<LaunchObservation> ExecuteAsync(string runId, LaunchSmokeRequest request);
}
public interface IExecutionStore
{
    void RecordRun(TestRun run, Evidence evidence);
}
public sealed class LaunchSmokeOrchestrator(IProcessWindowAdapter adapter, IExecutionStore store)
{
    public const string ProjectId = "macro-studio";
    public const string CaseId = "IMP-GUI-20261028-001";
    public static bool Supports(TestCase tc) => tc.ProjectId == ProjectId && tc.Id == CaseId;
    public async Task<TestRun> ExecuteAsync(TestCase tc, LaunchSmokeRequest request)
    {
        if (!Supports(tc)) throw new InvalidOperationException("이 TC에는 실행 어댑터가 연결되지 않았습니다.");
        string id = "LAUNCH-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        var observed = await adapter.ExecuteAsync(id, request).ConfigureAwait(false);
        if (observed.Result is not (ExecutionResult.Pass or ExecutionResult.Fail or ExecutionResult.Error))
            throw new InvalidDataException("Unexpected launch result");
        var run = new TestRun(tc.ProjectId, id, tc.Id, observed.Result, observed.Assertion,
            observed.EvidencePath, DateTimeOffset.UtcNow, $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; x64",
            typeof(LaunchSmokeOrchestrator).Assembly.GetName().Version?.ToString() ?? "unknown",
            JsonSerializer.Serialize(new { executable = request.Executable, arguments = request.Arguments ?? [], mode = request.ExistingProcessId.HasValue ? "attach" : "launch", request.ExistingProcessId, request.ExpectedStartTimeUtc }),
            JsonSerializer.Serialize(new { originalCase = tc, executionScope = "Selected MacroStudio process and visible main window only", request.TitleFragment }));
        store.RecordRun(run, new(tc.ProjectId, "EV-" + id, id, observed.EvidencePath, observed.EvidenceSha256));
        return run;
    }
}
