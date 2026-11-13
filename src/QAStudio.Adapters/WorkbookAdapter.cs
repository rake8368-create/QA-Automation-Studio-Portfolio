using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using QAStudio.Application;
using QAStudio.Domain;

namespace QAStudio.Adapters;

// WHAT: Office Open XML import preview and data-only export against the preserved template.
// WHY: Titles/merged rows precede the actual header; positional CSV parsing loses Korean prose and styles.
// CONTRACT: Detect headers by TC ID, support sparse/shared/inline cells, preserve every source column.
// FAILURE: Missing sheets/headers, formulas in data, duplicate IDs, invalid required values and orphan designs block commit.
// TEST: Real workbook plus malformed ZIP/XML, sparse cells, aliases, formula rejection and byte-preserving style tests.
public sealed partial class WorkbookAdapter : IWorkbookAdapter
{
    private static readonly string[] RequiredCaseValues = ["TC ID", "테스트 목적", "QA 기법", "우선순위", "수행 절차", "기대 결과"];
    public static readonly string[] CaseColumns = ["TC ID", "영역", "테스트 목적", "QA 기법", "우선순위", "사전조건", "입력/경계 데이터", "수행 절차", "기대 결과", "실행 유형", "기존 코드 증거", "자동화 준비도", "AI 검토", "실행 결과", "실제 결과", "증적 경로", "참조 소스"];
    public static readonly string[] DesignColumns = ["TC ID", "어댑터", "SETUP", "ACTION 1", "ACTION 2", "ASSERT / 기대조건", "TIMEOUT", "연결 상태", "안전/증적"];
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private sealed record Row(int Number, Dictionary<string, string> Fields);

    public ImportPreview Preview(byte[] source, ImportOptions options)
    {
        List<ImportIssue> issues = [];
        List<TestCase> cases = [];
        List<AutomationDefinition> designs = [];
        using var stream = new MemoryStream(source, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        GuardArchive(zip);
        var sheets = SheetPaths(zip);
        var strings = SharedStrings(zip);
        var master = ReadMasterColumns(zip, sheets, strings);
        var required = master is not null ? RequiredCaseValues.Concat(master.Headers).ToArray()
            : options.TestCasesOnly ? RequiredCaseValues : CaseColumns;
        var rows = ReadRows(zip, sheets, strings, "TC_Master", required, options, issues, master is not null);
        var ids = new HashSet<(string Project, string Id)>();
        foreach (var row in rows)
        {
            var f = row.Fields; var id = f.GetValueOrDefault("TC ID", "");
            string? project = master is not null ? f.GetValueOrDefault(master.Project) : options.CaseProjects.GetValueOrDefault(id);
            if (master is not null)
                foreach (string heading in master.Headers) f.Remove(heading);
            foreach (var key in RequiredCaseValues)
                if (string.IsNullOrWhiteSpace(f.GetValueOrDefault(key))) issues.Add(new("TC_Master", row.Number, key, "Required value is blank"));
            if (!ids.Add((master is null ? "" : project ?? "", id))) issues.Add(new("TC_Master", row.Number, "TC ID", "Duplicate TC ID"));
            if (!new[] { "P0", "P1", "P2" }.Contains(f.GetValueOrDefault("우선순위")))
                issues.Add(new("TC_Master", row.Number, "우선순위", "Only P0/P1/P2 are supported; no silent conversion"));
            if (string.IsNullOrWhiteSpace(project))
                issues.Add(new("TC_Master", row.Number, "Project", "Explicit project mapping required"));
            cases.Add(new(project ?? "", id, f));
        }
        var designIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in master is not null || options.TestCasesOnly && !sheets.ContainsKey("Auto_Design") ? new List<Row>() : ReadRows(zip, sheets, strings, "Auto_Design", DesignColumns, options, issues))
        {
            var id = row.Fields.GetValueOrDefault("TC ID", "");
            if (!designIds.Add(id)) issues.Add(new("Auto_Design", row.Number, "TC ID", "Duplicate design"));
            var tc = cases.FirstOrDefault(x => x.Id == id);
            if (tc is null) issues.Add(new("Auto_Design", row.Number, "TC ID", "Unknown test case"));
            designs.Add(new(tc?.ProjectId ?? "", "AUTO-" + id, id, row.Fields));
        }
        if (cases.Count == 0) issues.Add(new("TC_Master", 0, "TC ID", "No test cases"));
        return new(source.ToArray(), Convert.ToHexString(SHA256.HashData(source)), cases, designs, issues,
            rows.Select(r => r.Number).ToArray(), IsProjectScopedMaster: master is not null);
    }

    // WHAT: Read source headers and row positions before the caller chooses aliases.
    // CONTRACT: Inspection never writes or invents values; Preview still owns all validation.
    // FAILURE: Unsupported archive/sheet is rejected before any database operation.
    // TEST: Missing-ID alias mapping, malformed files, original source preservation.
    public sealed record SheetRow(int Number, IReadOnlyDictionary<string, string> Fields);
    public sealed record SheetInspection(IReadOnlyList<string> Headers, IReadOnlyList<SheetRow> Rows,
        bool IsProjectScopedMaster = false);
    public SheetInspection Inspect(byte[] source)
    {
        using var zip = new ZipArchive(new MemoryStream(source, false), ZipArchiveMode.Read);
        GuardArchive(zip);
        var paths = SheetPaths(zip); var strings = SharedStrings(zip);
        var master = ReadMasterColumns(zip, paths, strings);
        Func<XElement, string[], string> read = master is null ? CellValue : MasterCellValue;
        if (!paths.TryGetValue("TC_Master", out var path)) throw new InvalidDataException("TC_Master 시트가 필요합니다.");
        var rows = Load(zip.GetEntry(path)!).Descendants(S + "sheetData").Elements(S + "row").ToArray();
        var header = rows.FirstOrDefault(r => r.Elements(S + "c").Any(c => read(c, strings) == "TC ID"))
            ?? rows.Take(50).OrderByDescending(r => r.Elements(S + "c").Count(c => !string.IsNullOrWhiteSpace(read(c, strings)))).FirstOrDefault();
        if (header is null) throw new InvalidDataException("TC 헤더가 없습니다.");
        var columns = header.Elements(S + "c").Select(c => (Col: Column(c), Header: read(c, strings))).Where(c => !string.IsNullOrWhiteSpace(c.Header)).ToArray();
        if (columns.Select(c => c.Header).Distinct(StringComparer.Ordinal).Count() != columns.Length) throw new InvalidDataException("원본 컬럼명이 중복됩니다.");
        List<SheetRow> result = [];
        foreach (var row in rows.Where(r => (int)r.Attribute("r")! > (int)header.Attribute("r")!))
        {
            var cells = row.Elements(S + "c").ToDictionary(Column);
            if (cells.Values.All(c => string.IsNullOrWhiteSpace(read(c, strings)))) continue;
            result.Add(new((int)row.Attribute("r")!, columns.ToDictionary(c => c.Header, c => cells.TryGetValue(c.Col, out var cell) ? read(cell, strings) : "")));
        }
        return new(columns.Select(c => c.Header).Except(master?.Headers ?? [], StringComparer.Ordinal).ToArray(),
            result, IsProjectScopedMaster: master is not null);
    }

    public byte[] Export(byte[] template, IReadOnlyList<TestCase> cases, IReadOnlyList<AutomationDefinition> automations,
        IReadOnlyDictionary<string, string>? headerMap = null)
    {
        using var input = new ZipArchive(new MemoryStream(template, false), ZipArchiveMode.Read);
        GuardArchive(input);
        var paths = SheetPaths(input); var strings = SharedStrings(input);
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        foreach (var entry in input.Entries)
        {
            using var destination = output.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
            var rows = entry.FullName == paths.GetValueOrDefault("TC_Master") ? cases.Select(x => x.Fields).ToArray() :
                entry.FullName == paths.GetValueOrDefault("Auto_Design") ? automations.Select(x => x.Fields).ToArray() : null;
            if (rows is null) { using var original = entry.Open(); original.CopyTo(destination); continue; }
            var doc = Load(entry); var data = doc.Root!.Element(S + "sheetData")!;
            string Canonical(string value) => headerMap?.GetValueOrDefault(value) ?? value;
            var header = data.Elements(S + "row").First(r => r.Elements(S + "c").Any(c => Canonical(CellValue(c, strings)) == "TC ID"));
            var columns = header.Elements(S + "c").Where(c => !string.IsNullOrWhiteSpace(CellValue(c, strings)))
                .ToDictionary(c => Column(c), c => Canonical(CellValue(c, strings)));
            var byId = rows.ToDictionary(x => x["TC ID"], StringComparer.Ordinal);
            var idColumn = columns.Single(x => x.Value == "TC ID").Key;
            var visited = new HashSet<string>();
            foreach (var row in data.Elements(S + "row").Where(r => (int)r.Attribute("r")! > (int)header.Attribute("r")!))
            {
                var id = row.Elements(S + "c").FirstOrDefault(c => Column(c) == idColumn);
                if (id is null || string.IsNullOrEmpty(CellValue(id, strings))) continue;
                string tcId = CellValue(id, strings);
                if (!byId.TryGetValue(tcId, out var fields)) throw new InvalidDataException("Export project scope omits template TC: " + tcId);
                visited.Add(tcId);
                foreach (var col in columns)
                {
                    if (!fields.TryGetValue(col.Value, out var value)) throw new InvalidDataException("Export missing field: " + col.Value);
                    var cell = row.Elements(S + "c").FirstOrDefault(c => Column(c) == col.Key);
                    if (cell is null) { cell = new XElement(S + "c", new XAttribute("r", col.Key + (string)row.Attribute("r")!)); row.Add(cell); }
                    cell.SetAttributeValue("t", "inlineStr"); cell.RemoveNodes();
                    cell.Add(new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
                }
                row.ReplaceNodes(row.Elements(S + "c").OrderBy(c => Column(c).Length).ThenBy(Column, StringComparer.Ordinal).ToArray());
            }
            if (visited.Count != byId.Count) throw new InvalidDataException("Export has rows outside the template; use their own import batch.");
            doc.Save(destination);
        }
        return buffer.ToArray();
    }

    private static List<Row> ReadRows(ZipArchive zip, Dictionary<string, string> sheets, string[] strings,
        string name, string[] required, ImportOptions options, List<ImportIssue> issues, bool projectScopedMaster = false)
    {
        if (!sheets.TryGetValue(name, out var path)) { issues.Add(new(name, 0, "Sheet", "Required sheet missing")); return []; }
        var rows = Load(zip.GetEntry(path)!).Descendants(S + "sheetData").Elements(S + "row").ToArray();
        string Canonical(string text) => projectScopedMaster ? text : options.HeaderMap?.GetValueOrDefault(text) ?? text;
        Func<XElement, string[], string> read = projectScopedMaster ? MasterCellValue : CellValue;
        var header = rows.FirstOrDefault(r => r.Elements(S + "c").Any(c => Canonical(read(c, strings)) == "TC ID"));
        if (header is null) { issues.Add(new(name, 0, "Header", "TC ID header missing")); return []; }
        var columns = header.Elements(S + "c").Where(c => !string.IsNullOrWhiteSpace(read(c, strings)))
            .Select(c => (Column: Column(c), Header: Canonical(read(c, strings)))).ToArray();
        foreach (var key in required.Where(key => columns.All(c => c.Header != key))) issues.Add(new(name, (int)header.Attribute("r")!, key, "Required column missing"));
        if (columns.Select(x => x.Header).Distinct().Count() != columns.Length) { issues.Add(new(name, 0, "Header", "Duplicate mapped headers")); return []; }
        List<Row> result = [];
        foreach (var row in rows.Where(r => (int)r.Attribute("r")! > (int)header.Attribute("r")!))
        {
            var cells = row.Elements(S + "c").ToDictionary(Column);
            if (cells.Values.All(c => string.IsNullOrWhiteSpace(read(c, strings)))) continue;
            var fields = columns.Where(c => !projectScopedMaster || cells.ContainsKey(c.Column))
                .ToDictionary(c => c.Header, c => cells.TryGetValue(c.Column, out var cell) ? read(cell, strings) : "");
            foreach (var cell in cells.Values.Where(c => c.Element(S + "f") is not null)) issues.Add(new(name, (int)row.Attribute("r")!, Column(cell), "Formula requires explicit value mapping; cached results are not trusted"));
            result.Add(new((int)row.Attribute("r")!, fields));
        }
        return result;
    }
    private static Dictionary<string, string> SheetPaths(ZipArchive zip)
    {
        var rels = Load(zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("Workbook relationships missing"))
            .Root!.Elements().ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        return Load(zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Workbook missing")).Descendants(S + "sheet")
            .ToDictionary(e => (string)e.Attribute("name")!, e =>
            {
                var target = rels[(string)e.Attribute(R + "id")!];
                var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                if (path.Contains("..", StringComparison.Ordinal) || zip.GetEntry(path) is null) throw new InvalidDataException("Invalid worksheet relationship");
                return path;
            });
    }
    private static void GuardArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > 1000 || zip.Entries.Sum(x => x.Length) > 32 * 1024 * 1024)
            throw new InvalidDataException("Workbook exceeds supported import limits (1000 entries / 32 MiB expanded)");
    }
    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static string[] SharedStrings(ZipArchive zip) => zip.GetEntry("xl/sharedStrings.xml") is { } e ?
        Load(e).Root!.Elements(S + "si").Select(x => string.Concat(x.Descendants(S + "t").Select(t => t.Value))).ToArray() : [];
    private static string Column(XElement cell) => new(((string?)cell.Attribute("r") ?? "").TakeWhile(char.IsLetter).ToArray());
    private static string CellValue(XElement cell, string[] strings) => (string?)cell.Attribute("t") switch
    {
        "inlineStr" => string.Concat(cell.Descendants(S + "t").Select(t => t.Value)),
        "s" => strings[int.Parse(cell.Element(S + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture)],
        _ => cell.Element(S + "v")?.Value ?? ""
    };
}
