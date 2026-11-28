using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;

namespace Polypiler.Tests.Verification.Runners;

/// <summary>
/// The C# driver generator (#220): the project file of a profile, and drivers that compile, with
/// warnings as errors, against declarations of every container the derivation table names. The
/// acceptance project runs the same drivers on the real SDK.
/// </summary>
[TestFixture]
public sealed class CSharpDriverGeneratorTests {

  private const string _declarations = """
    using System;

    namespace Shapes;

    public enum Colour : short { Red = 1 }

    public struct Point { public int X; public double Y { get; set; } }

    public static class T {
      public static Point Tour(sbyte a, byte b, short c, ushort d, int e, uint f, long g, ulong h, bool i, char j, float k, double l,
        string m, string? n, byte[] o, ReadOnlySpan<byte> p, Span<byte> q, int[] r, ReadOnlySpan<long> s, Span<int> t, int[]? u,
        Colour v, (int, bool) w, Point x, Colour[] y, string?[] z, ref uint state, out int doubled, in long fixedValue) {
        doubled = 0;
        return x;
      }
      public static void Nothing() { }
      public static Span<int> Window(Span<int> values) => values;
      public static string?[]? Texts(int[][] nested) => null;
      public static (Colour, (byte, char)) Pair(Point point) => default;
      public static byte[] Copy(byte[] data) => data;
    }
    """;

  private static readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _files
    = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["Shapes.cs"] = Encoding.UTF8.GetBytes(_declarations) };

  private static CSharpExportBinding Binding(string export) => CSharpSignatures.Bind(CSharpSignatures.Find(_files, "Shapes." + export), null, export);

  private static IReadOnlyList<Diagnostic> Compile(params string[] drivers) {
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll")
      .Select(path => MetadataReference.CreateFromFile(path));
    var options = new CSharpParseOptions(LanguageVersion.CSharp14);
    var trees = drivers.Select(driver => CSharpSyntaxTree.ParseText(driver, options)).Append(CSharpSyntaxTree.ParseText(_declarations, options));
    var compilation = CSharpCompilation.Create("Driver", trees, references,
      new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable, generalDiagnosticOption: ReportDiagnostic.Error, mainTypeName: "PolypilerDriver.Program"));
    return [.. compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];
  }

  [TestCase("T.Tour")]
  [TestCase("T.Nothing")]
  [TestCase("T.Window")]
  [TestCase("T.Texts")]
  [TestCase("T.Pair")]
  [TestCase("T.Copy")]
  public void TheDriverOfEveryShape_CompilesWithWarningsAsErrors(string export) {
    var driver = CSharpDriverGenerator.Generate(Binding(export));

    Assert.That(Compile(driver), Is.Empty, driver);
  }

  [Test]
  public void TheDriver_CallsTheMethodDirectlyWithItsModifiers() {
    var driver = CSharpDriverGenerator.Generate(Binding("T.Tour"));

    Assert.That(driver, Does.Contain("result = global::Shapes.T.Tour(a0, a1,"));
    Assert.That(driver, Does.Contain(", ref a26, out a27, in a28);"));
    Assert.That(driver, Does.Contain("int a27;\n"));
    Assert.That(driver, Does.Contain("global::Shapes.Colour a21 = (global::Shapes.Colour)r.I16();"));
    Assert.That(driver, Does.Contain("args[0] != \"T.Tour\""));
    Assert.That(driver, Does.Not.Contain("System.Reflection"));
  }

  [Test]
  public void TheTrapLine_FollowsTheTrapTable() {
    var driver = CSharpDriverGenerator.Generate(Binding("T.Nothing"));

    foreach (var (exception, kind) in CSharpTraps.Table)
      Assert.That(driver, Does.Contain($"\"{exception}\" => \"{new TrapClass(kind)}\","));
    Assert.That(driver, Does.Contain("_ => \"other:\" + name"));
  }

  [Test]
  public void AnExportWithQuotes_IsEscapedInTheDriver() {
    var binding = Binding("T.Nothing") with { Export = "T.\"odd\\name" };

    Assert.That(Compile(CSharpDriverGenerator.Generate(binding)), Is.Empty);
  }

  [TestCase(false, "<CheckForOverflowUnderflow>false</CheckForOverflowUnderflow>")]
  [TestCase(true, "<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>")]
  public void TheProjectFile_StatesEveryOptionOfTheProfile(bool checkedArithmetic, string overflow) {
    var project = CSharpDriverGenerator.Project(CSharpProfile.Net10 with { CheckedArithmetic = checkedArithmetic });

    Assert.That(project, Does.Contain(overflow));
    Assert.That(project, Does.Contain("<TargetFramework>net10.0</TargetFramework>").And.Contain("<LangVersion>14</LangVersion>")
      .And.Contain("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>").And.Contain("<Nullable>enable</Nullable>")
      .And.Contain("<Deterministic>true</Deterministic>").And.Contain("<AllowUnsafeBlocks>false</AllowUnsafeBlocks>")
      .And.Contain("<OutputType>Exe</OutputType>"));
  }

  [Test]
  public void TheKnownProfiles_AreTheDefaultAndItsCheckedVariant() {
    Assert.That(CSharpProfile.Known.Keys, Is.EquivalentTo(new[] { "csharp/net10", "csharp/net10-checked" }));
    Assert.That(CSharpProfile.Known["csharp/net10-checked"].CheckedArithmetic, Is.True);
    Assert.That(CSharpProfile.Net10.CheckedArithmetic, Is.False);
  }

  [Test]
  public void NullArguments_AreRefused() {
    Assert.Throws<ArgumentNullException>(() => CSharpDriverGenerator.Generate(null!));
    Assert.Throws<ArgumentNullException>(() => CSharpDriverGenerator.Project(null!));
  }

  [Test]
  public void TheSignatureOfABinding_IsTheImagesOfItsShapes() {
    Assert.That(Binding("T.Pair").Signature.ToString(), Is.EqualTo("(point: record{X:i32,Y:f64} in) -> tuple<enum<i16>,tuple<u8,char16>>"));
    Assert.That(CSharpShape.Scalar(ImageType.I32, "int"), Is.EqualTo(new CSharpShape(ImageType.I32, "int", CSharpContainer.Scalar, null, [])));
  }

}
