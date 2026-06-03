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

    // ── ComputeDHash(Bitmap) — surcharge PURE in-memory ──────────────────

    [Fact]
    public void ComputeDHash_bitmap_overload_matches_file_overload()
    {
        // La surcharge in-memory doit donner EXACTEMENT le meme hash que la
        // surcharge fichier (meme algorithme partage via ComputeDHashCore).
        var pngPath = MakeGradientPng("grad-shared");
        var fromFile = ScreenshotDiffService.ComputeDHash(pngPath);
        using var bmp = LoadBitmapCopy(pngPath);
        var fromBitmap = ScreenshotDiffService.ComputeDHash(bmp);
        fromBitmap.Should().Be(fromFile,
            "fichier et bitmap in-memory partagent le meme coeur d'algorithme");
    }

    [Fact]
    public void ComputeDHash_bitmap_overload_throws_on_null()
    {
        Action act = () => ScreenshotDiffService.ComputeDHash((Bitmap)null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ComputeDHash_bitmap_does_not_dispose_caller_bitmap()
    {
        // Contrat : la surcharge bitmap ne dispose PAS l'image fournie
        // (propriete de l'appelant). On verifie qu'on peut re-hasher apres.
        using var bmp = MakeGradientBitmap();
        var h1 = ScreenshotDiffService.ComputeDHash(bmp);
        var h2 = ScreenshotDiffService.ComputeDHash(bmp); // ne doit pas throw ObjectDisposed
        h2.Should().Be(h1);
    }

    // ── ComputeDHashFromRegion — crop in-memory ──────────────────────────

    [Fact]
    public void ComputeDHashFromRegion_full_bounds_equals_full_hash()
    {
        // Crop == bornes completes → identique au hash plein cadre.
        using var bmp = MakeGradientBitmap(64, 48);
        var full = ScreenshotDiffService.ComputeDHash(bmp);
        var region = ScreenshotDiffService.ComputeDHashFromRegion(
            bmp, new Rectangle(0, 0, 64, 48));
        region.Should().Be(full);
    }

    [Fact]
    public void ComputeDHashFromRegion_oversized_rectangle_is_clamped_to_bounds()
    {
        // Un rectangle qui deborde largement est clampe a l'image → equivaut
        // au plein cadre (pas de throw, pas d'OutOfMemory sur Bitmap.Clone).
        using var bmp = MakeGradientBitmap(64, 48);
        var full = ScreenshotDiffService.ComputeDHash(bmp);
        var oversized = ScreenshotDiffService.ComputeDHashFromRegion(
            bmp, new Rectangle(-100, -100, 5000, 5000));
        oversized.Should().Be(full,
            "rectangle qui couvre toute l'image apres clamp == plein cadre");
    }

    [Fact]
    public void ComputeDHashFromRegion_empty_intersection_throws()
    {
        using var bmp = MakeGradientBitmap(64, 48);
        // Rectangle entierement hors de l'image → intersection vide.
        Action act = () => ScreenshotDiffService.ComputeDHashFromRegion(
            bmp, new Rectangle(1000, 1000, 50, 50));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeDHashFromRegion_degenerate_rectangle_throws()
    {
        using var bmp = MakeGradientBitmap(64, 48);
        Action act = () => ScreenshotDiffService.ComputeDHashFromRegion(
            bmp, new Rectangle(10, 10, 0, 0));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeDHashFromRegion_throws_on_null_bitmap()
    {
        Action act = () => ScreenshotDiffService.ComputeDHashFromRegion(
            null, new Rectangle(0, 0, 10, 10));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ComputeDHashFromRegion_isolates_subregion_content()
    {
        // Image composite : moitie gauche uniforme (gris), moitie droite gradient.
        // Le crop de la moitie gauche (uniforme) doit refleter du contenu PLAT
        // (proche d'une image uniforme de reference) et etre NETTEMENT different
        // du plein cadre qui contient le gradient de droite.
        //
        // NB : on n'exige pas hash==0 sur le crop. Le resize 9x8 utilise une
        // interpolation BICUBIQUE dont le kernel touche le bord du crop et peut
        // introduire de legers artefacts d'overshoot (≈0x4040... ici). On teste
        // donc une PROPRIETE PERCEPTUELLE robuste (faible distance Hamming a une
        // reference uniforme), pas une egalite bit-a-bit fragile.
        using var bmp = new Bitmap(64, 48, PixelFormat.Format24bppRgb);
        for (int x = 0; x < 64; x++)
        {
            int v = x < 32 ? 128 : (int)(255.0 * (x - 32) / 31);
            for (int y = 0; y < 48; y++)
                bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
        }
        var full = ScreenshotDiffService.ComputeDHash(bmp);
        var leftRegion = ScreenshotDiffService.ComputeDHashFromRegion(
            bmp, new Rectangle(0, 0, 30, 48)); // zone uniforme uniquement

        // Reference : une image entierement uniforme (hash attendu 0).
        using var flat = new Bitmap(30, 48, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(flat)) g.Clear(Color.FromArgb(128, 128, 128));
        var flatHash = ScreenshotDiffService.ComputeDHash(flat);

        full.Should().NotBe(0UL, "le plein cadre contient le gradient de droite");
        ScreenshotDiffService.HammingDistance(leftRegion, flatHash).Should().BeLessThan(10,
            "la region gauche est quasi-uniforme → proche d'une image plate");
        ScreenshotDiffService.HammingDistance(leftRegion, full).Should().BeGreaterThan(
            ScreenshotDiffService.HammingDistance(leftRegion, flatHash),
            "le crop uniforme est plus proche du plat que du plein cadre avec gradient");
    }

    [Fact]
    public void ComputeDHashFromRegion_ignores_border_noise_outside_region()
    {
        // SCENARIO REEL : deux captures identiques au CENTRE (fenetre TestViewer)
        // mais bruit different sur les BORDS (wallpaper / taskbar / curseur).
        // Le dHash plein cadre diverge ; le dHash de la region centrale est
        // IDENTIQUE → c'est exactement le but de la surcharge crop.
        const int W = 80, H = 60;
        var center = new Rectangle(20, 15, 40, 30);

        using var capA = MakeCompositeNoisyBitmap(W, H, center, noiseSeed: 1);
        using var capB = MakeCompositeNoisyBitmap(W, H, center, noiseSeed: 2);

        var fullA = ScreenshotDiffService.ComputeDHash(capA);
        var fullB = ScreenshotDiffService.ComputeDHash(capB);
        var regionA = ScreenshotDiffService.ComputeDHashFromRegion(capA, center);
        var regionB = ScreenshotDiffService.ComputeDHashFromRegion(capB, center);

        // Le contenu central est identique → region dHash identique.
        regionA.Should().Be(regionB,
            "region centrale identique → dHash crop identique (bruit des bords ignore)");
        // Le bruit des bords fait diverger le plein cadre.
        ScreenshotDiffService.HammingDistance(fullA, fullB).Should().BeGreaterThan(
            ScreenshotDiffService.HammingDistance(regionA, regionB),
            "le crop reduit (ou egalise) la distance par rapport au plein cadre bruite");
    }

    [Fact]
    public void ComputeDHash_path_region_overload_matches_in_memory_region()
    {
        // La surcharge fichier+Rectangle doit donner le meme hash que la
        // surcharge bitmap+Rectangle pour la meme image et le meme crop.
        var path = MakeGradientPng("grad-region");
        var crop = new Rectangle(10, 5, 30, 30);
        var fromFile = ScreenshotDiffService.ComputeDHash(path, crop);
        using var bmp = LoadBitmapCopy(path);
        var fromMem = ScreenshotDiffService.ComputeDHashFromRegion(bmp, crop);
        fromFile.Should().Be(fromMem);
    }

    // ── Helpers supplementaires (crop) ───────────────────────────────────

    /// <summary>Charge un PNG en copie detachee du fichier (pas de lock disque).</summary>
    private static Bitmap LoadBitmapCopy(string path)
    {
        using var tmp = (Bitmap)Image.FromFile(path);
        return new Bitmap(tmp);
    }

    private static Bitmap MakeGradientBitmap(int w = 64, int h = 48)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        for (int x = 0; x < w; x++)
        {
            int v = (int)(255.0 * x / Math.Max(1, w - 1));
            for (int y = 0; y < h; y++)
                bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
        }
        return bmp;
    }

    /// <summary>
    /// Bitmap composite : un gradient FIXE dans <paramref name="center"/>
    /// (identique quel que soit le seed) entoure de "bruit" deterministe
    /// dependant de <paramref name="noiseSeed"/> hors de cette region.
    /// Simule deux captures plein ecran dont seule la fenetre centrale est
    /// stable.
    /// </summary>
    private static Bitmap MakeCompositeNoisyBitmap(int w, int h, Rectangle center, int noiseSeed)
    {
        var rng = new Random(noiseSeed);
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (center.Contains(x, y))
                {
                    // Gradient horizontal STABLE dans la region centrale.
                    int rel = x - center.Left;
                    int v = (int)(255.0 * rel / Math.Max(1, center.Width - 1));
                    bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
                }
                else
                {
                    // Bruit dependant du seed hors region.
                    int n = rng.Next(0, 256);
                    bmp.SetPixel(x, y, Color.FromArgb(n, n, n));
                }
            }
        }
        return bmp;
    }
}
