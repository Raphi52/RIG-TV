using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests.MlLoop;

/// <summary>
/// Tests pour <see cref="ScreenshotDiffService"/> — ML LOOP Phase 5.
///
/// Couvre : dHash sur PNG (taille fixe 9x8 grayscale → 64-bit hash),
/// Hamming distance, format/parse hex 16-char round-trip.
///
/// Crée des PNG synthétiques (uniforme noir, uniforme blanc, gradient,
/// damier) pour valider les propriétés mathématiques sans dépendre d'un
/// vrai screenshot disque.
/// </summary>
public class ScreenshotDiffServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ScreenshotDiffServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rig-dhash-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // ── Helpers : génère des PNG synthétiques ────────────────────────────

    private string MakeUniformPng(string name, Color color, int w = 64, int h = 48)
    {
        var path = Path.Combine(_tempDir, name + ".png");
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(color);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    private string MakeGradientPng(string name, int w = 64, int h = 48)
    {
        var path = Path.Combine(_tempDir, name + ".png");
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        for (int x = 0; x < w; x++)
        {
            int v = (int)(255.0 * x / Math.Max(1, w - 1));
            for (int y = 0; y < h; y++)
                bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
        }
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    // ── HammingDistance ──────────────────────────────────────────────────

    [Fact]
    public void HammingDistance_on_identical_hashes_returns_zero()
    {
        ScreenshotDiffService.HammingDistance(0xABCDEF0123456789UL, 0xABCDEF0123456789UL).Should().Be(0);
    }

    [Fact]
    public void HammingDistance_on_inverse_hashes_returns_64()
    {
        ScreenshotDiffService.HammingDistance(0xFFFFFFFFFFFFFFFFUL, 0x0000000000000000UL).Should().Be(64);
    }

    [Theory]
    [InlineData(0x0UL, 0x1UL, 1)]
    [InlineData(0x0UL, 0x3UL, 2)]
    [InlineData(0x0UL, 0xFFUL, 8)]
    [InlineData(0xF0F0F0F0F0F0F0F0UL, 0x0F0F0F0F0F0F0F0FUL, 64)]
    public void HammingDistance_counts_differing_bits(ulong a, ulong b, int expected)
    {
        ScreenshotDiffService.HammingDistance(a, b).Should().Be(expected);
    }

    [Fact]
    public void HammingDistance_is_symmetric()
    {
        const ulong a = 0xCAFE1234DEADBEEFUL;
        const ulong b = 0x1234CAFE5678ABCDUL;
        ScreenshotDiffService.HammingDistance(a, b)
            .Should().Be(ScreenshotDiffService.HammingDistance(b, a));
    }

    // ── FormatHash / ParseHash round-trip ────────────────────────────────

    [Fact]
    public void FormatHash_returns_16_hex_chars_lowercase()
    {
        ScreenshotDiffService.FormatHash(0xABCDEF0123456789UL).Should().Be("abcdef0123456789");
    }

    [Fact]
    public void FormatHash_pads_small_values_to_16_chars()
    {
        ScreenshotDiffService.FormatHash(0x1UL).Should().Be("0000000000000001");
    }

    [Fact]
    public void Parse_then_Format_round_trip_preserves_value()
    {
        const ulong original = 0xDEADBEEFCAFE1234UL;
        var formatted = ScreenshotDiffService.FormatHash(original);
        var parsed = ScreenshotDiffService.ParseHash(formatted);
        parsed.Should().Be(original);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-hex")]
    [InlineData("toolong-not-16-chars-ulong-hex-value")]
    public void ParseHash_returns_0_on_invalid_input(string input)
    {
        ScreenshotDiffService.ParseHash(input).Should().Be(0UL);
    }

    // ── ComputeDHash on real PNGs ────────────────────────────────────────

    [Fact]
    public void ComputeDHash_throws_on_missing_file()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.png");
        Action act = () => ScreenshotDiffService.ComputeDHash(missing);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ComputeDHash_on_same_image_twice_returns_same_hash()
    {
        var png = MakeUniformPng("gray", Color.Gray);
        var h1 = ScreenshotDiffService.ComputeDHash(png);
        var h2 = ScreenshotDiffService.ComputeDHash(png);
        h2.Should().Be(h1);
    }

    [Fact]
    public void ComputeDHash_on_uniform_image_gives_predictable_hash()
    {
        // Image uniforme = tous les pixels identiques après resize → toutes les
        // différences pixel[x] > pixel[x+1] sont FALSE → hash = 0 (tous bits à 0).
        var blackPng = MakeUniformPng("black", Color.Black);
        ScreenshotDiffService.ComputeDHash(blackPng).Should().Be(0UL,
            "image noire uniforme : aucune différence entre pixels adjacents");

        var whitePng = MakeUniformPng("white", Color.White);
        ScreenshotDiffService.ComputeDHash(whitePng).Should().Be(0UL,
            "image blanche uniforme : aucune différence entre pixels adjacents");
    }

    [Fact]
    public void ComputeDHash_on_inverse_gradients_gives_different_hashes()
    {
        var leftDark = MakeGradientPng("ltr"); // 0 → 255 left-to-right
        // Inverse : gradient droite à gauche
        var path = Path.Combine(_tempDir, "rtl.png");
        using (var bmp = new Bitmap(64, 48, PixelFormat.Format24bppRgb))
        {
            for (int x = 0; x < 64; x++)
            {
                int v = (int)(255.0 * (63 - x) / 63);
                for (int y = 0; y < 48; y++)
                    bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
            }
            bmp.Save(path, ImageFormat.Png);
        }
        var h1 = ScreenshotDiffService.ComputeDHash(leftDark);
        var h2 = ScreenshotDiffService.ComputeDHash(path);
        h1.Should().NotBe(h2,
            "gradients horizontalement inversés → dHash très différents");
        ScreenshotDiffService.HammingDistance(h1, h2).Should().BeGreaterThan(20,
            "deux gradients inverses doivent être loin perceptuellement");
    }

    [Fact]
    public void ComputeDHash_format_round_trip_via_FormatHash_and_ParseHash()
    {
        var png = MakeGradientPng("grad");
        var raw = ScreenshotDiffService.ComputeDHash(png);
        var hex = ScreenshotDiffService.FormatHash(raw);
        var back = ScreenshotDiffService.ParseHash(hex);
        back.Should().Be(raw);
    }

    [Fact]
    public void ComputeDHash_on_perceptually_similar_images_gives_close_hashes()
    {
        // Deux gradients quasi-identiques (offset 1 pixel sur largeur 64) →
        // Hamming distance devrait être faible (≤ 5).
        var g1 = MakeGradientPng("g1");
        var path2 = Path.Combine(_tempDir, "g2.png");
        // Quasi-identique : gradient légèrement décalé
        using (var bmp = new Bitmap(64, 48, PixelFormat.Format24bppRgb))
        {
            for (int x = 0; x < 64; x++)
            {
                int v = (int)(255.0 * Math.Min(63, x + 1) / 63);
                for (int y = 0; y < 48; y++)
                    bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
            }
            bmp.Save(path2, ImageFormat.Png);
        }
        var h1 = ScreenshotDiffService.ComputeDHash(g1);
        var h2 = ScreenshotDiffService.ComputeDHash(path2);
        ScreenshotDiffService.HammingDistance(h1, h2).Should().BeLessThan(8,
            "gradients quasi-identiques (offset 1px) → distance Hamming faible");
    }
}
