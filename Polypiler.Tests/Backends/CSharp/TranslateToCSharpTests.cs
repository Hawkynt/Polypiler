using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// The <c>translate</c> command with <c>--to csharp</c>, run inside the test process. The acceptance
/// tests start the real executable.
/// </summary>
[TestFixture]
public sealed class TranslateToCSharpTests {

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

  private (int ExitCode, string Output, string Error) Emit(IrModule module, params (string Option, string Value)[] options) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var all = options.Append(("--to", "csharp")).Append(("--output", this._out)).ToDictionary(option => option.Item1, option => option.Item2);
    var exitCode = TranslateCommand.Emit(module, all, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  /// <summary>The round trip: the file the command writes is C# again and computes the published value.</summary>
  [Test]
  public void Translate_WritesTheClassAndNamesIt() {
    var path = Path.Combine(this._out, "Adler32.cs");
    var (exitCode, output, error) = Invoke("translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", "--to", "csharp", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(output.Trim(), Is.EqualTo(path));
    Assert.That(Directory.GetFiles(this._out), Is.EqualTo(new[] { path }));
    var text = File.ReadAllText(path);
    Assert.That(text, Does.Contain("\npublic static class Adler32 {\n").And.Contain("  public static uint Compute(byte[] data) {\n").And.Not.Contain("namespace"));
    Assert.That(GeneratedCSharp.Compile(text, true).Method("Compute").Invoke(null, [PrototypeCases.Text("Wikipedia")]), Is.EqualTo(300286872u));
  }

  /// <summary>Scenario: The namespace option wraps the class — wherever the option stands.</summary>
  [Test]
  public void Namespace_IsWrittenIntoTheFile() {
    var (exitCode, _, error) = Invoke("translate", "--namespace", "Demo.Checksums", PrototypeFixture.PathOf("Crc32"), "--to", "csharp", "--output", this._out, "--from", "csharp");
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    var text = File.ReadAllText(Path.Combine(this._out, "Crc32.cs"));
    Assert.That(text.Split('\n').First(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal)), Is.EqualTo("namespace Demo.Checksums;"));
    Assert.That(GeneratedCSharp.Compile(text, false).ClassName, Is.EqualTo("Demo.Checksums.Crc32"));
  }

  /// <summary>Test design 10: a command line that cannot be carried out is PLY0001, exit code 2, and writes nothing.</summary>
  [TestCase(new[] { "--to", "cobol", "--output", "out" }, "unknown target language 'cobol'; the prototype writes 'javascript' and 'csharp'", TestName = "Misuse: unknown target")]
  [TestCase(new[] { "--to", "CSharp", "--output", "out" }, "unknown target language 'CSharp'", TestName = "Misuse: the target is case-sensitive")]
  [TestCase(new[] { "--to", "csharp" }, "the option '--output' is missing", TestName = "Misuse: missing output")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace" }, "the option '--namespace' needs a value", TestName = "Misuse: namespace without a value")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "" }, "'' is not a namespace name", TestName = "Misuse: empty namespace")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "2fast" }, "'2fast' is not a namespace name", TestName = "Misuse: namespace starting with a digit")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "Demo..Checksums" }, "'Demo..Checksums' is not a namespace name", TestName = "Misuse: namespace with an empty part")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "Demo; class X" }, "'Demo; class X' is not a namespace name", TestName = "Misuse: namespace that is more than a name")]
  [TestCase(new[] { "--to", "javascript", "--output", "out", "--namespace", "Demo" }, "the option '--namespace' does not apply to the target language 'javascript'", TestName = "Misuse: namespace for another target")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--profile", "net1.1" }, "unknown C# profile 'net1.1'; the profiles are 'net2.0', 'net3.5', 'net4.6', 'net4.8', 'netstandard2.0', 'netstandard2.1', 'netcoreapp3.1', 'net8.0', 'net10.0'", TestName = "Misuse: unknown profile")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--profile", "Net8.0" }, "unknown C# profile 'Net8.0'", TestName = "Misuse: the profile is case-sensitive")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--profile" }, "the option '--profile' needs a value", TestName = "Misuse: profile without a value")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--profile", "net10.0", "--langversion", "7.3" }, "the C# profile 'net10.0' is written in C# 14 or later, not in C# 7.3", TestName = "Misuse: language version below the profile's")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--profile", "net4.8", "--langversion", "7.2" }, "the C# profile 'net4.8' is written in C# 7.3 or later, not in C# 7.2", TestName = "Misuse: language version just below the profile's")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--langversion", "13" }, "the C# profile 'net10.0' is written in C# 14 or later, not in C# 13", TestName = "Misuse: language version below the default profile's")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--langversion", "latest" }, "'latest' is not a C# language version; the versions are 1, 2, 3, 4, 5, 6, 7, 7.1, 7.2, 7.3, 8, 9, 10, 11, 12, 13, 14", TestName = "Misuse: language version that is none")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--langversion", "15" }, "'15' is not a C# language version", TestName = "Misuse: language version that does not exist yet")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--langversion" }, "the option '--langversion' needs a value", TestName = "Misuse: language version without a value")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "2fast", "--profile", "net1.1" }, "'2fast' is not a namespace name", TestName = "Misuse: the namespace is judged before the profile")]
  [TestCase(new[] { "--to", "javascript", "--output", "out", "--langversion", "8" }, "the option '--langversion' does not apply to the target language 'javascript'", TestName = "Misuse: language version for another target")]
  public void Misuse_IsAUsageError(string[] arguments, string message) {
    string[] all = ["translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", .. arguments.Select(argument => argument == "out" ? this._out : argument)];
    var (exitCode, output, error) = Invoke(all);
    Assert.That((exitCode, output), Is.EqualTo((Program.UsageError, "")));
    Assert.That(error, Does.StartWith("error PLY0001: " + message)
      .And.Contain("usage: polypiler translate <file> --from <csharp|javascript> --to <javascript|csharp> --output <directory> [--export <name>[,<name>...]] [--explain-types] [--profile <name>] [--module <esm|cjs|iife>] [--global <name>] [--typed-arrays] [--namespace <name>] [--langversion <version>]"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>What the frontend refuses is refused whatever the target is.</summary>
  [TestCase("public static decimal F(decimal a) => a + 1;", TranslateCommand.UnsupportedConstruct, "PLY1002")]
  [TestCase("public static int F( => 1;", TranslateCommand.InvalidSource, "PLY1000")]
  public void RefusedSource_WritesNothing(string method, int expected, string code) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(method));
    var (exitCode, output, error) = Invoke("translate", source, "--from", "csharp", "--to", "csharp", "--output", this._out);
    Assert.That((exitCode, output), Is.EqualTo((expected, "")));
    Assert.That(error, Does.Contain($"error {code}: "));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>
  /// What the C# frontend imports beyond the first prototype - the wider types, the checked operators,
  /// the further statements, constants and library operations - comes back as C# the compiler accepts
  /// without complaint, whatever the project's overflow setting.
  /// </summary>
  [TestCase("public static long F(long a) => a + 1;", 41L, 42L)]
  [TestCase("public static double F(double a) => a * 0.5;", 3.0, 1.5)]
  [TestCase("public static int F(int a) => checked(a + 1);", 41, 42)]
  [TestCase("public static byte F(int a) => checked((byte)a);", 255, (byte)255)]
  [TestCase("public static int F(int a) { do { a--; } while (a > 0); return a; }", 5, 0)]
  [TestCase("public static int F(int a) { switch (a) { case 1: return 2; } return a; }", 1, 2)]
  [TestCase("public static int F(int a) { while (a > 0) { a--; if (a == 3) break; } return a; }", 9, 3)]
  [TestCase("public static int F(int a) { for (int i = 0; i < a; ++i) { continue; } return a; }", 4, 4)]
  [TestCase("public static int F(int a) => a++ + a;", 1, 3)]
  [TestCase("static readonly int[] T = { 1, 5 }; public static int F(int a) => T[a];", 1, 5)]
  [TestCase("public static int F(int a) => new[] { a, 7 }[1];", 0, 7)]
  [TestCase("public static int F(int a) => System.Math.Abs(a);", -3, 3)]
  public void WidenedSource_IsWrittenAsCSharp(string methods, object argument, object expected) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(methods));
    var (exitCode, output, error) = Invoke("translate", source, "--from", "csharp", "--to", "csharp", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    var text = File.ReadAllText(output.Trim());
    foreach (var checking in new[] { false, true })
      Assert.That(GeneratedCSharp.Compile(text, checking).Method("F").Invoke(null, [argument]), Is.EqualTo(expected));
  }

  private sealed record Riddle : IrStmt;

  /// <summary>Test design 9: an IR construct the C# backend does not know is exit code 3 and writes nothing.</summary>
  [Test]
  public void UnknownConstruct_IsExitCode3AndWritesNothing() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock([new Riddle()]))]);
    var (exitCode, output, error) = this.Emit(module);
    Assert.That((exitCode, TranslateCommand.UnsupportedConstruct, output), Is.EqualTo((3, 3, "")));
    Assert.That(error.TrimEnd(), Is.EqualTo("error PLY1052: 'Riddle' is not supported by the C# backend yet"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>
  /// Test design 10: a construct that has no form in the chosen profile is PLY1053, exit code 3, and
  /// writes nothing. No row of the real table lacks a replacement, so the command is given a table
  /// in which the rotate has none; in the profile that has the rotate the same table writes the file.
  /// </summary>
  [Test]
  public void ConstructWithoutAFormInTheProfile_IsExitCode3AndWritesNothing() {
    var bare = CSharpCapability.OfOperation("BitOperations.RotateLeft(uint, int)", CSharpProfile.NetCoreApp31, IrIntrinsicOp.RotateLeft, [IrTypeKind.U32],
      new CSharpForm("global::System.Numerics.BitOperations.RotateLeft"));
    var table = new CSharpCapabilities([bare, .. CSharpCapabilities.Default.Rows]);
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static uint F(uint a) => System.Numerics.BitOperations.RotateLeft(a, 3);"));
    using var output = new StringWriter();
    using var error = new StringWriter();
    var options = new Dictionary<string, string> { ["--output"] = this._out, ["--profile"] = "net4.8" };
    Assert.That(TranslateCommand.Emit(module, options, output, error, table), Is.EqualTo(TranslateCommand.UnsupportedConstruct).And.EqualTo(3));
    Assert.That((output.ToString(), error.ToString().TrimEnd()), Is.EqualTo(("", "error PLY1053: 'BitOperations.RotateLeft(uint, int)' cannot be expressed in C# profile 'net4.8'")));
    Assert.That(Directory.Exists(this._out), Is.False);

    options["--profile"] = "netcoreapp3.1";
    Assert.That(TranslateCommand.Emit(module, options, output, error, table), Is.EqualTo(Program.Success));
    Assert.That(File.ReadAllText(output.ToString().Trim()), Does.Contain("global::System.Numerics.BitOperations.RotateLeft(a, 3)").And.Not.Contain("private"));
  }

  /// <summary>Test design 9: IR the verifier rejects is an internal error, exit code 70, and writes nothing.</summary>
  [Test]
  public void IllTypedIr_IsAnInternalError() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrConst(IrType.I32, 1), new IrConst(IrType.Bool, 1)))
    ]))]);
    var (exitCode, output, error) = this.Emit(module);
    Assert.That((exitCode, output), Is.EqualTo((TranslateCommand.InternalError, "")));
    Assert.That(error, Does.StartWith("error PLY0100: ill-typed IR in 'F': Add is applied to i32 and bool"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  [Test]
  public void Emit_TakesTheNamespaceFromTheOptions() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static int F() => 1;"));
    var (exitCode, output, error) = this.Emit(module, ("--namespace", "Demo"));
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(File.ReadAllText(output.Trim()), Does.Contain("\nnamespace Demo;\n"));
  }

  /// <summary>An output directory that cannot exist, because a file has its name.</summary>
  [Test]
  public void OutputThatIsAFile_IsAUsageError() {
    var blocked = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(blocked, PrototypeFixture.Wrap("public static int F() => 1;"));
    var (exitCode, output, error) = Invoke("translate", blocked, "--from", "csharp", "--to", "csharp", "--output", blocked);
    Assert.That((exitCode, output), Is.EqualTo((Program.UsageError, "")));
    Assert.That(error, Does.StartWith("error PLY0001: the output directory '").And.Contain("cannot be written"));
  }

  [Test]
  public void Help_NamesBothTargetsAndTheNamespaceOption()
    => Assert.That(Invoke("--help").Output, Does.Contain(TranslateCommand.UsageLine).And.Contain("--to <javascript|csharp>").And.Contain("[--namespace <name>]"));

  /// <summary>The help names the two options that choose what the C# is written for.</summary>
  [Test]
  public void Help_NamesTheProfileOptions()
    => Assert.That(Invoke("--help").Output, Does.Contain("[--profile <name>]").And.Contain("[--langversion <version>]"));

  /// <summary>
  /// <c>--profile</c> and <c>--langversion</c> choose what the file is written for: it is the file
  /// the emitter writes for that profile, its header names the profile, and it compiles against the
  /// profile's reference assemblies. Without either option it is written for the default profile.
  /// </summary>
  [TestCase("net2.0", null, "net2.0, C# 2")]
  [TestCase("net3.5", null, "net3.5, C# 3")]
  [TestCase("net4.8", null, "net4.8, C# 7.3")]
  [TestCase("net4.8", "10", "net4.8, C# 10")]
  [TestCase("netstandard2.1", "8.0", "netstandard2.1, C# 8")]
  [TestCase("net10.0", null, "net10.0, C# 14")]
  [TestCase(null, null, "net10.0, C# 14")]
  [TestCase(null, "14", "net10.0, C# 14")]
  public void Profile_IsTakenFromTheCommandLine(string? profile, string? languageVersion, string header) {
    string[] options = [.. profile is null ? [] : new[] { "--profile", profile }, .. languageVersion is null ? [] : new[] { "--langversion", languageVersion }];
    var (exitCode, output, error) = Invoke(["translate", PrototypeFixture.PathOf("wide/Library"), "--from", "csharp", "--to", "csharp", "--output", this._out, "--namespace", "Demo", .. options]);
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    var text = File.ReadAllText(output.Trim());
    var parsed = CSharpProfile.Parse(profile ?? "net10.0", languageVersion);
    Assert.That(text, Does.Contain($"\n// Profile: {header}.\n"));
    Assert.That(text, Is.EqualTo(CSharpEmitter.Emit(PrototypeFixture.Imported("wide/Library"), parsed, "Demo").Single().Content));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, parsed.Name, parsed.LanguageVersionName), Is.Empty);
  }

  /// <summary>JavaScript becomes C# for an old framework as well: Cipher's rotate as C# 2, with the result of the original.</summary>
  [Test]
  public void JavaScript_IsWrittenForAProfile() {
    var (exitCode, output, error) = Invoke("translate", JavaScriptFixture.OpCodesPath, "--from", "javascript", "--to", "csharp", "--output", this._out, "--export", "RotL32", "--profile", "net2.0");
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    var text = File.ReadAllText(output.Trim());
    Assert.That(text, Does.Contain("\n// Profile: net2.0, C# 2.\n").And.Not.Contain("=>").And.Not.Contain("var "));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net2.0", "2"), Is.Empty);
    foreach (var checking in new[] { false, true })
      Assert.That(GeneratedCSharp.Compile(text, checking).Method("RotL32").Invoke(null, [0x12345678u, 8]), Is.EqualTo(878082066u));
  }

  /// <summary>The options <see cref="TranslateCommand.Emit"/> is given choose the profile for the check and for the file alike.</summary>
  [Test]
  public void Emit_TakesTheProfileFromTheOptions() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static uint F(uint a) => System.Numerics.BitOperations.RotateLeft(a, 3);"));
    var (exitCode, output, error) = this.Emit(module, ("--profile", "net4.6"), ("--langversion", "7.3"));
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(File.ReadAllText(output.Trim()), Does.Contain("\n// Profile: net4.6, C# 7.3.\n").And.Contain("private static uint RotateLeft(uint value, int count) {"));
  }

}
