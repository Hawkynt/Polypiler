using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Tooling;
using Polypiler.Tooling.Report;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Report;

/// <summary>
/// Unit tests for <see cref="ShowcaseCommand"/>, <see cref="ShowcaseGenerator"/>,
/// <see cref="MsilDisassembler"/>, and <see cref="JvmDisassembler"/>.
/// </summary>
[TestFixture]
public sealed class ShowcaseTests {

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-showcase-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteDirectory() {
    if (Directory.Exists(this._directory))
      Directory.Delete(this._directory, true);
  }

  private string At(string path) => Path.Combine(this._directory, path.Replace('/', Path.DirectorySeparatorChar));

  private void Write(string path, string content) {
    Directory.CreateDirectory(Path.GetDirectoryName(this.At(path))!);
    File.WriteAllText(this.At(path), content, new UTF8Encoding(false));
  }

  private string CreateFakePolypilerMarker(string relative = "fake-polypiler") {
    this.Write(relative, "stub");
    return this.At(relative);
  }

  /// <summary>Runs <see cref="Polypiler.Cli.Program.Run"/> in-process so tests stay fast without spawning subprocesses.</summary>
  private sealed class InProcessCliRunner : IProcessRunner {

    public ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout) {
      var start = argv[0] == "dotnet" ? 2 : 1;
      var cliArgs = argv.Skip(start).ToArray();
      using var output = new StringWriter();
      using var error = new StringWriter();
      var code = Polypiler.Cli.Program.Run(cliArgs, output, error);
      return new(code, output.ToString(), error.ToString(), false);
    }

  }

  private sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> handler) : IProcessRunner {

    public ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout) => handler(argv);

  }

  [Test]
  public void DiscoverTargets_FindsAllSupportedBackendsAndDialectsFromCli() {
    var targets = ShowcaseGenerator.DiscoverTargets(new InProcessCliRunner(), ["polypiler"]);
    var byBackend = targets.ToDictionary(t => t.Backend, t => t.Profiles, StringComparer.Ordinal);
    Assert.That(byBackend.Keys, Does.Contain("csharp").And.Contain("javascript").And.Contain("rust")
      .And.Contain("python").And.Contain("perl").And.Contain("cpp")
      .And.Contain("vbscript").And.Contain("visualbasic").And.Contain("batch").And.Contain("msil").And.Contain("jvm").And.Contain("go").And.Contain("kotlin"));
    Assert.That(byBackend["javascript"], Does.Contain("es5").And.Contain("es2022"));
    Assert.That(byBackend["csharp"], Does.Contain("net2.0").And.Contain("net10.0"));
    Assert.That(byBackend["batch"], Does.Contain("cmd").And.Contain("dos622"));
    Assert.That(byBackend["cpp"], Does.Contain("cpp/cpp17").And.Contain("cpp/cpp23"));
    Assert.That(byBackend["go"], Does.Contain("go1.18").And.Contain("go1.27"));
  }

  [Test]
  public void DiscoverTargets_ThrowsOnFailureTimeoutInvalidJsonOrEmptyList() {
    var failed = new FakeRunner(_ => new(1, "", "boom", false));
    Assert.That(() => ShowcaseGenerator.DiscoverTargets(failed, ["polypiler"]), Throws.InvalidOperationException);

    var timedOut = new FakeRunner(_ => new(-1, "", "", true));
    Assert.That(() => ShowcaseGenerator.DiscoverTargets(timedOut, ["polypiler"]), Throws.InvalidOperationException);

    var badJson = new FakeRunner(_ => new(0, "not-json", "", false));
    Assert.That(() => ShowcaseGenerator.DiscoverTargets(badJson, ["polypiler"]), Throws.InvalidOperationException);

    var emptyList = new FakeRunner(_ => new(0, "[]", "", false));
    Assert.That(() => ShowcaseGenerator.DiscoverTargets(emptyList, ["polypiler"]), Throws.InvalidOperationException);
  }

  [Test]
  public void TargetsCommand_ReportsErrorsForMissingOrInvalidBatchFiles() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Polypiler.Cli.Program.Run(["targets", "--run-batch", this.At("missing.json")], output, error),
      Is.EqualTo((int)Polypiler.Core.ExitCode.EnvironmentError));

    this.Write("bad.json", "{not-an-array");
    Assert.That(Polypiler.Cli.Program.Run(["targets", "--run-batch", this.At("bad.json")], output, error),
      Is.EqualTo((int)Polypiler.Core.ExitCode.UsageError));

    this.Write("null.json", "null");
    Assert.That(Polypiler.Cli.Program.Run(["targets", "--run-batch", this.At("null.json")], output, error),
      Is.EqualTo((int)Polypiler.Core.ExitCode.UsageError));
  }

  [Test]
  public void LoadExamples_ParsesMetadataHeadersAndPlainFiles() {
    this.Write("examples/01-header.cs", "// Showcase: Custom Title\r\n// Category: Cat\r\n// Summary: Does things.\r\n// extra comment\r\npublic static class A { public static int F() => 1; }\r\n");
    this.Write("examples/02-plain.cs", "public static class B { public static int G() => 2; }\n");

    var examples = ShowcaseGenerator.LoadExamples(this.At("examples"));
    Assert.That(examples, Has.Count.EqualTo(2));
    Assert.That(examples[0].Title, Is.EqualTo("Custom Title"));
    Assert.That(examples[0].Category, Is.EqualTo("Cat"));
    Assert.That(examples[0].Summary, Is.EqualTo("Does things."));
    Assert.That(examples[0].DisplaySource, Does.StartWith("// extra comment"));
    Assert.That(examples[1].Title, Is.EqualTo("02-plain"));
    Assert.That(examples[1].Category, Is.EqualTo("General"));
    Assert.That(examples[1].Summary, Is.Empty);
  }

  [Test]
  public void MsilDisassembler_DisassemblesRealEmittedAssemblyWithBranchesSwitchFloatsStaticsAndStrings() {
    const string source = """
      using System.Numerics;
      public static class Sample {
        private static int _state = 3;
        public static int Compute(int x, long l, float f, double d, int[] arr) {
          _state += x;
          int v = checked(x + 1);
          long y = l + 10000000000L;
          float z = f + 1.5f;
          double w = d + 2.25;
          uint r = BitOperations.RotateLeft((uint)x, 3);
          int picked;
          switch (x) { case 0: picked = 10; break; case 1: picked = 20; break; case 2: picked = 30; break; default: picked = 40; break; }
          if (arr.Length > 0)
            arr[0] = picked + (int)y + (int)z + (int)w + (int)r + _state;
          return v;
        }
      }
      """;
    var imported = CSharpFrontend.Import("Sample.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    var files = MsilEmitter.Emit(imported.Module!, MsilProfile.Default);
    var dll = files.Single(f => f.Path.EndsWith(".dll", StringComparison.Ordinal));
    var text = MsilDisassembler.Disassemble(dll.GetBytes());

    Assert.That(text, Does.Contain(".assembly Sample {}"));
    Assert.That(text, Does.Contain(".class public abstract sealed Sample"));
    Assert.That(text, Does.Contain(".field public static int32 _state"));
    Assert.That(text, Does.Contain(".method public hidebysig static int32 Compute("));
    Assert.That(text, Does.Contain("add.ovf"));
    Assert.That(text, Does.Contain("ldc.i8 10000000000"));
    Assert.That(text, Does.Contain("ldc.r4 1.5"));
    Assert.That(text, Does.Contain("ldc.r8 2.25"));
    Assert.That(text, Does.Contain("beq IL_"));
    Assert.That(text, Does.Contain("ret"));
  }

  [Test]
  public void JvmDisassembler_DisassemblesRealJarAndClassWithControlFlowSwitchArraysAndStatics() {
    const string source = """
      public static class Sample {
        private static int _state = 5;
        public static int Compute(int x, long l, float f, double d) {
          _state += x;
          int[] buf = new int[4];
          buf[0] = x;
          int sum = 0;
          for (int i = 0; i < 4; ++i)
            sum += buf[i];
          int a;
          switch (x) { case 0: a = 1; break; case 1: a = 2; break; case 2: a = 3; break; default: a = 4; break; }
          int b;
          switch (x) { case 10: b = 7; break; case 1000: b = 9; break; default: b = 11; break; }
          long y = l + 9000000000L;
          float z = f + 1.25f;
          double w = d + 3.5;
          return sum + a + b + (int)y + (int)z + (int)w + _state;
        }
      }
      """;
    var imported = CSharpFrontend.Import("Sample.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);

    var jarFile = JvmEmitter.Emit(imported.Module!, JvmProfile.Default).Single(f => f.Path.EndsWith(".jar", StringComparison.Ordinal));
    var jarText = JvmDisassembler.DisassembleJar(jarFile.GetBytes());
    Assert.That(jarText, Does.Contain("public class Sample"));
    Assert.That(jarText, Does.Contain("tableswitch"));
    Assert.That(jarText, Does.Contain("lookupswitch"));
    Assert.That(jarText, Does.Contain("newarray int"));
    Assert.That(jarText, Does.Contain("ireturn"));

    var classFile = JvmEmitter.Emit(imported.Module!, JvmProfile.Default with { PackageFormat = JvmPackageFormat.Class })
      .Single(f => f.Path.EndsWith(".class", StringComparison.Ordinal));
    var classText = JvmDisassembler.DisassembleClass(classFile.GetBytes());
    Assert.That(classText, Is.EqualTo(jarText));
  }

  [Test]
  public void JvmDisassembler_HandlesErrorsAndSyntheticClassFeatures() {
    Assert.That(() => JvmDisassembler.DisassembleClass([0, 1, 2, 3]), Throws.InstanceOf<InvalidDataException>());

    // Invalid constant pool tag (99)
    byte[] badTag = [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x3D, 0x00, 0x02, 99];
    Assert.That(() => JvmDisassembler.DisassembleClass(badTag), Throws.InstanceOf<InvalidDataException>());

    // Synthetic minimal class with super class, interface, invokeinterface, goto_w, unknown opcode, newarray variants, string constant, method handle
    var bytes = BuildSyntheticJvmClass();
    var text = JvmDisassembler.DisassembleClass(bytes);
    Assert.That(text, Does.Contain("extends Base"));
    Assert.That(text, Does.Contain("implements Iface"));
    Assert.That(text, Does.Contain("invokeinterface"));
    Assert.That(text, Does.Contain("goto_w"));
    Assert.That(text, Does.Contain(".byte 0xFE"));
    Assert.That(text, Does.Contain("newarray boolean"));
    Assert.That(text, Does.Contain("newarray char"));
    Assert.That(text, Does.Contain("newarray float"));
    Assert.That(text, Does.Contain("newarray double"));
    Assert.That(text, Does.Contain("newarray byte"));
    Assert.That(text, Does.Contain("newarray short"));
    Assert.That(text, Does.Contain("newarray long"));
    Assert.That(text, Does.Contain("newarray 99"));
    Assert.That(text, Does.Contain("\"hello\""));
    Assert.That(text, Does.Contain("iinc 1, -2"));
  }

  [Test]
  public void StructuralKey_IgnoresTheProfileOfAVisualBasicNetModuleAndProject() {
    static ShowcaseFile[] Files(string version, string body) => [
      new("M.vb", "vb", $"' M: generated by polypiler.\n' Visual Basic profile: vbnet/vb{version} (Visual Basic {version} on .NET 10), compiled with RemoveIntegerChecks.\n{body}", false),
      new("M.vbproj", "xml", $"<Project>\n    <LangVersion>{version}</LangVersion>\n    <TargetFramework>net10.0</TargetFramework>\n</Project>\n", false)
    ];
    Assert.That(ShowcaseGenerator.StructuralKey(Files("9", "Public Module M\n")), Is.EqualTo(ShowcaseGenerator.StructuralKey(Files("16.9", "Public Module M\n"))));
    Assert.That(ShowcaseGenerator.StructuralKey(Files("9", "Public Module M\n")), Is.Not.EqualTo(ShowcaseGenerator.StructuralKey(Files("16.9", "Public Module N\n"))));
    // The language version is the profile's only in a project file.
    Assert.That(ShowcaseGenerator.StructuralKey([new("M.vb", "vb", "<LangVersion>9</LangVersion>\n", false)]), Is.Not.EqualTo(ShowcaseGenerator.StructuralKey([new("M.vb", "vb", "<LangVersion>10</LangVersion>\n", false)])));
  }

  [Test]
  public void RenderOutputFile_HandlesTextBinaryAndCorruptFallbacks() {
    var cs = ShowcaseGenerator.RenderOutputFile("A.cs", Encoding.UTF8.GetBytes("class A {}\r\n"));
    Assert.That(cs.Fence, Is.EqualTo("csharp"));
    Assert.That(cs.Disassembled, Is.False);
    Assert.That(cs.Content, Does.Not.Contain("\r"));

    Assert.That(ShowcaseGenerator.RenderOutputFile("a.js", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("javascript"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.py", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("python"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.rs", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("rust"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.kt", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("kotlin"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.kts", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("kotlin"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.toml", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("toml"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.pm", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("perl"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.hpp", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("cpp"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.vbs", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("vbscript"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.bas", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("vb"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.go", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("go"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.bat", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("bat"));
    Assert.That(ShowcaseGenerator.RenderOutputFile("a.xyz", Encoding.UTF8.GetBytes("1")).Fence, Is.EqualTo("text"));

    // Corrupt .dll / .jar / .class fall back cleanly to hex summary
    var corruptDll = ShowcaseGenerator.RenderOutputFile("bad.dll", [1, 2, 3, 4]);
    Assert.That(corruptDll.Disassembled, Is.True);
    Assert.That(corruptDll.Content, Does.Contain("; binary artifact (4 bytes)"));

    var corruptJar = ShowcaseGenerator.RenderOutputFile("bad.jar", [1, 2, 3]);
    Assert.That(corruptJar.Content, Does.Contain("; binary artifact (3 bytes)"));

    var corruptClass = ShowcaseGenerator.RenderOutputFile("bad.class", [1, 2]);
    Assert.That(corruptClass.Content, Does.Contain("; binary artifact (2 bytes)"));

    var rawBinary = ShowcaseGenerator.RenderOutputFile("raw.bin", [0x00, 0xFF, 0x10]);
    Assert.That(rawBinary.Disassembled, Is.True);
    Assert.That(rawBinary.Content, Does.Contain("0000: 00 FF 10"));
  }

  [Test]
  public void ShowcaseCommand_GeneratesChecksAndGroupsDialectsUnderBackends() {
    this.Write("examples/01-add.cs", "// Showcase: Add & Async\n// Category: Core\n// Summary: Simple ops.\nusing System.Threading.Tasks;\npublic static class K { public static async Task<int> AddAsync(int a, int b) => await Task.FromResult(a + b); }\n");
    this.Write("examples/02-ops.cs", "// Showcase: Bitwise\n// Category: Core\n// Summary: Rotate.\nusing System.Numerics;\npublic static class O { public static uint Rot(uint x, int n) => BitOperations.RotateLeft(x, n); }\n");
    this.Write("examples/03-boundary.cs", "// Showcase: Unsupported\n// Category: Boundary\n// Summary: Refused.\nusing System.IO;\npublic static class B { public static int N(string p) => File.ReadAllLines(p).Length; }\n");
    this.Write("examples/04-native.cs", "// Showcase: Native\n// Category: Core\n// Summary: P/Invoke.\nusing System.Runtime.InteropServices;\npublic static class N { [DllImport(\"geom\")] private static extern int geom_norm(int x); public static int Measure(int x) => geom_norm(x); }\n");
    var marker = this.CreateFakePolypilerMarker();
    var runner = new InProcessCliRunner();

    // Generate across multi-dialect text + binary backends (showing partial refusal ⚠️ and distinct dialect groups)
    var (exit1, out1, err1) = this.RunCommand(runner, "--examples", "examples", "--out", "docs/out.md", "--polypiler", marker,
      "--targets", "csharp:net2.0,csharp:net10.0,javascript:es5,javascript:es2022,rust:rust2018,rust:rust2024,msil,jvm:jvm/21,batch,kotlin:kotlin2.0,kotlin:kotlin2.4");
    Assert.That(exit1, Is.Zero, err1);
    Assert.That(out1, Does.Contain("written"));
    var markdown = File.ReadAllText(this.At("docs/out.md"));
    Assert.That(markdown, Does.Contain("# Cross-Backend Translation Showcase"));
    Assert.That(markdown, Does.Contain("## Dialect support by backend"));
    Assert.That(markdown, Does.Contain("⚠️ 1/2"));
    Assert.That(markdown, Does.Contain("<strong>MSIL</strong>"));
    Assert.That(markdown, Does.Contain("<strong>JVM</strong>"));
    // The Kotlin profile banner differs per dialect only, so both dialects collapse into one group.
    Assert.That(markdown, Does.Contain("<strong>Kotlin</strong>"));
    Assert.That(markdown, Does.Contain("```kotlin"));
    Assert.That(markdown, Does.Contain("<code>kotlin2.0</code>, <code>kotlin2.4</code>"));
    Assert.That(markdown, Does.Contain("Refused dialects:"));
    Assert.That(markdown, Does.Contain("Frontend refusal (all backends & dialects)"));
    Assert.That(markdown, Does.Contain("Refused by:"));
    Assert.That(markdown, Does.Contain("| Construct | Category | Static ratio |"), "the static ratio of each translation (#1590)");
    Assert.That(markdown, Does.Contain("| Core | 1.00 |"), "a C# source is typed throughout");
    Assert.That(markdown, Does.Contain("| Boundary | — |"), "a source the frontend refuses has no report");
    Assert.That(ShowcaseGenerator.StaticRatio(this.At("missing.types.json")), Is.Null);

    // Second run reports unchanged
    var (exit2, out2, _) = this.RunCommand(runner, "--examples", "examples", "--out", "docs/out.md", "--polypiler", marker,
      "--targets", "csharp:net2.0,csharp:net10.0,javascript:es5,javascript:es2022,rust:rust2018,rust:rust2024,msil,jvm:jvm/21,batch,kotlin:kotlin2.0,kotlin:kotlin2.4");
    Assert.That(exit2, Is.Zero);
    Assert.That(out2, Does.Contain("unchanged"));

    // --check passes on both LF and CRLF
    var (exit3, out3, _) = this.RunCommand(runner, "--examples", "examples", "--out", "docs/out.md", "--polypiler", marker,
      "--targets", "csharp:net2.0,csharp:net10.0,javascript:es5,javascript:es2022,rust:rust2018,rust:rust2024,msil,jvm:jvm/21,batch,kotlin:kotlin2.0,kotlin:kotlin2.4", "--check");
    Assert.That(exit3, Is.Zero);
    Assert.That(out3, Does.Contain("up to date"));

    File.WriteAllText(this.At("docs/out.md"), markdown.Replace("\n", "\r\n", StringComparison.Ordinal));
    var (exitCrlf, _, _) = this.RunCommand(runner, "--examples", "examples", "--out", "docs/out.md", "--polypiler", marker,
      "--targets", "csharp:net2.0,csharp:net10.0,javascript:es5,javascript:es2022,rust:rust2018,rust:rust2024,msil,jvm:jvm/21,batch,kotlin:kotlin2.0,kotlin:kotlin2.4", "--check");
    Assert.That(exitCrlf, Is.Zero);

    // --check fails with PLY0091 when file differs or is missing
    File.WriteAllText(this.At("docs/out.md"), "stale");
    var (exitDiff, _, errDiff) = this.RunCommand(runner, "--examples", "examples", "--out", "docs/out.md", "--polypiler", marker, "--targets", "csharp:net10.0", "--check");
    Assert.That(exitDiff, Is.EqualTo(Polypiler.Tooling.Program.Failure));
    Assert.That(errDiff, Does.Contain("PLY0091").And.Contain("differs"));

    var (exitMiss, _, errMiss) = this.RunCommand(runner, "--examples", "examples", "--out", "docs/missing.md", "--polypiler", marker, "--targets", "csharp:net10.0", "--check");
    Assert.That(exitMiss, Is.EqualTo(Polypiler.Tooling.Program.Failure));
    Assert.That(errMiss, Does.Contain("PLY0091").And.Contain("is missing"));

    // Auto-discovery with .dll marker
    var dllMarker = this.CreateFakePolypilerMarker("polypiler.dll");
    var (exitAuto, _, errAuto) = this.RunCommand(runner, "--examples", "examples", "--out", "docs/auto.md", "--polypiler", dllMarker);
    Assert.That(exitAuto, Is.Zero, errAuto);

    // Default candidate probing inside currentDirectory (Polypiler.Cli/bin/Release/net10.0/polypiler.dll)
    this.CreateFakePolypilerMarker("Polypiler.Cli/bin/Release/net10.0/polypiler.dll");
    var (exitDefault, _, errDefault) = this.RunCommand(runner, "--examples", "examples", "--out", "docs/default.md", "--targets", "csharp:net10.0");
    Assert.That(exitDefault, Is.Zero, errDefault);
  }

  [Test]
  public void MsilDisassembler_CoversRealRoslynCompiledAssemblyWithGenericsStringsSwitchRefsPointersAndPinned() {
    const string source = """
      using System;
      namespace Demo {
        internal sealed class Holder<T> {
          public readonly int Tag;
          public Holder(int tag) => this.Tag = tag;
          public T Echo<U>(T value, ref int counter, int[,] matrix, int a, int b, int c, int d, int e) {
            counter += a + b + c + d + e + matrix[0, 0];
            return value;
          }
        }
        public static unsafe class Features {
          public static string Describe(int x, int[] data, delegate*<int, int> fn) {
            var h = new Holder<int>(x);
            int c = 0;
            h.Echo<string>(x, ref c, new int[1, 1], 1, 2, 3, 4, 5);
            fixed (int* p = data) {
              c += *p;
            }
            if (fn != null)
              c += fn(x);
            return x switch {
              0 => "zero",
              1 => "one",
              2 => "two",
              3 => "three",
              _ => "other"
            };
          }
        }
      }
      """;
    var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
    var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
      "RichDemo",
      [tree],
      [
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location)
      ],
      new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    using var peStream = new MemoryStream();
    var emitResult = compilation.Emit(peStream);
    Assert.That(emitResult.Success, Is.True, string.Join("\n", emitResult.Diagnostics));

    var text = MsilDisassembler.Disassemble(peStream.ToArray());
    Assert.That(text, Does.Contain(".assembly RichDemo {}"));
    Assert.That(text, Does.Contain("Demo.Holder`1"));
    Assert.That(text, Does.Contain("switch ("));
    Assert.That(text, Does.Contain("\"zero\""));
    Assert.That(text, Does.Contain("initonly"));
    Assert.That(text, Does.Contain("pinned"));
  }

  [Test]
  public void ShowcaseCommand_ReportsUsageAndEnvironmentErrors() {
    this.Write("examples/01.cs", "public static class K { public static int F() => 1; }\n");
    var marker = this.CreateFakePolypilerMarker();
    var runner = new InProcessCliRunner();

    Assert.That(this.RunCommand(runner, "--unknown").ExitCode, Is.EqualTo(Polypiler.Tooling.Program.UsageError));
    Assert.That(this.RunCommand(runner, "--out").ExitCode, Is.EqualTo(Polypiler.Tooling.Program.UsageError));
    Assert.That(this.RunCommand(runner, "--check", "--check").ExitCode, Is.EqualTo(Polypiler.Tooling.Program.UsageError));
    Assert.That(this.RunCommand(runner, "--out", "a.md", "--out", "b.md").ExitCode, Is.EqualTo(Polypiler.Tooling.Program.UsageError));
    Assert.That(this.RunCommand(runner, "--examples", "examples", "--polypiler", marker, "--targets", " , ").ExitCode, Is.EqualTo(Polypiler.Tooling.Program.UsageError));
    Assert.That(this.RunCommand(runner, "--examples", "examples", "--polypiler", marker, "--targets", "unknown-backend").ExitCode, Is.EqualTo(Polypiler.Tooling.Program.UsageError));
    Assert.That(this.RunCommand(runner, "--examples", "does-not-exist", "--polypiler", marker).ExitCode, Is.EqualTo(Polypiler.Tooling.Program.Failure));
    Assert.That(this.RunCommand(runner, "--examples", "examples", "--polypiler", "missing-exe").ExitCode, Is.EqualTo(Polypiler.Tooling.Program.Failure));
    Assert.That(this.RunCommand(runner, "--examples", "examples").ExitCode, Is.EqualTo(Polypiler.Tooling.Program.Failure));

    // Failing backend discovery
    var failDiscovery = new FakeRunner(_ => new(1, "", "help failed", false));
    Assert.That(this.RunCommand(failDiscovery, "--examples", "examples", "--polypiler", marker).ExitCode, Is.EqualTo(Polypiler.Tooling.Program.Failure));

    // Failing batch execution
    var failBatch = new FakeRunner(argv => argv.Contains("--run-batch")
      ? new(1, "", "batch failed", false)
      : new(0, "[{\"backend\":\"custom\",\"profiles\":[\"v1\"]}]", "", false));
    Assert.That(this.RunCommand(failBatch, "--examples", "examples", "--polypiler", marker).ExitCode, Is.EqualTo(Polypiler.Tooling.Program.Failure));

    // Non-PLY stderr, empty stderr, and missing-manifest outputs during batch translation
    var syntheticBatch = new FakeRunner(argv => {
      if (!argv.Contains("--run-batch"))
        return new(0, "[{\"backend\":\"custom\",\"profiles\":[\"v1\",\"v2\"]},{\"backend\":\"nomanifest\",\"profiles\":[\"d1\"]}]", "", false);
      var batchPath = argv[argv.ToList().IndexOf("--run-batch") + 1];
      var jobs = System.Text.Json.JsonDocument.Parse(File.ReadAllText(batchPath)).RootElement;
      var results = new List<object>();
      foreach (var job in jobs.EnumerateArray()) {
        var id = job.GetProperty("id").GetString()!;
        var jobArgs = job.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToList();
        var profile = jobArgs[jobArgs.IndexOf("--profile") + 1];
        var outDir = jobArgs[jobArgs.IndexOf("--output") + 1];
        if (profile == "v1") {
          results.Add(new { id, exitCode = 1, stderr = "plain error without code" });
        } else if (profile == "v2") {
          results.Add(new { id, exitCode = 1, stderr = "" });
        } else {
          Directory.CreateDirectory(outDir);
          File.WriteAllText(Path.Combine(outDir, "polypiler.manifest.json"), "{}");
          File.WriteAllText(Path.Combine(outDir, "out.txt"), "ok");
          results.Add(new { id, exitCode = 0, stderr = "" });
        }
      }
      return new(0, System.Text.Json.JsonSerializer.Serialize(results), "", false);
    });
    var (exitFake, _, errFake) = this.RunCommand(syntheticBatch, "--examples", "examples", "--out", "docs/fake.md", "--polypiler", marker);
    Assert.That(exitFake, Is.Zero, errFake);
    var fakeMd = File.ReadAllText(this.At("docs/fake.md"));
    Assert.That(fakeMd, Does.Contain("plain error without code").And.Contain("translation failed"));

    // Unwritable output path (target path is an existing directory)
    Directory.CreateDirectory(this.At("dir-as-file"));
    var (exitIo, _, errIo) = this.RunCommand(runner, "--examples", "examples", "--out", "dir-as-file", "--polypiler", marker, "--targets", "csharp");
    Assert.That(exitIo, Is.EqualTo(Polypiler.Tooling.Program.Failure));
    Assert.That(errIo, Does.Contain("cannot be written"));

    // Tooling Program usage & dispatch
    using var usageOut = new StringWriter();
    using var usageErr = new StringWriter();
    Assert.That(Polypiler.Tooling.Program.Run([], usageOut, usageErr), Is.Zero);
    Assert.That(usageOut.ToString(), Does.Contain("report showcase"));
    Assert.That(Polypiler.Tooling.Program.Run(["report", "showcase", "--unknown"], usageOut, usageErr), Is.EqualTo(Polypiler.Tooling.Program.UsageError));
  }

  private (int ExitCode, string Output, string Error) RunCommand(IProcessRunner runner, params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var code = ShowcaseCommand.Run(args, output, error, this._directory, runner);
    return (code, output.ToString(), error.ToString());
  }

  private static byte[] BuildSyntheticJvmClass() {
    using var ms = new MemoryStream();
    void U1(byte v) => ms.WriteByte(v);
    void U2(ushort v) { U1((byte)(v >> 8)); U1((byte)v); }
    void U4(uint v) { U2((ushort)(v >> 16)); U2((ushort)v); }
    void Utf8(string s) { var b = Encoding.UTF8.GetBytes(s); U1(1); U2((ushort)b.Length); ms.Write(b); }

    U4(0xCAFEBABEu);
    U2(0); // minor
    U2(65); // major
    // 14 constant pool entries (count = 15)
    U2(15);
    Utf8("Synth");           // #1
    U1(7); U2(1);            // #2 Class Synth
    Utf8("Base");            // #3
    U1(7); U2(3);            // #4 Class Base
    Utf8("Iface");           // #5
    U1(7); U2(5);            // #6 Class Iface
    Utf8("hello");           // #7
    U1(8); U2(7);            // #8 String "hello"
    Utf8("m");               // #9
    Utf8("()V");             // #10
    U1(12); U2(9); U2(10);   // #11 NameAndType m:()V
    U1(11); U2(6); U2(11);   // #12 InterfaceMethodref Iface.m:()V
    Utf8("Code");            // #13
    U1(15); U1(6); U2(12);   // #14 MethodHandle

    U2(0x0011); // public final
    U2(2);      // this
    U2(4);      // super
    U2(1); U2(6); // 1 interface: Iface
    U2(1);      // 1 field with 1 dummy attribute
    U2(0x0019); // public static final
    U2(9);      // name "m"
    U2(10);     // desc "()V"
    U2(1);      // 1 attribute
    U2(13); U4(0);
    U2(1);      // 1 method

    U2(0x001A); // private static final
    U2(9);      // name "m"
    U2(10);     // desc "()V"
    U2(1);      // 1 attribute: Code

    byte[] code = [
      0x12, 8,                 // ldc "hello"
      0x12, 2,                 // ldc class Synth
      0x12, 14,                // ldc MethodHandle -> #14
      0x12, 99,                // ldc out-of-range -> #99
      0x11, 0x01, 0x00,        // sipush 256
      0x57,                    // pop
      0xBC, 4,                 // newarray boolean
      0xBC, 5,                 // newarray char
      0xBC, 6,                 // newarray float
      0xBC, 7,                 // newarray double
      0xBC, 8,                 // newarray byte
      0xBC, 9,                 // newarray short
      0xBC, 11,                // newarray long
      0xBC, 99,                // newarray 99
      0x84, 1, 0xFE,           // iinc 1, -2
      0xB9, 0, 12, 1, 0,       // invokeinterface #12
      0xFE,                    // unknown opcode -> .byte 0xFE
      0xC8, 0, 0, 0, 5,        // goto_w +5
      0xB1                     // return
    ];

    U2(13); // "Code"
    U4((uint)(12 + code.Length));
    U2(4); // max_stack
    U2(2); // max_locals
    U4((uint)code.Length);
    ms.Write(code);
    U2(0); // exception_table_length
    U2(0); // attributes_count
    U2(0); // class attributes_count
    return ms.ToArray();
  }

}
