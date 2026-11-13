using System.Security.Cryptography;
using QAStudio.Domain;

namespace QAStudio.Application;

public interface IManualRunStore
{
    void RecordManualRun(ManualRun run);
}

// WHAT: Append a tester's observations and preserve a verified copy of their optional attachment.
// WHY: Manual evidence remains accessible if the original file later moves.
// CONTRACT: No automation calls. Save one immutable run only after attachment verification.
// FAILURE: Missing/changed files or a rejected DB insert fail the save and remove this attempt's copy.
// TEST: Missing attachment, SHA-256, duplicate/invalid record and source preservation.
public sealed class ManualExecutionRecorder(IManualRunStore store)
{
    public ManualRun Record(string databasePath, TestCase tc, string tester, ManualResult result,
        string actualResult, string notes, string? attachment, DateTimeOffset executedAt)
    {
        var run = new ManualRun(tc.ProjectId, $"M-{Guid.NewGuid():N}", tc.Id, tester.Trim(),
            executedAt.ToUniversalTime(), result, actualResult, notes, null, null);
        run.Validate();
        if (string.IsNullOrWhiteSpace(attachment))
        {
            store.RecordManualRun(run);
            return run;
        }

        string root = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        string relativeFolder = $"evidence/manual/{run.Id}";
        string folder = Path.Combine(root, relativeFolder);
        string name = Path.GetFileName(attachment);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new InvalidDataException("첨부 파일 이름은 120자 이하여야 합니다.");
        using var source = new FileStream(attachment, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(SHA256.HashData(source));
        source.Position = 0;
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, name);
        bool created = false;
        try
        {
            using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                source.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            using (var verify = File.OpenRead(copy))
                if (Convert.ToHexString(SHA256.HashData(verify)) != hash)
                    throw new IOException("수동 증적 원본과 사본의 SHA-256이 일치하지 않습니다.");
            run = run with { AttachmentPath = $"{relativeFolder}/{name}", AttachmentSha256 = hash };
            store.RecordManualRun(run);
            return run;
        }
        catch
        {
            if (created)
            {
                try { File.Delete(copy); Directory.Delete(folder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }
}
