using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QAStudio.Application;
using QAStudio.Domain;
using QAStudio.Infrastructure;

namespace QAStudio.Tests;

// WHAT: Verify target-mismatch preconditions, shared absence verdicts and v3->v4 migration.
// WHY: ON/restriction/process/foreground evidence is required in addition to silence.
// CONTRACT: Fake adapters and disposable SQLite copies; no real input or production writes.
// FAILURE: Any bad verdict, lost row/trigger, non-idempotent migration or saved false PASS fails.
// TEST: --day8 <output> <read-only-before-day8.sqlite>; existing f/ff/mapping checks included.
internal static class Day8Checks
{
    public static int Run(string directory, string baseline)
    {
        Directory.CreateDirectory(directory); var results = new List<object>(); int failures = 0; var total = Stopwatch.StartNew();
        void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new { name, status = "PASS", milliseconds = timer.Elapsed.TotalMilliseconds }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        SingleTextRequest Request(bool off = false) => new("macro-studio", off ? "MAC-NP-003" : "MAC-NP-004", "", "F3", directory, "FAKE INPUT / OBSERVER ONLY",
            LinkedTestCaseId: off ? "MAC-QA-003" : "MAC-QA-004", Expectation: off ? TextExpectation.NoOutputWhileMacroOff : TextExpectation.NoOutputWhileTargetMismatch);
        TestCase Case(bool off = false) => new("macro-studio", off ? "MAC-QA-003" : "MAC-QA-004", new Dictionary<string,string>
        { ["TC ID"] = off ? "MAC-QA-003" : "MAC-QA-004", ["우선순위"] = "P1", ["QA 기법"] = "Negative fixture", ["테스트 목적"] = "isolated", ["수행 절차"] = "fake only", ["기대 결과"] = "absence" });
        TextExecution Execute(Probe probe, bool off = false, RecordingStore? store = null) => new SingleTextOrchestrator().ExecuteAsync(probe, Request(off),
            store: store ?? new(), registeredCase: Case(off), trigger: probe).GetAwaiter().GetResult();
        Test("Target mismatch ON+restriction+live different target and >=2s silence append PASS", () =>
        {
            using var store = new FoundationStore(Path.Combine(directory,"negative.sqlite")); store.RegisterProject(new("macro-studio","fixture"));
            byte[] bytes = [8]; store.CommitNew("fixture", new(bytes,Convert.ToHexString(SHA256.HashData(bytes)),[Case()],[],[]));
            store.SetAutomationMapping("macro-studio","MAC-QA-004","MAC-NP-004");
            var probe = new Probe();
            var result = new SingleTextOrchestrator().ExecuteAsync(probe,Request(),store:store,registeredCase:Case(),trigger:probe).GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(File.ReadAllBytes(result.Evidence.Path)); var r=json.RootElement;
            var samples=r.GetProperty("observations").EnumerateArray().Skip(2).ToArray();
            Check(result.Run.Result==ExecutionResult.Pass && probe.Calls==1 && probe.MismatchChecks>2 && probe.OffChecks==0,"Separate ON precondition");
            Check(r.GetProperty("expectation").GetString()=="NoOutputWhileTargetMismatch" && samples.All(s=>s.GetProperty("Text").GetString()=="") &&
                (samples[^1].GetProperty("At").GetDateTimeOffset()-samples[0].GetProperty("At").GetDateTimeOffset()).TotalMilliseconds>=2000,"Actual >=2s samples");
            Check(store.Runs("macro-studio").Count==1 && store.EvidenceFor("macro-studio",result.Run.Id).Count==1,"Atomic append");
        });
        Test("OFF mode still requires OFF and reuses the same absence loop",()=>
        {
            var probe=new Probe{On=false}; var result=Execute(probe,off:true);
            Check(result.Run.Result==ExecutionResult.Pass && probe.OffChecks>2 && probe.MismatchChecks==0,"OFF mode preserved");
            Check(Execute(new Probe{On=true},off:true).Run.Result==ExecutionResult.Error,"ON is ERROR for OFF mode");
        });
        foreach (string fault in new[]{"off","restriction","target","matched","profile","baseline","focus"})
            Test("Target-mismatch precondition ERROR without F3: "+fault,()=>
            {
                var probe=new Probe{Fault=fault}; var result=Execute(probe);
                Check(result.Run.Result==ExecutionResult.Error && probe.Calls==0,"No silent PASS or trigger on invalid "+fault);
            });
        foreach (string fault in new[]{"trigger","read","document","reader","off-after-trigger","target-after-trigger"})
            Test("Target-mismatch execution ERROR: "+fault,()=>Check(Execute(new Probe{Fault=fault}).Run.Result==ExecutionResult.Error,"Observation/trigger guard"));
        Test("Any character FAILs and cannot recover to empty PASS",()=>Check(Execute(new Probe{Output="f"}).Run.Result==ExecutionResult.Fail,"Character FAIL"));
        Test("Late unexpected text before observation deadline FAILs",()=>Check(Execute(new Probe{Output="x",OutputAt=1800}).Run.Result==ExecutionResult.Fail,"Late text FAIL"));
        Test("Mismatch cannot reuse OFF scenario ID or short observation",()=>
        {
            foreach(var request in new[]{Request() with{ScenarioId="MAC-NP-003"},Request() with{StabilizationMilliseconds=100}})
            {
                bool rejected=false; try{new SingleTextOrchestrator().ExecuteAsync(new Probe(),request).GetAwaiter().GetResult();}catch(ArgumentException){rejected=true;}
                Check(rejected,"Explicit scenario and >=2s required");
            }
        });
        Test("Target-mismatch DB save failure leaves ERROR JSON",()=>
        {
            var before=Directory.GetFiles(directory,"TEXT-*.json").ToHashSet(); bool failed=false;
            try{Execute(new Probe(),store:new RecordingStore{Fail=true});}catch(IOException){failed=true;}
            using var json=JsonDocument.Parse(File.ReadAllBytes(Directory.GetFiles(directory,"TEXT-*.json").Single(p=>!before.Contains(p))));
            Check(failed && json.RootElement.GetProperty("result").GetString()=="ERROR","No saved false PASS");
        });
        string Copy(string name){string path=Path.Combine(directory,name+".sqlite");using var source=new SqliteDatabase(baseline);source.BackupTo(path);return path;}
        string Rows(SqliteDatabase raw)=>JsonSerializer.Serialize(raw.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('manual_runs','case_assessments') ORDER BY name").Select(r=>new{table=r[0],rows=raw.Query("SELECT * FROM \""+r[0]+"\" ORDER BY rowid")}));
        string Triggers(SqliteDatabase raw)=>JsonSerializer.Serialize(raw.Query("SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name!='manual_runs' ORDER BY name"));
        Test("v3->v4 full row/trigger preservation and repeated reopen is idempotent",()=>
        {
            string path=Copy("migration"); string rows,triggers;
            using(var raw=new SqliteDatabase(path)){rows=Rows(raw);triggers=Triggers(raw);Check(raw.Query("PRAGMA user_version")[0][0]=="3","Actual v3 baseline");}
            for(int i=0;i<2;i++){using var migrated=new FoundationStore(path);Check(migrated.AutomationMappings("macro-studio").Count==3,"Three original mappings");}
            using var inspect=new SqliteDatabase(path);
            Check(Rows(inspect)==rows && Triggers(inspect)==triggers,"Every row and trigger unchanged after reopen");
            Check(inspect.Query("PRAGMA user_version")[0][0]==FoundationStore.SchemaVersion.ToString() && inspect.Query("PRAGMA integrity_check")[0][0]=="ok" && inspect.Query("PRAGMA foreign_key_check").Count==0,"Current schema integrity");
            inspect.Execute("INSERT INTO tc_automation_mappings VALUES('macro-studio','MAC-001','MAC-NP-004')");
        });
        Test("v4 migration retains mapping-attached triggers and explicit index",()=>
        {
            string path=Copy("attached");
            using(var raw=new SqliteDatabase(path)){raw.Execute("CREATE TRIGGER mapping_fixture_guard BEFORE DELETE ON tc_automation_mappings BEGIN SELECT RAISE(ABORT,'immutable fixture'); END");raw.Execute("CREATE INDEX mapping_fixture_index ON tc_automation_mappings(scenario_id)");}
            using(var migrated=new FoundationStore(path)){}
            using var inspect=new SqliteDatabase(path);bool blocked=false;try{inspect.Execute("DELETE FROM tc_automation_mappings");}catch(InvalidDataException){blocked=true;}
            Check(blocked && inspect.Query("SELECT name FROM sqlite_master WHERE name IN ('mapping_fixture_guard','mapping_fixture_index')").Count==2 && inspect.Query("SELECT * FROM tc_automation_mappings").Count==3,"Retained objects enforce");
        });
        Test("v4 migration failure rolls back data/schema/version",()=>
        {
            string path=Copy("rollback");using(var raw=new SqliteDatabase(path)){raw.Execute("CREATE TABLE tc_automation_mappings_v5(blocker TEXT)");}
            bool failed=false;try{using var migrated=new FoundationStore(path);}catch(InvalidDataException){failed=true;}
            using var inspect=new SqliteDatabase(path);Check(failed && inspect.Query("PRAGMA user_version")[0][0]=="3" && inspect.Query("SELECT * FROM tc_automation_mappings").Count==3,"Atomic failure");
        });
        SingleTextChecks.Run(Path.Combine(directory,"existing-text"),Test,Check);
        MappingChecks.Run(Path.Combine(directory,"existing-mapping"),Test,Check);
        total.Stop();File.WriteAllText(Path.Combine(directory,"tests.json"),JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failures,failed=failures,seconds=total.Elapsed.TotalSeconds,realProductExecutions=0,tests=results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"SUMMARY {results.Count-failures}/{results.Count}; failed={failures}; {total.Elapsed.TotalSeconds:F3}s; real product executions=0");return failures==0?0:1;
    }
    private sealed class Probe : ITargetMismatchTextProbe,IMacroOffTextProbe,ITextTrigger
    {
        public bool On{get;init;}=true; public string Fault{get;init;}=""; public string Output{get;init;}="";public int OutputAt{get;init;}
        public int Calls{get;private set;} public int MismatchChecks{get;private set;}public int OffChecks{get;private set;}private readonly Stopwatch watch=new();
        public void VerifyMacroOff(){OffChecks++;if(On)throw new InvalidOperationException("ON forbidden for OFF mode");}
        public void VerifyTargetMismatch(){MismatchChecks++;if(!On || Fault is "off" or "restriction" or "target" or "matched" or "focus" || Calls>0 && Fault is "off-after-trigger" or "target-after-trigger")throw new InvalidOperationException("Unverified ON/restriction/target/focus/mismatch");}
        public TextSample Arm(CancellationToken token){if(Fault=="profile")throw new InvalidOperationException("Wrong profile");return new(DateTimeOffset.UtcNow,Fault=="baseline"?"existing text":"","fake UIA","fixture document");}
        public TextSample Read(){if(Calls>0 && Fault=="read")throw new InvalidOperationException("Unreadable/focus lost");return new(DateTimeOffset.UtcNow,Calls>0 && watch.ElapsedMilliseconds>=OutputAt?Output:"",Calls>0 && Fault=="reader"?"changed":"fake UIA",Calls>0 && Fault=="document"?"changed":"fixture document");}
        public void Fire(CancellationToken token,Action<string> log){Calls++;if(Fault=="trigger")throw new InvalidOperationException("Fake SendInput 0/2");watch.Start();}
        public object Diagnostics=>new{method="FAKE INPUT ONLY",calls=Calls}; public void Dispose(){}
    }
    private sealed class RecordingStore:IExecutionStore{public bool Fail{get;init;}public void RecordRun(TestRun run,Evidence evidence){if(Fail)throw new IOException("Fake DB save failure");}}
}
