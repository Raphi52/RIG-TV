using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests;

/// <summary>
/// Tests unitaires pour <see cref="RegressionCatalog"/> — logique pure de parsing source C#
/// et classification.
///
/// Couvre :
/// - ExtractFromFile : summary inline / multi-ligne / absent ; ignore [Theory]/[Fact]
/// - ClassifyByClassName : mapping connu + fallback "Autres"
/// - RegressionTestInfo.Backend : extraction regex du paramètre backend: dans FQN
///
/// Les méthodes testées sont internal (exposées via InternalsVisibleTo dans
/// Rig.Wpf.Kbis.TestViewer/Properties/AssemblyInfo.cs).
/// </summary>
public class RegressionCatalogTests : IDisposable
{
    private readonly string _tempDir;

    public RegressionCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rig-regcat-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private string WriteCs(string name, string content)
    {
        var path = Path.Combine(_tempDir, name + ".cs");
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
        return path;
    }

    private Dictionary<string, string> Extract(string path)
    {
        var bucket = new Dictionary<string, string>(StringComparer.Ordinal);
        RegressionCatalog.ExtractFromFile(path, bucket);
        return bucket;
    }

    // ── ExtractFromFile : summary inline ─────────────────────────────────

    [Fact]
    public void ExtractFromFile_inline_summary_is_captured()
    {
        var path = WriteCs("inline", @"
public class MyTests
{
    /// <summary>Vérifie que X fonctionne.</summary>
    public void TestX() { }
}");
        var result = Extract(path);
        result.Should().ContainKey("TestX");
        result["TestX"].Should().Be("Vérifie que X fonctionne.");
    }

    [Fact]
    public void ExtractFromFile_inline_summary_with_text_before_and_after_tags()
    {
        var path = WriteCs("inline2", @"
public class MyTests
{
    /// <summary>Vérifie le round-trip.</summary>
    public void TestRoundTrip() { }
}");
        var result = Extract(path);
        result["TestRoundTrip"].Should().Be("Vérifie le round-trip.");
    }

    // ── ExtractFromFile : summary multi-ligne ─────────────────────────────

    [Fact]
    public void ExtractFromFile_multiline_summary_joins_lines()
    {
        var path = WriteCs("multi", @"
public class MyTests
{
    /// <summary>
    /// Vérifie que la valeur
    /// est correctement parsée.
    /// </summary>
    public void TestParsing() { }
}");
        var result = Extract(path);
        result.Should().ContainKey("TestParsing");
        result["TestParsing"].Should().Contain("Vérifie que la valeur");
        result["TestParsing"].Should().Contain("est correctement parsée");
    }

    [Fact]
    public void ExtractFromFile_multiline_summary_does_not_include_summary_tags()
    {
        var path = WriteCs("multi2", @"
public class MyTests
{
    /// <summary>
    /// Ligne une.
    /// Ligne deux.
    /// </summary>
    public void TestMulti() { }
}");
        var result = Extract(path);
        result["TestMulti"].Should().NotContain("<summary>")
            .And.NotContain("</summary>");
    }

    // ── ExtractFromFile : summary absent ──────────────────────────────────

    [Fact]
    public void ExtractFromFile_method_without_summary_is_not_in_bucket()
    {
        var path = WriteCs("nosummary", @"
public class MyTests
{
    [Fact]
    public void TestWithoutSummary() { }
}");
        var result = Extract(path);
        result.Should().NotContainKey("TestWithoutSummary");
    }

    [Fact]
    public void ExtractFromFile_method_with_only_other_doc_comment_is_not_in_bucket()
    {
        var path = WriteCs("nosum2", @"
public class MyTests
{
    /// <remarks>Juste une remarque.</remarks>
    public void TestWithRemarks() { }
}");
        var result = Extract(path);
        result.Should().NotContainKey("TestWithRemarks");
    }

    // ── ExtractFromFile : ignore [Theory]/[Fact] ──────────────────────────

    [Fact]
    public void ExtractFromFile_ignores_Fact_attribute_between_summary_and_method()
    {
        var path = WriteCs("attrs", @"
public class MyTests
{
    /// <summary>Test avec attributs.</summary>
    [Fact]
    public void TestWithFact() { }
}");
        var result = Extract(path);
        result.Should().ContainKey("TestWithFact",
            "[Fact] entre summary et méthode ne doit pas empêcher la capture");
        result["TestWithFact"].Should().Be("Test avec attributs.");
    }

    [Fact]
    public void ExtractFromFile_ignores_Theory_attribute_between_summary_and_method()
    {
        var path = WriteCs("theory", @"
public class MyTests
{
    /// <summary>Test théorie.</summary>
    [Theory]
    [InlineData(1)]
    public void TestWithTheory(int x) { }
}");
        var result = Extract(path);
        result.Should().ContainKey("TestWithTheory");
        result["TestWithTheory"].Should().Be("Test théorie.");
    }

    [Fact]
    public void ExtractFromFile_ignores_multiple_stacked_attributes()
    {
        var path = WriteCs("multattrs", @"
public class MyTests
{
    /// <summary>Test multi-attrs.</summary>
    [Fact]
    [Trait(""Category"", ""Integration"")]
    public void TestStacked() { }
}");
        var result = Extract(path);
        result.Should().ContainKey("TestStacked");
    }

    // ── ExtractFromFile : async methods ───────────────────────────────────

    [Fact]
    public void ExtractFromFile_async_Task_method_is_captured()
    {
        var path = WriteCs("async", @"
using System.Threading.Tasks;
public class MyTests
{
    /// <summary>Test async.</summary>
    public async Task TestAsync() { }
}");
        var result = Extract(path);
        result.Should().ContainKey("TestAsync");
        result["TestAsync"].Should().Be("Test async.");
    }

    // ── ExtractFromFile : multiple methods in same file ───────────────────

    [Fact]
    public void ExtractFromFile_captures_multiple_methods_independently()
    {
        var path = WriteCs("multi_methods", @"
public class MyTests
{
    /// <summary>Méthode A.</summary>
    public void MethodA() { }

    /// <summary>Méthode B.</summary>
    public void MethodB() { }

    public void MethodC_NoSummary() { }
}");
        var result = Extract(path);
        result.Should().ContainKey("MethodA").And.ContainKey("MethodB");
        result.Should().NotContainKey("MethodC_NoSummary");
        result["MethodA"].Should().Be("Méthode A.");
        result["MethodB"].Should().Be("Méthode B.");
    }

    [Fact]
    public void ExtractFromFile_does_not_overwrite_existing_key_in_bucket()
    {
        // Deux fichiers partagent un nom de méthode → le bucket doit garder
        // la première valeur insérée (ContainsKey check dans le code source).
        var path1 = WriteCs("dup1", @"
public class Tests1
{
    /// <summary>Version 1.</summary>
    public void SharedMethod() { }
}");
        var path2 = WriteCs("dup2", @"
public class Tests2
{
    /// <summary>Version 2.</summary>
    public void SharedMethod() { }
}");
        var bucket = new Dictionary<string, string>(StringComparer.Ordinal);
        RegressionCatalog.ExtractFromFile(path1, bucket);
        RegressionCatalog.ExtractFromFile(path2, bucket);

        bucket["SharedMethod"].Should().Be("Version 1.",
            "la première insertion gagne (pas d'overwrite)");
    }

    // ── ClassifyByClassName ───────────────────────────────────────────────

    [Theory]
    [InlineData("FoundActiveScenarios", "Lecture")]
    [InlineData("ShapeScenarios", "Lecture (patterns)")]
    [InlineData("OrderByInvariantScenarios", "Lecture (invariants)")]
    [InlineData("NotFoundScenarios", "Erreurs")]
    [InlineData("AuthScenarios", "Auth & Validation")]
    public void ClassifyByClassName_returns_known_categories(string className, string expectedCategory)
    {
        RegressionCatalog.ClassifyByClassName(className).Should().Be(expectedCategory);
    }

    [Theory]
    [InlineData("UnknownClass")]
    [InlineData("SomethingElse")]
    [InlineData("")]
    [InlineData("foundactivescenarios")]  // case-sensitive
    public void ClassifyByClassName_returns_Autres_for_unknown_classes(string className)
    {
        RegressionCatalog.ClassifyByClassName(className).Should().Be("Autres");
    }

    // ── RegressionTestInfo.Backend ────────────────────────────────────────

    [Fact]
    public void Backend_extracts_value_from_parameters_string()
    {
        var info = new RegressionTestInfo(
            FullyQualifiedName: "NS.Class.Method(backend: \"postgresql\")",
            ClassName: "Class",
            MethodName: "Method",
            Parameters: "(backend: \"postgresql\")",
            Description: "",
            Category: "Autres");

        info.Backend.Should().Be("postgresql");
    }

    [Fact]
    public void Backend_returns_null_when_no_backend_param()
    {
        var info = new RegressionTestInfo(
            FullyQualifiedName: "NS.Class.Method(42)",
            ClassName: "Class",
            MethodName: "Method",
            Parameters: "(42)",
            Description: "",
            Category: "Autres");

        info.Backend.Should().BeNull();
    }

    [Fact]
    public void Backend_returns_null_when_parameters_is_empty()
    {
        var info = new RegressionTestInfo(
            FullyQualifiedName: "NS.Class.Method",
            ClassName: "Class",
            MethodName: "Method",
            Parameters: "",
            Description: "",
            Category: "Autres");

        info.Backend.Should().BeNull();
    }

    [Fact]
    public void Backend_handles_backend_with_spaces_around_colon()
    {
        // Le regex est "backend:\s*\"..."  → supporte l'espace après le colon.
        var info = new RegressionTestInfo(
            FullyQualifiedName: "NS.Class.Method",
            ClassName: "Class",
            MethodName: "Method",
            Parameters: "(backend:  \"sqlserver\")",
            Description: "",
            Category: "Autres");

        info.Backend.Should().Be("sqlserver");
    }

    [Fact]
    public void Backend_extracts_only_the_first_match()
    {
        var info = new RegressionTestInfo(
            FullyQualifiedName: "NS.Class.Method",
            ClassName: "Class",
            MethodName: "Method",
            Parameters: "(backend: \"first\", backend: \"second\")",
            Description: "",
            Category: "Autres");

        // Regex.Match = premier match uniquement.
        info.Backend.Should().Be("first");
    }

    // ── DisplayName ───────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_without_params_returns_just_MethodName()
    {
        var info = new RegressionTestInfo("fqn", "Cls", "MyMethod", "", "desc", "cat");
        info.DisplayName.Should().Be("MyMethod");
    }

    [Fact]
    public void DisplayName_with_params_appends_parameters()
    {
        var info = new RegressionTestInfo("fqn", "Cls", "MyMethod", "(42)", "desc", "cat");
        info.DisplayName.Should().Be("MyMethod(42)");
    }
}
