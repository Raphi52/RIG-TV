using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.Views;

/// <summary>
/// Dialog modal "Paramètres globaux" — édite les <see cref="GlobalSettings"/>.
/// Slider Parallelism (1-32) + Checkbox ScreenshotsLoopEnabled. Save écrit le
/// fichier JSON et ferme avec DialogResult=true. Cancel ferme sans persister.
/// </summary>
public partial class GlobalSettingsDialog : Window, INotifyPropertyChanged
{
    private readonly GlobalSettingsService _service;
    private readonly TestResultCacheService? _testCache;
    private int _parallelism;
    private bool _screenshotsLoopEnabled;
    private bool _headlessMode;
    private bool _useTestResultCache;
    private string _databaseServer = string.Empty;
    private string _databaseName = string.Empty;
    private string _databaseScanStatus = "Clique « Scanner réseau » pour découvrir les instances SQL (ou saisis le serveur).";

    /// <summary>Serveurs découverts par le scan réseau (ItemsSource de la combobox éditable).</summary>
    public ObservableCollection<string> DiscoveredServers { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Parallelism
    {
        get => _parallelism;
        set { if (_parallelism != value) { _parallelism = value; Raise(); } }
    }

    public bool ScreenshotsLoopEnabled
    {
        get => _screenshotsLoopEnabled;
        set { if (_screenshotsLoopEnabled != value) { _screenshotsLoopEnabled = value; Raise(); } }
    }

    public bool HeadlessMode
    {
        get => _headlessMode;
        set { if (_headlessMode != value) { _headlessMode = value; Raise(); } }
    }

    public bool UseTestResultCache
    {
        get => _useTestResultCache;
        set { if (_useTestResultCache != value) { _useTestResultCache = value; Raise(); } }
    }

    public string DatabaseServer
    {
        get => _databaseServer;
        set { if (_databaseServer != value) { _databaseServer = value; Raise(); } }
    }

    public string DatabaseName
    {
        get => _databaseName;
        set { if (_databaseName != value) { _databaseName = value; Raise(); } }
    }

    public string DatabaseScanStatus
    {
        get => _databaseScanStatus;
        set { if (_databaseScanStatus != value) { _databaseScanStatus = value; Raise(); } }
    }

    public string FilePath => _service.FilePath;

    public string CacheStatsSummary
    {
        get
        {
            if (_testCache == null) return "(cache service indisponible)";
            var (total, pass, fail) = _testCache.Stats();
            return total == 0
                ? "Cache vide (0 entrée)"
                : $"{total} entrée(s) — {pass} PASS / {fail} FAIL";
        }
    }

    public GlobalSettingsDialog(GlobalSettingsService service, TestResultCacheService? testCache = null)
    {
        _service = service;
        _testCache = testCache;
        var current = service.Current;
        _parallelism = current.Parallelism;
        _screenshotsLoopEnabled = current.ScreenshotsLoopEnabled;
        _headlessMode = current.HeadlessMode;
        _useTestResultCache = current.UseTestResultCache;
        _databaseServer = current.DatabaseServer;
        _databaseName = current.DatabaseName;
        InitializeComponent();
        DataContext = this;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var updated = new GlobalSettings
        {
            Parallelism = Parallelism,
            ScreenshotsLoopEnabled = ScreenshotsLoopEnabled,
            HeadlessMode = HeadlessMode,
            UseTestResultCache = UseTestResultCache,
            DatabaseServer = string.IsNullOrWhiteSpace(DatabaseServer) ? @"SQL-DEV\DEV" : DatabaseServer.Trim(),
            DatabaseName = string.IsNullOrWhiteSpace(DatabaseName) ? "RIG_DEV" : DatabaseName.Trim(),
            SchemaVersion = 3,
        };
        _service.Save(updated);
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Vide le cache. Appelé par le bouton "Vider le cache" du dialog.
    /// Ne ferme pas le dialog — l'utilisateur peut continuer à éditer.
    /// </summary>
    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (_testCache == null) return;
        var (total, _, _) = _testCache.Stats();
        if (total == 0) return;
        var confirm = MessageBox.Show(
            $"Supprimer les {total} entrées du cache test-results ?",
            "Vider le cache",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        _testCache.Clear();
        Raise(nameof(CacheStatsSummary));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // fix-ok: 6e édition COUPLÉE de la feature sélecteur-DB (handler référencé par le XAML), pas un fix aveugle.
    /// <summary>
    /// Scan réseau des instances SQL (SqlDataSourceEnumerator, UDP 1434 / SQL Browser).
    /// Souvent lent et fréquemment vide selon le réseau → ne bloque jamais l'UI (Task.Run)
    /// et la saisie manuelle du serveur reste le fallback. Le serveur courant est préservé.
    /// </summary>
    private async void ScanServers_Click(object sender, RoutedEventArgs e)
    {
        var current = DatabaseServer;
        DatabaseScanStatus = "Scan réseau en cours… (UDP 1434, peut prendre quelques secondes)";
        try
        {
            var found = await Task.Run(() =>
            {
                var list = new System.Collections.Generic.List<string>();
                var table = System.Data.Sql.SqlDataSourceEnumerator.Instance.GetDataSources();
                foreach (System.Data.DataRow r in table.Rows)
                {
                    var srv = r["ServerName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(srv)) continue;
                    var inst = r["InstanceName"]?.ToString();
                    list.Add(string.IsNullOrWhiteSpace(inst) ? srv! : $"{srv}\\{inst}");
                }
                return list;
            });

            DiscoveredServers.Clear();
            foreach (var s in found.Distinct().OrderBy(x => x, System.StringComparer.OrdinalIgnoreCase))
                DiscoveredServers.Add(s);

            // Préserve le serveur saisi/persisté même absent de la liste découverte.
            if (!string.IsNullOrWhiteSpace(current)) DatabaseServer = current;

            DatabaseScanStatus = DiscoveredServers.Count == 0
                ? "Aucune instance détectée (UDP 1434 souvent filtré) — saisis le serveur à la main."
                : $"{DiscoveredServers.Count} instance(s) détectée(s).";
        }
        catch (System.Exception ex)
        {
            DatabaseScanStatus = $"Scan échoué ({ex.GetType().Name}) — saisis le serveur à la main.";
        }
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}
