using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.CSharp;

/// <summary>
/// Test design 4 of #1301 for C#: every member of the mapping table, called once, imports into IR
/// the verifier accepts and that holds the operations the table names; the members .NET 10 defines as
/// a sequence of other operations compute what .NET computes.
/// </summary>
[TestFixture]
public sealed class CSharpFloatApiTests {

  private static readonly Dictionary<string, Type> _types = new() {
    ["System.Math"] = typeof(Math), ["System.MathF"] = typeof(MathF), ["double"] = typeof(double), ["float"] = typeof(float)
  };

  private static IEnumerable<TestCaseData> Members
    => CSharpFloatApi.Members.Select(member => new TestCaseData(member).SetArgDisplayNames(member.ToString()));

  /// <summary>The overload of a member the table means: the one with a floating-point parameter and the arity.</summary>
  private static MethodInfo Overload(CSharpFloatMember member)
    => _types[member.Type].GetMethods(BindingFlags.Public | BindingFlags.Static).First(method => method.Name == member.Name
      && method.GetParameters().Length == member.Arity
      && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(double) || parameter.ParameterType == typeof(float))
      && method.GetParameters().All(parameter => _keywords.ContainsKey(parameter.ParameterType)));

  private static readonly Dictionary<Type, string> _keywords = new() {
    [typeof(double)] = "double", [typeof(float)] = "float", [typeof(int)] = "int", [typeof(bool)] = "bool", [typeof(MidpointRounding)] = "System.MidpointRounding"
  };

  /// <summary>A method that calls the member once, a mode given as the constant it has to be.</summary>
  private static string Snippet(CSharpFloatMember member, string mode = "ToZero") {
    var method = Overload(member);
    var parameters = method.GetParameters().Where(parameter => parameter.ParameterType != typeof(MidpointRounding))
      .Select(parameter => $"{_keywords[parameter.ParameterType]} {parameter.Name}");
    var arguments = method.GetParameters().Select(parameter => parameter.ParameterType == typeof(MidpointRounding) ? "System.MidpointRounding." + mode : parameter.Name);
    var owner = member.Type.StartsWith("System.", StringComparison.Ordinal) ? member.Type : member.Type == "double" ? "System.Double" : "System.Single";
    return PrototypeFixture.Wrap($"public static {_keywords[method.ReturnType]} F({string.Join(", ", parameters)}) => {owner}.{member.Name}({string.Join(", ", arguments)});");
  }

  [TestCaseSource(nameof(Members))]
  public void Member_ImportsIntoTheOperationsTheTableNames(object entry) {
    var member = (CSharpFloatMember)entry;
    var module = PrototypeFixture.Import("Snippet.cs", Snippet(member));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var used = Intrinsics(module.Functions[0].Body).Select(intrinsic => intrinsic.Op).ToHashSet();
    Assert.That(used.All(member.Operations.Contains) || member.Name == "RootN", Is.True, $"{string.Join(", ", used)} is not within {string.Join(", ", member.Operations)}");
    Assert.That(member.Operations.Count == 0 || used.Overlaps(member.Operations), Is.True);
  }

  [Test]
  public void TheTable_CoversBothFormatsOfEveryMember() {
    Assert.That(CSharpFloatApi.Members.Select(member => member.ToString()), Is.Unique);
    // MathF has no Clamp: Math.Clamp has the overload of float.
    var math = CSharpFloatApi.Members.Where(member => member.Type == "System.Math" && member.Name != "Clamp").Select(member => (member.Name, member.Arity));
    var mathF = CSharpFloatApi.Members.Where(member => member.Type == "System.MathF").Select(member => (member.Name, member.Arity));
    Assert.That(mathF, Is.EquivalentTo(math));
    Assert.That(CSharpFloatApi.Find("System.Math", "Sin", 1)!.Operations, Is.EqualTo(new[] { IrIntrinsicOp.Sin }));
    Assert.That(CSharpFloatApi.Find("System.Math", "Random", 0), Is.Null);
  }

  /// <summary>Every mode of MidpointRounding, given as a constant, is its own operation.</summary>
  [TestCase("ToEven", IrIntrinsicOp.RoundEven)]
  [TestCase("AwayFromZero", IrIntrinsicOp.RoundAway)]
  [TestCase("ToZero", IrIntrinsicOp.Truncate)]
  [TestCase("ToNegativeInfinity", IrIntrinsicOp.Floor)]
  [TestCase("ToPositiveInfinity", IrIntrinsicOp.Ceiling)]
  public void RoundWithAConstantMode_IsTheOperationOfTheMode(string mode, IrIntrinsicOp op) {
    foreach (var (owner, number) in new[] { ("System.Math", "double"), ("System.MathF", "float"), ("System.Double", "double"), ("System.Single", "float") }) {
      var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap($"public static {number} F({number} x) => {owner}.Round(x, System.MidpointRounding.{mode});"));
      Assert.That(Intrinsics(module.Functions[0].Body).Single().Op, Is.EqualTo(op), owner);
    }
  }

  [Test]
  public void RoundToDigits_CarriesItsModeAsAConstant() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(
      "public static double F(double x, int d) => System.Math.Round(x, d, System.MidpointRounding.ToPositiveInfinity); "
      + "public static float G(float x) => System.MathF.Round(x, 2);"));
    var digits = Intrinsics(module.Functions[0].Body).Single();
    Assert.That((digits.Op, digits.Arguments[2]), Is.EqualTo((IrIntrinsicOp.RoundDigits, (IrExpr)new IrConst(IrType.I32, 4))));
    Assert.That(Intrinsics(module.Functions[1].Body).Single().Arguments[2], Is.EqualTo(new IrConst(IrType.I32, 0)));
    Assert.That(IrInterpreter.Call(module, "F", [1.231, 2]), Is.EqualTo(Math.Round(1.231, 2, MidpointRounding.ToPositiveInfinity)));
  }

  [Test]
  public void SinCos_IsItsTwoParts() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(
      "public static double S(double x) => System.Math.SinCos(x).Sin + System.Math.SinCos(x).Item2; "
      + "public static float C(float x) => System.MathF.SinCos(x).Cos * System.Single.SinCos(x).Item1;"));
    Assert.That(Intrinsics(module.Functions[0].Body).Select(intrinsic => intrinsic.Op), Is.EqualTo(new[] { IrIntrinsicOp.Sin, IrIntrinsicOp.Cos }));
    Assert.That(Intrinsics(module.Functions[1].Body).Select(intrinsic => intrinsic.Op), Is.EqualTo(new[] { IrIntrinsicOp.Cos, IrIntrinsicOp.Sin }));
  }

  /// <summary>Test design 11: what the table cannot express is refused by the member's name.</summary>
  [TestCase("public static double F(double x) => System.Math.SinCos(x).Sin + System.Double.SinCosPi(x).SinPi;", "Double.SinCosPi")]
  [TestCase("public static double F(double a, double b, double t) => System.Double.Lerp(a + 1, b, t);", "Double.Lerp")]
  [TestCase("public static double F(double x, int n) => System.Double.RootN(x * 2, n);", "Double.RootN")]
  [TestCase("public static int F(int a) => System.Math.Clamp(a, 0, 1);", "Math.Clamp")]
  [TestCase("public static double F(double x, int m) => System.Math.Round(x, (System.MidpointRounding)m);", "Math.Round")]
  public void WhatTheTableCannotExpress_IsRefusedByName(string method, string name) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)),
      Has.Some.EqualTo(("PLY1002", $"'{name}' is not supported yet")));
  }

  /// <summary>
  /// The members .NET defines as a fixed sequence of exact operations compute what .NET computes, bit
  /// for bit: Lerp (fused on this host), the classification and the angle conversion.
  /// </summary>
  [Test]
  public void SequencesOfExactOperations_ComputeWhatDotNetComputes() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(
      "public static double L(double a, double b, double t) => System.Double.Lerp(a, b, t); "
      + "public static float M(float a, float b, float t) => System.Single.Lerp(a, b, t); "
      + "public static bool P(double x) => System.Double.IsPositive(x) || System.Double.IsPositiveInfinity(x) || System.Double.IsNegativeInfinity(x); "
      + "public static double D(double x) => System.Double.DegreesToRadians(x) + System.Double.RadiansToDegrees(x);"));
    var state = 2026UL;
    for (var index = 0; index < 2000; ++index) {
      var a = Value(ref state);
      var b = Value(ref state);
      var t = Value(ref state) / 1e3;
      Assert.That(IrInterpreter.Call(module, "L", [a, b, t]), Is.EqualTo(double.Lerp(a, b, t)), $"{a:R} {b:R} {t:R}");
      Assert.That(IrInterpreter.Call(module, "M", [(float)a, (float)b, (float)t]), Is.EqualTo(float.Lerp((float)a, (float)b, (float)t)));
      Assert.That(IrInterpreter.Call(module, "D", [a]), Is.EqualTo(double.DegreesToRadians(a) + double.RadiansToDegrees(a)));
    }

    foreach (var x in new[] { 0.0, -0.0, 1.0, -1.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN })
      Assert.That(IrInterpreter.Call(module, "P", [x]),
        Is.EqualTo(double.IsPositive(x) || double.IsPositiveInfinity(x) || double.IsNegativeInfinity(x)), $"{x}");
  }

  private static double Value(ref ulong state) {
    state += 0x9E3779B97F4A7C15UL;
    var z = state;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
    z ^= z >> 31;
    return ((long)(z >> 11) - (1L << 52)) / 1e12;
  }

  private static IEnumerable<IrIntrinsic> Intrinsics(IrBlock block) {
    var found = new List<IrIntrinsic>();
    void Walk(object? node) {
      switch (node) {
        case IrIntrinsic intrinsic:
          foreach (var argument in intrinsic.Arguments)
            Walk(argument);

          found.Add(intrinsic);
          break;
        case IrBinary binary:
          Walk(binary.Left);
          Walk(binary.Right);
          break;
        case IrUnary unary:
          Walk(unary.Operand);
          break;
        case IrConvert conversion:
          Walk(conversion.Operand);
          break;
        case IrConditional choice:
          Walk(choice.Condition);
          Walk(choice.WhenTrue);
          Walk(choice.WhenFalse);
          break;
        case IrReturn result:
          Walk(result.Value);
          break;
        case IrBlock inner:
          foreach (var statement in inner.Statements)
            Walk(statement);

          break;
      }
    }

    Walk(block);
    return found;
  }

}
