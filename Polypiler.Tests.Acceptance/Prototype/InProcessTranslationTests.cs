using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// A translation in process (<see cref="Toolchain.Translate"/>) is the translation of the executable:
/// for every kind of command line the tests translate in process, the same exit code, the same text
/// on standard output and on standard error, and the same files, byte for byte. A translation kept for the run is shared only by command lines
/// that differ in nothing but the output directory.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
public sealed class InProcessTranslationTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>A working directory of its own with the sources a command line reads.</summary>
  private string WorkingDirectory(string name) {
    var directory = Path.Combine(this._directory, name);
    Directory.CreateDirectory(Path.Combine(directory, "wide"));
    File.Copy(PrototypeFixture.PathOf("Adler32"), Path.Combine(directory, "Adler32.cs"));
    File.Copy(PrototypeFixture.PathOf("wide/Fnv1a64"), Path.Combine(directory, "wide", "Fnv1a64.cs"));
    File.Copy(JavaScriptFixture.PathOf("operators"), Path.Combine(directory, "Operators.mjs"));
    File.WriteAllText(Path.Combine(directory, "Refused.cs"), PrototypeFixture.Wrap("public static void F() { new object(); }"));
    File.WriteAllText(Path.Combine(directory, "Invalid.cs"), PrototypeFixture.Wrap("public static int F( => 1;"));
    File.WriteAllText(Path.Combine(directory, "Rotate.cs"), PrototypeFixture.Wrap("public static uint F(uint x) => (x << 8) | (x >> 24);"));
    return directory;
  }

  /// <summary>The kinds of command line the tests translate in process, and the ways they go wrong.</summary>
  private static IEnumerable<TestCaseData> CommandLines() {
    IEnumerable<string[]> lines = [
      .. ProfileCase.All.Select(profile => (string[])["Adler32.cs", "--from", "csharp", "--to", "javascript", .. profile.Arguments]),
      .. CSharpProfile.All.Select(profile => (string[])["wide/Fnv1a64.cs", "--from", "csharp", "--to", "csharp", "--profile", profile.Name]),
      ["Adler32.cs", "--from", "csharp", "--to", "javascript"],
      ["Adler32.cs", "--from", "csharp", "--to", "csharp", "--namespace", "Demo", "--profile", "net4.8"],
      ["wide/Fnv1a64.cs", "--from", "csharp", "--to", "csharp", "--profile", "net2.0", "--float", "accurate"],
      ["wide/Fnv1a64.cs", "--from", "csharp", "--to", "javascript", "--profile", "es5", "--float", "strict"],
      ["Operators.mjs", "--from", "javascript", "--to", "javascript"],
      ["Operators.mjs", "--from", "javascript", "--to", "csharp", "--profile", "net3.5"],
      ["Rotate.cs", "--from", "csharp", "--to", "csharp", "--profile", "net10.0", "--explain-opt"],
      ["Rotate.cs", "--from", "csharp", "--to", "javascript", "--opt", "none"],
      ["Rotate.cs", "--from", "csharp", "--to", "csharp", "--profile", "net10.0", "--disable-opt", "O0100"],
      ["Refused.cs", "--from", "csharp", "--to", "javascript"],
      ["Invalid.cs", "--from", "csharp", "--to", "csharp"],
      ["Adler32.cs", "--from", "csharp", "--to", "csharp", "--profile", "net1.1"],
      ["Adler32.cs", "--from", "csharp", "--to", "javascript", "--profile", "es5", "--module", "esm"]
    ];
    return lines.Select(line => new TestCaseData((object)line).SetArgDisplayNames(string.Join(' ', line)));
  }

  /// <summary>Test design 4: the same exit code, the same text on each stream and the same files, in process as with the executable.</summary>
  [TestCaseSource(nameof(CommandLines))]
  public void InProcess_IsTheExecutable(string[] line) {
    string[] arguments = ["translate", .. line, "--output", "out"];
    var executable = this.WorkingDirectory("executable");
    var inProcess = this.WorkingDirectory("in-process");
    var expected = Toolchain.Executable(executable, arguments);
    var actual = Toolchain.TranslateInProcess(inProcess, arguments);
    Assert.That(actual, Is.EqualTo(expected), "exit code, standard output and standard error");
    Assert.That(Files(Path.Combine(inProcess, "out")), Is.EqualTo(Files(Path.Combine(executable, "out"))));
  }

  /// <summary>The same with absolute paths for the source and the output, as the fixtures are given.</summary>
  [Test]
  public void InProcess_IsTheExecutable_WithAbsolutePaths() {
    string[] arguments = ["translate", PrototypeFixture.PathOf("wide/Crc32Table"), "--from", "csharp", "--to", "csharp", "--profile", "net4.6"];
    var executable = Path.Combine(this._directory, "executable");
    var inProcess = Path.Combine(this._directory, "in-process");
    var expected = Toolchain.RunPolypiler(this._directory, [.. arguments, "--output", executable]);
    var actual = Toolchain.Translate(this._directory, [.. arguments, "--output", inProcess]);
    Assert.That(actual.ExitCode, Is.EqualTo(expected.ExitCode));
    Assert.That(actual.Output.Replace(inProcess, executable, StringComparison.Ordinal), Is.EqualTo(expected.Output));
    Assert.That(Files(inProcess), Is.EqualTo(Files(executable)));
  }

  /// <summary>Test design 7: a translation is shared only by command lines that differ in the output directory alone.</summary>
  [Test]
  public void TranslationKey_DiffersInEveryByteAndOption_ButNotInTheOutputDirectory() {
    var source = Encoding.UTF8.GetBytes(PrototypeFixture.Wrap("public static int F(int x) => x + 1;"));
    var changed = Encoding.UTF8.GetBytes(PrototypeFixture.Wrap("public static int F(int x) => x + 2;"));
    string[] line = ["translate", "Snippet.cs", "--from", "csharp", "--to", "javascript", "--profile", "es5", "--output", "out"];
    var key = Toolchain.TranslationKey(line, source);
    Assert.That(Toolchain.TranslationKey([.. line[..^1], "elsewhere"], source), Is.EqualTo(key), "the output directory");
    Assert.That(Toolchain.TranslationKey(line, changed), Is.Not.EqualTo(key), "one byte of the source");
    Assert.That(Toolchain.TranslationKey(["translate", "Snippet.cs", "--from", "csharp", "--to", "javascript", "--profile", "es2015", "--output", "out"], source), Is.Not.EqualTo(key), "one option");
    Assert.That(Toolchain.TranslationKey(["translate", "Other.cs", "--from", "csharp", "--to", "javascript", "--profile", "es5", "--output", "out"], source), Is.Not.EqualTo(key), "the name of the source");
  }

  /// <summary>Two translations that differ in one source byte or one option write their own files; one that differs in the output directory alone writes the same.</summary>
  [Test]
  public void KeptTranslation_IsNotSharedAcrossSourcesOrOptions() {
    var marker = Guid.NewGuid().ToString("N");
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap($"public static int F(int x) => x + 1; /* {marker} */"));
    var first = this.Generated("first", "--profile", "es5");
    Assert.That(this.Generated("again", "--profile", "es5"), Is.EqualTo(first));
    Assert.That(this.Generated("level", "--profile", "es2015"), Is.Not.EqualTo(first).And.Contain("es2015"));

    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap($"public static int F(int x) => x + 2; /* {marker} */"));
    Assert.That(this.Generated("byte", "--profile", "es5"), Does.Contain("x + 2").And.Not.Contain("x + 1"));
  }

  private string Generated(string output, params string[] further) {
    var (exitCode, text) = Toolchain.Translate(this._directory, ["translate", "Snippet.cs", "--from", "csharp", "--to", "javascript", .. further, "--output", output]);
    Assert.That(exitCode, Is.Zero, text);
    var file = Directory.GetFiles(Path.Combine(this._directory, output)).Single();
    Assert.That(text.Trim(), Is.EqualTo(Path.Combine(output, Path.GetFileName(file))));
    return File.ReadAllText(file);
  }

  /// <summary>A translation in process needs what it is for: a source file and an output directory.</summary>
  [TestCase(new object[] { new[] { "translate", "--from", "csharp", "--to", "javascript", "--output", "out" } })]
  [TestCase(new object[] { new[] { "translate", "Adler32.cs", "--from", "csharp", "--to", "javascript" } })]
  [TestCase(new object[] { new[] { "translate", "Missing.cs", "--from", "csharp", "--to", "javascript", "--output", "out" } })]
  [TestCase(new object[] { new[] { "--version" } })]
  public void CommandLineWithoutSourceOrOutput_IsLeftToTheExecutable(string[] arguments) {
    File.Copy(PrototypeFixture.PathOf("Adler32"), Path.Combine(this._directory, "Adler32.cs"));
    Assert.That(() => Toolchain.Translate(this._directory, arguments), Throws.TypeOf<AssertionException>());
  }

  /// <summary>The files of a directory by name, with their bytes; empty when there is no directory.</summary>
  private static Dictionary<string, byte[]> Files(string directory)
    => Directory.Exists(directory) ? Directory.GetFiles(directory).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes) : [];

}
