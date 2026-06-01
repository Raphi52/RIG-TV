using System;
using System.IO;
using PDFtoImage;
using SkiaSharp;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Convertit le PDF K-bis (Apache FOP, 1-2 pages A4) en PNG via PDFium (PDFtoImage).
/// Indispensable parce que WebView2 — le contrôle qui affiche le PDF dans le Shell —
/// est un HWND natif (Edge Chromium) que <see cref="System.Windows.Media.Imaging.RenderTargetBitmap"/>
/// ne capture PAS. Sans ce sidecar le smoke n'aurait jamais de preuve visuelle du K-bis.
/// </summary>
public static class PdfPreviewRenderer
{
    /// <summary>
    /// Rend la première page de <paramref name="pdfPath"/> dans <paramref name="outputPath"/>.
    /// Retourne la taille en octets du PNG produit.
    /// </summary>
    /// <param name="dpi">96 = taille A4 native (~794×1123). 150 = haute déf (~1240×1754).</param>
    public static long RenderFirstPageToPng(string pdfPath, string outputPath, int dpi = 96)
    {
        if (string.IsNullOrEmpty(pdfPath)) throw new ArgumentException("pdfPath requis", nameof(pdfPath));
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF introuvable", pdfPath);
        if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("outputPath requis", nameof(outputPath));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

        // PDFtoImage.Conversion.ToImage retourne un SKBitmap. On l'encode en PNG.
        var pdfBytes = File.ReadAllBytes(pdfPath);
        using var bitmap = Conversion.ToImage(pdfBytes, page: 0, options: new RenderOptions { Dpi = dpi });
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        using var fs = new FileStream(outputPath, FileMode.Create);
        data.SaveTo(fs);

        return new FileInfo(outputPath).Length;
    }
}
