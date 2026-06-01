using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

/// <summary>
/// VM du panneau de droite « détail d'un scénario ». Affiche le contexte
/// (pourquoi ce test existe, source xUnit, summary) et expose la réponse
/// HTTP attendue en champs éditables (status code, headers, body).
/// </summary>
public sealed partial class ScenarioDetailViewModel : ObservableObject
{
    private readonly ScenarioFileService _files;
    private readonly TestSourceExtractor _sources;
    private readonly RegressionRunner? _runner;
    private readonly Action<string>? _jumpToXUnit;

    private ScenarioFileMetadata? _metadata;

    public ScenarioDetailViewModel(
        ScenarioFileService files,
        TestSourceExtractor sources,
        RegressionRunner? runner = null,
        Action<string>? jumpToXUnit = null)
    {
        _files = files;
        _sources = sources;
        _runner = runner;
        _jumpToXUnit = jumpToXUnit;
        Headers = new ObservableCollection<HeaderEntryViewModel>();
        Headers.CollectionChanged += (_, _) => IsDirty = true;
        AvailableStatusCodes = new[] { 200, 201, 204, 301, 302, 400, 401, 403, 404, 409, 422, 500, 502, 503 };
    }

    // ── Context (read-only) ──────────────────────────────────────────────
    [ObservableProperty] private string? fileName;
    [ObservableProperty] private string? className;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JumpToXUnitCommand))]
    private string? methodName;
    [ObservableProperty] private string? backend;
    [ObservableProperty] private string? otherParamsLabel;
    [ObservableProperty] private string? summary;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInExplorerCommand))]
    private string? sourceFilePath;
    [ObservableProperty] private int sourceLineNumber;
    [ObservableProperty] private string? sourceBody;

    // ── Requête (déduite, read-only) ────────────────────────────────────
    [ObservableProperty] private string? requestMethodAndPath;
    [ObservableProperty] private string? requestAuth;

    // ── Response (editable) ──────────────────────────────────────────────
    [ObservableProperty] private int? statusCode;
    public ObservableCollection<HeaderEntryViewModel> Headers { get; }
    [ObservableProperty] private bool hasBody;
    [ObservableProperty] private string bodyPretty = "";
    public int[] AvailableStatusCodes { get; }

    // ── Dirty tracking ──────────────────────────────────────────────────
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private string? statusMessage;

    // Mute les triggers IsDirty pendant le load.
    private bool _suspendDirty;

    partial void OnStatusCodeChanged(int? value)        { if (!_suspendDirty) IsDirty = true; }
    partial void OnBodyPrettyChanged(string value)      { if (!_suspendDirty) IsDirty = true; }

    /// <summary>Charge tous les champs depuis le .verified.json + la source.</summary>
    public void Load(ScenarioFileInfo info)
    {
        _suspendDirty = true;
        try
        {
            _metadata = info.Metadata;
            FileName    = info.FileName;
            ClassName   = _metadata.ClassName;
            MethodName  = _metadata.MethodName;
            Backend     = _metadata.Backend;
            OtherParamsLabel = _metadata.OtherParams.Count == 0
                ? null
                : string.Join(" · ", _metadata.OtherParams.Select(kv => $"{kv.Key}={kv.Value}"));

            // Source + summary
            var src = _sources.Find(_metadata.ClassName, _metadata.MethodName);
            Summary          = src?.Summary;
            SourceFilePath   = src?.FilePath;
            SourceLineNumber = src?.LineNumber ?? 0;
            SourceBody       = src?.Body;

            // Heuristique pour la requête HTTP : on cherche un .GetAsync/.PostAsync dans le snippet.
            (RequestMethodAndPath, RequestAuth) = ExtractRequest(src?.Body, _metadata);

            // Contenu Verify
            var content = _files.ReadStructured(info.FileName);
            if (content is null)
            {
                StatusCode = null;
                Headers.Clear();
                HasBody = false;
                BodyPretty = "";
                IsLoaded = false;
                StatusMessage = "Snapshot illisible (JSON invalide ?)";
                return;
            }

            StatusCode = content.StatusCode;
            Headers.Clear();
            foreach (var h in content.Headers)
            {
                var entry = new HeaderEntryViewModel(h.Name, h.Value);
                entry.Changed += () => { if (!_suspendDirty) IsDirty = true; };
                Headers.Add(entry);
            }
            HasBody = content.HasBody;
            BodyPretty = content.BodyPretty;
            // RawExtras (numbers / arrays / objects bruts) — conservés invisibles, pour round-trip propre.
            _rawExtrasSnapshot = content.RawExtras;

            IsLoaded = true;
            IsDirty = false;
            StatusMessage = null;
        }
        finally
        {
            _suspendDirty = false;
        }
    }

    private System.Collections.Generic.Dictionary<string, string> _rawExtrasSnapshot =
        new System.Collections.Generic.Dictionary<string, string>();

    private static (string?, string?) ExtractRequest(string? body, ScenarioFileMetadata? meta)
    {
        if (string.IsNullOrEmpty(body)) return (null, null);
        // .GetAsync("/api/v1/kbis/123456789", JwtFor(...)) ou .PostAsync("...")
        var verb = System.Text.RegularExpressions.Regex.Match(body!,
            @"\.(?<verb>Get|Post|Put|Delete|Patch)Async\s*\(\s*""(?<path>[^""]+)""");
        string? methodPath = null;
        if (verb.Success)
        {
            methodPath = verb.Groups["verb"].Value.ToUpperInvariant() + "  " + verb.Groups["path"].Value;
        }
        else if (meta is not null)
        {
            methodPath = "GET  /api/v1/<endpoint> (déduit indisponible — lire snippet)";
        }

        // JwtFor(tenant: "0203", roles: ...) — chope les arguments
        var jwt = System.Text.RegularExpressions.Regex.Match(body!,
            @"JwtFor\s*\(\s*(?<args>[^)]+)\)");
        string? auth = jwt.Success ? "JWT  " + jwt.Groups["args"].Value.Trim() : null;

        return (methodPath, auth);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (FileName is null) return;
        try
        {
            var content = new ScenarioContent
            {
                FileName = FileName,
                StatusCode = StatusCode,
                HasBody = HasBody,
                BodyPretty = BodyPretty,
            };
            foreach (var h in Headers) content.Headers.Add(new ScenarioHeader(h.Name, h.Value));
            foreach (var kv in _rawExtrasSnapshot) content.RawExtras[kv.Key] = kv.Value;

            _files.Write(FileName, content);
            IsDirty = false;
            StatusMessage = $"💾 Sauvegardé à {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusMessage = "✗ Échec sauvegarde : " + ex.Message;
            MessageBox.Show(ex.ToString(), "Save scenario",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private bool CanSave() => IsDirty && IsLoaded;

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        if (_metadata is null || FileName is null) return;
        Load(new ScenarioFileInfo(FileName, 0));
        StatusMessage = "↻ Restauré depuis disque";
    }
    private bool CanRevert() => IsDirty && IsLoaded;

    [RelayCommand(CanExecute = nameof(CanRerun))]
    private async Task RerunAsync()
    {
        if (_runner is null || _metadata is null) return;
        try
        {
            StatusMessage = "▶ Re-run en cours…";
            await _runner.RunAsync($"DisplayName~{_metadata.MethodName}");
            StatusMessage = "✓ Re-run terminé — vérifie l'onglet xUnit pour le résultat";
        }
        catch (Exception ex)
        {
            StatusMessage = "✗ Re-run échoué : " + ex.Message;
        }
    }
    private bool CanRerun() => _runner is not null && IsLoaded && _runner.ProjectExists && !_runner.IsRunning;

    partial void OnIsDirtyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenInExplorer))]
    private void OpenInExplorer()
    {
        if (string.IsNullOrEmpty(SourceFilePath))
        {
            StatusMessage = "✗ Source .cs introuvable pour ce test (extracteur n'a pas trouvé le fichier).";
            return;
        }
        if (!File.Exists(SourceFilePath))
        {
            StatusMessage = $"✗ Source path inexistant sur disque : {SourceFilePath}";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + SourceFilePath + "\"");
            StatusMessage = $"📂 Ouvert dans l'explorateur";
        }
        catch (Exception ex)
        {
            StatusMessage = "✗ Échec ouverture explorateur : " + ex.Message;
        }
    }
    private bool CanOpenInExplorer() => !string.IsNullOrEmpty(SourceFilePath);

    /// <summary>Saute à l'onglet xUnit, sélectionne le test qui a produit ce snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanJumpToXUnit))]
    private void JumpToXUnit()
    {
        Log.Info($"JumpToXUnit clicked — _jumpToXUnit null? {_jumpToXUnit is null}, MethodName='{MethodName}'");
        if (_jumpToXUnit is null)
        {
            StatusMessage = "✗ Navigation indisponible (jump action non câblée).";
            Log.Error("JumpToXUnit: _jumpToXUnit callback is null — MainVM didn't wire it");
            return;
        }
        if (string.IsNullOrEmpty(MethodName))
        {
            StatusMessage = "✗ Nom de méthode introuvable pour ce scénario.";
            Log.Warn("JumpToXUnit: MethodName is null/empty");
            return;
        }
        _jumpToXUnit(MethodName!);
        // Si l'invocation ne sélectionne rien côté MainVM (catalogue xUnit vide ou
        // méthode absente), le MainVM met à jour StatusMessage lui-même.
    }
    private bool CanJumpToXUnit() => _jumpToXUnit is not null && !string.IsNullOrEmpty(MethodName);
}

public sealed partial class HeaderEntryViewModel : ObservableObject
{
    public HeaderEntryViewModel(string name, string value)
    {
        this.name = name;
        this.value = value;
    }

    [ObservableProperty] private string name;
    [ObservableProperty] private string value;

    public event Action? Changed;

    partial void OnNameChanged(string value) => Changed?.Invoke();
    partial void OnValueChanged(string value) => Changed?.Invoke();
}
