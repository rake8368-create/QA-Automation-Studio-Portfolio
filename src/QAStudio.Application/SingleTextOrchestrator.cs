using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Domain;

namespace QAStudio.Application;

// WHAT: Arm an empty document, invoke a supplied automatic trigger, and reuse exact text assertions.
// WHY: A live, empty baseline and repeated exact reads are required before a fresh PASS.
// CONTRACT: Only the trigger adapter dispatches input; a successful API call is never an assertion.
// FAILURE: Missing/unreliable/focus-lost observations are ERROR; evidence/save faults propagate.
// TEST: Scripted no-output, mismatch, duplicate, late repeat, lost observation and persistence cases.
public sealed record TextSample(DateTimeOffset At, string Text, string Reader, string DocumentIdentity);
public interface ISingleTextProbe : IDisposable
{
    TextSample Arm(CancellationToken cancellationToken);
    TextSample Read();
}
public interface ITextTrigger
{
    void Fire(CancellationToken cancellationToken, Action<string> log);
    object Diagnostics { get; }
}
// WHAT/WHY: Explicit negative mode requires a live OFF-state verifier, not an empty exact-text match.
// CONTRACT: The desktop probe validates the designated QA process/profile and OFF without changing it.
// FAILURE: Missing/failed OFF verification is ERROR before any successful no-output verdict.
// TEST: Day7 isolated wrong-state, missing-verifier and successful-trigger/no-output checks.
public interface IMacroOffTextProbe : ISingleTextProbe
{
    void VerifyMacroOff();
}
// WHAT/WHY: Target restriction is a separate ON-state precondition for the same absence observer.
// CONTRACT: Verify live restriction, safe configured process and focused Notepad mismatch.
// FAILURE: OFF, disabled restriction, absent target or matched foreground must be ERROR.
// TEST: Day8 fake mismatch-state checks and actual configured-target/foreground evidence.
public interface ITargetMismatchTextProbe : ISingleTextProbe
{
    void VerifyTargetMismatch();
}
public enum TextExpectation { ExactText, NoOutputWhileMacroOff, NoOutputWhileTargetMismatch, TwoSingleFTriggers }
public sealed record SingleTextRequest(string ProjectId, string ScenarioId, string Expected,
    string Trigger, string EvidenceDirectory, string TargetDescription,
    int OutputTimeoutMilliseconds = 10000, int StabilizationMilliseconds = 2000, int PollMilliseconds = 100,
    string? LinkedTestCaseId = null, TextExpectation Expectation = TextExpectation.ExactText);
public sealed record TextExecution(TestRun Run, Evidence Evidence);
public sealed class SingleTextOrchestrator
{
    public Task<TextExecution> ExecuteAsync(ISingleTextProbe probe, SingleTextRequest request,
        Action<string>? progress = null, CancellationToken cancellationToken = default,
        IExecutionStore? store = null, TestCase? registeredCase = null, ITextTrigger? trigger = null) => Task.Run(() =>
    {
        bool targetMismatch = request.Expectation == TextExpectation.NoOutputWhileTargetMismatch;
        bool twoTriggers = request.Expectation == TextExpectation.TwoSingleFTriggers;
        bool noOutput = request.Expectation is TextExpectation.NoOutputWhileMacroOff or TextExpectation.NoOutputWhileTargetMismatch;
        string absenceCondition = targetMismatch ? "MacroStudio ON / target restriction / foreground mismatch verified" : "MacroStudio OFF verified";
        if (!Enum.IsDefined(request.Expectation) || (noOutput ? request.Expected != "" || request.ScenarioId != (targetMismatch ? "MAC-NP-004" : "MAC-NP-003") || request.Trigger != "F3" ||
                request.StabilizationMilliseconds < 2000 : string.IsNullOrEmpty(request.Expected)) || request.OutputTimeoutMilliseconds < 100 ||
            request.OutputTimeoutMilliseconds > 60000 || request.StabilizationMilliseconds < 100 ||
            request.StabilizationMilliseconds > 10000 || request.PollMilliseconds < 10 || request.PollMilliseconds > 500)
            throw new ArgumentException("Invalid observation timing/expected text");
        if (twoTriggers && (request.ScenarioId != "MAC-NP-005" || request.Expected != "ff" || request.Trigger != "F3" || request.StabilizationMilliseconds < 2000))
            throw new ArgumentException("MAC-NP-005 requires two observed single-F triggers and >=2s stabilization");
        if (request.LinkedTestCaseId is not null && (store is null || registeredCase is null))
            throw new ArgumentException("Linked execution requires its registered TC and execution store");
        if (noOutput && (request.LinkedTestCaseId is null || store is null || registeredCase is null))
            throw new ArgumentException("무출력 검증은 연결된 TC와 실행 이력 저장소가 필요합니다.");
        if (twoTriggers && (request.LinkedTestCaseId is null || store is null || registeredCase is null))
            throw new ArgumentException("두 F3 검증은 연결된 TC와 실행 이력 저장소가 필요합니다.");
        if (store is not null && (registeredCase?.Id != (request.LinkedTestCaseId ?? request.ScenarioId) || registeredCase.ProjectId != request.ProjectId))
            throw new ArgumentException("Persistence requires this scenario's own registered TC");
        var started = DateTimeOffset.UtcNow;
        string id = "TEXT-" + started.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetFullPath(request.EvidenceDirectory), id + ".json");
        var samples = new List<TextSample>();
        var logs = new List<string>();
        string reason = "", actual = "<not observed>";
        ExecutionResult result = ExecutionResult.Error;
        bool triggerAttempted = false, triggerCompleted = false;
        long observedNoOutputMilliseconds = 0;
        int triggerAttempts = 0, completedTriggers = 0;
        long stabilizedMilliseconds = 0;
        var stages = new List<object>();
        void Log(string message) { logs.Add($"{DateTimeOffset.UtcNow:O} {message}"); progress?.Invoke(message); }
        try
        {
            Log("자동 실행: 프로세스·설정·권한을 확인하고 안전한 빈 문서와 포커스를 준비합니다. 키보드에 손대지 마세요.");
            Action? verifyNoOutputPreconditions = null;
            if (request.Expectation == TextExpectation.NoOutputWhileMacroOff)
                verifyNoOutputPreconditions = (probe as IMacroOffTextProbe ?? throw new InvalidOperationException("OFF 전용 상태 검증 어댑터가 없습니다.")).VerifyMacroOff;
            if (targetMismatch)
                verifyNoOutputPreconditions = (probe as ITargetMismatchTextProbe ?? throw new InvalidOperationException("대상 불일치 전용 검증 어댑터가 없습니다.")).VerifyTargetMismatch;
            if (!targetMismatch) verifyNoOutputPreconditions?.Invoke();
            var baseline = probe.Arm(cancellationToken); samples.Add(baseline); actual = baseline.Text;
            if (baseline.Text != "") throw new InvalidOperationException("사전 문서가 비어 있지 않습니다. 문서는 변경하지 않았습니다.");
            if (string.IsNullOrWhiteSpace(baseline.Reader) || string.IsNullOrWhiteSpace(baseline.DocumentIdentity))
                throw new InvalidOperationException("신뢰할 수 있는 문서/읽기 방식 식별 정보가 없습니다.");
            if (trigger is null) throw new InvalidOperationException("자동 트리거 어댑터가 없습니다. 사용자 키 입력을 기다리지 않고 중단합니다.");
            var beforeTrigger = probe.Read(); samples.Add(beforeTrigger);
            if (beforeTrigger.Text != "" || beforeTrigger.DocumentIdentity != baseline.DocumentIdentity || beforeTrigger.Reader != baseline.Reader)
                throw new InvalidOperationException("트리거 직전 빈 문서/포커스 식별 상태가 바뀌었습니다.");
            cancellationToken.ThrowIfCancellationRequested();
            verifyNoOutputPreconditions?.Invoke();
            Log($"준비 완료 · {request.Trigger} 자동 트리거 1회 · 기대값 {JsonSerializer.Serialize(request.Expected)}");
            triggerAttempted = true;
            triggerAttempts++;
            if (twoTriggers) stages.Add(new { stage = 1, eventName = "trigger-attempt", at = DateTimeOffset.UtcNow });
            trigger.Fire(cancellationToken, Log);
            triggerCompleted = true;
            completedTriggers++;
            if (twoTriggers) stages.Add(new { stage = 1, eventName = "trigger-completed", at = DateTimeOffset.UtcNow });
            var timer = Stopwatch.StartNew();
            // WHAT: Only this dedicated scenario adds a second trigger after an actual exact f.
            // WHY: A final ff alone cannot prove that each separate trigger produced one character.
            // CONTRACT: Reuse the probe, baseline identity, F3 adapter and final exact assertion loop.
            // FAILURE: First-stage mismatch/timeout forbids trigger 2; untrusted reads remain ERROR.
            // TEST: Day11 staged counts, order, identity/focus faults and existing f/ff regressions.
            bool FirstFThenSecondTrigger()
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sample = probe.Read();
                    if (sample.DocumentIdentity != baseline.DocumentIdentity || sample.Reader != baseline.Reader)
                        throw new InvalidOperationException("관찰 중 문서 또는 읽기 방식이 바뀌었습니다.");
                    samples.Add(sample); actual = sample.Text;
                    if (sample.Text == "f")
                    {
                        stages.Add(new { stage = 1, eventName = "exact-f-observed", at = sample.At, sample });
                        Log("1단계 실제 관찰: \"f\" · 확인 후 두 번째 F3를 준비합니다.");
                        break;
                    }
                    if (sample.Text.Length != 0 || timer.ElapsedMilliseconds >= request.OutputTimeoutMilliseconds)
                    {
                        result = ExecutionResult.Fail; reason = "첫 번째 F3 후 정확한 f 불일치 또는 출력 시간 초과: " + JsonSerializer.Serialize(sample.Text);
                        return false;
                    }
                    cancellationToken.WaitHandle.WaitOne(request.PollMilliseconds);
                }
                var beforeSecond = probe.Read();
                if (beforeSecond.DocumentIdentity != baseline.DocumentIdentity || beforeSecond.Reader != baseline.Reader)
                    throw new InvalidOperationException("두 번째 F3 직전 문서 또는 읽기 방식이 바뀌었습니다.");
                samples.Add(beforeSecond); actual = beforeSecond.Text;
                if (actual != "f") { result = ExecutionResult.Fail; reason = "두 번째 F3 직전 첫 출력 f가 변경되었습니다: " + JsonSerializer.Serialize(actual); return false; }
                cancellationToken.ThrowIfCancellationRequested();
                triggerAttempts++;
                stages.Add(new { stage = 2, eventName = "trigger-attempt", at = DateTimeOffset.UtcNow });
                trigger.Fire(cancellationToken, Log);
                completedTriggers++;
                stages.Add(new { stage = 2, eventName = "trigger-completed", at = DateTimeOffset.UtcNow });
                timer.Restart();
                return true;
            }
            if (noOutput)
            {
                // WHAT: Observe absence only after a successful real trigger and empty baseline.
                // WHY: Silence before a trigger or a precondition error cannot establish PASS.
                // CONTRACT: >=2s between first/last post-trigger reads; exact document/reader retained.
                // FAILURE: Any character latches FAIL; OFF/focus/reader/environment faults are ERROR.
                // TEST: Empty PASS, transient/late character FAIL, switched document and send failure ERROR.
                long? firstRead = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sample = probe.Read();
                    if (sample.DocumentIdentity != baseline.DocumentIdentity || sample.Reader != baseline.Reader)
                        throw new InvalidOperationException("관찰 중 문서 또는 읽기 방식이 바뀌었습니다.");
                    samples.Add(sample); actual = sample.Text;
                    if (sample.Text.Length != 0)
                    {
                        result = ExecutionResult.Fail; reason = "무출력 관찰 중 실제 문자가 입력되었습니다: " + JsonSerializer.Serialize(sample.Text);
                        break;
                    }
                    long readAt = timer.ElapsedMilliseconds;
                    verifyNoOutputPreconditions!.Invoke();
                    firstRead ??= readAt;
                    observedNoOutputMilliseconds = readAt - firstRead.Value;
                    if (observedNoOutputMilliseconds >= request.StabilizationMilliseconds)
                    {
                        result = ExecutionResult.Pass;
                        reason = $"{absenceCondition}; F3 실제 전송 후 동일 문서/읽기 방식에서 {observedNoOutputMilliseconds}ms 동안 빈 문서를 관찰했습니다.";
                        break;
                    }
                    cancellationToken.WaitHandle.WaitOne(request.PollMilliseconds);
                }
            }
            else if (!twoTriggers || FirstFThenSecondTrigger())
            {
            long? firstOutput = null;
            bool mismatch = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = probe.Read();
                if (sample.DocumentIdentity != baseline.DocumentIdentity || sample.Reader != baseline.Reader)
                    throw new InvalidOperationException("관찰 중 문서 또는 읽기 방식이 바뀌었습니다.");
                samples.Add(sample); actual = sample.Text;
                if (twoTriggers && sample.Text is not ("f" or "ff")) { mismatch = true; firstOutput ??= timer.ElapsedMilliseconds; }
                // WHAT/WHY: A multi-character result can be observed while it is still being typed.
                // CONTRACT: Only an exact leading prefix may wait for completion within the output timeout.
                // FAILURE: Wrong/extra input latches FAIL; after completion every change still fails.
                // TEST: f -> ff, incomplete f, fff, wrong text, and late repetition with expected ff.
                bool pendingPrefix = !firstOutput.HasValue && sample.Text.Length > 0 && sample.Text != request.Expected &&
                    request.Expected.StartsWith(sample.Text, StringComparison.Ordinal);
                if (sample.Text.Length > 0 && !pendingPrefix)
                {
                    if (twoTriggers && !firstOutput.HasValue && sample.Text == "ff") stages.Add(new { stage = 2, eventName = "exact-ff-observed", at = sample.At, sample });
                    firstOutput ??= timer.ElapsedMilliseconds;
                }
                if (firstOutput.HasValue && !string.Equals(sample.Text, request.Expected, StringComparison.Ordinal)) mismatch = true;
                if (samples[^2].Text != sample.Text) Log("실제 관찰: " + JsonSerializer.Serialize(sample.Text));
                if (firstOutput.HasValue && timer.ElapsedMilliseconds - firstOutput.Value >= request.StabilizationMilliseconds)
                { stabilizedMilliseconds = timer.ElapsedMilliseconds - firstOutput.Value; break; }
                if (!firstOutput.HasValue && timer.ElapsedMilliseconds >= request.OutputTimeoutMilliseconds) break;
                cancellationToken.WaitHandle.WaitOne(request.PollMilliseconds);
            }
            result = firstOutput.HasValue && !mismatch && actual == request.Expected ? ExecutionResult.Pass : ExecutionResult.Fail;
            reason = !firstOutput.HasValue ? (actual.Length == 0
                ? "자동 트리거 호출 후 제한 시간 동안 실제 출력 문자가 없습니다. API 성공은 매크로 실행 성공이 아닙니다. 트리거·설정·권한 진단을 확인하세요."
                : "제한 시간 내 기대값 전체가 입력되지 않았습니다. 실제 관찰: " + JsonSerializer.Serialize(actual)) : mismatch
                ? "기대값 불일치, 중복 또는 안정화 중 추가/변경 입력을 관찰했습니다."
                : $"빈 문서 이후 정확히 {JsonSerializer.Serialize(request.Expected)}를 관찰했고 {request.StabilizationMilliseconds}ms 안정화 동안 유지했습니다.";
            }
        }
        catch (OperationCanceledException) { result = ExecutionResult.Cancelled; reason = "사용자가 관찰을 취소했습니다."; }
        catch (Exception ex) { result = ExecutionResult.Error; reason = ex.GetType().Name + ": " + ex.Message; }
        finally { probe.Dispose(); }
        var finished = DateTimeOffset.UtcNow;
        // Include the verdict in evidence, but publish it to the UI only after successful persistence.
        logs.Add($"{finished:O} {ResultText.Format(result)} · {reason}");
        string assertion = noOutput
            ? $"{request.ScenarioId}: {absenceCondition}; baseline == empty; successful F3 KEYDOWN/KEYUP; no output observed for >= {request.StabilizationMilliseconds}ms after trigger; same focused document/read method throughout"
            : $"{request.ScenarioId}: baseline == empty; actual == {JsonSerializer.Serialize(request.Expected)}; unchanged for >= {request.StabilizationMilliseconds}ms; same focused document/read method throughout";
        if (twoTriggers) assertion += "; first successful F3 -> exact f -> second successful F3 -> exact ff; single-F profile; two separate triggers";
        // Explicit mapping keeps the scenario assertion identity separate from its registered TC history.
        var run = new TestRun(request.ProjectId, id, request.LinkedTestCaseId ?? request.ScenarioId, result, assertion, path, finished,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            typeof(SingleTextOrchestrator).Assembly.GetName().Version?.ToString() ?? "unknown",
            JsonSerializer.Serialize(new { mode = "automatic", trigger = request.Trigger, triggerAttempted, triggerCompleted, request.TargetDescription }),
            JsonSerializer.Serialize(new { scenarioId = request.ScenarioId, expected = request.Expected, registeredCase,
                scope = "Automatic trigger attempt followed by actual Notepad text observation; never types the expected output" }));
        run.Validate();
        byte[] SerializeEvidence() => JsonSerializer.SerializeToUtf8Bytes(new { runId = id, scenarioId = request.ScenarioId,
            started, finished, mode = "automatic", result = ResultText.Format(result), expected = request.Expected,
            expectation = request.Expectation.ToString(), observedNoOutputMilliseconds, triggerAttempts, completedTriggers, stages, stabilizedMilliseconds,
            actual, reason, assertion, request, observations = samples, logs, run, triggerAttempted, triggerCompleted, triggerDiagnostics = trigger?.Diagnostics,
            sqliteHistory = store is null ? "Not stored: temporary scenario has no registered TC; existing 71 TCs preserved" : "Existing RecordRun transaction used after evidence flush",
            screenshot = "Not captured; raw UI Automation text observations are the primary evidence" }, new JsonSerializerOptions { WriteIndented = true });
        byte[] bytes = SerializeEvidence();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(true); }
        var evidence = new Evidence(request.ProjectId, "EV-" + id, id, path, Convert.ToHexString(SHA256.HashData(bytes)));
        try { store?.RecordRun(run, evidence); }
        catch (Exception ex) when (noOutput || twoTriggers)
        {
            // A negative PASS is not publishable when its history transaction fails.
            result = ExecutionResult.Error; reason = "SQLite 실행 이력 저장 실패: " + ex.Message;
            run = run with { Result = ExecutionResult.Error };
            File.WriteAllBytes(path, SerializeEvidence());
            throw new IOException(reason, ex);
        }
        return new TextExecution(run, evidence);
    });
}
