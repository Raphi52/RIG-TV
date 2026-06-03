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

    // ──────────────────────────────────────────────────────────────────────
    // ML LOOP S2.x — INVARIANTS de granularite du cache.
    //
    // Contexte : la cle de cache = (codeHash x scenarioId). L'appelant
    // (MainWindowViewModel.RunAllRaptureScenariosAsync) construit deja une cle
    // FINE : codeHash = hash des FICHIERS SOURCE drivers (pas la binaire
    // entiere), scenarioId = "{id}:{mode}:apply={0|1}", et scnHash inclut le
    // JSON du scenario. Donc deux scenarios INDEPENDANTS vivent sous des cles
    // distinctes et ne s'invalident JAMAIS mutuellement.
    //
    // Ces tests VERROUILLENT cet invariant au niveau du service : un futur
    // refactor de MakeKey/Get/Set ne doit pas reintroduire un partage de slot
    // entre scenarios (qui ferait qu'un rebuild d'un scenario casse le cache
    // des autres). On NE change PAS le comportement — on le documente.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Distinct_scenarios_are_stored_in_independent_slots()
    {
        // Meme codeHash, deux scenarios differents → deux entrees, chacune
        // retrouvable independamment.
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("codeH", "scn-A", "PASS", 1.0, "okA");
        svc.Set("codeH", "scn-B", "FAIL", 2.0, "koB");

        svc.CountEntries.Should().Be(2);
        svc.Get("codeH", "scn-A").Status.Should().Be("PASS");
        svc.Get("codeH", "scn-B").Status.Should().Be("FAIL");
    }

    [Fact]
    public void Overwriting_one_scenario_does_not_touch_another()
    {
        // Invariant central : re-ecrire le resultat du scenario A (ex. apres
        // re-run) ne doit PAS alterer le resultat memorise du scenario B.
        // C'est la garantie "scenarios independants ne s'invalident pas".
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("codeH", "scn-A", "FAIL", 5.0, "bug A v1");
        svc.Set("codeH", "scn-B", "PASS", 1.5, "B stable");

        // Re-run de A uniquement.
        svc.Set("codeH", "scn-A", "PASS", 2.0, "A fixed v2");

        var b = svc.Get("codeH", "scn-B");
        b.Should().NotBeNull();
        b.Status.Should().Be("PASS", "B ne doit pas etre impacte par le re-run de A");
        b.Detail.Should().Be("B stable");
        b.DurationS.Should().Be(1.5);

        svc.Get("codeH", "scn-A").Status.Should().Be("PASS");
        svc.CountEntries.Should().Be(2, "toujours 2 slots distincts");
    }

    [Fact]
    public void Same_scenario_under_different_codeHash_is_independent()
    {
        // Un changement de driver (nouveau codeHash) cree un NOUVEAU slot pour
        // le meme scenario sans ecraser l'ancien. L'ancien reste en archive
        // (mais ne sera jamais matche car le codeHash courant differe).
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("codeH-v1", "scn-A", "PASS", 1.0, "ancienne version driver");
        svc.Set("codeH-v2", "scn-A", "FAIL", 3.0, "nouvelle version driver");

        svc.CountEntries.Should().Be(2);
        svc.Get("codeH-v1", "scn-A").Status.Should().Be("PASS");
        svc.Get("codeH-v2", "scn-A").Status.Should().Be("FAIL");
    }

    [Fact]
    public void Composite_key_is_collision_free_for_real_codeHashes()
    {
        // MakeKey = codeHash + ":" + scenarioId (concatenation simple).
        // PRECONDITION du design : codeHash provient TOUJOURS de
        // ComputeCodeHash → 16 caracteres hex [0-9a-f], JAMAIS de ":".
        // Sous cette precondition, la cle concatenee est non ambigue : la
        // frontiere ":" est unique. Ce test verifie qu'avec des codeHashes
        // realistes (hex 16), deux couples distincts ne partagent jamais un
        // slot — c'est ce qui garantit que des scenarios independants ne
        // s'invalident pas mutuellement.
        var svc = new TestResultCacheService(_tempCachePath);
        const string hashA = "0123456789abcdef"; // 16 hex
        const string hashB = "fedcba9876543210"; // 16 hex

        svc.Set(hashA, "scn-1", "PASS", 1.0, "X");
        svc.Set(hashB, "scn-1", "FAIL", 2.0, "Y");
        svc.Set(hashA, "scn-2", "FAIL", 3.0, "Z");

        svc.CountEntries.Should().Be(3, "3 couples distincts → 3 slots");
        svc.Get(hashA, "scn-1").Detail.Should().Be("X");
        svc.Get(hashB, "scn-1").Detail.Should().Be("Y");
        svc.Get(hashA, "scn-2").Detail.Should().Be("Z");
    }

    [Fact]
    public void Composite_key_concatenation_is_naive_documented_limitation()
    {
        // INVARIANT DOCUMENTE (comportement observable inchange) : MakeKey est
        // une concatenation NAIVE "codeHash:scenarioId". Donc ("a","b:c") et
        // ("a:b","c") collisionnent en "a:b:c". Ce N'EST PAS un bug en pratique
        // (cf. precondition : codeHash = hex 16 sans ":"), mais on le VERROUILLE
        // ici : si quelqu'un voulait passer des codeHash arbitraires contenant
        // ":", il devrait d'abord durcir MakeKey (longueur prefixee / hash de
        // la paire). Ce test alerte si le comportement de frontiere change.
        var svc = new TestResultCacheService(_tempCachePath);
        svc.Set("a", "b:c", "PASS", 1.0, "ecrit en premier");
        svc.Set("a:b", "c", "FAIL", 2.0, "ecrase via collision de cle");

        // Les deux mappent sur la meme cle "a:b:c" → un seul slot, derniere
        // ecriture gagne. C'est la LIMITE connue, pas une garantie d'isolation.
        svc.CountEntries.Should().Be(1, "collision attendue : meme cle concatenee");
        svc.Get("a", "b:c").Status.Should().Be("FAIL",
            "derniere ecriture (contexte Y) gagne sur la cle partagee");
        svc.Get("a:b", "c").Status.Should().Be("FAIL");
    }

    [Fact]
    public void ComputeCodeHash_isolates_change_to_a_single_scenario_json()
    {
        // INVARIANT de granularite (cote ComputeCodeHash, tel qu'utilise par
        // l'appelant) : scnHash = hash(driverSources + UN json scenario).
        // Modifier le JSON du scenario A change scnHash(A) mais PAS scnHash(B).
        // → un seul scenario est invalide, pas tout le batch.
        var driver = Path.Combine(_tempDir, "LegacyDriver.cs");
        File.WriteAllText(driver, "// driver code stable");
        var jsonA = Path.Combine(_tempDir, "scenario-A.json");
        var jsonB = Path.Combine(_tempDir, "scenario-B.json");
        File.WriteAllText(jsonA, "{\"a\":1}");
        File.WriteAllText(jsonB, "{\"b\":1}");

        var hashA1 = TestResultCacheService.ComputeCodeHash(new[] { driver, jsonA });
        var hashB1 = TestResultCacheService.ComputeCodeHash(new[] { driver, jsonB });

        // On modifie SEULEMENT le JSON du scenario A.
        File.WriteAllText(jsonA, "{\"a\":2,\"changed\":true}");

        var hashA2 = TestResultCacheService.ComputeCodeHash(new[] { driver, jsonA });
        var hashB2 = TestResultCacheService.ComputeCodeHash(new[] { driver, jsonB });

        hashA2.Should().NotBe(hashA1, "le JSON de A a change → scnHash(A) change");
        hashB2.Should().Be(hashB1, "le JSON de B est inchange → scnHash(B) stable");
    }

    [Fact]
    public void ComputeCodeHash_changing_a_driver_invalidates_all_scenarios()
    {
        // CONTRE-PARTIE (et justification de l'invalidation large quand elle
        // est CORRECTE) : si un FICHIER DRIVER partage change, scnHash de TOUS
        // les scenarios change → invalidation globale VOULUE (le pipeline reel
        // a change, les anciens resultats sont stales). Ce test documente que
        // ce comportement est intentionnel, pas un bug.
        var driver = Path.Combine(_tempDir, "LegacyDriver.cs");
        File.WriteAllText(driver, "// driver v1");
        var jsonA = Path.Combine(_tempDir, "scenario-A.json");
        var jsonB = Path.Combine(_tempDir, "scenario-B.json");
        File.WriteAllText(jsonA, "{\"a\":1}");
        File.WriteAllText(jsonB, "{\"b\":1}");

        var hashA1 = TestResultCacheService.ComputeCodeHash(new[] { driver, jsonA });
        var hashB1 = TestResultCacheService.ComputeCodeHash(new[] { driver, jsonB });

        // Le driver partage change → impacte TOUS les scenarios.
        File.WriteAllText(driver, "// driver v2 — comportement different");

        var hashA2 = TestResultCacheService.ComputeCodeHash(new[] { driver, jsonA });
        var hashB2 = TestResultCacheService.ComputeCodeHash(new[] { driver, jsonB });

        hashA2.Should().NotBe(hashA1, "driver change → A invalide (voulu)");
        hashB2.Should().NotBe(hashB1, "driver change → B invalide (voulu)");
    }
}
