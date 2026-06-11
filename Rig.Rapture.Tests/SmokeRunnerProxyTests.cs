using System;
using System.IO;
using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests;

/// <summary>
/// Tests unitaires pour <see cref="SmokeRunnerProxy"/> — logique pure de parsing stdout.
///
/// Couvre : ResultLineRegex (lignes ✓/✗/⊘), ManualInjectStdoutForParsing
/// (count + outcome), AppendLineForParsing (append + workerId), LastGeneratedPdfPath
/// (extraction PDF depuis stdout). Aucun process lancé.
/// </summary>
public class SmokeRunnerProxyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SmokeRunnerProxy _proxy;

    public SmokeRunnerProxyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rig-proxy-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
        // L'exe n'a pas besoin d'exister pour les tests de parsing purs.
        _proxy = new SmokeRunnerProxy(
            Path.Combine(_tempDir, "fake-smoke.exe"),
            _tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // ── ManualInjectStdoutForParsing ─────────────────────────────────────

    [Fact]
    public void ManualInject_empty_string_produces_zero_lines()
    {
        _proxy.ManualInjectStdoutForParsing("");
        _proxy.Lines.Should().BeEmpty();
    }

    [Fact]
    public void ManualInject_passed_line_produces_one_Passed_line()
    {
        _proxy.ManualInjectStdoutForParsing("  ✓ Mon test passe\n");
        _proxy.Lines.Should().HaveCount(1);
        _proxy.Lines[0].Outcome.Should().Be(SmokeOutcome.Passed);
        _proxy.Lines[0].Description.Should().Be("Mon test passe");
    }

    [Fact]
    public void ManualInject_failed_line_produces_one_Failed_line()
    {
        _proxy.ManualInjectStdoutForParsing("  ✗ Bouton introuvable\n");
        _proxy.Lines.Should().HaveCount(1);
        _proxy.Lines[0].Outcome.Should().Be(SmokeOutcome.Failed);
        _proxy.Lines[0].Description.Should().Be("Bouton introuvable");
    }

    [Fact]
    public void ManualInject_skipped_line_produces_one_Skipped_line()
    {
        _proxy.ManualInjectStdoutForParsing("  ⊘ Scénario ignoré\n");
        _proxy.Lines.Should().HaveCount(1);
        _proxy.Lines[0].Outcome.Should().Be(SmokeOutcome.Skipped);
        _proxy.Lines[0].Description.Should().Be("Scénario ignoré");
    }

    [Fact]
    public void ManualInject_non_result_lines_are_ignored()
    {
        var stdout = "INFO Starting batch...\nWARN Something odd\n[2026-06-05] Log line\n";
        _proxy.ManualInjectStdoutForParsing(stdout);
        _proxy.Lines.Should().BeEmpty();
    }

    [Fact]
    public void ManualInject_mixed_lines_only_parses_result_lines()
    {
        var stdout =
            "Starting batch\n" +
            "  ✓ Step A\n" +
            "INFO noise\n" +
            "  ✗ Step B\n" +
            "  ⊘ Step C\n" +
            "Done.\n";
        _proxy.ManualInjectStdoutForParsing(stdout);
        _proxy.Lines.Should().HaveCount(3);
        _proxy.Lines[0].Outcome.Should().Be(SmokeOutcome.Passed);
        _proxy.Lines[0].Description.Should().Be("Step A");
        _proxy.Lines[1].Outcome.Should().Be(SmokeOutcome.Failed);
        _proxy.Lines[1].Description.Should().Be("Step B");
        _proxy.Lines[2].Outcome.Should().Be(SmokeOutcome.Skipped);
        _proxy.Lines[2].Description.Should().Be("Step C");
    }

    [Fact]
    public void ManualInject_clears_previous_lines_before_parsing()
    {
        _proxy.ManualInjectStdoutForParsing("  ✓ Old line\n");
        _proxy.Lines.Should().HaveCount(1);

        _proxy.ManualInjectStdoutForParsing("  ✗ New line A\n  ✓ New line B\n");
        _proxy.Lines.Should().HaveCount(2, "inject remplace (Clear) les anciens résultats");
        _proxy.Lines[0].Outcome.Should().Be(SmokeOutcome.Failed);
        _proxy.Lines[1].Outcome.Should().Be(SmokeOutcome.Passed);
    }

    [Fact]
    public void ManualInject_sets_LastFullStdout()
    {
        var content = "  ✓ Test\n";
        _proxy.ManualInjectStdoutForParsing(content);
        _proxy.LastFullStdout.Should().Be(content);
    }

    [Fact]
    public void ManualInject_orders_lines_by_insertion_order()
    {
        var stdout = "  ✓ A\n  ✗ B\n  ⊘ C\n";
        _proxy.ManualInjectStdoutForParsing(stdout);
        _proxy.Lines[0].Order.Should().Be(0);
        _proxy.Lines[1].Order.Should().Be(1);
        _proxy.Lines[2].Order.Should().Be(2);
    }

    [Fact]
    public void ManualInject_handles_crlf_line_endings()
    {
        var stdout = "  ✓ Step A\r\n  ✗ Step B\r\n";
        _proxy.ManualInjectStdoutForParsing(stdout);
        _proxy.Lines.Should().HaveCount(2);
        _proxy.Lines[0].Description.Should().Be("Step A");
        _proxy.Lines[1].Description.Should().Be("Step B");
    }

    // ── AppendLineForParsing ─────────────────────────────────────────────

    [Fact]
    public void AppendLine_appends_passed_line_to_existing_lines()
    {
        _proxy.ManualInjectStdoutForParsing("  ✓ First\n");
        _proxy.AppendLineForParsing("  ✓ Second");
        _proxy.Lines.Should().HaveCount(2);
        _proxy.Lines[1].Description.Should().Be("Second");
        _proxy.Lines[1].Outcome.Should().Be(SmokeOutcome.Passed);
    }

    [Fact]
    public void AppendLine_non_result_line_is_silently_ignored()
    {
        _proxy.AppendLineForParsing("INFO noise");
        _proxy.Lines.Should().BeEmpty();
    }

    [Fact]
    public void AppendLine_sets_workerId_on_resulting_line()
    {
        _proxy.AppendLineForParsing("  ✓ Kbis step", "kbis-vk");
        _proxy.Lines.Should().HaveCount(1);
        _proxy.Lines[0].WorkerId.Should().Be("kbis-vk");
    }

    [Fact]
    public void AppendLine_null_workerId_leaves_WorkerId_null()
    {
        _proxy.AppendLineForParsing("  ✓ Step sans tag");
        _proxy.Lines[0].WorkerId.Should().BeNull();
    }

    [Fact]
    public void AppendLine_failed_line_has_correct_outcome()
    {
        _proxy.AppendLineForParsing("  ✗ Login échoué", "kbis-xex");
        _proxy.Lines[0].Outcome.Should().Be(SmokeOutcome.Failed);
        _proxy.Lines[0].WorkerId.Should().Be("kbis-xex");
    }

    [Fact]
    public void AppendLine_skipped_line_has_correct_outcome()
    {
        _proxy.AppendLineForParsing("  ⊘ Cache hit skip");
        _proxy.Lines[0].Outcome.Should().Be(SmokeOutcome.Skipped);
    }

    [Fact]
    public void AppendLine_ordering_increments_per_line()
    {
        _proxy.AppendLineForParsing("  ✓ A");
        _proxy.AppendLineForParsing("  ✗ B");
        _proxy.AppendLineForParsing("  ⊘ C");
        _proxy.Lines[0].Order.Should().Be(0);
        _proxy.Lines[1].Order.Should().Be(1);
        _proxy.Lines[2].Order.Should().Be(2);
    }

    [Fact]
    public void AppendLine_handles_trailing_cr()
    {
        _proxy.AppendLineForParsing("  ✓ With CR\r");
        _proxy.Lines.Should().HaveCount(1);
        _proxy.Lines[0].Description.Should().Be("With CR");
    }

    // ── ResetLines + ResetLinesForWorker ─────────────────────────────────

    [Fact]
    public void ResetLines_clears_all_lines()
    {
        _proxy.AppendLineForParsing("  ✓ A");
        _proxy.AppendLineForParsing("  ✗ B");
        _proxy.ResetLines();
        _proxy.Lines.Should().BeEmpty();
    }

    [Fact]
    public void ResetLinesForWorker_removes_only_lines_for_that_worker()
    {
        _proxy.AppendLineForParsing("  ✓ VK step", "kbis-vk");
        _proxy.AppendLineForParsing("  ✓ XEX step", "kbis-xex");
        _proxy.AppendLineForParsing("  ✗ VK fail", "kbis-vk");

        _proxy.ResetLinesForWorker("kbis-vk");

        _proxy.Lines.Should().HaveCount(1);
        _proxy.Lines[0].WorkerId.Should().Be("kbis-xex");
    }

    [Fact]
    public void ResetLinesForWorker_on_unknown_worker_leaves_other_lines_intact()
    {
        _proxy.AppendLineForParsing("  ✓ A", "worker-a");
        _proxy.ResetLinesForWorker("worker-nonexistent");
        _proxy.Lines.Should().HaveCount(1);
    }

    // ── LastGeneratedPdfPath ─────────────────────────────────────────────

    [Fact]
    public void LastGeneratedPdfPath_returns_null_when_stdout_is_null()
    {
        // Aucun run effectué → LastFullStdout = null.
        _proxy.LastGeneratedPdfPath().Should().BeNull();
    }

    [Fact]
    public void LastGeneratedPdfPath_returns_null_when_stdout_has_no_pdf_marker()
    {
        _proxy.ManualInjectStdoutForParsing("  ✓ Test sans PDF\n");
        _proxy.LastGeneratedPdfPath().Should().BeNull();
    }

    [Fact]
    public void LastGeneratedPdfPath_returns_path_when_file_exists()
    {
        var pdfPath = Path.Combine(_tempDir, "output.pdf");
        File.WriteAllText(pdfPath, "fake pdf content");

        var stdout = $"PDF généré sur disque ({pdfPath})\n";
        _proxy.ManualInjectStdoutForParsing(stdout);

        _proxy.LastGeneratedPdfPath().Should().Be(pdfPath);
    }

    [Fact]
    public void LastGeneratedPdfPath_returns_null_when_file_does_not_exist()
    {
        var missingPath = Path.Combine(_tempDir, "missing.pdf");
        // S'assurer que le fichier n'existe vraiment pas.
        if (File.Exists(missingPath)) File.Delete(missingPath);

        var stdout = $"PDF généré sur disque ({missingPath})\n";
        _proxy.ManualInjectStdoutForParsing(stdout);

        _proxy.LastGeneratedPdfPath().Should().BeNull(
            "le service vérifie File.Exists avant de retourner le path");
    }

    [Fact]
    public void LastGeneratedPdfPath_handles_path_with_spaces()
    {
        var pdfDir = Path.Combine(_tempDir, "folder with spaces");
        Directory.CreateDirectory(pdfDir);
        var pdfPath = Path.Combine(pdfDir, "rapport final.pdf");
        File.WriteAllText(pdfPath, "fake");

        var stdout = $"PDF généré sur disque ({pdfPath})\n";
        _proxy.ManualInjectStdoutForParsing(stdout);

        _proxy.LastGeneratedPdfPath().Should().Be(pdfPath);
    }
}
