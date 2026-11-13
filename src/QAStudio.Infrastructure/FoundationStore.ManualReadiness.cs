using System.Text;
using QAStudio.Domain;

namespace QAStudio.Infrastructure;

public sealed partial class FoundationStore
{
    // WHAT: Match the reviewed manual requirements to the exact stored case revision.
    // WHY: Readiness describes how to exercise a case independently of automation suitability.
    // CONTRACT: The catalog is read-only and joins on project, ID and original payload SHA-256.
    // FAILURE: Unknown or edited cases require review. A malformed catalog fails visibly.
    // TEST: Compare all 71 catalog entries with the original case payloads and exported master.
    private static readonly Lazy<IReadOnlyDictionary<(string Project, string Id, string Hash), ManualReadiness>>
        ManualReadinessCatalog = new(LoadManualReadiness);

    private IReadOnlyDictionary<string, ManualReadiness> ReadManualReadiness(string projectId)
    {
        var catalog = ManualReadinessCatalog.Value;
        var readiness = new Dictionary<string, ManualReadiness>(StringComparer.Ordinal);
        foreach (var row in db.Query("SELECT id,payload FROM cases WHERE project=?", projectId))
            if (catalog.TryGetValue((projectId, row[0]!, PayloadHash(row[1]!)), out var entry))
                readiness.Add(row[0]!, entry);
        return readiness;
    }

    private static IReadOnlyDictionary<(string, string, string), ManualReadiness> LoadManualReadiness()
    {
        using var resource = typeof(FoundationStore).Assembly.GetManifestResourceStream("QAStudio.ManualReadiness.tsv")
            ?? throw new InvalidDataException("수동 실행 준비도 목록을 찾을 수 없습니다.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var entries = new Dictionary<(string, string, string), ManualReadiness>();
        while (reader.ReadLine() is { } line)
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 5 || fields.Any(string.IsNullOrWhiteSpace) ||
                !Enum.TryParse<ManualReadinessStatus>(fields[3], out var status) || !Enum.IsDefined(status) ||
                !entries.TryAdd((fields[0], fields[1], fields[2]), new(status, fields[4])))
                throw new InvalidDataException("수동 실행 준비도 목록 형식이 잘못되었습니다.");
        }
        return entries;
    }
}
