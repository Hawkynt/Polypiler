using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Tests.Helpers.Support;
using Polypiler.Tooling;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// The library of helpers and how a module gets the helpers it needs: the sources and their import,
/// the merge with the floating-point implementations, the binding of legalization with its errors and
/// its renaming, the operations the backends hand to it, and the tool that precomputes it.
/// </summary>
[TestFixture]
public sealed class HelperLibraryTests {

  // ---------------------------------------------------------------- the sources

  [Test]
  public void Sources_AreEveryFamilyWithItsLicence() {
    var names = HelperOperationSources.All.Select(file => file.Name).ToList();
    Assert.That(names, Does.Contain("Rotate.cs").And.Contain("Counts.cs").And.Contain("Rounding.cs").And.Contain("Intrinsics.cs"));
    Assert.That(HelperOperationSources.All, Has.All.Matches<(string Name, string Text)>(file => file.Text.StartsWith("// SPDX-License-Identifier: MIT", StringComparison.Ordinal)));
    Assert.That(HelperOperationSources.Origin(nameof(HelperOperations.RotateLeft32)), Is.EqualTo(("Polypiler.Core.IR/Helpers/Operations/Rotate.cs", "MIT")));
    Assert.That(HelperOperationSources.Origin("NoSuchHelper"), Is.Null);
    Assert.That(HelperOperationSources.Notices([nameof(HelperOperations.RotateLeft32)]), Is.Empty, "an MIT file needs no notice beyond the grant");
    Assert.That(() => HelperOperationSources.Notices(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void EveryPublicHelper_IsAFunctionOfTheImport_ButTheStubsOfOperations() {
    var functions = FloatSupport.Libraries.Functions.Select(function => function.Name).ToHashSet();
    var methods = typeof(HelperOperations).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(method => method.Name).ToList();
    Assert.That(methods.Where(name => !functions.Contains(name)), Is.EquivalentTo(HelperOperationSources.Intrinsics.Keys));
  }

  [Test]
  public void AStub_IsImportedAsItsOperation() {
    var module = CSharpFrontend.ImportLibrary([new CSharpSource("L.cs", """
      public static class L {
        public static double Round(double value) => value;
        public static double F(double x) => Round(x) + 1.0;
      }
      """)], new Dictionary<string, IrIntrinsicOp> { ["Round"] = IrIntrinsicOp.RoundHalfUp }).Module!;
    Assert.That(module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "F" }));
    Assert.That(IrTree.Nodes(((IrReturn)module.Functions[0].Body.Statements[0]).Value!).OfType<IrIntrinsic>().Single().Op, Is.EqualTo(IrIntrinsicOp.RoundHalfUp));
  }

  [Test]
  public void AStubOnOperandsItsOperationDoesNotTake_IsReported() {
    var result = CSharpFrontend.ImportLibrary([new CSharpSource("L.cs", """
      public static class L {
        public static int Round(int value) => value;
        public static int F(int x) => Round(x);
      }
      """)], new Dictionary<string, IrIntrinsicOp> { ["Round"] = IrIntrinsicOp.RoundHalfUp });
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Member(CSharpFrontend.Unsupported));
  }

  [Test]
  public void TheTwoLibraries_MergeAndTellTheirOrigins() {
    var merged = FloatSupport.Libraries;
    Assert.That(merged.Name, Is.EqualTo(nameof(FloatOperations)));
    Assert.That(ImplementationSources.IsFloat(nameof(FloatOperations.Exp)), Is.True);
    Assert.That(ImplementationSources.IsFloat(nameof(HelperOperations.PopCount32)), Is.False);
    Assert.That(ImplementationSources.Origin(nameof(HelperOperations.PopCount32))!.Value.Path, Does.EndWith("Counts.cs"));
    Assert.That(ImplementationSources.Helper("Pop", nameof(HelperOperations.PopCount32), "x").Licence, Is.EqualTo("MIT"));
    Assert.That(() => ImplementationSources.Helper("X", "NoSuchFunction", ""), Throws.ArgumentException.With.Message.Contains("NoSuchFunction"));
    Assert.That(() => ImplementationSources.Notices(null!), Throws.ArgumentNullException);
    Assert.That(() => ImplementationSources.Merge(null!, merged), Throws.ArgumentNullException);
    Assert.That(() => ImplementationSources.Merge(merged, null!), Throws.ArgumentNullException);
    var clash = new IrModule("H", [merged.Functions[0]], [new IrModuleConst(merged.Constants[0].Name, IrType.I32, [1])]);
    Assert.That(() => ImplementationSources.Merge(merged, clash), Throws.ArgumentException.With.Message.Contains(merged.Functions[0].Name));
  }

  /// <summary>The notice of a file stays in the output where its function was inlined and is gone.</summary>
  [Test]
  public void TheNoticeOfAnInlinedImplementation_IsKept() {
    var notice = ImplementationSources.Notices([nameof(FloatOperations.FusedMultiplyAddF)]);
    Assert.That(notice, Has.Some.Contains("FreeBSD"));
    var module = new IrModule("M", []) { Inlined = [nameof(FloatOperations.FusedMultiplyAddF)] };
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Net20).Single().Content;
    Assert.That(text, Does.Contain("David Schultz"));
  }

  // ---------------------------------------------------------------- the binding

  private static readonly IrLocal _value = new("v", IrType.U16);

  private static IrModule Rotating(IrExpr count) => new("M", [
    new IrFunction("F", true, IrType.U16, [_value], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U16, [new IrLocalRef(_value), count]))]))
  ]);

  [Test]
  public void Binding_AdaptsTheArgumentsAndTheResultToTheHelper() {
    var module = Rotating(new IrConst(IrType.I32, 3));
    var lowered = HelperHarness.Legalized(module);
    Assert.That(lowered.Implementations, Is.EqualTo(new[] { ("RotateLeftNarrow", "RotateLeftNarrow") }));
    var call = (IrConvert)((IrReturn)lowered.Functions[0].Body.Statements[0]).Value!;
    Assert.That(call.Operand, Is.InstanceOf<IrCall>().And.Property("Type").EqualTo(IrType.U32));
    Assert.That(IrInterpreter.Call(lowered, "F", [(ushort)0x8001]), Is.EqualTo((ushort)0x000C));
    Assert.That(LibraryLegalization.Lower(new IrModule("E", []), () => throw new InvalidOperationException("not read"), IntegerLibrary.Call).Functions, Is.Empty);
  }

  [Test]
  public void Binding_RenamesAHelperTheModuleHasANameOf() {
    var module = Rotating(new IrConst(IrType.I32, 3)) with {
      Functions = [.. Rotating(new IrConst(IrType.I32, 3)).Functions, new IrFunction("RotateLeftNarrow", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 0))]))]
    };
    var lowered = HelperHarness.Legalized(module);
    Assert.That(lowered.Implementations, Is.EqualTo(new[] { ("RotateLeftNarrow_", "RotateLeftNarrow") }));
  }

  [Test]
  public void Binding_RefusesWhatTheHelperCannotTake() {
    var library = HelperHarness.Library;
    var truth = new IrConst(IrType.Bool, 1);
    var tooFew = (LibraryRewrite)((node, calls) => node is IrIntrinsic ? calls.Call("RotateLeft32", IrType.U32, [new IrConst(IrType.U32, 1)]) : null);
    Assert.That(() => LibraryLegalization.Lower(Rotating(new IrConst(IrType.I32, 1)), () => library, tooFew), Throws.ArgumentException.With.Message.Contains("takes 2 arguments"));
    var notANumber = (LibraryRewrite)((node, calls) => node is IrIntrinsic ? calls.Call("RotateLeft32", IrType.U32, [truth, new IrConst(IrType.I32, 1)]) : null);
    Assert.That(() => LibraryLegalization.Lower(Rotating(new IrConst(IrType.I32, 1)), () => library, notANumber), Throws.ArgumentException.With.Message.Contains("cannot stand for"));
    var missing = (LibraryRewrite)((node, calls) => node is IrIntrinsic ? calls.Call("NoSuchHelper", IrType.U32) : null);
    Assert.That(() => LibraryLegalization.Lower(Rotating(new IrConst(IrType.I32, 1)), () => library, missing), Throws.ArgumentException.With.Message.Contains("NoSuchHelper"));
    Assert.That(() => LibraryLegalization.Lower(null!, () => library, missing), Throws.ArgumentNullException);
    Assert.That(() => LibraryLegalization.Lower(Rotating(new IrConst(IrType.I32, 1)), () => library, null!), Throws.ArgumentNullException);
  }

  /// <summary>A helper never becomes a call of itself: the operation it implements in its own body stays the operation.</summary>
  [Test]
  public void Binding_KeepsTheOperationAHelperIsMadeOf() {
    var shifts = 0;
    var rewrite = (LibraryRewrite)((node, calls) => {
      if (node is not IrBinary { Op: IrBinaryOp.ShiftLeft, Type.Kind: IrTypeKind.U32 })
        return node is IrIntrinsic ? calls.Call("RotateLeft32", IrType.U16, ((IrIntrinsic)node).Arguments) : null;

      ++shifts;
      return calls.Call("RotateLeft32", IrType.U32, [((IrBinary)node).Left, new IrConst(IrType.I32, 0)]);
    });
    var lowered = LibraryLegalization.Lower(Rotating(new IrConst(IrType.I32, 1)), () => HelperHarness.Library, rewrite);
    Assert.That(shifts, Is.GreaterThan(0));
    Assert.That(lowered.Functions.Single(function => function.Name == "RotateLeft32").Body.Statements.SelectMany(IrTree.Nodes).OfType<IrBinary>(),
      Has.Some.Property("Op").EqualTo(IrBinaryOp.ShiftLeft));
  }

  [Test]
  public void Binding_RewritesAnExpressionWithoutCallingTheLibrary() {
    var module = Rotating(new IrConst(IrType.I32, 0));
    var rewrite = (LibraryRewrite)((node, calls) => node is IrIntrinsic intrinsic ? intrinsic.Arguments[0] : null);
    var lowered = LibraryLegalization.Lower(module, () => throw new InvalidOperationException("not read"), rewrite);
    Assert.That(lowered, Is.Not.SameAs(module));
    Assert.That(lowered.Implementations, Is.Empty);
    Assert.That(((IrReturn)lowered.Functions[0].Body.Statements[0]).Value, Is.EqualTo(new IrLocalRef(_value)));
  }

  /// <summary>An operation in the initializer of a global is legalized, and every reference names the new global.</summary>
  [Test]
  public void Binding_LegalizesTheInitializersOfGlobals() {
    var first = new IrGlobal("A", IrType.U32, new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U32, [new IrConst(IrType.U32, 0x80000001), new IrConst(IrType.I32, 1)]));
    var second = new IrGlobal("B", IrType.U32, new IrBinary(IrBinaryOp.Add, new IrGlobalRef(first), new IrConst(IrType.U32, 1)));
    var plain = new IrGlobal("C", IrType.U32, new IrConst(IrType.U32, 7));
    var module = new IrModule("M", [
      new IrFunction("F", true, IrType.U32, [], [], new IrBlock([
        new IrGlobalAssign(plain, new IrGlobalRef(second)),
        new IrReturn(new IrBinary(IrBinaryOp.Add, new IrGlobalRef(first), new IrGlobalRef(plain)))
      ]))
    ]) { Globals = [first, second, plain] };
    var lowered = HelperHarness.Legalized(module);
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    Assert.That(lowered.Globals[2], Is.SameAs(plain), "a global nothing changes stays the same");
    Assert.That(IrInterpreter.Call(lowered, "F", []), Is.EqualTo(3u + 4u));
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(3u + 4u));
  }

  // ---------------------------------------------------------------- what the backends hand to it

  [Test]
  public void IntegerLibrary_HasNoHelperForWhatItDoesNotKnow() {
    var calls = (LibraryCalls)null!;
    Assert.That(() => IntegerLibrary.Call(new IrConst(IrType.I32, 1), calls), Throws.ArgumentNullException);
    var library = HelperHarness.Library;
    LibraryRewrite probe = (node, inner) => {
      Assert.That(IntegerLibrary.Call(new IrConst(IrType.I32, 1), inner), Is.Null);
      Assert.That(IntegerLibrary.Call(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [new IrConst(IrType.F64, 0)]), inner), Is.Null);
      Assert.That(IntegerLibrary.Call(new IrIntrinsic(IrIntrinsicOp.Min, IrType.I16, [new IrConst(IrType.I16, 0), new IrConst(IrType.I16, 1)]), inner), Is.Null);
      Assert.That(IntegerLibrary.Call(new IrIntrinsic(IrIntrinsicOp.CarryOut, IrType.Bool, [new IrConst(IrType.U8, 0), new IrConst(IrType.U8, 1), new IrConst(IrType.Bool, 0)]), inner), Is.Null);
      Assert.That(IntegerLibrary.Call(new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F64, [new IrConst(IrType.I32, 0)]), inner), Is.Null);
      Assert.That(IntegerLibrary.Call(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), []), inner), Is.Null);
      return null;
    };
    LibraryLegalization.Lower(Rotating(new IrConst(IrType.I32, 1)), () => library, probe);
  }

  [Test]
  public void ARowOfTheCSharpTable_TellsWhereItTakesTheLibrary() {
    var row = CSharpCapabilities.Default.Of(IrIntrinsicOp.RotateLeft, IrTypeKind.U32)!;
    Assert.That((row.IsLibraryIn(CSharpProfile.Net20), row.IsLibraryIn(CSharpProfile.Net100)), Is.EqualTo((true, false)));
    Assert.That(CSharpCapabilities.Default.Of(IrIntrinsicOp.Abs, IrTypeKind.I32)!.IsLibraryIn(CSharpProfile.Net20), Is.False, "a row without a helper of the library");
    var module = Rotating(new IrConst(IrType.I32, 1));
    Assert.That(CSharpEmitter.LegalizeHelpers(module, () => HelperHarness.Library, CSharpProfile.Net100), Is.SameAs(module));
    Assert.That(CSharpEmitter.LegalizeHelpers(module, () => HelperHarness.Library, CSharpProfile.Net20).Implementations, Has.Count.EqualTo(1));
  }

  [Test]
  public void AJavaScriptProfile_TakesTheLibraryWhereItLacksTheFunction() {
    var lacking = JavaScriptCapabilities.Standard.With(JavaScriptCapabilities.Standard["Math.clz32"] with { Replacement = null });
    var value = new IrLocal("v", IrType.U32);
    IrModule Of(IrIntrinsicOp op, IrType type) => new("M", [new IrFunction("F", true, type, [value], [], new IrBlock([
      new IrReturn(new IrIntrinsic(op, type, [op == IrIntrinsicOp.Floor ? new IrConvert(IrType.F64, new IrLocalRef(value)) : new IrLocalRef(value)]))
    ]))]);
    var count = Of(IrIntrinsicOp.LeadingZeroCount, IrType.I32);
    IrModule Bound(IrModule module, string level, JavaScriptCapabilities? table = null) => JavaScriptEmitter.LegalizeHelpers(module, () => HelperHarness.Library, JavaScriptProfile.Parse(level), table);
    Assert.That(Bound(count, "es5").Implementations.Select(pair => pair.Implementation), Is.EqualTo(new[] { "LeadingZeroCount32" }));
    Assert.That(Bound(count, "es2015"), Is.SameAs(count), "Math.clz32 is the level's own");
    Assert.That(Bound(count, "es5", lacking), Is.SameAs(count), "a function without a replacement is the writer's to report");
    Assert.That(JavaScriptEmitter.Check(count, JavaScriptProfile.Parse("es5"), lacking).Select(problem => problem.Code), Has.Member(JavaScriptEmitter.Inexpressible));
    Assert.That(JavaScriptEmitter.Check(count, JavaScriptProfile.Parse("es5")).Select(problem => problem.Message), Has.Some.Contains("needs its helper of the library"));
    var floor = Of(IrIntrinsicOp.Floor, IrType.F64);
    Assert.That(Bound(floor, "es5"), Is.SameAs(floor));
  }

  // ---------------------------------------------------------------- the tool

  [Test]
  public void TheTool_WritesBothLibrariesAsOneModule() {
    var file = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N") + ".irm");
    try {
      using var output = new StringWriter();
      using var error = new StringWriter();
      var exitCode = Program.WriteImplementations(
        [new CSharpSource("A.cs", "public static class A { public static int F(int x) => x + 1; }")],
        [new CSharpSource("B.cs", "public static class B { public static int G(int x) => x * 2; }")], file, output, error);
      Assert.That((exitCode, error.ToString()), Is.EqualTo((Program.Success, "")));
      Assert.That(IrModuleFormat.Read(File.ReadAllBytes(file)).Functions.Select(function => function.Name), Is.EqualTo(new[] { "F", "G" }));
    } finally {
      File.Delete(file);
    }
  }

  [Test]
  public void TheTool_WritesNothingForLibrariesThatDoNotImportOrClash() {
    var file = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N") + ".irm");
    using var output = new StringWriter();
    using var error = new StringWriter();
    var good = new CSharpSource("A.cs", "public static class A { public static int F(int x) => x + 1; }");
    Assert.That(Program.WriteImplementations([good], [new CSharpSource("B.cs", "public static class B { public static int G() => ; }")], file, output, error), Is.EqualTo(Program.Failure));
    Assert.That(Program.WriteImplementations([good], [new CSharpSource("B.cs", "public static class B { public static int F(int x) => x; }")], file, output, error), Is.EqualTo(Program.Failure));
    Assert.That(error.ToString(), Does.Contain(CSharpFrontend.CompilerError).And.Contain("Both libraries declare 'F'"));
    Assert.That(File.Exists(file), Is.False);
  }

}

/// <summary>The libraries as the C# frontend imports their sources in the test, merged as the build merges them.</summary>
internal static class FloatSupport {

  public static IrModule Libraries => Polypiler.Tests.Float.Support.FloatImplementations.Module;

}
