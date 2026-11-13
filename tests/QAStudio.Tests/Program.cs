using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using QAStudio.Adapters;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

// WHAT: Executable, dependency-free unit/integration acceptance harness using the user's actual workbook.
// WHY: Fresh assertion logs must distinguish platform tests from the 70 unexecuted product TCs.
// CONTRACT: Source/mapping/output are explicit; synthetic runs exist only in disposable test databases.
// FAILURE: Each exception is a failed test with a nonzero process exit; no skipped test is marked PASS.
// TEST: Invoked by tools/verify-phase1.ps1 after Release solution build.
if (args.Length == 3 && args[0] == "--day7") return QAStudio.Tests.Day7Checks.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--day8") return QAStudio.Tests.Day8Checks.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--day9") return QAStudio.Tests.Day9Checks.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--day11") return QAStudio.Tests.Day11Checks.Run(args[1], args[2]);
if (args.Length == 2 && args[0] == "--result-package") return QAStudio.Tests.ResultPackageChecks.Run(args[1]);
if (args.Length == 3 && args[0] == "--manual-sprint") return QAStudio.Tests.ManualSprintChecks.Run(args[1], args[2]);
if (args.Length != 3) { Console.Error.WriteLine("Usage: QAStudio.Tests <source.xlsx> <mapping.json> <evidence-dir>"); return 2; }
var sourcePath = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
byte[] source = File.ReadAllBytes(sourcePath);
var options = new ImportOptions(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!);
var adapter = new WorkbookAdapter();
var preview = adapter.Preview(source, options);
var results = new List<object>(); int failures = 0;
var total = Stopwatch.StartNew();
void Test(string name, Action action)
{
    var timer = Stopwatch.StartNew();
    try { action(); results.Add(new { name, status = "PASS", milliseconds = timer.Elapsed.TotalMilliseconds }); Console.WriteLine("PASS: " + name); }
    catch (Exception e) { failures++; results.Add(new { name, status = "FAIL", milliseconds = timer.Elapsed.TotalMilliseconds, error = e.ToString() }); Console.WriteLine("FAIL: " + name + "\n" + e); }
}
void Check(bool ok, string message = "Assertion failed") { if (!ok) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
void EqualFields(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
    Check(a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value), "Field loss or change");
void EqualCases(IEnumerable<TestCase> a, IEnumerable<TestCase> b)
{
    var left = a.OrderBy(x => x.Id).ToArray(); var right = b.OrderBy(x => x.Id).ToArray();
    Check(left.Length == right.Length, "Row count differs");
    for (int i = 0; i < left.Length; i++) { Check(left[i].ProjectId == right[i].ProjectId && left[i].Id == right[i].Id); EqualFields(left[i].Fields, right[i].Fields); }
}
var scratch = Path.Combine(output, "isolated-tests"); Directory.CreateDirectory(scratch);
FoundationStore NewStore(string name, bool seed = true)
{
    var store = new FoundationStore(Path.Combine(scratch, name + "-" + Guid.NewGuid().ToString("N") + ".sqlite"));
    if (seed) foreach (var id in options.CaseProjects.Values.Distinct()) store.RegisterProject(new(id, id));
    return store;
}
XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
byte[] Modify(string entryName, Action<XDocument> edit)
{
    using var buffer = new MemoryStream(); buffer.Write(source);
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, true))
    {
        var entry = zip.GetEntry(entryName)!; XDocument doc;
        using (var stream = entry.Open()) doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        edit(doc); entry.Delete(); using var destination = zip.CreateEntry(entryName).Open(); doc.Save(destination);
    }
    return buffer.ToArray();
}
byte[] Cell(string reference, string value, string sheet = "sheet2.xml") => Modify("xl/worksheets/" + sheet, doc =>
{
    var cell = doc.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == reference);
    cell.SetAttributeValue("t", "inlineStr"); cell.RemoveNodes(); cell.Add(new XElement(ns + "is", new XElement(ns + "t", value)));
});
void Invalid(byte[] bytes, string field)
{
    var p = adapter.Preview(bytes, options); Check(!p.IsValid && p.Issues.Any(x => x.Field == field), JsonSerializer.Serialize(p.Issues));
    using var store = NewStore("invalid"); var service = new ImportService(adapter, store);
    Throws<InvalidDataException>(() => service.Commit("invalid", bytes, options));
    Check(options.CaseProjects.Values.Distinct().All(id => store.Cases(id).Count == 0));
    Throws<InvalidOperationException>(() => store.Source("invalid"));
}

Test("Real input: six sheets, 70 TCs, explicit 65/5 project mapping", () =>
{
    Check(preview.IsValid, JsonSerializer.Serialize(preview.Issues)); Check(preview.Cases.Count == 70);
    Check(preview.Cases.Count(x => x.ProjectId == "macro-studio") == 65);
    Check(preview.Cases.Count(x => x.ProjectId == "qa-studio") == 5);
    using var z = new ZipArchive(new MemoryStream(source));
    Check(XDocument.Load(z.GetEntry("xl/workbook.xml")!.Open()).Descendants(ns + "sheet").Count() == 6);
});
Test("17 original fields, representative IDs, techniques, boundaries and expected values", () =>
{
    Check(preview.Cases.All(x => x.Fields.Count == 17));
    foreach (var id in new[] { "MAC-004", "MAC-007", "MAC-013", "PRO-002", "EVT-002", "EVT-006", "AUD-006", "QAT-004" })
    {
        var tc = preview.Cases.Single(x => x.Id == id);
        foreach (var key in new[] { "QA 기법", "입력/경계 데이터", "기대 결과" }) Check(!string.IsNullOrWhiteSpace(tc.Fields[key]), id + " " + key);
    }
    Check(preview.Cases.Single(x => x.Id == "PRO-002").Fields["입력/경계 데이터"].Contains("51"));
    Check(preview.Cases.Single(x => x.Id == "MAC-007").Fields["입력/경계 데이터"].Contains("33"));
});
Test("11 linked automation designs remain NEEDS_MAPPING and are not executable", () =>
{
    Check(preview.Automations.Count == 11);
    Check(preview.Automations.All(x => x.State == AutomationState.NeedsMapping && !x.Approved && x.Binding == "" && x.Fields.Count == 9));
    foreach (var a in preview.Automations) { Check(preview.Cases.Any(c => c.Id == a.TestCaseId && c.ProjectId == a.ProjectId)); Throws<InvalidOperationException>(() => a.Transition(AutomationState.Ready)); }
});
Test("Fresh import: all 70 NOT RUN, zero execution records", () =>
{
    using var store = NewStore("unrun"); new ImportService(adapter, store).Commit("first", source, options);
    Check(preview.Cases.All(c => store.LatestResult(c.ProjectId, c.Id) == ExecutionResult.NotRun));
    Check(options.CaseProjects.Values.Distinct().All(p => store.Runs(p).Count == 0));
});
Test("Excel -> SQLite -> export -> reload compares every TC/design field and source bytes", () =>
{
    string dbPath = Path.Combine(output, "portfolio.sqlite");
    using (var store = new FoundationStore(dbPath))
    {
        foreach (var id in options.CaseProjects.Values.Distinct()) store.RegisterProject(new(id, id));
        var service = new ImportService(adapter, store); service.Commit("portfolio", source, options);
        Check(store.Source("portfolio").SequenceEqual(source));
        EqualCases(preview.Cases, options.CaseProjects.Values.Distinct().SelectMany(store.Cases));
        var exported = service.Export("portfolio", options.CaseProjects.Values.Distinct());
        File.WriteAllBytes(Path.Combine(output, "portfolio-roundtrip.xlsx"), exported);
        var reloaded = adapter.Preview(exported, options); Check(reloaded.IsValid); EqualCases(preview.Cases, reloaded.Cases);
        foreach (var a in preview.Automations) EqualFields(a.Fields, reloaded.Automations.Single(x => x.Id == a.Id).Fields);
    }
    using var reopened = new FoundationStore(dbPath); Check(reopened.Cases("macro-studio").Count == 65 && reopened.Cases("qa-studio").Count == 5);
    Check(reopened.Runs("macro-studio").Count == 0 && reopened.Runs("qa-studio").Count == 0);
});
Test("Export preserves styles, other sheets and formatting attributes", () =>
{
    byte[] exported = adapter.Export(source, preview.Cases, preview.Automations);
    using var a = new ZipArchive(new MemoryStream(source)); using var b = new ZipArchive(new MemoryStream(exported));
    Check(a.Entries.Select(e => e.FullName).Order().SequenceEqual(b.Entries.Select(e => e.FullName).Order()));
    foreach (var e in a.Entries)
    {
        if (e.FullName is "xl/worksheets/sheet2.xml" or "xl/worksheets/sheet3.xml")
        {
            var left = XDocument.Load(e.Open()); var right = XDocument.Load(b.GetEntry(e.FullName)!.Open());
            Check(left.Descendants(ns + "c").Select(c => (string?)c.Attribute("s")).SequenceEqual(right.Descendants(ns + "c").Select(c => (string?)c.Attribute("s"))));
            foreach (var name in new[] { "mergeCells", "cols", "sheetViews", "dataValidations", "conditionalFormatting" })
                Check(string.Concat(left.Descendants(ns + name)) == string.Concat(right.Descendants(ns + name)), name);
        }
        else
        {
            using var l = new MemoryStream(); using var r = new MemoryStream(); using var ls = e.Open(); using var rs = b.GetEntry(e.FullName)!.Open();
            ls.CopyTo(l); rs.CopyTo(r); Check(l.ToArray().SequenceEqual(r.ToArray()), "Modified unrelated entry: " + e.FullName);
        }
    }
});
Test("Duplicate TC ID rejects entire import", () => Invalid(Cell("A7", "MAC-001"), "TC ID"));
Test("Blank TC ID rejects entire import", () => Invalid(Cell("A6", ""), "TC ID"));
Test("Blank expected result rejects entire import", () => Invalid(Cell("I6", "  "), "기대 결과"));
Test("Invalid priority rejects entire import without coercion", () => Invalid(Cell("E6", "HIGH"), "우선순위"));
Test("Missing required header is reported", () => Invalid(Cell("I5", "unsupported"), "기대 결과"));
Test("Custom header alias maps to the standard schema", () =>
{
    var mapped = adapter.Preview(Cell("C5", "Purpose"), options with { HeaderMap = new Dictionary<string, string> { ["Purpose"] = "테스트 목적" } });
    Check(mapped.IsValid); EqualCases(preview.Cases, mapped.Cases);
});
Test("Duplicate mapped headers are rejected", () =>
{
    var p = adapter.Preview(source, options with { HeaderMap = new Dictionary<string, string> { ["영역"] = "테스트 목적" } });
    Check(!p.IsValid && p.Issues.Any(x => x.Message.Contains("Duplicate mapped")));
});
Test("Missing project mapping is rejected", () =>
{
    var map = options.CaseProjects.ToDictionary(); map.Remove("QAT-001");
    Check(adapter.Preview(source, new(map)).Issues.Any(x => x.Field == "Project"));
});
Test("Orphan automation design rejects import", () => Invalid(Cell("A6", "UNKNOWN-001", "sheet3.xml"), "TC ID"));
Test("Duplicate automation design rejects import", () => Invalid(Cell("A7", "MAC-004", "sheet3.xml"), "TC ID"));
Test("Cached formula cells never become trusted imported values", () =>
{
    var changed = Modify("xl/worksheets/sheet2.xml", d => d.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "I6").Add(new XElement(ns + "f", "1+1")));
    Invalid(changed, "I");
});
Test("Missing Auto_Design sheet rejects preview", () =>
{
    var changed = Modify("xl/workbook.xml", d => d.Descendants(ns + "sheet").Single(s => (string?)s.Attribute("name") == "Auto_Design").Remove());
    Invalid(changed, "Sheet");
});
Test("Malformed workbook fails without writing a database", () => Throws<InvalidDataException>(() => adapter.Preview([1, 2, 3], options)));
Test("Sparse blank cells retain their correct columns", () =>
{
    var changed = Modify("xl/worksheets/sheet2.xml", d => d.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "O6").Remove());
    var p = adapter.Preview(changed, options); Check(p.IsValid); EqualCases(preview.Cases, p.Cases);
});
Test("Shared strings retain rich-text Korean content", () =>
{
    var changed = Modify("xl/worksheets/sheet2.xml", d => { var c = d.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "C6"); c.SetAttributeValue("t", "s"); c.RemoveNodes(); c.Add(new XElement(ns + "v", "0")); });
    using var buffer = new MemoryStream(); buffer.Write(changed);
    using (var z = new ZipArchive(buffer, ZipArchiveMode.Update, true))
    {
        z.GetEntry("xl/sharedStrings.xml")?.Delete();
        using var s = z.CreateEntry("xl/sharedStrings.xml").Open();
        new XDocument(new XElement(ns + "sst", new XElement(ns + "si", new XElement(ns + "r", new XElement(ns + "t", "한글 ")), new XElement(ns + "r", new XElement(ns + "t", "유지"))))).Save(s);
    }
    var p = adapter.Preview(buffer.ToArray(), options); Check(p.IsValid && p.Cases[0].Fields["테스트 목적"] == "한글 유지");
});
Test("Literal formula-looking text, Unicode, quotes and newline survive DB/export", () =>
{
    const string text = "=HYPERLINK(\"example\")\n한글 ' 😃";
    var changed = Cell("I6", text); using var store = NewStore("unicode");
    var service = new ImportService(adapter, store); service.Commit("unicode", changed, options);
    var p = adapter.Preview(service.Export("unicode", options.CaseProjects.Values.Distinct()), options);
    Check(p.IsValid && p.Cases.Single(x => x.Id == "MAC-001").Fields["기대 결과"] == text);
});
Test("Imported PASS is retained as source text while current execution remains NOT RUN", () =>
{
    var changed = Cell("N6", "PASS"); using var store = NewStore("imported-pass"); new ImportService(adapter, store).Commit("pass-text", changed, options);
    Check(store.Cases("macro-studio").Single(c => c.Id == "MAC-001").Fields["실행 결과"] == "PASS");
    Check(store.LatestResult("macro-studio", "MAC-001") == ExecutionResult.NotRun && store.Runs("macro-studio").Count == 0);
});
Test("Duplicate batch ID leaves existing data unchanged", () =>
{
    using var store = NewStore("duplicate-batch"); var service = new ImportService(adapter, store); service.Commit("batch", source, options);
    Throws<InvalidDataException>(() => service.Commit("batch", Cell("C6", "Changed"), options));
    EqualCases(preview.Cases, options.CaseProjects.Values.Distinct().SelectMany(store.Cases));
});
Test("Missing registered project rolls back imported rows and batch provenance", () =>
{
    using var store = NewStore("rollback", false); store.RegisterProject(new("macro-studio", "First project"));
    Throws<InvalidDataException>(() => new ImportService(adapter, store).Commit("partial", source, options));
    Check(store.Cases("macro-studio").Count == 0); Throws<InvalidOperationException>(() => store.Source("partial"));
});
Test("Future database schema is rejected without downgrade", () =>
{
    var path = Path.Combine(scratch, "future.sqlite"); using (var sql = new SqliteDatabase(path)) sql.Execute("PRAGMA user_version=99");
    Throws<InvalidDataException>(() => { using var store = new FoundationStore(path); });
    using var inspect = new SqliteDatabase(path); Check(inspect.Query("PRAGMA user_version")[0][0] == "99");
});
Test("Lifecycle DRAFT -> NEEDS_MAPPING -> READY -> EXECUTING -> FINISHED needs approved binding", () =>
{
    var a = preview.Automations[0] with { State = AutomationState.Draft };
    Throws<InvalidOperationException>(() => a.Transition(AutomationState.Ready));
    a = a.Transition(AutomationState.NeedsMapping); Throws<InvalidOperationException>(() => a.Transition(AutomationState.Executing));
    a = (a with { Binding = "fake-testhost://assertion", Approved = true }).Transition(AutomationState.Ready).Transition(AutomationState.Executing).Transition(AutomationState.Finished);
    Check(a.State == AutomationState.Finished); Throws<InvalidOperationException>(() => a.Transition(AutomationState.Executing));
});
Test("PASS/FAIL requires evidence; NOT RUN and FLAKY cannot be execution records", () =>
{
    var r = new TestRun("macro-studio", "synthetic", "MAC-001", ExecutionResult.Pass, "", "", DateTimeOffset.UtcNow, "isolated-fake", "test-only", "fake", "snapshot");
    Throws<InvalidDataException>(r.Validate); Throws<InvalidDataException>(() => (r with { Result = ExecutionResult.Fail }).Validate());
    Throws<InvalidDataException>(() => (r with { Result = ExecutionResult.NotRun }).Validate());
    Throws<InvalidDataException>(() => (r with { Result = (ExecutionResult)999 }).Validate());
    Check(Enum.GetValues<ExecutionResult>().Select(ResultText.Format).SequenceEqual(new[] { "NOT RUN", "PASS", "FAIL", "ERROR", "BLOCKED", "CANCELLED" }));
});
Test("Synthetic append-only runs/evidence, retry/defect history and project FK isolation", () =>
{
    string path = Path.Combine(scratch, "history.sqlite"); using var store = new FoundationStore(path);
    foreach (var id in options.CaseProjects.Values.Distinct()) store.RegisterProject(new(id, id));
    new ImportService(adapter, store).Commit("history", source, options);
    var tc = store.Cases("macro-studio").First();
    var first = new TestRun(tc.ProjectId, "synthetic-failure", tc.Id, ExecutionResult.Fail, "fake value == 1", "fake-output:0", DateTimeOffset.UtcNow, "isolated-fake", "test-build-1", "fake-host", JsonSerializer.Serialize(tc));
    store.AppendRun(first);
    var retry = first with { Id = "synthetic-retest", Result = ExecutionResult.Pass, Evidence = "fake-output:1", PreviousRunId = first.Id };
    store.AppendRun(retry); Check(store.Runs(tc.ProjectId).Count == 2 && ResultText.IsFlaky(store.Runs(tc.ProjectId)));
    Check(store.LatestResult(tc.ProjectId, tc.Id) == ExecutionResult.Pass);
    store.AppendEvidence(new(tc.ProjectId, "E-1", first.Id, "fake-only.json", "test-only"));
    store.SaveDefect(new(tc.ProjectId, "D-1", first.Id, "Synthetic defect for storage unit test", retry.Id));
    store.SavePlan(new(tc.ProjectId, "P-1", "Synthetic test plan", [tc.Id]));
    store.AppendProposal(new(tc.ProjectId, "AI-1", tc.Id, "synthetic source", "fake/no-api", "unit-v1", "PENDING", "", "before", "after", "synthetic evidence", "not a real model call", null));
    Throws<InvalidDataException>(() => store.AppendRun(retry));
    Throws<InvalidDataException>(() => store.AppendRun(first with { ProjectId = "qa-studio", Id = "cross-project" }));
    Throws<InvalidDataException>(() => store.AppendEvidence(new("qa-studio", "cross", first.Id, "fake", "fake")));
    Throws<InvalidDataException>(() => store.SaveDefect(new("qa-studio", "cross", first.Id, "fake", null)));
    Throws<InvalidDataException>(() => store.SavePlan(new("qa-studio", "cross", "invalid plan", [tc.Id])));
    Throws<InvalidDataException>(() => store.AppendProposal(new("qa-studio", "cross", tc.Id, "fake", "fake", "v1", "PENDING", "", "", "", "fake", "", null)));
    using var raw = new SqliteDatabase(path);
    foreach (var table in new[] { "runs", "evidence", "proposals", "imports" })
    {
        Throws<InvalidDataException>(() => raw.Execute($"UPDATE {table} SET id='tampered'"));
        Throws<InvalidDataException>(() => raw.Execute($"DELETE FROM {table}"));
    }
    Check(store.Runs(tc.ProjectId).Count == 2 && store.Runs("qa-studio").Count == 0);
    Check(raw.Query("PRAGMA foreign_key_check").Count == 0 && raw.Query("PRAGMA integrity_check")[0][0] == "ok");
    // Reimport edits design prose, while the immutable execution snapshot remains the original TC.
    new ImportService(adapter, store).Commit("edited", Cell("C6", "Revised purpose"), options);
    Check(store.Runs(tc.ProjectId)[0].CaseSnapshot == JsonSerializer.Serialize(tc));
    Check(raw.Query("SELECT COUNT(*) FROM plans WHERE project='qa-studio'")[0][0] == "0");
});
Test("Independent projects may use the same TC ID without data collision", () =>
{
    using var store = NewStore("same-id"); var service = new ImportService(adapter, store); service.Commit("one", source, options);
    store.RegisterProject(new("other-project", "Unrelated product"));
    var otherOptions = new ImportOptions(options.CaseProjects.ToDictionary(x => x.Key, _ => "other-project"));
    service.Commit("two", Cell("C6", "Other project purpose"), otherOptions);
    Check(store.Cases("other-project").Count == 70 && store.Cases("macro-studio").Count == 65);
    Check(store.Cases("other-project").Single(c => c.Id == "MAC-001").Fields["테스트 목적"] == "Other project purpose");
    Check(store.Cases("macro-studio").Single(c => c.Id == "MAC-001").Fields["테스트 목적"] == preview.Cases[0].Fields["테스트 목적"]);
});
Test("Aliased columns roundtrip using the same explicit mapping", () =>
{
    var map = new Dictionary<string, string> { ["Purpose"] = "테스트 목적", ["Identifier"] = "TC ID" };
    var changed = Modify("xl/worksheets/sheet2.xml", doc =>
    {
        foreach (var pair in new[] { ("A5", "Identifier"), ("C5", "Purpose") })
        {
            var c = doc.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == pair.Item1);
            c.SetAttributeValue("t", "inlineStr"); c.RemoveNodes(); c.Add(new XElement(ns + "is", new XElement(ns + "t", pair.Item2)));
        }
    });
    var p = adapter.Preview(changed, options with { HeaderMap = map }); Check(p.IsValid);
    var exported = adapter.Export(changed, p.Cases, p.Automations, map);
    EqualCases(p.Cases, adapter.Preview(exported, options with { HeaderMap = map }).Cases);
});
Test("Additional source columns survive DB/export/reload", () =>
{
    var changed = Modify("xl/worksheets/sheet2.xml", d =>
    {
        d.Descendants(ns + "row").Single(r => (string?)r.Attribute("r") == "5").Add(new XElement(ns + "c", new XAttribute("r", "R5"), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", "사용자 열"))));
        d.Descendants(ns + "row").Single(r => (string?)r.Attribute("r") == "6").Add(new XElement(ns + "c", new XAttribute("r", "R6"), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", "확장 데이터"))));
    });
    using var store = NewStore("extra-column"); var service = new ImportService(adapter, store); service.Commit("extended", changed, options);
    var p = adapter.Preview(service.Export("extended", options.CaseProjects.Values.Distinct()), options);
    Check(p.IsValid && p.Cases[0].Fields["사용자 열"] == "확장 데이터");
    EqualCases(adapter.Preview(changed, options).Cases, p.Cases);
});
Test("Automation state transitions persist and cannot cross project boundaries", () =>
{
    using var store = NewStore("lifecycle"); new ImportService(adapter, store).Commit("states", source, options);
    var a = store.Automations("macro-studio").First();
    Throws<InvalidOperationException>(() => store.TransitionAutomation(a.ProjectId, a.Id, AutomationState.Ready));
    store.TransitionAutomation(a.ProjectId, a.Id, AutomationState.Ready, "fake-testhost://bound", true);
    store.TransitionAutomation(a.ProjectId, a.Id, AutomationState.Executing);
    store.TransitionAutomation(a.ProjectId, a.Id, AutomationState.Finished);
    Check(store.Automations(a.ProjectId).Single(x => x.Id == a.Id).State == AutomationState.Finished);
    Throws<InvalidOperationException>(() => store.TransitionAutomation("qa-studio", a.Id, AutomationState.Ready));
    Check(store.Runs(a.ProjectId).Count == 0, "A state transition is not an executed test");
});
Test("DTD/entity declarations are prohibited during XML import", () =>
{
    using var buffer = new MemoryStream(); buffer.Write(source);
    using (var z = new ZipArchive(buffer, ZipArchiveMode.Update, true))
    {
        z.GetEntry("xl/workbook.xml")!.Delete(); using var w = new StreamWriter(z.CreateEntry("xl/workbook.xml").Open());
        w.Write("<!DOCTYPE workbook [<!ENTITY external SYSTEM 'file:///missing'>]><workbook>&external;</workbook>");
    }
    Throws<System.Xml.XmlException>(() => adapter.Preview(buffer.ToArray(), options));
});
Test("SQLite enforces PASS assertion and result vocabulary even through direct insert", () =>
{
    var path = Path.Combine(scratch, "constraints.sqlite"); using var store = new FoundationStore(path);
    foreach (var id in options.CaseProjects.Values.Distinct()) store.RegisterProject(new(id, id));
    new ImportService(adapter, store).Commit("constraints", source, options);
    using var db = new SqliteDatabase(path);
    foreach (var value in new[] { "PASS", "FLAKY", "NOT RUN", "unknown" })
        Throws<InvalidDataException>(() => db.Execute("INSERT INTO runs(project,id,case_id,result,assertion,evidence,payload) VALUES('macro-studio','raw','MAC-001',?,'','','{}')", value));
    Check(store.Runs("macro-studio").Count == 0);
});
Test("An export omitting a template project fails explicitly instead of losing rows", () =>
{
    using var store = NewStore("export-scope"); var service = new ImportService(adapter, store); service.Commit("scope", source, options);
    Throws<InvalidDataException>(() => service.Export("scope", ["macro-studio"]));
});
Test("Source workbook SHA-256 remains unchanged after all tests", () => Check(SHA256.HashData(File.ReadAllBytes(sourcePath)).SequenceEqual(SHA256.HashData(source))));
// Runner infrastructure checks reuse the single existing binding in isolated copies, not additional product TCs.
Test("Native launch missing executable records ERROR and immutable evidence", () =>
{
    string path = Path.Combine(scratch, "native-error.sqlite");
    File.Copy(Path.GetFullPath("work/native-run-20261028/before.sqlite"), path);
    using var store = new FoundationStore(path);
    var tc = store.Cases(LaunchSmokeOrchestrator.ProjectId).Single(c => c.Id == LaunchSmokeOrchestrator.CaseId);
    var run = new LaunchSmokeOrchestrator(new ProcessWindowAdapter(), store).ExecuteAsync(tc,
        new(Path.Combine(scratch, "missing.exe"), "main", Path.Combine(scratch, "error-evidence"))).GetAwaiter().GetResult();
    Check(run.Result == ExecutionResult.Error && store.LatestResult(tc.ProjectId, tc.Id) == ExecutionResult.Error);
    var evidence = store.EvidenceFor(tc.ProjectId, run.Id).Single();
    Check(File.Exists(evidence.Path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidence.Path))) == evidence.Sha256);
    Check(store.Projects().Sum(p => store.Cases(p.Id).Count) == 71);
});
Test("Actual console process without a main window records FAIL instead of PASS", () =>
{
    string path = Path.Combine(scratch, "native-fail.sqlite");
    File.Copy(Path.GetFullPath("work/native-run-20261028/before.sqlite"), path);
    using var store = new FoundationStore(path);
    var tc = store.Cases(LaunchSmokeOrchestrator.ProjectId).Single(c => c.Id == LaunchSmokeOrchestrator.CaseId);
    var run = new LaunchSmokeOrchestrator(new ProcessWindowAdapter(), store).ExecuteAsync(tc,
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"), "MacroStudio must not match a console", Path.Combine(scratch, "fail-evidence"), 5000, ["__qa_missing_executable__"])).GetAwaiter().GetResult();
    Check(run.Result == ExecutionResult.Fail && store.LatestResult(tc.ProjectId, tc.Id) == ExecutionResult.Fail);
    Check(store.EvidenceFor(tc.ProjectId, run.Id).Count == 1 && !string.IsNullOrWhiteSpace(run.Assertion));
});
Test("Unbound TC cannot run; run/evidence constraint failure rolls back both", () =>
{
    string path = Path.Combine(scratch, "native-atomic.sqlite");
    File.Copy(Path.GetFullPath("work/native-run-20261028/before.sqlite"), path);
    using var store = new FoundationStore(path);
    var other = store.Cases("macro-studio").First(c => c.Id != LaunchSmokeOrchestrator.CaseId);
    Throws<InvalidOperationException>(() => new LaunchSmokeOrchestrator(new ProcessWindowAdapter(), store)
        .ExecuteAsync(other, new("missing.exe", "main", scratch)).GetAwaiter().GetResult());
    var tc = store.Cases("macro-studio").Single(c => c.Id == LaunchSmokeOrchestrator.CaseId);
    var run = new TestRun(tc.ProjectId, "atomic-storage-check", tc.Id, ExecutionResult.Error, "storage verification", "path", DateTimeOffset.UtcNow, "isolated", "test", "no process", JsonSerializer.Serialize(tc));
    Throws<InvalidDataException>(() => store.RecordRun(run, new(tc.ProjectId, "bad", "wrong-run", "path", "hash")));
    Check(store.Runs(tc.ProjectId).Count == 0);
    store.RecordRun(run, new(tc.ProjectId, "unique-evidence", run.Id, "path", "hash"));
    Throws<InvalidDataException>(() => store.RecordRun(run with { Id = "must-rollback" }, new(tc.ProjectId, "unique-evidence", "must-rollback", "path", "hash")));
    Check(store.Runs(tc.ProjectId).Count == 1 && store.Runs(tc.ProjectId).Single().Id == run.Id);
});
// Attach failure must not terminate or replace a user-owned process.
Test("Attach rechecks PID identity and never closes an attached process on FAIL or ERROR", () =>
{
    using var host = System.Diagnostics.Process.GetCurrentProcess();
    string executable = host.MainModule!.FileName!;
    DateTime started = host.StartTime.ToUniversalTime();
    var adapter = new ProcessWindowAdapter();
    var request = new LaunchSmokeRequest(executable, "__no_matching_main_window__", Path.Combine(scratch, "attach-evidence"), 1000,
        ExistingProcessId: host.Id, ExpectedStartTimeUtc: started);
    var failed = adapter.ExecuteAsync("attach-fail", request).GetAwaiter().GetResult();
    Check(failed.Result == ExecutionResult.Fail && !host.HasExited);
    var wrongTime = adapter.ExecuteAsync("attach-stale", request with { ExpectedStartTimeUtc = started.AddSeconds(-1) }).GetAwaiter().GetResult();
    Check(wrongTime.Result == ExecutionResult.Error && !host.HasExited);
    var wrongPath = adapter.ExecuteAsync("attach-path", request with { Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe") }).GetAwaiter().GetResult();
    Check(wrongPath.Result == ExecutionResult.Error && !host.HasExited);
    var noIdentity = adapter.ExecuteAsync("attach-no-identity", request with { ExpectedStartTimeUtc = null }).GetAwaiter().GetResult();
    Check(noIdentity.Result == ExecutionResult.Error && !host.HasExited);
});

QAStudio.Tests.SingleTextChecks.Run(Path.Combine(output, "text-assertion-checks"), Test, (ok, message) => Check(ok, message));
QAStudio.Tests.MappingChecks.Run(Path.Combine(output, "mapping-checks"), Test, (ok, message) => Check(ok, message));
total.Stop();
File.WriteAllText(Path.Combine(output, "tests.json"), JsonSerializer.Serialize(new { startedFrom = sourcePath, sourceSha256 = Convert.ToHexString(SHA256.HashData(source)), total = results.Count, passed = results.Count - failures, failed = failures, seconds = total.Elapsed.TotalSeconds, productTestCasesExecuted = 0, productTestCasesNotRun = 70, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"SUMMARY: {results.Count - failures}/{results.Count} passed; failures={failures}; seconds={total.Elapsed.TotalSeconds:F3}; product TCs=70 NOT RUN");
return failures == 0 ? 0 : 1;
