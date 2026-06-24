using System;
using System.Windows;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer;

public partial class App : Application
{
    /// <summary>
    /// Parse les arguments de ligne de commande AVANT le démarrage de la fenêtre.
    /// Surcharges DB : <c>--server=&lt;instance&gt;</c>, <c>--db=&lt;base&gt;</c>
    /// (alias <c>--database=</c>), <c>--connection=&lt;chaîne complète&gt;</c>.
    /// Stockées dans <see cref="DatabaseStartup"/> (précédence CLI &gt; persisté &gt; défaut),
    /// appliquées par le constructeur du MainWindowViewModel.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        ParseDbArgs(e.Args);
        base.OnStartup(e);
    }

    private static void ParseDbArgs(string[] args)
    {
        if (args == null) return;
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var a = raw.Trim();
            if (TryOption(a, "--server=", out var srv)) DatabaseStartup.CliServer = srv;
            else if (TryOption(a, "--db=", out var db)) DatabaseStartup.CliDatabase = db;
            else if (TryOption(a, "--database=", out var db2)) DatabaseStartup.CliDatabase = db2;
            else if (TryOption(a, "--connection=", out var cs)) DatabaseStartup.CliConnection = cs;
        }
    }

    private static bool TryOption(string arg, string prefix, out string value)
    {
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg.Substring(prefix.Length).Trim().Trim('"');
            return value.Length > 0;
        }
        value = string.Empty;
        return false;
    }
}
