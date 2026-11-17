using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;

namespace Polypiler.Tests.Cli.Translate;

/// <summary>
/// The exports of <c>translate</c> (#180, test design 4, 9, 14): every public function without
/// <c>--export</c>, the named ones with it, <c>PLY0013</c> for a name the input lacks and for an input
/// without any public function, and nothing written after either.
/// </summary>
[TestFixture]
public sealed class TranslateExportTests {

  private const string Sum32 = TranslateWorkspace.Fixtures + "/sum32.cs";

  private TranslateWorkspace _workspace = null!;

  [SetUp]
  public void CreateWorkspace() => this._workspace = new();

  [TearDown]
  public void DeleteWorkspace() => this._workspace.Dispose();

  [Test]
  public void AMissingExport_IsPLY0013() {
    var (exitCode, output, error) = this._workspace.Run("translate", Sum32, "--from", "csharp", "--to", "javascript", "--export", "Mul", "--output", "out");
    Assert.That((exitCode, output, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.InvalidSource, "", $"polypiler: error PLY0013: export 'Mul' not found in '{Sum32}'")));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [Test]
  public void AnInputWithoutAPublicFunction_IsPLY0013() {
    var empty = this._workspace.Write("Empty.cs", "public static class Empty { private static int F() => 1; }");
    var (exitCode, _, error) = this._workspace.Run("translate", empty, "--from", "csharp", "--to", "javascript", "--output", "out");
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.InvalidSource, "polypiler: error PLY0013: 'Empty.cs' contains no exportable function")));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [Test]
  public void TheExportGivenOrOmitted_GivesTheSameFiles() {
    Assert.That(this._workspace.Run("translate", Sum32, "--from", "csharp", "--to", "javascript", "--export", "Add", "--output", "a").ExitCode, Is.Zero);
    Assert.That(this._workspace.Run("translate", Sum32, "--from", "csharp", "--to", "javascript", "--output", "b").ExitCode, Is.Zero);
    Assert.That(File.ReadAllText(this._workspace.Resolve("a/Sum32.mjs")), Is.EqualTo(File.ReadAllText(this._workspace.Resolve("b/Sum32.mjs"))));
    Assert.That(this._workspace.Manifest("a").Api.Select(symbol => symbol.Name), Is.EqualTo(new[] { "Add" }));
  }

  [Test]
  public void AnExport_LeavesTheOtherFunctionsInsideTheModule() {
    var two = this._workspace.Write("Two.cs", "public static class Two { public static int A(int x) => B(x) + 1; public static int B(int x) => x * 2; public static int C(int x) => x; }");
    var (exitCode, _, error) = this._workspace.Run("translate", two, "--from", "csharp", "--to", "javascript", "--export", "A,C", "--output", "out");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(this._workspace.Manifest("out").Api.Select(symbol => symbol.Name), Is.EqualTo(new[] { "A", "C" }));
    var text = File.ReadAllText(this._workspace.Resolve("out/Two.mjs"));
    Assert.That(text, Does.Contain("export function A(").And.Contain("export function C(").And.Not.Contain("export function B("));
  }

  [Test]
  public void ANativeFunction_IsAnExport() {
    var native = this._workspace.Write("Ext.cs", """
      using System.Runtime.InteropServices;
      public static class Ext {
        [DllImport("liba")]
        public static extern int A();
        [DllImport("liba")]
        public static extern int B();
      }
      """);
    var (exitCode, _, error) = this._workspace.Run("translate", native, "--from", "csharp", "--to", "csharp", "--export", "B", "--output", "out");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    var text = File.ReadAllText(this._workspace.Resolve("out/Ext.cs"));
    Assert.That(text, Does.Contain("public static extern int B(").And.Not.Contain("public static extern int A("));
  }

  [Test]
  public void TheExportOfARustInput_IsChosenAsForCSharp() {
    var rust = this._workspace.Write("lib.rs", "pub fn a(x: i32) -> i32 { x }\npub fn b(x: i32) -> i32 { x }\n");
    var (exitCode, _, error) = this._workspace.Run("translate", rust, "--from", "rust", "--to", "javascript", "--export", "b", "--output", "out");
    Assert.That((exitCode, error.Contains("error", System.StringComparison.Ordinal)), Is.EqualTo(((int)ExitCode.Success, false)), error);
    Assert.That(this._workspace.Manifest("out").Api.Select(symbol => symbol.Name), Is.EqualTo(new[] { "b" }));
  }

}
