namespace QAStudio.Domain;

// WHAT: Product-neutral entities and execution contracts, owned by the Domain layer.
// WHY: Imported prose, approved automation, execution facts and AI suggestions have different trust levels.
// CONTRACT: No target-specific prefix, browser, storage or input API; NOT RUN until an evidenced execution.
// FAILURE: Illegal lifecycle transitions and unsupported results throw before persistence.
// TEST: Domain status, lifecycle, assertion and project-isolation tests in QAStudio.Tests.
public enum ExecutionResult { NotRun, Pass, Fail, Error, Blocked, Cancelled }
public enum AutomationState { Draft, NeedsMapping, Ready, Executing, Finished }
public static class AutomationStateText
{
    public static string Format(AutomationState state) => state switch
    {
        AutomationState.Draft => "DRAFT", AutomationState.NeedsMapping => "NEEDS_MAPPING",
        AutomationState.Ready => "READY", AutomationState.Executing => "EXECUTING", AutomationState.Finished => "FINISHED",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
public sealed record Project(string Id, string Name);
public sealed record TestPlan(string ProjectId, string Id, string Name, IReadOnlyList<string> CaseIds);
public sealed record TestCase(string ProjectId, string Id, IReadOnlyDictionary<string, string> Fields)
{
    public ExecutionResult Result => ExecutionResult.NotRun;
}
public sealed record AutomationDefinition(string ProjectId, string Id, string TestCaseId,
    IReadOnlyDictionary<string, string> Fields, AutomationState State = AutomationState.NeedsMapping,
    string Binding = "", bool Approved = false)
{
    public AutomationDefinition Transition(AutomationState next)
    {
        bool allowed = (State, next) switch
        {
            (AutomationState.Draft, AutomationState.NeedsMapping) => true,
            (AutomationState.NeedsMapping, AutomationState.Ready) => Approved &&
                !string.IsNullOrWhiteSpace(Binding) && Fields.TryGetValue("ASSERT / 기대조건", out var assertion) &&
                !string.IsNullOrWhiteSpace(assertion) && Fields.TryGetValue("TIMEOUT", out var timeout) &&
                int.TryParse(timeout.Replace("ms", "", StringComparison.Ordinal).Trim(), out var ms) && ms > 0,
            (AutomationState.Ready, AutomationState.Executing) => true,
            (AutomationState.Executing, AutomationState.Finished) => true,
            (AutomationState.Finished, AutomationState.Ready) => Approved && !string.IsNullOrWhiteSpace(Binding),
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Illegal automation transition: {State} -> {next}");
        return this with { State = next };
    }
}
public sealed record TestRun(string ProjectId, string Id, string TestCaseId, ExecutionResult Result,
    string Assertion, string Evidence, DateTimeOffset ExecutedAt, string Environment, string BuildVersion,
    string Command, string CaseSnapshot, string? PreviousRunId = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Result) || Result == ExecutionResult.NotRun)
            throw new InvalidDataException("An execution must have an actual terminal result.");
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Environment) ||
            string.IsNullOrWhiteSpace(BuildVersion) || string.IsNullOrWhiteSpace(Command) ||
            string.IsNullOrWhiteSpace(CaseSnapshot) || ExecutedAt == default)
            throw new InvalidDataException("Run provenance is required.");
        if (Result is ExecutionResult.Pass or ExecutionResult.Fail &&
            (string.IsNullOrWhiteSpace(Assertion) || string.IsNullOrWhiteSpace(Evidence)))
            throw new InvalidDataException("PASS/FAIL requires explicit assertion and execution evidence.");
    }
}
public sealed record Evidence(string ProjectId, string Id, string RunId, string Path, string Sha256);
public sealed record Defect(string ProjectId, string Id, string RunId, string Description, string? RetestRunId);
public sealed record AIProposal(string ProjectId, string Id, string TestCaseId, string Source,
    string Model, string PromptVersion, string ReviewStatus, string Reviewer, string Before, string After,
    string Evidence, string Uncertainty, double? Confidence);
public static class ResultText
{
    public static string Format(ExecutionResult result) => result switch
    {
        ExecutionResult.NotRun => "NOT RUN", ExecutionResult.Pass => "PASS", ExecutionResult.Fail => "FAIL",
        ExecutionResult.Error => "ERROR", ExecutionResult.Blocked => "BLOCKED", ExecutionResult.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };
    public static bool IsFlaky(IEnumerable<TestRun> attempts) =>
        attempts.Select(x => x.Result).Contains(ExecutionResult.Pass) && attempts.Any(x => x.Result == ExecutionResult.Fail);
}
