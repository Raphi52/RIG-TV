using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// Convertit un path PNG en BitmapImage **entièrement chargée en mémoire** (CacheOption.OnLoad),
/// Freeze pour cross-thread, FileShare.ReadWrite|Delete pour ne pas bloquer le worker qui
/// écrit le PNG en parallèle.
///
/// **Pourquoi pas {Binding ..., IsAsync=True}** : IsAsync rend Source null pendant le decode
/// asynchrone (flicker ~50-100ms à chaque snap = ~5-10 flickers/seconde par tile à 250ms refresh).
/// Le converter charge en sync sur le thread UI (~5-10ms pour un PNG ~100KB), assignment Source
/// atomique, zéro flicker.
/// </summary>
public sealed class PathToCachedBitmapConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path)) return null;
        try
        {
            // Read entirely into MemoryStream before BitmapImage init : évite le bug WPF
            // "ArgumentNullException : Key cannot be null" qui apparaît avec FileStream +
            // BitmapCreateOptions.IgnoreImageCache (interne WPF déréférence un Key d'un
            // cache jamais init). MemoryStream-only + sans CreateOptions = chemin standard
            // fiable. Petit coût mémoire transient (PNG ~100KB), libéré au Freeze().
            // FileShare.ReadWrite | Delete : worker SmokeRunner écrit en concurrence + cleanup.
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[fs.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = fs.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != bytes.Length) return null; // partial read mid-write, retry next tick
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // PNG corrompu, partial write race, etc. Retourner null = Image.Source vide,
            // retentée au prochain LastSnapPath change.
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
