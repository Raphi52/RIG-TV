using System;
using System.IO;
using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests.MlLoop;

/// <summary>
/// Tests pour <see cref="TestResultCacheService"/> — ML LOOP Phase 4.
///
/// Couverture : Get/Set round-trip, persistance disque, invalidation par
/// hash code, Clear, Stats. Pas de DI requis : on instancie avec un
/// custom path temporaire pour éviter de toucher le vrai cache.
/// </summary>
public class TestResultCacheServiceTests : IDisposable
{
    private readonly string _tempCachePath;
    private readonly string _tempDir;

    public TestResultCacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rig-cache-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
        _tempCachePath = Path.Combine(_tempDir, "cache.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* leftover files OK, test isolation déjà via Guid */ }
    }

    [Fact]
    public void Get_returns_null_when_cache_is_empty()
    {
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Get("any-code-hash", "any-scenario").Should().BeNull();
    }

    [Fact]
    public void Set_then_Get_returns_the_stored_result()
    {
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("abcd1234", "cas-a-subset", "PASS", 2.4, "exit=0");

        var hit = svc.Get("abcd1234", "cas-a-subset");
        hit.Should().NotBeNull();
        hit.Status.Should().Be("PASS");
        hit.DurationS.Should().Be(2.4);
        hit.Detail.Should().Be("exit=0");
        hit.CachedAt.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Get_with_different_codeHash_returns_null()
    {
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("hash-A", "scn-1", "PASS", 1.0, "ok");
        svc.Get("hash-B", "scn-1").Should().BeNull();
    }

    [Fact]
    public void Get_with_different_scenarioId_returns_null()
    {
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("hash-A", "scn-1", "PASS", 1.0, "ok");
        svc.Get("hash-A", "scn-2").Should().BeNull();
    }

    [Fact]
    public void Clear_removes_all_entries()
    {
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("h", "a", "PASS", 1, "");
        svc.Set("h", "b", "FAIL", 2, "");
        svc.CountEntries.Should().Be(2);
        svc.Clear();
        svc.CountEntries.Should().Be(0);
        svc.Get("h", "a").Should().BeNull();
    }

    [Fact]
    public void Stats_counts_pass_and_fail_correctly()
    {
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("h", "a", "PASS", 1, "");
        svc.Set("h", "b", "PASS", 1, "");
        svc.Set("h", "c", "FAIL", 1, "");
        var (total, pass, fail) = svc.Stats();
        total.Should().Be(3);
        pass.Should().Be(2);
        fail.Should().Be(1);
    }

    [Fact]
    public void Persistence_round_trip_across_service_instances()
    {
        // Instance 1 : Set 3 entries
        var svc1 = new TestResultCacheService(_tempCachePath);
        svc1.Set("h1", "scn-A", "PASS", 1.5, "exit=0");
        svc1.Set("h1", "scn-B", "FAIL", 8.0, "Bouton introuvable");

        // Instance 2 (cold start, reads from disk): doit retrouver les entries
        var svc2 = new TestResultCacheService(_tempCachePath);
        svc2.CountEntries.Should().Be(2);

        var hitA = svc2.Get("h1", "scn-A");
        hitA.Should().NotBeNull();
        hitA.Status.Should().Be("PASS");
        hitA.DurationS.Should().Be(1.5);

        var hitB = svc2.Get("h1", "scn-B");
        hitB.Should().NotBeNull();
        hitB.Status.Should().Be("FAIL");
        hitB.Detail.Should().Be("Bouton introuvable");
    }

    [Fact]
    public void Set_with_same_key_overwrites_previous_entry()
    {
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("h", "scn", "FAIL", 5.0, "bug v1");
        svc.Set("h", "scn", "PASS", 2.0, "fixed v2");

        svc.CountEntries.Should().Be(1);
        var hit = svc.Get("h", "scn");
        hit.Status.Should().Be("PASS");
        hit.Detail.Should().Be("fixed v2");
    }

    [Fact]
    public void ComputeCodeHash_returns_16_hex_chars()
    {
        var f1 = Path.Combine(_tempDir, "file1.txt");
        File.WriteAllText(f1, "hello world");
        var hash = TestResultCacheService.ComputeCodeHash(new[] { f1 });
        hash.Should().HaveLength(16);
        hash.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void ComputeCodeHash_is_stable_for_same_content()
    {
        var f1 = Path.Combine(_tempDir, "file1.txt");
        File.WriteAllText(f1, "content");
        var h1 = TestResultCacheService.ComputeCodeHash(new[] { f1 });
        var h2 = TestResultCacheService.ComputeCodeHash(new[] { f1 });
        h2.Should().Be(h1);
    }

    [Fact]
    public void ComputeCodeHash_changes_when_file_content_changes()
    {
        var f1 = Path.Combine(_tempDir, "file1.txt");
        File.WriteAllText(f1, "v1");
        var h1 = TestResultCacheService.ComputeCodeHash(new[] { f1 });

        File.WriteAllText(f1, "v2-different");
        var h2 = TestResultCacheService.ComputeCodeHash(new[] { f1 });

        h2.Should().NotBe(h1);
    }

    [Fact]
    public void ComputeCodeHash_order_independent()
    {
        // Vérif : la fonction ordonne les paths en interne, donc deux appels
        // avec des orderings différents doivent donner le même hash.
        var fa = Path.Combine(_tempDir, "a.txt");
        var fb = Path.Combine(_tempDir, "b.txt");
        File.WriteAllText(fa, "A");
        File.WriteAllText(fb, "B");

        var h1 = TestResultCacheService.ComputeCodeHash(new[] { fa, fb });
        var h2 = TestResultCacheService.ComputeCodeHash(new[] { fb, fa });
        h2.Should().Be(h1);
    }

    [Fact]
    public void ComputeCodeHash_handles_missing_file_gracefully()
    {
        var existing = Path.Combine(_tempDir, "exists.txt");
        File.WriteAllText(existing, "real");
        var missing = Path.Combine(_tempDir, "does-not-exist.txt");

        // Ne doit pas throw — le hash est juste calculé sur le fichier réel.
        var hash = TestResultCacheService.ComputeCodeHash(new[] { existing, missing });
        hash.Should().HaveLength(16);
    }

    [Fact]
    public void Get_with_null_or_empty_args_returns_null()
    {
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("h", "scn", "PASS", 1, "");
        svc.Get(null, "scn").Should().BeNull();
        svc.Get("h", null).Should().BeNull();
        svc.Get("", "scn").Should().BeNull();
        svc.Get("h", "").Should().BeNull();
    }
}
