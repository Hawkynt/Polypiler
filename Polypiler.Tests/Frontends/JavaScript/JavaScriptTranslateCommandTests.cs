using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// The <c>translate</c> command with a JavaScript source, run inside the test process. The acceptance
/// tests start the real executable and run what it writes.
/// </summary>
[TestFixture]
public sealed class JavaScriptTranslateCommandTests {

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static (int ExitCode, string Output, string Error) Invoke(params string[] arguments) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(arguments, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private (int ExitCode, string Output, string Error) Translate(string source, params string[] further) {
    var path = Path.Combine(this._directory, "snippet.js");
    File.WriteAllText(path, source);
    return Invoke(["translate", path, "--from", "javascript", "--to", "javascript", "--output", this._out, .. further]);
  }

  /// <summary>A JavaScript module goes through the IR and comes out as a module; the caveats of the import are noted.</summary>
  [Test]
  public void Translate_WritesTheModuleAndNotesTheCaveats() {
    var source = JavaScriptFixture.PathOf("adler32");
    var path = Path.Combine(this._out, "adler32.mjs");
    var (exitCode, output, error) = Invoke("translate", source, "--from", "javascript", "--to", "javascript", "--output", this._out);
    Assert.That((exitCode, output.Trim()), Is.EqualTo((Program.Success, path)));
    Assert.That(File.ReadAllText(path), Does.Contain("export function compute(data) {"));
    Assert.That(error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line[..line.IndexOf(':', source.Length + 1)]), Is.EqualTo(new[] {
      source + ": note js-oob-read",
      source + ": note js-div-zero",
      source + ": note js-step-wrap"
    }));
  }

  /// <summary>
  /// <c>--export</c> picks functions out of a file that does not import as a whole; the names are
  /// separated by commas, and what the functions do not use is not noted.
  /// </summary>
  [TestCase("RotL32,RotR32")]
  [TestCase(" RotR32 , RotL32,")]
  public void Translate_ExportsTheNamedFunctions(string names) {
    var (exitCode, _, error) = Invoke("translate", "--export", names, JavaScriptFixture.OpCodesPath, "--from", "javascript", "--to", "javascript", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    var module = File.ReadAllText(Path.Combine(this._out, "OpCodes.mjs"));
    Assert.That(module.Split('\n').Where(line => line.StartsWith("export function ", StringComparison.Ordinal)),
      Is.EqualTo(new[] { "export function RotL32(value, positions) {", "export function RotR32(value, positions) {" }));
  }

  /// <summary>
  /// The same IR goes to every target: Cipher's rotation becomes C# that the C# compiler accepts
  /// without complaint and that returns what the work order states for it.
  /// </summary>
  [Test]
  public void Translate_WritesCSharpFromJavaScript() {
    var (exitCode, _, error) = Invoke("translate", JavaScriptFixture.OpCodesPath, "--from", "javascript", "--to", "csharp", "--output", this._out, "--export", "RotL32", "--namespace", "Cipher");
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    var text = File.ReadAllText(Path.Combine(this._out, "OpCodes.cs"));
    Assert.That(text, Does.Contain("namespace Cipher;").And.Contain("public static uint RotL32(uint value, int positions) {"));
    foreach (var checking in new[] { false, true }) {
      var build = GeneratedCSharp.Compile(text, checking);
      Assert.That(build.ClassName, Is.EqualTo("Cipher.OpCodes"));
      Assert.That(build.Method("RotL32").Invoke(null, [0x12345678u, 8]), Is.EqualTo(878082066u));
    }
  }

  /// <summary>Test design 9 to 13: what the importer refuses is exit code 4 for a syntax error and 3 otherwise, and writes nothing.</summary>
  [TestCase("function f( {", new string[0], 4, "PLY1100: Unexpected end of input")]
  [TestCase("function f(a) { return a | 0; }", new string[0], 3, "PLY1101: parameter 'a' of 'f' has no supported JSDoc type")]
  [TestCase("function f() { while (true) { break; } }", new string[0], 3, "PLY1102: 'break' is not supported yet")]
  [TestCase("/** @param {int32} a @returns {int32} */ function f(a) { return a + 1; }", new string[0], 3, "PLY1103: the result of 'a + 1' is a JavaScript number; coerce it with | 0 or >>> 0")]
  [TestCase("function f() { }", new[] { "--export", "g" }, 3, "PLY1104: the file has no function 'g' to export")]
  [TestCase("const size = 4;", new string[0], 3, "PLY1102: 'a file without a function' is not supported yet")]
  public void RefusedSource_IsExitCode3Or4AndWritesNothing(string source, string[] further, int expected, string message) {
    var (exitCode, output, error) = this.Translate(source, further);
    Assert.That((exitCode, output), Is.EqualTo((expected, "")));
    Assert.That(error, Does.Match(@"^.*snippet\.js\(1,\d+\): error " + System.Text.RegularExpressions.Regex.Escape(message) + @"\r?\n$"));
    Assert.That(Directory.Exists(this._out), Is.False);
    Assert.That((TranslateCommand.InvalidSource, TranslateCommand.UnsupportedConstruct), Is.EqualTo((4, 3)));
  }

  /// <summary>A command line that cannot be carried out is PLY0001, exit code 2, and names what exists.</summary>
  [TestCase(new[] { "A.js", "--from", "javascript", "--to", "javascript", "--output", "out", "--export" }, "the option '--export' needs a value", TestName = "Misuse: --export without value")]
  [TestCase(new[] { "A.js", "--from", "javascript", "--to", "javascript", "--output", "out", "--export", " , " }, "the option '--export' names no function", TestName = "Misuse: --export without a name")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "javascript", "--output", "out", "--export", "F" }, "the option '--export' does not apply to the source language 'csharp'", TestName = "Misuse: --export for C#")]
  [TestCase(new[] { "A.js", "--from", "typescript", "--to", "javascript", "--output", "out" }, "unknown source language 'typescript'; the prototype reads 'csharp' and 'javascript'", TestName = "Misuse: unknown --from names the sources")]
  [TestCase(new[] { "A.js", "--from", "javascript", "--to", "cobol", "--output", "out" }, "unknown target language 'cobol'; the prototype writes 'javascript' and 'csharp'", TestName = "Misuse: unknown --to names the targets")]
  [TestCase(new[] { "missing.js", "--from", "javascript", "--to", "javascript", "--output", "out" }, "the source file 'missing.js' cannot be read", TestName = "Misuse: unreadable JavaScript file")]
  public void Misuse_IsAUsageError(string[] arguments, string message) {
    var (exitCode, output, error) = Invoke(arguments.Prepend("translate").ToArray());
    Assert.That((exitCode, output), Is.EqualTo((Program.UsageError, "")));
    Assert.That(error, Does.StartWith("error PLY0001: " + message).And.Contain("usage: " + TranslateCommand.UsageLine));
  }

  /// <summary>The help and the usage line name every language the command takes, and <c>--export</c>.</summary>
  [Test]
  public void UsageLine_NamesTheLanguagesAndTheExportOption() {
    Assert.That(TranslateCommand.UsageLine, Is.EqualTo(
      "polypiler translate <file> --from <csharp|javascript> --to <javascript|csharp> --output <directory> [--export <name>[,<name>...]] [--namespace <name>] [--profile <name>] [--langversion <version>]"));
    Assert.That(Invoke("--help").Output, Does.Contain(TranslateCommand.UsageLine).And.Contain("small subsets of its languages"));
  }

}
