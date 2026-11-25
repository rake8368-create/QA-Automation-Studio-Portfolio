using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Adapters;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

// Seed the existing harness and import smoke with isolated, public inputs.
if (args.Length != 4)
    throw new ArgumentException("Expected workbook, mapping, import baseline and native launch baseline.");
if (File.Exists(args[2]) || File.Exists(args[3]))
    throw new IOException("Fixture outputs must be new.");

var source = File.ReadAllBytes(args[0]);
var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))
    ?? throw new InvalidDataException("Missing project mapping.");
using (var store = new FoundationStore(args[2]))
{
    foreach (var id in mapping.Values.Distinct(StringComparer.Ordinal))
        store.RegisterProject(new(id, id));
    new ImportService(new WorkbookAdapter(), store).Commit("public-workbook", source, new(mapping));
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    store.BackupTo(args[3]);
}

using var launches = new FoundationStore(args[3]);
var fields = new Dictionary<string, string>
{
    ["TC ID"] = LaunchSmokeOrchestrator.CaseId,
    ["테스트 목적"] = "Synthetic Windows process and window check",
    ["QA 기법"] = "Negative test",
    ["우선순위"] = "P1",
    ["수행 절차"] = "Use the harness-provided Windows process fixture.",
    ["기대 결과"] = "A missing executable or expected window cannot pass."
};
var tc = new TestCase(LaunchSmokeOrchestrator.ProjectId, LaunchSmokeOrchestrator.CaseId, fields);
byte[] marker = "Synthetic native launch TC for CI"u8.ToArray();
launches.CommitNew("synthetic-native", new(marker, Convert.ToHexString(SHA256.HashData(marker)), [tc], [], []));
