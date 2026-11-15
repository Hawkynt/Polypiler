using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests.Generics;

/// <summary>
/// The option <c>--instantiate</c> of <c>translate</c>: the instantiations of public generic methods
/// that the class itself does not use. A malformed one, an unknown method and an unknown type are
/// mistakes of the command line.
/// </summary>
[TestFixture]
public sealed class InstantiateOptionTests {

  private const string Source = "public static class Lib { public static T Id<T>(T a) => a; public static T First<T>(T[] a, T b) => a.Length > 0 ? a[0] : b; }";

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
    File.WriteAllText(Path.Combine(this._directory, "Lib.cs"), Source);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private (int ExitCode, string Output, string Error) Translate(string to, params string[] options) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", Path.Combine(this._directory, "Lib.cs"), "--from", "csharp", "--to", to, "--output", Path.Combine(this._directory, "out"), .. options], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private string Written(string file) => File.ReadAllText(Path.Combine(this._directory, "out", file));

  /// <summary>Test design 8: without an instantiation a public generic is refused; with one, repeated or listed, it is written.</summary>
  [Test]
  public void Instantiate_WritesTheNamedInstantiations() {
    var refused = this.Translate("csharp");
    Assert.That(refused.ExitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
    Assert.That(refused.Error, Does.Contain("PLY1002").And.Contain("'Id' has no instantiation; name one with --instantiate"));

    var (exitCode, _, error) = this.Translate("csharp", "--instantiate", "Id[double]", "--instantiate", "First[int],Id[long]");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(this.Written("Lib.cs"), Does.Contain("public static T Id<T>(T a)").And.Contain("public static T First<T>(T[] a, T b)"));

    var javaScript = this.Translate("javascript", "--instantiate", "Id[double],Id[long]", "--instantiate", "First[int]");
    Assert.That(javaScript.ExitCode, Is.EqualTo((int)ExitCode.Success), javaScript.Error);
    Assert.That(this.Written("Lib.mjs"), Does.Contain("export function First(").And.Contain("export function Id(a)").And.Not.Contain("Id$"));
  }

  /// <summary>Test design 10: a malformed instantiation, an unknown method, an unknown type and another number of type arguments are PLY0003, exit code 2, and nothing is written.</summary>
  [TestCase("Id")]
  [TestCase("Id[]")]
  [TestCase("Nope[int]")]
  [TestCase("Id[Nope]")]
  [TestCase("Id[int,int]")]
  public void Instantiate_RefusesAMistake(string instantiation) {
    var (exitCode, output, error) = this.Translate("javascript", "--instantiate", instantiation, "--instantiate", "First[int]");
    Assert.That((exitCode, output), Is.EqualTo(((int)ExitCode.UsageError, "")));
    Assert.That(error, Does.Contain("error PLY0003"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>The option belongs to the C# source language.</summary>
  [Test]
  public void Instantiate_IsAnOptionOfCSharp() {
    File.WriteAllText(Path.Combine(this._directory, "m.js"), "export function f() { return 1; }");
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", Path.Combine(this._directory, "m.js"), "--from", "javascript", "--to", "csharp", "--output", Path.Combine(this._directory, "out"), "--instantiate", "Id[int]"], output, error);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error.ToString(), Does.Contain("the option '--instantiate' does not apply to the source language 'javascript'"));
  }

}
