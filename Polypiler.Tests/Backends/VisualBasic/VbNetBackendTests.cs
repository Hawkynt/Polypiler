using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Backends.VisualBasic.Net;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>
/// The Visual Basic .NET profiles, their capability table, the names, the printer, the helpers and the
/// refusals of the backend; the frameworks compiled against their reference assemblies in memory.
/// </summary>
[TestFixture]
public sealed class VbNetBackendTests {

  [TestCase("vbnet/vb16.9", "16.9", "net10.0", "vbnet/vb16.9")]
  [TestCase("VBNET/VB9", "9", "net10.0", "vbnet/vb9")]
  [TestCase("vbnet/vb15.5/net4.8", "15.5", "net4.8", "vbnet/vb15.5/net4.8")]
  [TestCase("vbnet/vb10/NETSTANDARD2.0", "10", "netstandard2.0", "vbnet/vb10/netstandard2.0")]
  public void Parse_ReadsTheLanguageVersionAndTheFramework(string text, string version, string framework, string name) {
    var profile = VisualBasicProfile.Parse(text);
    Assert.That((profile.IsNet, profile.LanguageVersion, profile.Framework!.Name, profile.Name, profile.Dialect), Is.EqualTo((true, version, framework, name, "vbnet")));
    Assert.That(profile.Title, Does.StartWith("Visual Basic " + version + " on "));
  }

  [TestCase("vbnet/vb13")]
  [TestCase("vbnet/vb16.9/net99")]
  [TestCase("vbnet/vb16.9/net10.0/x")]
  [TestCase("vbnet/vb")]
  public void Parse_OfNoProfile_SaysWhatIsWrong(string text) {
    Assert.That(VisualBasicProfile.TryParse(text, out var profile, out var error), Is.False);
    Assert.That((profile, error), Is.EqualTo(((VisualBasicProfile?)null, error)));
    Assert.That(error, Does.Contain(text));
  }

  [Test]
  public void Profiles_ListTheClassicDialectsThenTheLanguageVersions() {
    Assert.That(VisualBasicProfile.Names, Is.EqualTo(new[] { "visualbasic/vb6", "visualbasic/vba6", "visualbasic/vba7", "visualbasic/vba7-64" }
      .Concat(VisualBasicProfile.NetLanguageVersions.Select(version => "vbnet/vb" + version))));
    Assert.That(VisualBasicProfile.Vb6.IsNet, Is.False);
    Assert.That(VisualBasicProfile.Vb6.Version, Is.EqualTo(new Version(0, 0)));
    Assert.That(VisualBasicProfile.VbNetLatest.Version, Is.EqualTo(new Version(16, 9)));
    Assert.That(VisualBasicProfile.Parse("vbnet/vb14").Version, Is.EqualTo(new Version(14, 0)));
    Assert.That(VisualBasicProfile.Parse("vbnet/vb14"), Is.SameAs(VisualBasicProfile.NetAll[4]));
    Assert.That(VisualBasicFramework.All.Select(framework => framework.Rank), Is.Ordered);
  }

  [TestCase("vbnet/vb9", VbNetCapabilities.ImplicitLineContinuation, false)]
  [TestCase("vbnet/vb10", VbNetCapabilities.ImplicitLineContinuation, true)]
  [TestCase("vbnet/vb16.9/net4.8", VbNetCapabilities.SingleBits, false)]
  [TestCase("vbnet/vb16.9/netstandard2.1", VbNetCapabilities.SingleBits, true)]
  [TestCase("vbnet/vb16.9/netstandard2.1", VbNetCapabilities.SignedZeroMinMax, false)]
  [TestCase("vbnet/vb16.9/netcoreapp3.1", VbNetCapabilities.SignedZeroMinMax, true)]
  [TestCase("vbnet/vb16.9/net2.0", VbNetCapabilities.ShortestFloatText, false)]
  [TestCase("vbnet/vb16.9", VbNetCapabilities.ShortestFloatText, true)]
  public void Capabilities_GoByLanguageVersionAndFramework(string profile, string capability, bool has)
    => Assert.That(VbNetCapabilities.Standard.Has(capability, VisualBasicProfile.Parse(profile)), Is.EqualTo(has));

  [Test]
  public void Capabilities_ReportWhatTheyCannotAnswer() {
    Assert.That(VbNetCapabilities.Standard.Missing(VisualBasicProfile.Parse("vbnet/vb9/net2.0")), Is.EqualTo(VbNetCapabilities.Standard.Entries.Select(entry => entry.Name)));
    Assert.That(VbNetCapabilities.Standard.Missing(VisualBasicProfile.VbNetLatest), Is.Empty);
    Assert.That(() => VbNetCapabilities.Standard["Nothing"], Throws.ArgumentException);
    Assert.That(() => VbNetCapabilities.Standard.Has(VbNetCapabilities.SingleBits, VisualBasicProfile.Vba7), Throws.ArgumentException);
    Assert.That(() => VbNetCapabilities.Standard.Has(VbNetCapabilities.SingleBits, null!), Throws.ArgumentNullException);
    Assert.That(() => new VbNetCapabilities([new("A", null, null), new("A", null, null)]), Throws.ArgumentException);
    Assert.That(() => new VbNetCapabilities(null!), Throws.ArgumentNullException);
  }

  [TestCase("Next", "[Next]")]
  [TestCase("order", "order")]
  [TestCase("ToString", "ToString_")]
  [TestCase("PlyHelper", "PlyHelper_")]
  [TestCase("a.b-c", "b_c")]
  [TestCase("_9", "x9")]
  [TestCase("--", "fallback")]
  public void Naming_KeepsNamesAndEscapesKeywords(string name, string valid) {
    Assert.That(VbNetNaming.Valid(name, "fallback"), Is.EqualTo(valid));
    Assert.That(() => VbNetNaming.Valid(null!, "x"), Throws.ArgumentNullException);
  }

  [Test]
  public void Naming_FreesATakenName() {
    Assert.That(VbNetNaming.Free("x", _ => false), Is.EqualTo("x"));
    Assert.That(VbNetNaming.Free("x", name => name == "x"), Is.EqualTo("x2"));
    Assert.That(VbNetNaming.Free("[Next]", name => name == "[Next]"), Is.EqualTo("Next_"));
    Assert.That(VbNetNaming.Bare("[Next]"), Is.EqualTo("Next"));
    Assert.That(VbNetNaming.Bare("x"), Is.EqualTo("x"));
    Assert.That(() => VbNetNaming.Free(null!, _ => false), Throws.ArgumentNullException);
    Assert.That(() => VbNetNaming.Free("x", null!), Throws.ArgumentNullException);
    Assert.That(() => VbNetNaming.Bare(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Printer_PutsParenthesesWherePrecedenceNeedsThem() {
    VnExpression N(string name) => new VnName(name);
    Assert.That(VnPrinter.Print(new VnBinary(new VnBinary(N("a"), "+", N("b")), "*", N("c"))), Is.EqualTo("(a + b) * c"));
    Assert.That(VnPrinter.Print(new VnBinary(N("a"), "-", new VnBinary(N("b"), "-", N("c")))), Is.EqualTo("a - (b - c)"));
    Assert.That(VnPrinter.Print(new VnBinary(N("a"), "Xor", new VnBinary(N("b"), "<<", new VnLiteral("2")))), Is.EqualTo("a Xor b << 2"));
    Assert.That(VnPrinter.Print(new VnUnary("-", new VnBinary(N("x"), "^", new VnLiteral("2")))), Is.EqualTo("-x ^ 2"));
    Assert.That(VnPrinter.Print(new VnBinary(N("x"), "^", new VnLiteral("-1"))), Is.EqualTo("x ^ (-1)"));
    Assert.That(VnPrinter.Print(new VnUnary("Not", new VnBinary(N("a"), "=", N("b")))), Is.EqualTo("Not (a = b)"));
    Assert.That(VnPrinter.Print(new VnUnary("Not", N("a"))), Is.EqualTo("Not a"));
    Assert.That(VnPrinter.Print(new VnMember(new VnLiteral("-5"), "ToString")), Is.EqualTo("(-5).ToString"));
    Assert.That(VnPrinter.Print(new VnString("say \"hi\"")), Is.EqualTo("\"say \"\"hi\"\"\""));
    Assert.That(VnPrinter.Print(new VnNewArray("Integer()", N("n"), [])), Is.EqualTo("New Integer(n)() {}"));
    Assert.That(VnPrinter.Print(new VnNewArray("Byte", null, [new VnLiteral("1")])), Is.EqualTo("New Byte() {1}"));
    Assert.That(VnPrinter.Print(new VnTernary(N("c"), N("a"), N("b"))), Is.EqualTo("If(c, a, b)"));
    Assert.That(() => VnPrinter.Print(new VnBinary(N("a"), "??", N("b"))), Throws.ArgumentException);
    Assert.That(() => VnPrinter.Print((VnExpression)null!), Throws.ArgumentNullException);
    Assert.That(() => VnPrinter.Print((VnDeclaration)null!), Throws.ArgumentNullException);
    Assert.That(() => VnPrinter.Print((VnModule)null!, true), Throws.ArgumentNullException);
  }

  [TestCase(true, ",\n")]
  [TestCase(false, ", _\n")]
  public void Printer_WritesALongTableOverLines(bool implicitContinuation, string separator) {
    var table = new VnField("Private ReadOnly", "T", "Integer()", new VnNewArray("Integer", null, [.. Enumerable.Range(0, 40).Select(value => new VnLiteral(value.ToString(System.Globalization.CultureInfo.InvariantCulture)))]));
    var text = VnPrinter.Print(new VnModule("M", [], [table]), implicitContinuation);
    Assert.That(text, Does.Contain("15" + separator).And.Contain("Public Module M"));
  }

  [Test]
  public void Printer_WritesEveryStatement() {
    VnExpression N(string name) => new VnName(name);
    var body = new VnStatement[] {
      new VnDim("x", "Integer", new VnLiteral("0")), new VnAssign(N("x"), "+=", new VnLiteral("1")), new VnCallStatement(new VnInvoke(N("F"), [])),
      new VnIf(N("a"), [new VnExit("Do")], [new VnIf(N("b"), [new VnContinue()], [new VnGoTo("L")])]),
      new VnSelect(N("x"), [new VnCase([new VnLiteral("1"), new VnLiteral("2")], [new VnExit("Select")])], [new VnThrow(null)]),
      new VnDo(N("c"), true, []), new VnDo(null, false, [new VnLabel("L")]), new VnTry([], [new VnCatch("e", "System.Exception", N("c"), [])], [new VnReturn(null)]),
      new VnIf(N("a"), [], [new VnThrow(new VnNew("System.OverflowException", []))]), new VnReturn(N("x"))
    };
    var text = VnPrinter.Print(new VnMethod("Public", "F", [new("v", "Integer", true)], "Integer", body) { TypeParameter = "T" });
    Assert.That(text, Does.Contain("Public Function F(Of T)(ByRef v As Integer) As Integer").And.Contain("ElseIf b Then").And.Contain("Loop While c")
      .And.Contain("Case 1, 2").And.Contain("Case Else").And.Contain("Catch e As System.Exception When c").And.Contain("Finally").And.Contain("L:\n").And.Contain("GoTo L")
      .And.Contain("Continue Do").And.Contain("Exit Select").And.Contain("Throw New System.OverflowException()"));
    Assert.That(VnPrinter.Print(new VnStructure("S", [new VnField("Public", "A", "Integer", null), new VnMethod("Public", "New", [], null, [])])), Does.Contain("Public Structure S\n        Public A As Integer\n\n        Public Sub New()"));
  }

  private sealed record Unknown : VnExpression;

  private sealed record UnknownStatement : VnStatement;

  private sealed record UnknownDeclaration : VnDeclaration;

  [Test]
  public void Printer_RefusesNodesItDoesNotKnow() {
    Assert.That(() => VnPrinter.Print(new Unknown()), Throws.ArgumentException);
    Assert.That(() => VnPrinter.Print(new VnMethod("Public", "F", [], null, [new UnknownStatement()])), Throws.ArgumentException);
    Assert.That(() => VnPrinter.Print(new UnknownDeclaration()), Throws.ArgumentException);
  }

  /// <summary>Every fixed helper, with every overload, compiles warning-free in the strictest profile.</summary>
  [Test]
  public void Helpers_AllCompile() {
    var keys = VbNetHelpers.Integers.SelectMany(type => new[] { VbNetHelpers.Check(type), VbNetHelpers.Fix(type), VbNetHelpers.Saturate(type), VbNetHelpers.Step(type), VbNetHelpers.StepChecked(type) })
      .Concat(new[] { "Integer", "UInteger", "Long", "ULong" }.SelectMany(type => new[] { IrBinaryOp.AddChecked, IrBinaryOp.SubtractChecked, IrBinaryOp.MultiplyChecked }.Select(op => VbNetHelpers.Checked(op, type))))
      .Concat([VbNetHelpers.NegateChecked("Integer"), VbNetHelpers.NegateChecked("Long"), VbNetHelpers.Step("Single"), VbNetHelpers.Step("Double"), VbNetHelpers.Index("UInteger"),
        VbNetHelpers.Index("Long"), VbNetHelpers.Index("ULong"), VbNetHelpers.Discard, VbNetHelpers.Bind, VbNetHelpers.Throw, VbNetHelpers.Chr, VbNetHelpers.RoundHalfUp,
        VbNetHelpers.Min, VbNetHelpers.Max, VbNetHelpers.Clear, VbNetHelpers.Fill]);
    var members = keys.Distinct().SelectMany(VbNetHelpers.Declarations).Cast<VnDeclaration>().ToList();
    var text = VnPrinter.Print(new VnModule("Helpers", [], members), true);
    Assert.That(VbNetFixtures.Complaints(text, VisualBasicProfile.Parse("vbnet/vb9")), Is.Empty, text);
    Assert.That(() => VbNetHelpers.Declarations("PlyNothing"), Throws.ArgumentException);
    Assert.That(() => VbNetHelpers.Declarations(null!), Throws.ArgumentNullException);
    Assert.That(() => VbNetHelpers.NameOf(null!), Throws.ArgumentNullException);
    Assert.That(VbNetHelpers.Needs(VbNetHelpers.StepChecked("Byte")), Is.EqualTo(new[] { VbNetHelpers.Check("Byte") }));
  }

  /// <summary>The helpers' behaviour at their bounds: the checked ones throw just outside the type, the saturating ones clamp.</summary>
  [Test]
  public void Helpers_ComputeAtTheirBounds() {
    var source = "Public Module H\n" + string.Concat(new[] { VbNetHelpers.Check("Byte"), VbNetHelpers.Fix("Integer"), VbNetHelpers.Saturate("Long"), VbNetHelpers.Saturate("ULong"),
      VbNetHelpers.Fix("ULong"), VbNetHelpers.Index("Long"), VbNetHelpers.RoundHalfUp, VbNetHelpers.Min, VbNetHelpers.Max }.SelectMany(VbNetHelpers.Declarations)
      .Select(method => VnPrinter.Print(method with { Modifiers = "Public" }))) + "End Module\n";
    var assembly = VbNetFixtures.Load(source);
    object? Call(string name, params object[] arguments) {
      try {
        return assembly.GetType("H")!.GetMethods().First(method => method.Name == name && method.GetParameters().Length == arguments.Length && method.GetParameters()[0].ParameterType == arguments[0].GetType()).Invoke(null, arguments);
      } catch (System.Reflection.TargetInvocationException exception) {
        return exception.InnerException!.GetType().Name;
      }
    }

    Assert.That(Call("PlyChkByte", 255m), Is.EqualTo((byte)255));
    Assert.That(Call("PlyChkByte", 256m), Is.EqualTo("OverflowException"));
    Assert.That(Call("PlyChkByte", -1m), Is.EqualTo("OverflowException"));
    Assert.That(Call("PlyFixInteger", 2147483647.9), Is.EqualTo(int.MaxValue));
    Assert.That(Call("PlyFixInteger", 2147483648.0), Is.EqualTo("OverflowException"));
    Assert.That(Call("PlyFixInteger", double.NaN), Is.EqualTo("OverflowException"));
    Assert.That(Call("PlyFixULong", 18446744073709551616.0), Is.EqualTo("OverflowException"));
    Assert.That(Call("PlyFixULong", -0.5), Is.EqualTo(0UL));
    Assert.That(Call("PlySatLong", 1e30), Is.EqualTo(long.MaxValue));
    Assert.That(Call("PlySatLong", -1e30), Is.EqualTo(long.MinValue));
    Assert.That(Call("PlySatLong", double.NaN), Is.EqualTo(0L));
    Assert.That(Call("PlySatULong", -3.0), Is.EqualTo(0UL));
    Assert.That(Call("PlySatULong", 2.9), Is.EqualTo(2UL));
    Assert.That(Call("PlyIndex", 5L), Is.EqualTo(5));
    Assert.That(Call("PlyIndex", -2147483649L), Is.EqualTo(-1));
    Assert.That(Call("PlyIndex", 2147483648L), Is.EqualTo(-1));
    Assert.That(Call("PlyRoundHalfUp", -0.4), Is.EqualTo(-0.0).And.Matches<double>(double.IsNegative));
    Assert.That(Call("PlyRoundHalfUp", 2.5), Is.EqualTo(3.0));
    Assert.That(double.IsNegative((double)Call("PlyMin", 0.0, -0.0)!), Is.True);
    Assert.That(double.IsNegative((double)Call("PlyMax", -0.0, 0.0)!), Is.False);
    Assert.That(Call("PlyMin", 1.0, double.NaN), Is.NaN);
    Assert.That(Call("PlyMax", 2.0f, 1.0f), Is.EqualTo(2.0f));
    Assert.That(Call("PlyMin", 2.0f, 1.0f), Is.EqualTo(1.0f));
  }

  private static IEnumerable<TestCaseData> Frameworks() => VisualBasicFramework.All.Select(framework => new TestCaseData(framework.Name));

  /// <summary>Each framework compiles the wide fixtures against its own reference assemblies, as the capability table chose their forms.</summary>
  [TestCaseSource(nameof(Frameworks))]
  public void PrototypeFixture_CompilesAgainstTheReferenceAssembliesOfTheFramework(string framework) {
    var profile = VisualBasicProfile.Parse($"vbnet/vb16.9/{framework}");
    foreach (var fixture in new[] { "wide/OperatorsWide", "wide/Conversions", "wide/Tables", "Numbers" }) {
      var translation = VbNetFixtures.Translate(PrototypeFixture.Imported(fixture), profile);
      if (translation.Problems.Count > 0) {
        Assert.That(translation.Problems, Has.All.Contains("PLY2657"), fixture);
        continue;
      }

      Assert.That(VbNetFixtures.Complaints(translation.Source!, profile, ReferenceAssemblies.Of(framework)), Is.Empty, $"{fixture}\n{translation.Source}");
      Assert.That(translation.Project, Does.Contain($"<TargetFramework>{profile.Framework!.Moniker}</TargetFramework>"));
    }
  }

  [Test]
  public void ProjectFile_RefusesAClassicProfile() {
    Assert.That(() => VisualBasicEmitter.ProjectFile(VisualBasicProfile.Vba7), Throws.ArgumentException);
    Assert.That(() => VisualBasicEmitter.ProjectFile(null!), Throws.ArgumentNullException);
    Assert.That(VisualBasicEmitter.RuntimeOf(VisualBasicProfile.Vba7), Is.EqualTo(VisualBasicEmitter.Runtime));
    Assert.That(VisualBasicEmitter.RuntimeOf(VisualBasicProfile.VbNetLatest), Is.EqualTo(VisualBasicEmitter.NetRuntime));
    Assert.That(() => VisualBasicEmitter.RuntimeOf(null!), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicEmitter.NetFileName(null!), Throws.ArgumentNullException);
  }

}
