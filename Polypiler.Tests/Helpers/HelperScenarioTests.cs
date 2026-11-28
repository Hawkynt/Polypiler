using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// The scenarios of #1353. A rotate with a constant count and width becomes straight-line code once
/// its helper is inlined and folded, and stays the call of the helper without the inlining (O0006).
/// Every helper of the backends is a helper of the library but the documented exceptions, which each
/// name the reason they stay. Whether the forms compute what the source does is asked of every
/// differential test of the families.
/// </summary>
[TestFixture]
public sealed class HelperScenarioTests {

  private const string Rotate = "public static ushort F(ushort x) => ushort.RotateLeft(x, 3);";

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-helpers-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private string Translate(params string[] further) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(Rotate));
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    using var standard = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", source, "--from", "csharp", "--to", "javascript", "--profile", "es5", "--output", output, .. further], standard, error);
    Assert.That((exitCode, error.ToString()), Is.EqualTo(((int)ExitCode.Success, "")));
    return File.ReadAllText(Directory.GetFiles(output).Single(path => Path.GetFileName(path) != Manifest.FileName));
  }

  /// <summary>The lines of the body of <c>F</c>.</summary>
  private static List<string> Body(string text) {
    var lines = text.Split('\n');
    var start = Array.FindIndex(lines, line => line.StartsWith("function F(", StringComparison.Ordinal));
    return [.. lines.Skip(start + 1).TakeWhile(line => line != "}")];
  }

  // Scenario: A rotate with constant count and width becomes straight-line code
  [Test]
  public void ConstantRotate_BecomesStraightLineCode() {
    var text = this.Translate();
    var body = Body(text);
    Assert.That(body, Is.Not.Empty);
    Assert.That(body, Has.None.Contains("if").And.None.Contains("?").And.None.Matches(@"[\w$]\("), text);
    Assert.That(text, Does.Not.Contain("RotateLeftNarrow"), "the helper the inlining used up is gone");

    // F(0x8001) is 0x000C: what the module that was written computes, in the interpreter.
    var module = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(Rotate)).Module!;
    var legalized = JavaScriptEmitter.Legalize(Optimizer.Run(module, new OptimizerOptions()).Module, () => PrecomputedImplementations.Module, JavaScriptProfile.Parse("es5"), FloatMode.Accurate);
    var optimized = IrPassPipeline.Default.Run(legalized, new OptimizerOptions()).Module;
    Assert.That(IrInterpreter.Call(optimized, "F", [(ushort)0x8001]), Is.EqualTo((ushort)0x000C));
    Assert.That(optimized.Functions.Select(function => function.Name), Is.EqualTo(new[] { "F" }));
  }

  [Test]
  public void WithoutInlining_TheRotateCallsItsHelper() {
    var text = this.Translate("--disable-opt", "O0006");
    Assert.That(Body(text), Has.Some.Contains("RotateLeftNarrow(x, 3, 16)"));
    Assert.That(text, Does.Contain("function RotateLeftNarrow(value, count, bits) {"));
  }

  // Scenario: Every helper exists once
  /// <summary>
  /// No file under the <c>Helpers</c> folders of the backends assembles a helper from source lines but
  /// those the backend guide lists as the exceptions, each with its reason; and no helper of the
  /// library has a text of its own in a backend.
  /// </summary>
  [Test]
  public void EveryHelperOfTheBackends_IsOfTheLibraryOrADocumentedException() {
    var root = Root();
    var guide = File.ReadAllText(Path.Combine(root, "docs", "architecture", "BACKENDS.md"));
    var section = guide[guide.IndexOf("## Helpers that stay with a backend", StringComparison.Ordinal)..];
    section = section[..section.IndexOf("\n## ", 3, StringComparison.Ordinal)];
    var documented = Regex.Matches(section, @"^\| `(Polypiler\.Core\.Backends\.[^`]+\.cs)` \| ([^|]+) \|", RegexOptions.Multiline)
      .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.Trim());
    var files = new[] { "Polypiler.Core.Backends.CSharp", "Polypiler.Core.Backends.JavaScript", "Polypiler.Core.Backends.Python", "Polypiler.Core.Backends.Rust", "Polypiler.Core.Backends.Go", "Polypiler.Core.Backends.VisualBasic", "Polypiler.Core.Backends.Kotlin" }
      .SelectMany(project => Directory.GetFiles(Path.Combine(root, project, "Helpers"), "*.cs", SearchOption.AllDirectories))
      .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
      .Order(StringComparer.Ordinal)
      .ToList();
    Assert.That(files, Is.EquivalentTo(documented.Keys), "every file of the helper folders is a documented exception, and every exception exists");
    Assert.That(documented.Values, Has.All.Length.GreaterThan(20), "each exception states its reason");

    // A helper of the library has no text in a backend: none of its names is declared as a helper there.
    var library = typeof(HelperOperations).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(method => method.Name).ToHashSet();
    foreach (var file in files) {
      var text = File.ReadAllText(Path.Combine(root, file));
      Assert.That(Regex.Matches(text, @"function (\w+)\(").Select(match => match.Groups[1].Value).Where(library.Contains), Is.Empty, file);
      Assert.That(Regex.Matches(text, @"new\(""(\w+)"", ""[^""]+"", ").Select(match => match.Groups[1].Value).Where(library.Contains), Is.Empty, file);
    }
  }

  private static string Root() {
    var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Polypiler.slnx")))
      directory = directory.Parent;

    return directory!.FullName;
  }

}
