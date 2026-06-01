using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Capture une <see cref="UIElement"/> WPF sous forme de PNG via
/// <see cref="RenderTargetBitmap"/>. Permet de valider que les bindings et la
/// structure XAML rendent sans exception, sans display Windows ni Shell.exe.
///
/// Limites :
///  - Pas d'Application.Current → les <c>DynamicResource</c> du thème Shell ne
///    résolvent pas (Brushes, Icons, Styles). Le rendu garde la structure et
///    le texte mais les couleurs sont par défaut.
///  - WebView2 est un contrôle natif (Edge Chromium) — il n'est PAS capturé
///    par RenderTargetBitmap (apparaît noir/transparent). Pour le PDF visuel
///    embarqué, lancer le Shell.exe avec le mode UI.
/// </summary>
public static class ViewRenderer
{
    /// <summary>
    /// Initialise Application.Current et charge le Theme.xaml du Rig.Wpf.Shell
    /// pour que les StaticResource des Views (HeroIconBackdrop, Eyebrow, ...) résolvent.
    /// Idempotent : sans effet si déjà initialisé.
    /// </summary>
    public static void EnsureApplicationAndThemeLoaded()
    {
        if (Application.Current is null) { _ = new Application(); }
        var resources = Application.Current!.Resources;
        // Évite de re-charger plusieurs fois
        foreach (ResourceDictionary md in resources.MergedDictionaries)
        {
            if (md.Source is not null && md.Source.OriginalString.Contains("Rig.Wpf.Shell"))
                return;
        }
        var themeUri = new Uri(
            "pack://application:,,,/Rig.Wpf.Shell;component/Theme/Theme.xaml",
            UriKind.Absolute);
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });
    }

    /// <summary>
    /// Capture la View en PNG.
    /// </summary>
    /// <param name="readyWhen">Optionnel : prédicat sur l'état VM (ex. <c>() => vm.Resultats.Count > 0</c>).
    /// Le rendu attend que le prédicat soit vrai, en pompant le Dispatcher en boucle.
    /// Throw <see cref="TimeoutException"/> si pas devenu vrai dans <paramref name="readyTimeoutMs"/>.
    /// Sans prédicat (null), pump unique du Dispatcher — comportement legacy.</param>
    public static long RenderToPng(UIElement view, string outputPath,
        int width = 1280, int height = 800,
        Func<bool>? readyWhen = null, int readyTimeoutMs = 5000)
    {
        EnsureApplicationAndThemeLoaded();
        if (view is null) throw new ArgumentNullException(nameof(view));
        if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("outputPath requis", nameof(outputPath));

        // Mesure + arrange + layout — indispensable avant render.
        var size = new Size(width, height);
        view.Measure(size);
        view.Arrange(new Rect(size));
        if (view is FrameworkElement fe) { fe.UpdateLayout(); }

        // Dispatcher run pour vider la queue de bindings + InitializeComponent.
        // Sans ça, certains DataTemplate ne se sont pas encore instanciés.
        PumpDispatcher();

        // Attente conditionnelle si un prédicat est fourni (Reload async dans VM ctor,
        // GenerateAsync sur Selection, etc.). Pump puis re-test en boucle.
        if (readyWhen is not null)
        {
            var deadline = Environment.TickCount + readyTimeoutMs;
            while (!readyWhen() && Environment.TickCount < deadline)
            {
                Thread.Sleep(50);
                PumpDispatcher();
                if (view is FrameworkElement fe2) { fe2.UpdateLayout(); }
            }
            if (!readyWhen())
            {
                throw new TimeoutException(
                    $"readyWhen() est resté faux après {readyTimeoutMs}ms — la VM n'est jamais arrivée " +
                    "à l'état attendu avant le snapshot. Le rendu aurait été un instantané vide/incomplet.");
            }
            // Un dernier pump après que le prédicat passe, pour laisser les bindings finir.
            PumpDispatcher();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = new FileStream(outputPath, FileMode.Create);
        encoder.Save(fs);

        return new FileInfo(outputPath).Length;
    }

    /// <summary>
    /// Description d'une région d'image attendue non-vide (pour assertion).
    /// Coordonnées en pixels dans le PNG, origine en haut-gauche.
    /// (Pas un record car net48 manque IsExternalInit pour les init-only.)
    /// </summary>
    public sealed class ExpectedRegion
    {
        public string Name { get; }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public ExpectedRegion(string name, int x, int y, int width, int height)
        {
            Name = name; X = x; Y = y; Width = width; Height = height;
        }
    }

    /// <summary>
    /// Charge le PNG et vérifie que chaque <see cref="ExpectedRegion"/> contient au
    /// moins <paramref name="minNonWhiteRatio"/> de pixels NON-blancs. Throw avec un
    /// message clair si une région est uniformément blanche (= rendu raté).
    /// Couleur "blanche" : R, G, B tous > 245. Tolère un peu de fond grisé.
    /// </summary>
    public static void AssertRegionsNotBlank(string pngPath, IEnumerable<ExpectedRegion> regions,
        double minNonWhiteRatio = 0.05)
    {
        using var fs = new FileStream(pngPath, FileMode.Open, FileAccess.Read);
        var decoder = new PngBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        // Force Bgra32 pour avoir un layout stable
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[bgra.PixelHeight * stride];
        bgra.CopyPixels(pixels, stride, 0);

        var failures = new List<string>();
        foreach (var r in regions)
        {
            int x0 = Math.Max(0, r.X);
            int y0 = Math.Max(0, r.Y);
            int x1 = Math.Min(bgra.PixelWidth, r.X + r.Width);
            int y1 = Math.Min(bgra.PixelHeight, r.Y + r.Height);
            if (x1 <= x0 || y1 <= y0) { failures.Add($"{r.Name} : région hors image"); continue; }

            int total = 0, nonWhite = 0;
            for (int y = y0; y < y1; y++)
            {
                int rowStart = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    int idx = rowStart + x * 4;
                    byte b = pixels[idx], g = pixels[idx + 1], rch = pixels[idx + 2];
                    total++;
                    if (b < 245 || g < 245 || rch < 245) { nonWhite++; }
                }
            }
            double ratio = total == 0 ? 0 : (double)nonWhite / total;
            if (ratio < minNonWhiteRatio)
            {
                failures.Add($"{r.Name} ({r.X},{r.Y}+{r.Width}×{r.Height}) : " +
                    $"{ratio:P1} non-blanc < seuil {minNonWhiteRatio:P0} → région vide/blanche");
            }
        }
        if (failures.Count > 0)
        {
            throw new Exception("Régions blanches détectées dans " + Path.GetFileName(pngPath) +
                " :\n  - " + string.Join("\n  - ", failures));
        }
    }

    /// <summary>
    /// Vide la queue du Dispatcher courant (jusqu'à priority Loaded).
    /// Force le déroulement des bindings différés.
    /// </summary>
    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
