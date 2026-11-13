using QAStudio.Adapters;
using QAStudio.Application;
using QAStudio.Infrastructure;

namespace QAStudio.Presentation.WinForms;

// WHAT: Native desktop composition boundary only; screens are Phase 2.
// WHY: UI can consume Phase 1 services directly without localhost or the legacy Python process.
// CONTRACT: Caller supplies the data directory and owns disposal; no application/global input is launched.
// FAILURE: Storage/import exceptions propagate for the future UI to display.
// TEST: Solution builds against WindowsDesktop; service behavior is tested in the foundation test harness.
public sealed class FoundationComposition : IDisposable
{
    public FoundationStore Store { get; }
    public ImportService Imports { get; }
    public FoundationComposition(string dataDirectory)
    {
        Store = new(Path.Combine(dataDirectory, "qa-studio.sqlite"));
        Imports = new(new WorkbookAdapter(), Store);
    }
    public void Dispose() => Store.Dispose();
}
