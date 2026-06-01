using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Rig.Wpf.Processus.Kbis.Etapes;

namespace Rig.Wpf.Processus.Kbis.Views;

public partial class KbisConsultationEtapeView : UserControl
{
    private KbisConsultationEtapeViewModel? _vm;
    private string? _lastNavigatedPath;

    public KbisConsultationEtapeView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await EnsureWebView2();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = e.NewValue as KbisConsultationEtapeViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        };
        PdfView.ZoomFactorChanged += (_, _) => UpdateZoomLabel();
    }

    private void UpdateZoomLabel()
    {
        var pct = (int)Math.Round(PdfView.ZoomFactor * 100);
        ZoomLabel.Text = pct + " %";
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        var newZoom = Math.Max(0.25, Math.Round(PdfView.ZoomFactor - 0.1, 2));
        PdfView.ZoomFactor = newZoom;
        // ZoomFactorChanged ne se lève pas toujours sur set programmatique
        // (selon version WebView2). On force l'update du label.
        UpdateZoomLabel();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        var newZoom = Math.Min(5.0, Math.Round(PdfView.ZoomFactor + 0.1, 2));
        PdfView.ZoomFactor = newZoom;
        UpdateZoomLabel();
    }

    private async Task EnsureWebView2()
    {
        try
        {
            await PdfView.EnsureCoreWebView2Async();
            HardenWebView();
        }
        catch (Exception ex)
        {
            ErrorMessage.Text = "WebView2 indisponible : " + ex.Message +
                " (installe le runtime Edge WebView2 sur ce poste).";
            ErrorMessage.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Désactive tout ce qui ouvrirait un popup ou crasherait WebView2 hosté
    /// dans WPF. Le viewer PDF d'Edge propose un menu "Options / Partager"
    /// (overflow "···") qui tente d'ouvrir des popups natifs → crash silencieux
    /// du host process avec impossibilité de recharger un autre PDF ensuite.
    /// Solution : on cache la toolbar Edge entière via <c>#toolbar=0</c> (cf.
    /// <see cref="BuildPdfUri"/>) et on fournit notre propre toolbar WPF avec
    /// les seules actions qu'on contrôle (Imprimer, Télécharger, Régénérer).
    /// </summary>
    private void HardenWebView()
    {
        var core = PdfView.CoreWebView2;
        if (core is null) return;

        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        // On garde les accelerator keys browser pour que Ctrl+wheel/Ctrl++/Ctrl+−
        // pilotent le zoom du PDF nativement. Le risque popup (Ctrl+P browser,
        // Ctrl+S) est mitigé par le hook NewWindowRequested ci-dessous.
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsStatusBarEnabled = false;

        // Belt-and-suspenders : si quoi que ce soit tente un popup, on bloque.
        core.NewWindowRequested += (_, args) => { args.Handled = true; };

        // Resilience : si WebView2 crash quand même, on re-init et on re-navigue.
        core.ProcessFailed += async (_, _) =>
        {
            try
            {
                await PdfView.EnsureCoreWebView2Async();
                HardenWebView();
                if (!string.IsNullOrEmpty(_lastNavigatedPath) && File.Exists(_lastNavigatedPath))
                    PdfView.CoreWebView2?.Navigate(BuildPdfUri(_lastNavigatedPath!));
            }
            catch { /* best-effort */ }
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KbisConsultationEtapeViewModel.PdfFilePath)) return;
        var path = _vm?.PdfFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _lastNavigatedPath = null;
            try { PdfView.CoreWebView2?.Navigate("about:blank"); } catch { }
            return;
        }
        _lastNavigatedPath = path;
        try { PdfView.CoreWebView2?.Navigate(BuildPdfUri(path!)); } catch { }
    }

    /// <summary>
    /// Construit l'URI à passer à WebView2 pour afficher le PDF avec la toolbar
    /// Edge cachée (#toolbar=0). Le scroll, le zoom (Ctrl+wheel) et la sélection
    /// de texte restent fonctionnels. Toolbar WPF maison au-dessus pour les actions.
    /// </summary>
    private static string BuildPdfUri(string path)
        => new Uri(path).AbsoluteUri + "#toolbar=0";

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastNavigatedPath) || !File.Exists(_lastNavigatedPath!)) return;
        try
        {
            // verb "print" → lance le shell handler PDF par défaut (Edge / Acrobat)
            // qui affiche le dialog d'impression standard Windows.
            var psi = new ProcessStartInfo(_lastNavigatedPath!)
            {
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Impossible de lancer l'impression : " + ex.Message,
                "Imprimer K-bis", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastNavigatedPath) || !File.Exists(_lastNavigatedPath!)) return;
        var defaultName = Path.GetFileName(_lastNavigatedPath!);
        var dlg = new SaveFileDialog
        {
            FileName = defaultName,
            Filter = "Document PDF (*.pdf)|*.pdf|Tous fichiers (*.*)|*.*",
            DefaultExt = ".pdf",
            Title = "Enregistrer le K-bis sous…",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.Copy(_lastNavigatedPath!, dlg.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Impossible d'enregistrer le PDF : " + ex.Message,
                "Télécharger K-bis", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
