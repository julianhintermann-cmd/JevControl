using System.Windows;

namespace Kairo.TestTargetApp;

public partial class App : Application
{
    /// <summary>Output file for submitted values (argument --out &lt;path&gt;).</summary>
    public static string? OutputPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var index = Array.IndexOf(e.Args, "--out");
        if (index >= 0 && index + 1 < e.Args.Length) { OutputPath = e.Args[index + 1]; }
        base.OnStartup(e);
    }
}
