using System;
using System.IO;
using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests;

/// <summary>
/// Tests unitaires pour <see cref="GlobalSettingsService"/> — logique pure
/// load/save/clamp.
///
/// Couvre : round-trip load/save, clamp Parallelism (valeurs &lt;1 et &gt;32),
/// fichier absent → defaults, JSON corrompu → fallback defaults sans throw.
/// Utilise un customPath vers un dossier temp (Guid-isolé), nettoyé en Dispose.
/// </summary>
public class GlobalSettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public GlobalSettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rig-settings-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "global-settings.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // ── Defaults (fichier absent) ────────────────────────────────────────

    [Fact]
    public void Ctor_with_missing_file_returns_default_settings()
    {
        File.Exists(_settingsPath).Should().BeFalse();

        var svc = new GlobalSettingsService(_settingsPath);

        svc.Current.Parallelism.Should().Be(4, "default Parallelism = 4");
        svc.Current.HeadlessMode.Should().BeTrue("default HeadlessMode = true");
        svc.Current.ScreenshotsLoopEnabled.Should().BeTrue("default ScreenshotsLoopEnabled = true");
        svc.Current.UseTestResultCache.Should().BeFalse("default UseTestResultCache = false");
        svc.Current.SchemaVersion.Should().Be(3, "default SchemaVersion = 3 (schéma bumpé, cf. GlobalSettingsService.cs SchemaVersion = 3)");
    }

    [Fact]
    public void Ctor_with_missing_file_creates_file_on_disk_with_defaults()
    {
        var svc = new GlobalSettingsService(_settingsPath);
        File.Exists(_settingsPath).Should().BeTrue("Load écrit le fichier quand il n'existe pas");
    }

    // ── Round-trip load/save ─────────────────────────────────────────────

    [Fact]
    public void Save_then_Load_roundtrip_preserves_all_fields()
    {
        var svc1 = new GlobalSettingsService(_settingsPath);
        var settings = new GlobalSettings
        {
            Parallelism = 8,
            HeadlessMode = false,
            ScreenshotsLoopEnabled = false,
            UseTestResultCache = true,
            SchemaVersion = 99,
        };
        svc1.Save(settings);

        // Nouvel instance — cold load depuis disque.
        var svc2 = new GlobalSettingsService(_settingsPath);
        svc2.Current.Parallelism.Should().Be(8);
        svc2.Current.HeadlessMode.Should().BeFalse();
        svc2.Current.ScreenshotsLoopEnabled.Should().BeFalse();
        svc2.Current.UseTestResultCache.Should().BeTrue();
        svc2.Current.SchemaVersion.Should().Be(99);
    }

    [Fact]
    public void Save_updates_Current_in_memory()
    {
        var svc = new GlobalSettingsService(_settingsPath);
        var modified = new GlobalSettings { Parallelism = 16 };
        svc.Save(modified);
        svc.Current.Parallelism.Should().Be(16, "Save met à jour Current en mémoire");
    }

    [Fact]
    public void Load_updates_Current_from_disk()
    {
        // Instance 1 : sauvegarde Parallelism = 3.
        var svc = new GlobalSettingsService(_settingsPath);
        svc.Save(new GlobalSettings { Parallelism = 3 });

        // Simuler une modif externe : instance 2 écrase avec Parallelism = 7.
        var svc2 = new GlobalSettingsService(_settingsPath);
        svc2.Save(new GlobalSettings { Parallelism = 7 });

        // Instance 1 relit depuis disque via Load().
        var loaded = svc.Load();
        loaded.Parallelism.Should().Be(7, "Load() relit depuis disque la valeur sauvegardée par svc2");
        svc.Current.Parallelism.Should().Be(7, "Current reflète la valeur rechargée");
    }

    // ── Clamp Parallelism ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(-100, 1)]
    public void Save_clamps_Parallelism_below_1_to_1(int rawValue, int expectedClamped)
    {
        var svc = new GlobalSettingsService(_settingsPath);
        svc.Save(new GlobalSettings { Parallelism = rawValue });
        svc.Current.Parallelism.Should().Be(expectedClamped,
            $"Parallelism={rawValue} doit être clampé à {expectedClamped}");
    }

    [Theory]
    [InlineData(33, 32)]
    [InlineData(100, 32)]
    [InlineData(int.MaxValue, 32)]
    public void Save_clamps_Parallelism_above_32_to_32(int rawValue, int expectedClamped)
    {
        var svc = new GlobalSettingsService(_settingsPath);
        svc.Save(new GlobalSettings { Parallelism = rawValue });
        svc.Current.Parallelism.Should().Be(expectedClamped,
            $"Parallelism={rawValue} doit être clampé à {expectedClamped}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(32)]
    public void Save_keeps_valid_Parallelism_values_unchanged(int validValue)
    {
        var svc = new GlobalSettingsService(_settingsPath);
        svc.Save(new GlobalSettings { Parallelism = validValue });
        svc.Current.Parallelism.Should().Be(validValue);
    }

    [Fact]
    public void Load_clamps_Parallelism_below_1_read_from_disk()
    {
        // Forcer un fichier JSON avec Parallelism = 0 (corrompu manuellement).
        File.WriteAllText(_settingsPath,
            "{\"parallelism\":0,\"screenshotsLoopEnabled\":true,\"headlessMode\":true,\"useTestResultCache\":false,\"schemaVersion\":2}");

        var svc = new GlobalSettingsService(_settingsPath);
        svc.Current.Parallelism.Should().Be(1, "0 lu depuis disque → clampé à 1 par Load()");
    }

    [Fact]
    public void Load_clamps_Parallelism_above_32_read_from_disk()
    {
        File.WriteAllText(_settingsPath,
            "{\"parallelism\":99,\"screenshotsLoopEnabled\":true,\"headlessMode\":true,\"useTestResultCache\":false,\"schemaVersion\":2}");

        var svc = new GlobalSettingsService(_settingsPath);
        svc.Current.Parallelism.Should().Be(32, "99 lu depuis disque → clampé à 32 par Load()");
    }

    // ── JSON corrompu → fallback sans throw ──────────────────────────────

    [Fact]
    public void Load_with_corrupted_json_returns_defaults_without_throwing()
    {
        File.WriteAllText(_settingsPath, "{ INVALID JSON !!! }");

        Action act = () => new GlobalSettingsService(_settingsPath);
        act.Should().NotThrow("JSON corrompu → fallback defaults, jamais d'exception");
    }

    [Fact]
    public void Load_with_corrupted_json_returns_default_values()
    {
        File.WriteAllText(_settingsPath, "not-json-at-all");

        var svc = new GlobalSettingsService(_settingsPath);
        svc.Current.Parallelism.Should().Be(4, "fallback = default Parallelism = 4");
        svc.Current.HeadlessMode.Should().BeTrue("fallback = default HeadlessMode = true");
    }

    [Fact]
    public void Load_with_empty_file_returns_defaults_without_throwing()
    {
        File.WriteAllText(_settingsPath, "");

        Action act = () => new GlobalSettingsService(_settingsPath);
        act.Should().NotThrow();
    }

    [Fact]
    public void Load_with_empty_json_object_returns_defaults()
    {
        // JSON valide mais vide → Deserialize retourne un objet avec toutes valeurs par défaut.
        File.WriteAllText(_settingsPath, "{}");

        var svc = new GlobalSettingsService(_settingsPath);
        svc.Current.Parallelism.Should().Be(4);
        svc.Current.HeadlessMode.Should().BeTrue();
    }

    // ── FilePath ─────────────────────────────────────────────────────────

    [Fact]
    public void FilePath_exposes_the_custom_path_passed_to_ctor()
    {
        var svc = new GlobalSettingsService(_settingsPath);
        svc.FilePath.Should().Be(_settingsPath);
    }

    // ── Save throws on null ───────────────────────────────────────────────

    [Fact]
    public void Save_throws_ArgumentNullException_on_null_settings()
    {
        var svc = new GlobalSettingsService(_settingsPath);
        Action act = () => svc.Save(null);
        act.Should().Throw<ArgumentNullException>();
    }
}
