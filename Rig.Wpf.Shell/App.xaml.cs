using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Core.DependencyInjection;
using Rig.Wpf.LegacyBridge.DependencyInjection;
using Rig.Wpf.LegacyHost.DependencyInjection;
using Rig.Wpf.RigMetier.DependencyInjection;
using Rig.Wpf.RigMetier.Legacy;
using Rig.Wpf.RigMetier.Legacy.DependencyInjection;
using Rig.Wpf.RigMetier.Sql.DependencyInjection;
using Rig.Wpf.Mvvm;
using Rig.Wpf.Mvvm.DependencyInjection;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.Processus.Ipe;
using Rig.Wpf.Processus.Ipe.DependencyInjection;
using Rig.Wpf.Processus.Kbis;
using Rig.Wpf.Processus.Mandataire;
using Rig.Wpf.Processus.Testnlh;
using Rig.Wpf.Shell.Bridges;
using Rig.Wpf.Shell.Demo;
using Rig.Wpf.Shell.ViewModels;
using Serilog;

namespace Rig.Wpf.Shell;

public partial class App : Application
{
    private const string CohabitationMutexName = @"Global\RigWpfShell";
    private IHost? _host;
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Handlers globaux d'exception : log + ne pas crasher l'app.
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try
            {
                var msg = ex.ExceptionObject?.ToString() ?? "(null)";
                File.AppendAllText(Path.Combine(GetLogDirectory(), "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " AppDomain.UnhandledException:" + Environment.NewLine + msg + Environment.NewLine + Environment.NewLine);
            }
            catch { /* best-effort */ }
        };
        DispatcherUnhandledException += (s, ex) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(GetLogDirectory(), "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " Dispatcher.UnhandledException:" + Environment.NewLine + ex.Exception + Environment.NewLine + Environment.NewLine);
            }
            catch { /* best-effort */ }
            // Defense-in-depth : si une commande VM oublie son try/catch
            // (ex. legacy repository qui throw NotSupportedException), on
            // ne crashe PAS le shell. Le bug doit être loggué et corrigé
            // côté VM, pas matérialisé par un kill du process utilisateur.
            ex.Handled = true;
        };

        // Résolveur runtime : permet de charger les assemblies legacy depuis
        // C:\rig\Bin Dot Net Gac\ et C:\rig\Bin Processus\ sans dupliquer 400+
        // DLLs dans le dossier de sortie du shell. Indispensable dès qu'un plugin
        // legacy est instancié via WindowsFormsHost (FormAutomate, RigControls, etc.).
        AppDomain.CurrentDomain.AssemblyResolve += LegacyAssemblyResolver;

        // Mutex de cohabitation : permet de détecter qu'une autre instance
        // (Shell WPF OU RigClientAccueil) tourne déjà. On ne refuse PAS de
        // démarrer (c'est précisément le sens de la cohabitation), mais on
        // logue un warning pour aider au diagnostic des conflits éventuels
        // sur les singletons partagés (RigConsoleAccueil.Connexion).
        _mutex = new Mutex(initiallyOwned: false, name: CohabitationMutexName,
            createdNew: out var createdNew);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(GetLogDirectory(), "rig-wpf-shell-.log"),
                rollingInterval: RollingInterval.Day,
                shared: true)
            .CreateLogger();

        if (!createdNew)
        {
            Log.Warning("Une autre instance RIG semble en cours (mutex {MutexName} déjà détenu).",
                CohabitationMutexName);
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(configuration);

                services.AddRigWpfCore();
                services.AddRigWpfMvvm();

                var pluginDir = configuration["RigWpf:PluginDirectory"] ?? @"C:\rig\Bin Processus";
                services.AddRigWpfLegacyHost(opts => opts.PluginDirectory = pluginDir);

                // Couche données — 3 implémentations possibles (priorité Sql > Legacy > InMemory) :
                //  - UseSqlRigMetier=true + SqlConnectionString=... → SqlMandataireRepository (ADO.NET direct, recommandé)
                //  - UseLegacyRigMetier=true → wrappers RIG.METIER.* (lecture OK, Save throw)
                //  - sinon → in-memory (par défaut, données perdues au redémarrage)
                services.AddRigMetierInMemory();
                if (string.Equals(configuration["RigWpf:UseLegacyRigMetier"], "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    services.AddRigMetierLegacy();
                }
                if (string.Equals(configuration["RigWpf:UseSqlRigMetier"], "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var sqlConn = configuration["RigWpf:SqlConnectionString"];
                    if (string.IsNullOrWhiteSpace(sqlConn))
                    {
                        Log.Warning("RigWpf:UseSqlRigMetier=true mais SqlConnectionString vide — fallback sur l'enregistrement précédent.");
                    }
                    else
                    {
                        services.AddRigMetierSql(opts => opts.ConnectionString = sqlConn!);
                        Log.Information("Repository SQL direct activé (mandataires).");
                    }
                }

                services.AddSingleton<IDispatcherService>(_ =>
                    new WpfDispatcherService(Current.Dispatcher));

                services.AddTestnlhProcessus();
                services.AddMandataireProcessus();
                services.AddIpeProcessus();
                services.AddRigWpfDemo();
                services.AddKbisProcessus();

                services.AddSingleton(sp =>
                    new SessionInfo(sp.GetRequiredService<IConfiguration>()));

                services.AddSingleton<ShellViewModel>(sp =>
                    new ShellViewModel(
                        sp.GetRequiredService<IPluginLoader>(),
                        sp.GetRequiredService<INativeProcessusRegistry>(),
                        sp.GetRequiredService<SessionInfo>()));

                services.AddSingleton<LoginViewModel>();

                services.AddSingleton<IFormAccueilBridge>(sp =>
                    new WpfFormAccueilBridge(
                        sp.GetRequiredService<ShellViewModel>(),
                        sp.GetRequiredService<IDispatcherService>()));

                services.AddRigLegacyBridge();

                services.AddSingleton<MainWindow>();
            })
            .Build();

        // Initialisation du runtime RigMetier legacy (P-invoke, Common.Init)
        // si activé en config.
        if (string.Equals(configuration["RigWpf:UseLegacyRigMetier"], "true",
                StringComparison.OrdinalIgnoreCase))
        {
            var defaultGreffe = configuration["RigWpf:DefaultGreffe"];
            if (!string.IsNullOrWhiteSpace(defaultGreffe))
            {
                var bootstrap = _host.Services.GetRequiredService<LegacyRigMetierBootstrap>();
                if (bootstrap.Initialize(defaultGreffe!))
                    Log.Information("RigMetier legacy initialisé sur le greffe {Code}.", defaultGreffe);
                else
                    Log.Warning("RigMetier legacy n'a pas pu être initialisé sur {Code} — fallback vers in-memory.",
                        defaultGreffe);
            }
        }

        // Installation de l'adapter dans le singleton statique legacy
        // FormAccueil.FrmAccueil. À faire AVANT toute ouverture de plugin
        // legacy (sinon ils utilisent le FakeFormAccueil par défaut).
        LegacyBridgeServiceCollectionExtensions.InstallLegacyBridge(_host.Services);

        // Câblage du registry à partir des registrations (NativeProcessusRegistration)
        // et de la liste éventuellement filtrée par appsettings (UseNativeFor).
        WireUpNativeRegistry(_host.Services, configuration);

        // Pré-ouvre le tab pilote pour valider la chaîne sans attendre un click utilisateur
        // (utile en smoke test FlaUI). Désactivable via la config.
        var shell = _host.Services.GetRequiredService<ShellViewModel>();
        var preopen = configuration.GetSection("RigWpf:PreOpenTabs").Get<string[]>() ?? Array.Empty<string>();
        foreach (var code in preopen)
        {
            try { shell.OpenTabCommand.Execute(code); }
            catch (Exception ex) { Log.Warning(ex, "Échec pré-ouverture du tab {Code}", code); }
        }

        Log.Information("[STARTUP] Pre-open termine, instanciation MainWindow...");
        MainWindow window;
        try
        {
            window = _host.Services.GetRequiredService<MainWindow>();
            Log.Information("[STARTUP] MainWindow instanciee OK");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[STARTUP] ECHEC instanciation MainWindow");
            try {
                File.AppendAllText(Path.Combine(GetLogDirectory(), "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " STARTUP MainWindow ctor: " +
                    Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            } catch { }
            throw;
        }
        window.DataContext = shell;
        MainWindow = window;
        try
        {
            window.Show();
            Log.Information("[STARTUP] window.Show() OK");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[STARTUP] ECHEC window.Show()");
            try {
                File.AppendAllText(Path.Combine(GetLogDirectory(), "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " STARTUP window.Show: " +
                    Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            } catch { }
            throw;
        }

        // Mode diagnostic : cycle automatique sur chaque tab pour forcer
        // l'instanciation de chaque plugin et capturer les méthodes IFormAccueil
        // qu'ils appellent au load. Désactivable via RigWpf:DiagCycleTabs.
        if (string.Equals(configuration["RigWpf:DiagCycleTabs"], "true",
                StringComparison.OrdinalIgnoreCase))
        {
            _ = window.Dispatcher.InvokeAsync(async () =>
            {
                foreach (var tab in shell.Tabs.ToList())
                {
                    Log.Information("[DIAG] Activation tab {Code}", tab.CodeProcessus);
                    shell.SelectedTab = tab;
                    await System.Threading.Tasks.Task.Delay(800);
                }
                Log.Information("[DIAG] Cycle des tabs terminé.");
            });
        }
    }

    private static readonly string[] LegacyProbingPaths =
    {
        @"C:\rig\Bin Dot Net Gac",
        @"C:\rig\Bin Processus",
        @"C:\rig\Bin",
        @"C:\rig\Bin Externe",
        @"C:\rig\exe",
    };

    private static Assembly? LegacyAssemblyResolver(object? sender, ResolveEventArgs args)
    {
        // Évite les boucles : on ne tente le résolveur que pour les assemblies
        // dont le nom simple ressemble à du legacy RIG (Rig*, RIG.*, Ami*, etc.).
        var asmName = new AssemblyName(args.Name);
        var simpleName = asmName.Name;
        if (string.IsNullOrEmpty(simpleName)) return null;
        if (simpleName!.StartsWith("Rig.Wpf.", StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var probingPath in LegacyProbingPaths)
        {
            var candidate = Path.Combine(probingPath, simpleName + ".dll");
            if (File.Exists(candidate))
            {
                try
                {
                    var loaded = Assembly.LoadFrom(candidate);
                    Log.Debug("Legacy assembly résolu : {Name} -> {Path}", simpleName, candidate);
                    return loaded;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Échec chargement legacy {Name} depuis {Path}", simpleName, candidate);
                }
            }
        }
        return null;
    }

    private static void WireUpNativeRegistry(IServiceProvider services, IConfiguration configuration)
    {
        var registry = services.GetRequiredService<INativeProcessusRegistry>();
        var allRegs = services.GetServices<NativeProcessusRegistration>();

        // Tous les Processus inscrits via AddNativeProcessus<>() sont actifs par défaut.
        // Pour forcer un Processus en mode legacy (rollback runtime), ajouter son code
        // à RigWpf:DisableNativeFor dans appsettings.json.
        var disabled = new HashSet<string>(
            configuration.GetSection("RigWpf:DisableNativeFor").Get<string[]>() ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var reg in allRegs)
        {
            if (disabled.Contains(reg.CodeProcessus))
            {
                Log.Information("Processus {Code} disponible en natif mais désactivé par DisableNativeFor (mode legacy).",
                    reg.CodeProcessus);
                continue;
            }

            if (registry is NativeProcessusRegistry concrete)
            {
                concrete.Register(reg.CodeProcessus, reg.ViewModelType);
                Log.Information("Processus natif enregistré : {Code} -> {Type}",
                    reg.CodeProcessus, reg.ViewModelType.Name);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.Dispose();
            _mutex?.Dispose();
            Log.CloseAndFlush();
        }
        finally
        {
            base.OnExit(e);
        }
    }

    private static string GetLogDirectory()
    {
        try
        {
            var dir = @"C:\rig\logs";
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), "rig-wpf-logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
