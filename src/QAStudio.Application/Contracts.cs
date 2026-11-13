using QAStudio.Domain;

namespace QAStudio.Application;

// WHAT: Preview -> validate -> atomic commit -> reload/export is the Phase 1 application flow.
// WHY: The future desktop UI must inspect failures without writing a partially imported workbook.
// CONTRACT: Explicit caller-owned header/project mappings; original fields and source bytes survive.
// FAILURE: Issues include sheet/row/field and block the entire commit; no auto-corrected priorities or PASS.
// TEST: Invalid mapping, duplicate/blank/priority, exact 70-row roundtrip and source-hash tests.
public sealed record ImportOptions(IReadOnlyDictionary<string, string> CaseProjects,
    IReadOnlyDictionary<string, string>? HeaderMap = null, bool TestCasesOnly = false);
public sealed record ImportIssue(string Sheet, int Row, string Field, string Message);
public sealed record ImportPreview(byte[] Source, string SourceSha256, IReadOnlyList<TestCase> Cases,
    IReadOnlyList<AutomationDefinition> Automations, IReadOnlyList<ImportIssue> Issues, IReadOnlyList<int>? CaseRows = null,
    bool IsProjectScopedMaster = false)
{
    public bool IsValid => Issues.Count == 0;
}
public interface IWorkbookAdapter
{
    ImportPreview Preview(byte[] source, ImportOptions options);
    byte[] Export(byte[] template, IReadOnlyList<TestCase> cases, IReadOnlyList<AutomationDefinition> automations,
        IReadOnlyDictionary<string, string>? headerMap = null);
}
public interface IFoundationStore
{
    void Commit(string batchId, ImportPreview preview);
    IReadOnlyList<TestCase> Cases(string projectId);
    IReadOnlyList<AutomationDefinition> Automations(string projectId);
    byte[] Source(string batchId);
}
public sealed class ImportService(IWorkbookAdapter workbook, IFoundationStore store)
{
    public ImportPreview Preview(byte[] source, ImportOptions options) => workbook.Preview(source, options);
    public ImportPreview Preview(string path, ImportOptions options) => workbook.Preview(File.ReadAllBytes(path), options);
    public ImportPreview Commit(string batchId, byte[] source, ImportOptions options)
    {
        var preview = workbook.Preview(source, options);
        if (!preview.IsValid) throw new InvalidDataException(string.Join("; ", preview.Issues));
        store.Commit(batchId, preview);
        return preview;
    }
    public byte[] Export(string batchId, IEnumerable<string> projectIds, IReadOnlyDictionary<string, string>? headerMap = null) => workbook.Export(store.Source(batchId),
        projectIds.Distinct().SelectMany(store.Cases).ToArray(),
        projectIds.Distinct().SelectMany(store.Automations).ToArray(), headerMap);
}
