using System.ComponentModel;
using System.Runtime.CompilerServices;
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
            SchemaVersion = _service.Current.SchemaVersion,
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

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}
