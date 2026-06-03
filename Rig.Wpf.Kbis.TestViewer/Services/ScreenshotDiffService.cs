// SPDX-License-Identifier: Proprietary
// ML LOOP Phase 5 — Perceptual hash (dHash) sur screenshots batch.
//
// Implémente dHash : "difference hash". Algorithme :
//   1) Resize l'image à 9x8 pixels en grayscale
//   2) Pour chaque ligne, compare pixel[col] > pixel[col+1] → 1 bit
//   3) Résultat = 64 bits (8 lignes × 8 différences)
//   4) Hamming distance entre deux dHash = perceptual similarity
//
// Avantages dHash vs pHash :
//   - Implementation triviale (~50 lignes), zéro dépendance.
//   - Robuste aux scale/rotation faibles, brightness shift.
//   - Hamming < 5 = identique visuellement, > 15 = scènes différentes.
//
// Usage : à la fin d'un batch RunAllRaptureScenariosAsync, capture screenshot
// de la fenêtre TestViewer (recap dans le log box). Compare au dHash de
// l'itération précédente — si Hamming distance grosse, c'est qu'une popup
// ou un état UI inattendu est apparu.
//
// ML LOOP S5.x — surcharges REGION (crop) : ComputeDHash(path, Rectangle) /
// ComputeDHashFromRegion(Bitmap, Rectangle). Hashent uniquement le rectangle
// de la fenêtre TestViewer au lieu du plein écran, pour ignorer le bruit
// (wallpaper, taskbar, curseur, notifications) qui faisait diverger deux
// captures pourtant identiques. La méthode plein écran ComputeDHash(path)
// reste inchangée pour compat.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Service de calcul de dHash et de Hamming distance pour comparer deux
/// screenshots PNG. Statless (toutes méthodes statiques) — pas besoin de DI,
/// appel direct depuis le code consommateur.
/// </summary>
public static class ScreenshotDiffService
{
    // 9 colonnes x 8 lignes pour pouvoir calculer 8 differences par ligne.
    private const int DHashW = 9;
    private const int DHashH = 8;

    /// <summary>
    /// Calcule le dHash 64-bit d'une image PNG (plein cadre). Lance une
    /// exception si le fichier n'existe pas ou n'est pas une image valide.
    /// </summary>
    public static ulong ComputeDHash(string pngPath)
    {
        using var src = LoadBitmap(pngPath);
        return ComputeDHashCore(src);
    }

    /// <summary>
    /// ML LOOP S5.x — dHash sur une REGION (crop) d'une image PNG plutot que
    /// le plein cadre. Permet de ne hasher que le rectangle de la fenetre
    /// TestViewer et d'ignorer le bruit (wallpaper, taskbar, curseur souris,
    /// notifications) qui fait diverger deux captures pourtant identiques.
    ///
    /// <paramref name="crop"/> est exprime en coordonnees PIXEL de l'image
    /// (origine haut-gauche). Le rectangle est clampe aux bornes de l'image :
    /// une zone qui deborde est tronquee a la partie visible. Si l'intersection
    /// avec l'image est vide (ou la region est degeneree), une
    /// <see cref="ArgumentException"/> est levee.
    /// </summary>
    public static ulong ComputeDHash(string pngPath, Rectangle crop)
    {
        using var src = LoadBitmap(pngPath);
        return ComputeDHashFromRegion(src, crop);
    }

    /// <summary>
    /// ML LOOP S5.x — dHash d'un <see cref="Bitmap"/> deja en memoire (plein
    /// cadre). Surcharge PURE / testable : on fournit le bitmap, aucune capture
    /// d'ecran ni I/O disque. Le bitmap n'est PAS dispose (propriete de l'appelant).
    /// </summary>
    public static ulong ComputeDHash(Bitmap bitmap)
    {
        if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
        return ComputeDHashCore(bitmap);
    }

    /// <summary>
    /// ML LOOP S5.x — dHash de la REGION <paramref name="crop"/> d'un
    /// <see cref="Bitmap"/> deja en memoire. Surcharge PURE / testable.
    /// Le rectangle est clampe aux bornes du bitmap (cf.
    /// <see cref="ComputeDHash(string, Rectangle)"/>). Le bitmap source n'est
    /// PAS dispose (propriete de l'appelant).
    /// </summary>
    public static ulong ComputeDHashFromRegion(Bitmap src, Rectangle crop)
    {
        if (src == null) throw new ArgumentNullException(nameof(src));

        var bounds = new Rectangle(0, 0, src.Width, src.Height);
        var clamped = Rectangle.Intersect(bounds, crop);
        if (clamped.Width <= 0 || clamped.Height <= 0)
            throw new ArgumentException(
                $"Region de crop vide apres clamp (demande={crop}, image={bounds}).",
                nameof(crop));

        // Si la region clampee == image entiere, pas la peine de cloner.
        if (clamped == bounds)
            return ComputeDHashCore(src);

        using var region = src.Clone(clamped, src.PixelFormat);
        return ComputeDHashCore(region);
    }

    /// <summary>
    /// Charge un PNG depuis le disque en validant existence / format.
    /// Le bitmap retourne doit etre dispose par l'appelant.
    /// </summary>
    private static Bitmap LoadBitmap(string pngPath)
    {
        if (string.IsNullOrEmpty(pngPath)) throw new ArgumentNullException(nameof(pngPath));
        if (!File.Exists(pngPath)) throw new FileNotFoundException("PNG introuvable", pngPath);
        return (Bitmap)Image.FromFile(pngPath);
    }

    /// <summary>
    /// Coeur de l'algorithme dHash : resize 9x8 grayscale puis 64 differences
    /// horizontales. Partage entre toutes les surcharges (fichier / bitmap /
    /// region) pour garantir un hash identique quel que soit le point d'entree.
    /// </summary>
    private static ulong ComputeDHashCore(Bitmap src)
    {
        const int W = DHashW;
        const int H = DHashH;

        using var resized = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, W, H);
        }

        // Convert to grayscale array
        var gray = new byte[H, W];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                var px = resized.GetPixel(x, y);
                // Luminance perceived weight (Rec. 709) sans virgule (perf)
                gray[y, x] = (byte)((px.R * 2126 + px.G * 7152 + px.B * 722) / 10000);
            }
        }

        // Build 64-bit hash : 8 lignes x 8 differences
        ulong hash = 0;
        int bit = 63;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W - 1; x++)
            {
                if (gray[y, x] > gray[y, x + 1])
                    hash |= (1UL << bit);
                bit--;
            }
        }
        return hash;
    }

    /// <summary>
    /// Hamming distance entre deux dHash. Compte le nombre de bits qui
    /// diffèrent. Borne : 0 (identique) à 64 (totalement opposé).
    /// Seuils empiriques :
    ///   - 0-4   : identique perceptuellement
    ///   - 5-10  : changements mineurs (label déplacé, couleur ajustée)
    ///   - 11-20 : scène différente (popup, écran différent)
    ///   - 20+   : screenshots probably unrelated
    /// </summary>
    public static int HammingDistance(ulong a, ulong b)
    {
        // XOR puis popcount
        ulong xor = a ^ b;
        int count = 0;
        while (xor != 0)
        {
            count += (int)(xor & 1UL);
            xor >>= 1;
        }
        return count;
    }

    /// <summary>
    /// Format hex 16-char (UL hex) pour stockage JSON et log.
    /// </summary>
    public static string FormatHash(ulong hash) => hash.ToString("x16");

    /// <summary>
    /// Parse un format hex 16-char vers ulong. Retourne 0 si invalide.
    /// </summary>
    public static ulong ParseHash(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return 0UL;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0UL;
    }
}
