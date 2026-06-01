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
    /// <summary>
    /// Calcule le dHash 64-bit d'une image PNG. Lance une exception si le
    /// fichier n'existe pas ou n'est pas une image valide.
    /// </summary>
    public static ulong ComputeDHash(string pngPath)
    {
        if (string.IsNullOrEmpty(pngPath)) throw new ArgumentNullException(nameof(pngPath));
        if (!File.Exists(pngPath)) throw new FileNotFoundException("PNG introuvable", pngPath);

        // 9 colonnes × 8 lignes pour pouvoir calculer 8 différences par ligne
        const int W = 9;
        const int H = 8;

        using var src = (Bitmap)Image.FromFile(pngPath);
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

        // Build 64-bit hash : 8 lignes × 8 différences
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
