using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

public sealed class RenderArtifactViewModel
{
    public RenderArtifactViewModel(string filePath)
    {
        FilePath = filePath;
        var fi = new FileInfo(filePath);
        FileName = fi.Name;
        Size = fi.Length;
        LastWrite = fi.LastWriteTime;
        // Charge le PNG en mémoire (BitmapCacheOption.OnLoad) pour éviter de
        // verrouiller le fichier sur disque — le SmokeRunner peut le réécrire.
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(filePath, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        Image = bmp;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public long Size { get; }
    public DateTime LastWrite { get; }
    public BitmapImage Image { get; }

    public string SizeLabel => Size < 1024 ? $"{Size} o" : $"{Size / 1024.0:F1} Ko";
    public string SubLabel  => $"{SizeLabel} · {LastWrite:HH:mm:ss}";
}
