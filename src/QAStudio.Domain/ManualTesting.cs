namespace QAStudio.Domain;

// WHAT: A review describes automation suitability independently of execution results.
// WHY: Existing mapping is an implementation fact, while candidate/review is a recommendation.
// CONTRACT: Unreviewed cases stay REVIEW and a removed binding never retains IMPLEMENTED.
// FAILURE: Missing review or binding data does not produce an execution result.
// TEST: Project-scoped seed and mapping removal are verified on a migrated copy.
public enum SuitabilityStatus { Implemented, Candidate, Manual, Blocked, Review, Retired }
public sealed record CaseAssessment(string ProjectId, string CaseId, SuitabilityStatus Status, string Observation,
    string Environment, string Stability, string MaintenanceCost, string Rationale)
{
    public string StatusText => Status.ToString().ToUpperInvariant();

    public static CaseAssessment Resolve(TestCase tc, CaseAssessment? review, string? mapping)
    {
        var assessment = review ?? new(tc.ProjectId, tc.Id, SuitabilityStatus.Review, "", "", "", "",
            "자동화 적합성 검토 근거가 아직 없습니다.");
        if (mapping is not null)
            return assessment.Status == SuitabilityStatus.Implemented ? assessment : assessment with
            {
                Status = SuitabilityStatus.Implemented,
                Rationale = "현재 DB에서 명시적 자동화 연결을 확인했습니다."
            };
        return assessment.Status == SuitabilityStatus.Implemented
            ? assessment with { Status = SuitabilityStatus.Review, Rationale = "현재 명시적 연결이 없습니다. 실행 경로를 다시 검토해야 합니다." }
            : assessment;
    }
}

public enum ManualReadinessStatus { GuiManual, EnvironmentRequired, HarnessRequired, ReviewRequired }

public sealed record ManualReadiness(ManualReadinessStatus Status, string Reason)
{
    public string StatusText => Status switch
    {
        ManualReadinessStatus.GuiManual => "GUI_MANUAL",
        ManualReadinessStatus.EnvironmentRequired => "ENVIRONMENT_REQUIRED",
        ManualReadinessStatus.HarnessRequired => "HARNESS_REQUIRED",
        _ => "REVIEW_REQUIRED"
    };
}

public enum ManualResult { Pass, Fail, Error, Blocked, Skipped }
public enum ExecutionMode { Automated, Manual }
public sealed record ManualRun(string ProjectId, string Id, string TestCaseId, string Tester, DateTimeOffset ExecutedAt,
    ManualResult Result, string ActualResult, string Notes, string? AttachmentPath, string? AttachmentSha256)
{
    public string ResultText => Result.ToString().ToUpperInvariant();

    public void Validate()
    {
        if (!Enum.IsDefined(Result) || ExecutedAt == default || ExecutedAt > DateTimeOffset.UtcNow ||
            new[] { ProjectId, Id, TestCaseId, Tester }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("프로젝트, TC, 실행자, 현재 또는 과거 실행 시각 및 유효한 결과가 필요합니다.");
        if (Tester.Length > 120 || ActualResult.Length > 4000 || Notes.Length > 4000)
            throw new InvalidDataException("실행자는 120자, 실제 결과와 메모는 각각 4,000자까지 입력할 수 있습니다.");
        if (Result is ManualResult.Pass or ManualResult.Fail && string.IsNullOrWhiteSpace(ActualResult))
            throw new InvalidDataException("PASS 또는 FAIL에는 관찰한 실제 결과가 필요합니다.");
        if (string.IsNullOrWhiteSpace(ActualResult) && string.IsNullOrWhiteSpace(Notes))
            throw new InvalidDataException("실제 결과 또는 실행이 제한된 사유를 기록하세요.");
        if ((AttachmentPath is null) != (AttachmentSha256 is null) ||
            AttachmentPath is not null && (string.IsNullOrWhiteSpace(AttachmentPath) ||
                AttachmentSha256!.Length != 64 || !AttachmentSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("첨부 증적 경로와 SHA-256이 함께 필요합니다.");
    }
}

// Compare the latest entry from each existing append-only stream. Manual wins an exact timestamp tie.
public sealed record CaseExecution(string Id, ExecutionMode Mode, string Result, DateTimeOffset ExecutedAt,
    string? Assertion, string? ActualResult)
{
    public string ModeText => Mode.ToString().ToUpperInvariant();

    public static CaseExecution? Latest(TestRun? automatic, ManualRun? manual)
    {
        if (manual is not null && (automatic is null || manual.ExecutedAt >= automatic.ExecutedAt))
            return new(manual.Id, ExecutionMode.Manual, manual.ResultText, manual.ExecutedAt, null, manual.ActualResult);
        return automatic is null ? null : new(automatic.Id, ExecutionMode.Automated, ResultText.Format(automatic.Result),
            automatic.ExecutedAt, automatic.Assertion, null);
    }
}
