using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using QAStudio.Application;
using QAStudio.Domain;

namespace QAStudio.Adapters;

// WHAT: Write the package tables using the adapter's existing OOXML and TC_Master conventions.
// WHY: A fixed batch template cannot contain cases and fields from multiple import batches.
// CONTRACT: Original columns and values stay intact. A hidden metadata sheet identifies the added columns.
// FAILURE: Reject tables outside Excel or the existing import limits instead of truncating fields.
// TEST: Reopen the master through Preview and compare every field, then check results/history against SQLite.
public sealed partial class WorkbookAdapter : IResultPackageWorkbook
{
    private const string MasterMetadataSheet = "QAStudio_Master_v1";
    private static readonly XNamespace PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string SpreadsheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
    private sealed record Table(string Name, string[] Headers, IReadOnlyList<object?[]> Rows, bool Hidden = false);
    private sealed record MasterColumns(string Project, string Mapping, string Runnable, string[] ReviewColumns)
    {
        public string[] Headers => [Project, Mapping, Runnable, .. ReviewColumns];
    }

    public byte[] ExportMaster(IReadOnlyList<ResultPackageCase> cases)
    {
        var fields = cases.SelectMany(tc => tc.Case.Fields.Keys).ToHashSet(StringComparer.Ordinal);
        string Reserve(string heading)
        {
            string name = heading;
            for (int i = 2; !fields.Add(name); i++) name = $"{heading} (QAStudio {i})";
            return name;
        }

        string[] originals = CaseColumns.Where(fields.Contains)
            .Concat(fields.Except(CaseColumns, StringComparer.Ordinal).Order(StringComparer.Ordinal)).ToArray();
        var generated = new MasterColumns(Reserve("프로젝트"), Reserve("자동화 연결 ID"), Reserve("자동 실행 가능 여부"),
            [Reserve("자동화 적합성"), Reserve("수동 실행 준비도"), Reserve("수동 실행 준비도 근거")]);
        string[] headers = ["TC ID", generated.Project, .. originals.Where(h => h != "TC ID"),
            generated.Mapping, generated.Runnable, .. generated.ReviewColumns];
        var rows = cases.Select<ResultPackageCase, object?[]>(tc => [tc.Case.Id, tc.Case.ProjectId,
            .. originals.Where(h => h != "TC ID").Select(h => tc.Case.Fields.GetValueOrDefault(h)),
            tc.Mapping ?? "", tc.Runnable, tc.Assessment.StatusText, tc.Readiness?.StatusText, tc.Readiness?.Reason]).ToArray();
        byte[] bytes = WriteTables(
            new("TC_Master", headers, rows),
            new(MasterMetadataSheet, generated.Headers, [], Hidden: true));
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        GuardArchive(zip);
        return bytes;
    }

    public byte[] ExportResults(IReadOnlyList<ResultPackageCase> cases)
    {
        static string? Date(DateTimeOffset? time) => time?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        static string? EvidenceStatus(bool? issue) => issue is null ? null : issue.Value ? "증적 목록 확인" : "검증됨";
        var latestRows = cases.Select(tc =>
        {
            var latest = tc.Latest;
            var automatic = tc.Runs.LastOrDefault();
            var manual = tc.ManualRuns.LastOrDefault();
            bool isManual = latest?.Mode == ExecutionMode.Manual;
            string? folder = isManual ? manual?.EvidenceFolder : automatic?.EvidenceFolder;
            bool? issue = isManual ? manual?.EvidenceIssue : automatic?.EvidenceIssue;
            return new object?[] { tc.Case.ProjectId, tc.Case.Id, tc.Case.Fields.GetValueOrDefault("테스트 목적"),
                tc.Mapping, tc.Runnable, latest?.Result ?? ResultText.Format(ExecutionResult.NotRun), Date(latest?.ExecutedAt),
                latest?.Id, latest?.Assertion, folder, tc.Runs.Count + tc.ManualRuns.Count, EvidenceStatus(issue),
                tc.Assessment.StatusText, latest?.ModeText, tc.Runs.Count, tc.ManualRuns.Count, latest?.ActualResult };
        }).ToArray();
        var historyRows = cases.SelectMany(tc => tc.Runs.Select(entry =>
        {
            var run = entry.Run;
            return new object?[] { run.ProjectId, run.TestCaseId, ResultText.Format(run.Result), Date(run.ExecutedAt), run.Id,
                run.Assertion, entry.EvidenceFolder, EvidenceStatus(entry.EvidenceIssue), "AUTOMATED" };
        })).ToArray();
        var manualRows = cases.SelectMany(tc => tc.ManualRuns.Select(entry =>
        {
            var run = entry.Run;
            return new object?[] { run.ProjectId, run.TestCaseId, "MANUAL", run.ResultText, Date(run.ExecutedAt), run.Id,
                run.Tester, run.ActualResult, run.Notes, entry.EvidenceFolder, run.AttachmentPath,
                run.AttachmentSha256, EvidenceStatus(entry.EvidenceIssue) };
        })).ToArray();
        var reviews = cases.Select(tc => new object?[] { tc.Case.ProjectId, tc.Case.Id,
            tc.Case.Fields.GetValueOrDefault("테스트 목적"), tc.Case.Fields.GetValueOrDefault("우선순위"),
            tc.Assessment.StatusText, tc.Runnable, tc.Mapping, tc.Assessment.Observation, tc.Assessment.Environment,
            tc.Assessment.Stability, tc.Assessment.MaintenanceCost, tc.Assessment.Rationale }).ToArray();
        var summary = Enum.GetValues<SuitabilityStatus>().Select(status => new object?[] {
            "자동화 적합성 (TC)", status.ToString().ToUpperInvariant(), cases.Count(tc => tc.Assessment.Status == status) })
            .Concat(new object?[][]
            {
                ["실행 이력", "AUTOMATED", historyRows.Length],
                ["실행 이력", "MANUAL", manualRows.Length],
                ["최근 결과 (TC)", "NOT RUN", cases.Count(tc => tc.Latest is null)]
            }).ToArray();
        return WriteTables(
            new("TC_Results", ["프로젝트", "TC ID", "테스트 목적", "자동화 연결 ID", "자동 실행 가능 여부",
                "최근 실행 결과", "최근 실행 일시 (UTC)", "RunID", "Assertion", "결과 증적 위치", "누적 실행 횟수", "증적 상태",
                "자동화 적합성", "최근 실행 방식", "자동 실행 횟수", "수동 실행 횟수", "수동 실제 결과"], latestRows),
            new("Run_History", ["프로젝트", "TC ID", "실행 결과", "실행 일시 (UTC)", "RunID",
                "Assertion", "결과 증적 위치", "증적 상태", "실행 방식"], historyRows),
            new("Manual_History", ["프로젝트", "TC ID", "실행 방식", "실행 결과", "실행 일시 (UTC)", "RunID",
                "실행자", "실제 결과", "메모", "결과 증적 위치", "첨부 등록 경로", "첨부 SHA-256", "증적 상태"], manualRows),
            new("Automation_Review", ["프로젝트", "TC ID", "테스트 목적", "우선순위", "추천 분류", "자동 실행 가능 여부",
                "자동화 연결 ID", "객관적 관찰 기준", "필요 환경과 확인 상태", "반복 안정성", "유지보수 비용", "분류 근거"], reviews),
            new("Summary", ["집계 범위", "항목", "건수"], summary));
    }

    private static MasterColumns? ReadMasterColumns(ZipArchive zip, Dictionary<string, string> paths, string[] strings)
    {
        if (!paths.TryGetValue(MasterMetadataSheet, out var path)) return null;
        var rows = Load(zip.GetEntry(path)!).Root!.Element(S + "sheetData")!.Elements(S + "row").ToArray();
        if (rows.Length != 1) throw new InvalidDataException("마스터 Excel의 프로젝트 메타데이터 형식이 잘못되었습니다.");
        var headings = rows[0].Elements(S + "c").Select(c => MasterCellValue(c, strings)).ToArray();
        if (headings.Length is not (3 or 6) || headings.Any(h => string.IsNullOrWhiteSpace(h) || CaseColumns.Contains(h))
            || headings.Distinct(StringComparer.Ordinal).Count() != headings.Length)
            throw new InvalidDataException("마스터 Excel의 프로젝트 메타데이터 열이 잘못되었습니다.");
        return new(headings[0], headings[1], headings[2], headings[3..]);
    }

    private static byte[] WriteTables(params Table[] tables)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var workbook = new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R),
                new XElement(S + "bookViews", new XElement(S + "workbookView")),
                new XElement(S + "sheets", tables.Select((table, i) =>
                    new XElement(S + "sheet", new XAttribute("name", table.Name), new XAttribute("sheetId", i + 1),
                        table.Hidden ? new XAttribute("state", "hidden") : null, new XAttribute(R + "id", $"rId{i + 1}")))));
            WriteXml(zip, "xl/workbook.xml", workbook);
            XElement Relationship(string id, string kind, string target) => new(PackageRels + "Relationship",
                new XAttribute("Id", id), new XAttribute("Type", $"{R}/{kind}"), new XAttribute("Target", target));
            WriteXml(zip, "_rels/.rels", new XElement(PackageRels + "Relationships",
                Relationship("rId1", "officeDocument", "xl/workbook.xml")));
            WriteXml(zip, "xl/_rels/workbook.xml.rels", new XElement(PackageRels + "Relationships",
                tables.Select((_, i) => Relationship($"rId{i + 1}", "worksheet", $"worksheets/sheet{i + 1}.xml")),
                Relationship($"rId{tables.Length + 1}", "styles", "styles.xml")));
            XElement Override(string path, string kind) => new(ContentTypes + "Override", new XAttribute("PartName", path),
                new XAttribute("ContentType", SpreadsheetContentType + kind + "+xml"));
            WriteXml(zip, "[Content_Types].xml", new XElement(ContentTypes + "Types",
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                Override("/xl/workbook.xml", "sheet.main"), Override("/xl/styles.xml", "styles"),
                tables.Select((_, i) => Override($"/xl/worksheets/sheet{i + 1}.xml", "worksheet"))));
            WriteXml(zip, "xl/styles.xml", XElement.Parse(PackageStyles));
            for (int i = 0; i < tables.Length; i++) WriteXml(zip, $"xl/worksheets/sheet{i + 1}.xml", Worksheet(tables[i]));
        }
        return buffer.ToArray();
    }

    private static XElement Worksheet(Table table)
    {
        if (table.Headers.Length is < 1 or > 16384 || table.Rows.Count >= 1048576)
            throw new InvalidDataException($"Excel의 행 또는 열 제한을 초과했습니다: {table.Name}");
        var columns = table.Headers.Select((_, i) => ColumnName(i + 1)).ToArray();
        var widths = table.Headers.Select((heading, i) => Math.Clamp(
            table.Rows.Select(row => row[i] is string text ? text.Split('\n').Max(DisplayLength) : 0)
                .Append(DisplayLength(heading)).Max() + 4, 12, 58)).ToArray();
        string range = $"A1:{columns[^1]}{table.Rows.Count + 1}";
        XElement Row(int number, object?[] values, int style)
        {
            int lines = values.Select((value, i) => value is string text
                ? text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling((double)DisplayLength(line) / (widths[i] - 3))))
                : 1).Max();
            int height = number == 1 ? 29 : Math.Clamp(lines * 16 + 5, 23, 405);
            return new XElement(S + "row", new XAttribute("r", number), new XAttribute("ht", height), new XAttribute("customHeight", 1),
                values.Select((value, i) => value is null ? null : CreateCell(columns[i] + number, value, style)));
        }
        return new XElement(S + "worksheet", new XElement(S + "dimension", new XAttribute("ref", range)),
            new XElement(S + "sheetViews", new XElement(S + "sheetView", new XAttribute("workbookViewId", 0),
                new XElement(S + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"),
                    new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")),
                new XElement(S + "selection", new XAttribute("pane", "bottomLeft"), new XAttribute("activeCell", "A2"), new XAttribute("sqref", "A2")))),
            new XElement(S + "sheetFormatPr", new XAttribute("defaultRowHeight", 23)),
            new XElement(S + "cols", widths.Select((width, i) => new XElement(S + "col",
                new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", width), new XAttribute("customWidth", 1)))),
            new XElement(S + "sheetData", Row(1, table.Headers, 1),
                table.Rows.Select((values, i) => Row(i + 2, values, i % 2 == 0 ? 2 : 3))),
            table.Hidden ? null : new XElement(S + "autoFilter", new XAttribute("ref", range)));
    }

    private static int DisplayLength(string text) => text.Sum(c => c >= '\u2e80' ? 2 : 1);
    private static string ColumnName(int number)
    {
        string name = "";
        for (; number > 0; number = (number - 1) / 26) name = (char)('A' + (number - 1) % 26) + name;
        return name;
    }

    private static XElement CreateCell(string reference, object value, int style)
    {
        var cell = new XElement(S + "c", new XAttribute("r", reference), new XAttribute("s", style));
        if (value is int number)
        {
            cell.Add(new XElement(S + "v", number));
            return cell;
        }
        string text = (string)value;
        if (text.Length > 32767) throw new InvalidDataException($"Excel 셀 길이 제한을 초과했습니다: {reference}");
        text = Regex.Replace(text, @"_x[0-9A-Fa-f]{4}_", match => "_x005F_" + match.Value[1..]);
        text = Regex.Replace(text, @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\uFFFE\uFFFF]",
            match => $"_x{(int)match.Value[0]:X4}_");
        cell.Add(new XAttribute("t", "inlineStr"),
            new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
        return cell;
    }

    private static string MasterCellValue(XElement cell, string[] strings) =>
        Regex.Replace(CellValue(cell, strings), @"_x([0-9A-Fa-f]{4})_", match =>
            ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    private static void WriteXml(ZipArchive zip, string path, XElement root)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { NewLineHandling = NewLineHandling.Entitize });
        root.Save(writer);
    }

    private const string PackageStyles = """
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="2">
            <font><sz val="11"/><color rgb="FF243548"/><name val="맑은 고딕"/></font>
            <font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="맑은 고딕"/></font>
          </fonts>
          <fills count="4">
            <fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF233C58"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFF0F5FA"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="4">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="3" borderId="0" xfId="0" applyFill="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}
