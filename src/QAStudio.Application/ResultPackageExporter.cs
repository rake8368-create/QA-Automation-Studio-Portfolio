using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Domain;

namespace QAStudio.Application;

public sealed record ResultPackageProject(IReadOnlyList<TestCase> Cases,
    IReadOnlyDictionary<string, string> Mappings, IReadOnlyList<TestRun> Runs, IReadOnlyList<Evidence> Evidence,
    IReadOnlyDictionary<string, CaseAssessment> Assessments, IReadOnlyList<ManualRun> ManualRuns,
    IReadOnlyDictionary<string, ManualReadiness>? Readiness = null)
{
    public CaseAssessment AssessmentFor(TestCase tc) =>
        CaseAssessment.Resolve(tc, Assessments.GetValueOrDefault(tc.Id), Mappings.GetValueOrDefault(tc.Id));

    public ManualReadiness? ReadinessFor(TestCase tc) => Mappings.ContainsKey(tc.Id) ? null
        : Readiness?.GetValueOrDefault(tc.Id) ?? new(ManualReadinessStatus.ReviewRequired, "현재 TC 원본과 일치하는 수동 실행 검토 기록이 없습니다.");
}

public sealed record ResultPackageCase(TestCase Case, string? Mapping, IReadOnlyList<ResultPackageRun> Runs,
    CaseAssessment Assessment, IReadOnlyList<ResultPackageManualRun> ManualRuns, ManualReadiness? Readiness = null)
{
    public string Runnable => Mapping is not null || LaunchSmokeOrchestrator.Supports(Case) ? "예" : "아니요";
    public CaseExecution? Latest => CaseExecution.Latest(Runs.LastOrDefault()?.Run, ManualRuns.LastOrDefault()?.Run);
}
public sealed record ResultPackageRun(TestRun Run, string EvidenceFolder, bool EvidenceIssue);
public sealed record ResultPackageManualRun(ManualRun Run, string EvidenceFolder, bool EvidenceIssue);
public interface IResultPackageWorkbook
{
    byte[] ExportMaster(IReadOnlyList<ResultPackageCase> cases);
    byte[] ExportResults(IReadOnlyList<ResultPackageCase> cases);
}

// WHAT: Publish one complete package from the caller's registered SQLite snapshot.
// WHY: TC review, run history and source evidence need a shared project/TC/RunID identity.
// CONTRACT: New output folders only. Copy recorded evidence and identified images; verify SHA-256.
// FAILURE: Missing or inconsistent source evidence is recorded per run. A failed export publishes no package.
// TEST: Repeat export, operating DB/evidence hashes, missing source, image association and output failure.
public sealed class ResultPackageExporter(IResultPackageWorkbook workbook)
{
    public const string MasterFile = "01_TC_Master/QAStudio_TC_Master.xlsx";
    public const string EvidenceDirectory = "02_Execution_Evidence";
    public const string ResultsFile = "03_Test_Results/QAStudio_Test_Results.xlsx";
    public const string EvidenceIndex = "evidence-index.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff" };
    private static readonly HashSet<string> ImageProperties = new(StringComparer.OrdinalIgnoreCase)
        { "screenshot", "screenshotPath", "screenshots", "screenshotPaths", "image", "imagePath", "images", "imagePaths" };

    public sealed record Receipt(string Directory, int Cases, int Runs, int EvidenceIssues, int ManualRuns);
    private sealed record EvidenceCopy(string Source, string? Copy, string? RecordedSha256,
        string? SourceSha256, string? CopySha256, string Status);

    public Receipt Export(string outputDirectory, string databasePath, IReadOnlyList<ResultPackageProject> snapshot)
    {
        string parent = Path.GetFullPath(outputDirectory);
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        // Keep room for the original TC ID, run ID and source filename under the chosen directory.
        string name = $"QA-{DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..12]}";
        string destination = Path.Combine(parent, name);
        string staging = Path.Combine(parent, "." + name + ".partial");
        if (Directory.Exists(staging)) throw new IOException("임시 출력 폴더가 이미 존재합니다.");
        Directory.CreateDirectory(staging);
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, EvidenceDirectory));
            var caseFolders = FolderNames(snapshot.SelectMany(project => project.Cases)
                .Select(tc => (tc.ProjectId, tc.Id)));
            List<ResultPackageCase> cases = [];
            foreach (var project in snapshot)
            {
                var runsByCase = project.Runs.ToLookup(run => run.TestCaseId, StringComparer.Ordinal);
                var manualByCase = project.ManualRuns.ToLookup(run => run.TestCaseId, StringComparer.Ordinal);
                var evidenceByRun = project.Evidence.ToLookup(evidence => evidence.RunId, StringComparer.Ordinal);
                foreach (var tc in project.Cases)
                {
                    string caseFolder = caseFolders[(tc.ProjectId, tc.Id)];
                    var runs = runsByCase[tc.Id].ToArray();
                    var manualRuns = manualByCase[tc.Id].ToArray();
                    var runFolders = FolderNames(runs.Select(run => ("AUTOMATED/" + run.ProjectId, run.Id))
                        .Concat(manualRuns.Select(run => ("MANUAL/" + run.ProjectId, run.Id))));
                    var packaged = runs.Select(run => CopyEvidence(staging, sourceDirectory, caseFolder,
                        runFolders[("AUTOMATED/" + run.ProjectId, run.Id)], run, evidenceByRun[run.Id])).ToArray();
                    var packagedManual = manualRuns.Select(run => CopyManualEvidence(staging, sourceDirectory, caseFolder,
                        runFolders[("MANUAL/" + run.ProjectId, run.Id)], run)).ToArray();
                    cases.Add(new(tc, project.Mappings.GetValueOrDefault(tc.Id), packaged, project.AssessmentFor(tc), packagedManual, project.ReadinessFor(tc)));
                }
            }
            WriteWorkbook(staging, MasterFile, workbook.ExportMaster(cases));
            WriteWorkbook(staging, ResultsFile, workbook.ExportResults(cases));
            Directory.Move(staging, destination);
            int manual = cases.Sum(tc => tc.ManualRuns.Count);
            return new(destination, cases.Count, cases.Sum(tc => tc.Runs.Count) + manual,
                cases.Sum(tc => tc.Runs.Count(run => run.EvidenceIssue) + tc.ManualRuns.Count(run => run.EvidenceIssue)), manual);
        }
        catch
        {
            // This absolute path is constructed as one new child of the chosen output directory.
            if (string.Equals(Path.GetDirectoryName(staging), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static void WriteWorkbook(string root, string relativePath, byte[] bytes)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static ResultPackageRun CopyEvidence(string root, string sourceDirectory, string caseFolder,
        string runFolder, TestRun run, IEnumerable<Evidence> recorded)
    {
        string relativeFolder = $"{EvidenceDirectory}/{caseFolder}/{runFolder}";
        string folder = Path.Combine(root, relativeFolder);
        Directory.CreateDirectory(folder);
        var copies = new List<EvidenceCopy>();
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EvidenceIndex };
        var unreadableJson = new List<string>();
        string Resolve(string source, string directory) => Path.GetFullPath(source, directory);

        void Copy(string source, string? sha)
        {
            string fileName = Path.GetFileName(source);
            string name = fileName;
            for (int index = 2; !names.Add(name); index++) name = $"{index}-{fileName}";
            copies.Add(CopyFile(source, folder, name, sha));
        }

        foreach (var evidence in recorded)
        {
            if (string.IsNullOrWhiteSpace(evidence.Path))
            {
                copies.Add(new(evidence.Path, null, evidence.Sha256, null, null, "Missing"));
                continue;
            }
            string source = Resolve(evidence.Path, sourceDirectory);
            tracked.Add(source);
            Copy(source, evidence.Sha256);
        }
        if (!string.IsNullOrWhiteSpace(run.Evidence))
        {
            string source = Resolve(run.Evidence, sourceDirectory);
            if (tracked.Add(source)) Copy(source, null);
        }
        foreach (var item in copies.Where(c => c.Copy is not null &&
                     Path.GetExtension(c.Source).Equals(".json", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            string directory = Path.GetDirectoryName(item.Source)!;
            string stem = Path.GetFileNameWithoutExtension(item.Source);
            var sidecars = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path))
                    && Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase));
            var imagePaths = new List<string>(sidecars.Order(StringComparer.OrdinalIgnoreCase));
            try
            {
                using var jsonStream = File.OpenRead(Path.Combine(folder, item.Copy!));
                using var json = JsonDocument.Parse(jsonStream);
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                    imagePaths.AddRange(json.RootElement.EnumerateObject().Where(p => ImageProperties.Contains(p.Name))
                        .SelectMany(p => ImageReferences(p.Value)).Select(path => Resolve(path, directory)));
            }
            catch (JsonException) { unreadableJson.Add(item.Source); }
            foreach (string imagePath in imagePaths)
                if (tracked.Add(imagePath)) Copy(imagePath, null);
        }
        bool issue = copies.Count == 0 || copies.Any(copy => copy.Status != "Copied") || unreadableJson.Count != 0;
        string imageStatus = copies.Any(copy => copy.Copy is not null && ImageExtensions.Contains(Path.GetExtension(copy.Source)))
            ? "Image files included" : "No image file included";
        File.WriteAllBytes(Path.Combine(folder, EvidenceIndex), JsonSerializer.SerializeToUtf8Bytes(new
        {
            run.ProjectId, tcId = run.TestCaseId, runId = run.Id, result = ResultText.Format(run.Result),
            evidenceStatus = issue ? "Review evidence list" : "Verified",
            imageStatus, unreadableJson, files = copies
        }, JsonOptions));
        return new(run, relativeFolder, issue);
    }

    private static ResultPackageManualRun CopyManualEvidence(string root, string sourceDirectory, string caseFolder,
        string runFolder, ManualRun run)
    {
        string relative = $"{EvidenceDirectory}/{caseFolder}/{runFolder}";
        string folder = Path.Combine(root, relative);
        Directory.CreateDirectory(folder);
        const string recordFile = "manual-record.json";
        EvidenceCopy? attachment = null;
        if (run.AttachmentPath is { } path)
        {
            string source = Path.GetFullPath(path, sourceDirectory);
            string name = Path.GetFileName(source);
            if (Path.Combine(folder, name).Length > 250 || name.Equals(recordFile, StringComparison.OrdinalIgnoreCase)
                || name.Equals(EvidenceIndex, StringComparison.OrdinalIgnoreCase))
                name = "attachment" + Path.GetExtension(name);
            attachment = CopyFile(source, folder, name, run.AttachmentSha256);
        }
        bool issue = attachment is not null && attachment.Status != "Copied";
        File.WriteAllBytes(Path.Combine(folder, recordFile), JsonSerializer.SerializeToUtf8Bytes(new
        {
            run.ProjectId, tcId = run.TestCaseId, runId = run.Id, executionMode = "MANUAL",
            run.Tester, run.ExecutedAt, result = run.ResultText, run.ActualResult, run.Notes
        }, JsonOptions));
        File.WriteAllBytes(Path.Combine(folder, EvidenceIndex), JsonSerializer.SerializeToUtf8Bytes(new
        {
            run.ProjectId, tcId = run.TestCaseId, runId = run.Id, executionMode = "MANUAL", result = run.ResultText,
            evidenceStatus = issue ? "Review evidence list" : "Verified",
            observationRecord = recordFile,
            attachmentStatus = attachment?.Status ?? "No attachment",
            files = attachment is null ? Array.Empty<EvidenceCopy>() : new[] { attachment }
        }, JsonOptions));
        return new(run, relative, issue);
    }

    private static IEnumerable<string> ImageReferences(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().SelectMany(ImageReferences);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("path", out var path))
            return ImageReferences(path);
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            ImageExtensions.Contains(Path.GetExtension(text)) && text.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            return [text];
        return [];
    }

    private static EvidenceCopy CopyFile(string source, string folder, string name, string? recordedSha)
    {
        FileStream input;
        try { input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new(source, null, recordedSha, null, null, "Missing"); }
        catch (DirectoryNotFoundException) { return new(source, null, recordedSha, null, null, "Missing"); }
        using (input)
        {
            string output = Path.Combine(folder, name);
            string sourceSha = Convert.ToHexString(SHA256.HashData(input));
            input.Position = 0;
            using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(target);
            using var check = File.OpenRead(output);
            string copySha = Convert.ToHexString(SHA256.HashData(check));
            if (sourceSha != copySha) throw new IOException($"증적 복사 SHA-256 불일치: {source}");
            string status = !string.IsNullOrEmpty(recordedSha) && !sourceSha.Equals(recordedSha, StringComparison.OrdinalIgnoreCase)
                ? "RecordedHashMismatch" : "Copied";
            return new(source, name, recordedSha, sourceSha, copySha, status);
        }
    }

    private static Dictionary<(string ProjectId, string Id), string> FolderNames(IEnumerable<(string ProjectId, string Id)> ids)
    {
        return ids.GroupBy(key => key.Id, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select(key => (Key: key, Name: SafeFolder(key, group.Count() > 1))))
            .ToDictionary(item => item.Key, item => item.Name);
    }

    private static string SafeFolder((string ProjectId, string Id) key, bool ambiguous)
    {
        string id = key.Id;
        if (!ambiguous && id.Length is > 0 and <= 100 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            !id.Contains('~') && !id.EndsWith('.') && !id.EndsWith(' ') &&
            !System.Text.RegularExpressions.Regex.IsMatch(id.Split('.')[0].TrimEnd(),
                @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return id;
        byte[] scope = JsonSerializer.SerializeToUtf8Bytes(new[] { key.ProjectId, id });
        return "~" + Convert.ToHexString(SHA256.HashData(scope));
    }
}
