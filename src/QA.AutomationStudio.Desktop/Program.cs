namespace QA.AutomationStudio.Desktop;

// WHAT: Start the Windows native STA message loop and the actual MainForm.
// WHY: The desktop product must open its own Form without a browser or server process.
// CONTRACT: .NET 10 Windows Forms, x64; no Python or HTTP listener; target launch occurs only on explicit TC execution.
// FAILURE: Standard WinForms startup errors remain actual errors, never a successful smoke check.
// TEST: Release build/publish plus launch and inspection of the published EXE's native controls.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 3 && args[0] == "--verify-import")
        {
            Environment.ExitCode = ImportVerification.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            return;
        }
        Application.Run(new MainForm());
    }
}
