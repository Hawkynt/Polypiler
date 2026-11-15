using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Precomputed.Support;

namespace Polypiler.Tests.Interop;

/// <summary>
/// Foreign functions and calls in the IR (#1332): the verifier's rules, the module format, the interpreter's trap and
/// the effect that keeps every optimization from moving, folding, dropping or duplicating a foreign call.
/// </summary>
[TestFixture]
public sealed class IrForeignTests {

  private static readonly IrStructType _pair = new("Pair", [new IrField("X", IrType.I32, false), new IrField("Y", IrType.I32, false)]);

  private static IrForeignFunction Function(IrType result, params IrForeignParameter[] parameters) => new("f", true, "lib", result, parameters);

  private static IrForeignParameter Parameter(IrType type, IrMarshal marshal) => new("p", type, marshal);

  private static IrModule Module(IrForeignFunction foreign, IReadOnlyList<IrStmt> body, IrType? result = null, IReadOnlyList<IrTypeDeclaration>? types = null)
    => new("M", [new IrFunction("g", true, result ?? IrType.Void, [], [], new IrBlock(body))]) { ForeignFunctions = [foreign], Types = types ?? [_pair] };

  private static string Verified(IrModule module) => string.Join("\n", IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message));

  /// <summary>Test design 2: one declaration per marshalling kind is well-formed, called with arguments of its parameters' types.</summary>
  [Test]
  public void EveryMarshallingKind_IsWellFormed() {
    var foreign = new IrForeignFunction("f", true, "lib", IrType.Bool, [
      Parameter(IrType.I32, IrMarshal.Blittable), Parameter(IrType.U64, IrMarshal.Blittable with { NativeSize = true }),
      Parameter(IrType.Bool, new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.U1 }), Parameter(IrType.String, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Utf8 }),
      Parameter(IrType.Array(IrType.U8), new IrMarshal(IrMarshalKind.Array, IrMarshalDirection.Out)), Parameter(IrType.Array(IrType.I64), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.Out) { NativeSize = true }),
      Parameter(IrType.Array(IrType.Struct(_pair)), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.InOut)), Parameter(IrType.Struct(_pair), new IrMarshal(IrMarshalKind.Struct)),
      Parameter(IrType.I64, new IrMarshal(IrMarshalKind.Handle)), Parameter(IrType.Pointer(IrType.U8), IrMarshal.Blittable)
    ]) { Result = new IrMarshal(IrMarshalKind.Bool) };
    IrExpr[] arguments = [.. foreign.ParameterTypes.Select(type => type.IsArray ? new IrNewArray(type, new IrConst(IrType.I32, 1)) : type.IsStruct ? new IrStructDefault(type)
      : type.IsString ? new IrStrConst("") : type.IsPointer ? new IrNull(type) : (IrExpr)new IrConst(type, 0))];
    var module = Module(foreign, [new IrReturn(new IrForeignCall("f", IrType.Bool, arguments))], IrType.Bool);
    Assert.That(Verified(module), Is.Empty);
    Assert.That(IrForeign.Problems(foreign), Is.Empty);
  }

  /// <summary>Test design 11: a type that does not fit its marshalling, a result that cannot be one, a library without a name.</summary>
  [TestCase("I32", "Bool", "the parameter 'p' of the type i32 is marshalled as Bool")]
  [TestCase("I32", "String", "is marshalled as String(Ansi)")]
  [TestCase("I32", "Array", "is marshalled as Array(In)")]
  [TestCase("I32", "ByRef", "is marshalled as ByRef(In)")]
  [TestCase("I32", "Struct", "is marshalled as Struct")]
  [TestCase("I32", "Handle", "is marshalled as Handle")]
  [TestCase("String", "Blittable", "of the type str is marshalled as Blittable")]
  public void AParameterThatDoesNotFit_IsReported(string type, string kind, string expected) {
    var irType = type == "I32" ? IrType.I32 : IrType.String;
    var foreign = Function(IrType.Void, Parameter(irType, new IrMarshal(System.Enum.Parse<IrMarshalKind>(kind))));
    Assert.That(string.Join("\n", IrForeign.Problems(foreign)), Does.Contain(expected));
  }

  /// <summary>The boundaries of the kinds: a native size of 32 bits, a string out, an array of strings, a reference to a string, a pointer result.</summary>
  [Test]
  public void TheBoundariesOfTheKinds_AreReported() {
    Assert.That(IrForeign.Problems(Function(IrType.Void, Parameter(IrType.U32, IrMarshal.Blittable with { NativeSize = true }))), Is.Not.Empty);
    Assert.That(IrForeign.Problems(Function(IrType.Void, Parameter(IrType.String, new IrMarshal(IrMarshalKind.String, IrMarshalDirection.Out)))), Is.Not.Empty);
    Assert.That(IrForeign.Problems(Function(IrType.Void, Parameter(IrType.Array(IrType.String), new IrMarshal(IrMarshalKind.Array)))), Is.Not.Empty);
    Assert.That(IrForeign.Problems(Function(IrType.Void, Parameter(IrType.Array(IrType.String), new IrMarshal(IrMarshalKind.ByRef)))), Is.Not.Empty);
    Assert.That(IrForeign.Problems(Function(IrType.Void, Parameter(IrType.Array(IrType.I32), new IrMarshal(IrMarshalKind.ByRef) { NativeSize = true }))), Is.Not.Empty);
    Assert.That(IrForeign.Problems(Function(IrType.Pointer(IrType.U8))), Is.Not.Empty);
    Assert.That(IrForeign.Problems(Function(IrType.String) with { Result = new IrMarshal(IrMarshalKind.String) }), Has.Some.Contains("the result of the type str"));
    Assert.That(IrForeign.Problems(new IrForeignFunction("f", true, "", IrType.Void, [])), Is.EqualTo(new[] { "the library has no name" }));
  }

  /// <summary>A call the module does not declare, of other types, of another result; a name declared twice; a struct of another module.</summary>
  [Test]
  public void IllFormedCalls_AreReported() {
    var foreign = Function(IrType.I32, Parameter(IrType.I32, IrMarshal.Blittable));
    Assert.That(Verified(Module(foreign, [new IrExprStmt(new IrForeignCall("h", IrType.I32, []))])), Does.Contain("a call of the foreign function 'h', which the module does not declare"));
    Assert.That(Verified(Module(foreign, [new IrExprStmt(new IrForeignCall("f", IrType.I32, [new IrConst(IrType.I64, 1)]))])), Does.Contain("the foreign function 'f' (i32) is called with (i64)"));
    Assert.That(Verified(Module(foreign, [new IrExprStmt(new IrForeignCall("f", IrType.I64, [new IrConst(IrType.I32, 1)]))])), Does.Contain("the foreign function 'f' returns i32, not i64"));
    Assert.That(Verified(Module(foreign with { Name = "g" }, [])), Does.Contain("the module declares the name twice"));
    var stranger = new IrStructType("Other", [new IrField("X", IrType.I32, false)]);
    Assert.That(Verified(Module(Function(IrType.Struct(stranger)) with { Result = new IrMarshal(IrMarshalKind.Struct) }, [])), Does.Contain("the type Other is not among the types of the module"));
    Assert.That(Verified(Module(Function(IrType.Void, Parameter(IrType.Bool, IrMarshal.Blittable)), [])), Does.Contain("is marshalled as Blittable"));
  }

  /// <summary>Test design 9: a layout whose offsets do not match its fields, a packing that is no power of two, a negative size.</summary>
  [TestCase(IrLayoutKind.Explicit, 0, 0, new[] { 0 }, "a Explicit layout with 1 offsets for 2 fields")]
  [TestCase(IrLayoutKind.Explicit, 0, 0, new[] { 0, -4 }, "a Explicit layout with 2 offsets for 2 fields")]
  [TestCase(IrLayoutKind.Sequential, 0, 0, new[] { 0, 4 }, "a Sequential layout with 2 offsets for 2 fields")]
  [TestCase(IrLayoutKind.Sequential, 3, 0, new int[0], "a layout packed by 3 of the size 0")]
  [TestCase(IrLayoutKind.Sequential, 256, 0, new int[0], "a layout packed by 256 of the size 0")]
  [TestCase(IrLayoutKind.Sequential, 0, -1, new int[0], "a layout packed by 0 of the size -1")]
  public void IllFormedLayouts_AreReported(IrLayoutKind kind, int pack, int size, int[] offsets, string expected) {
    var laid = new IrStructType("Laid", [new IrField("X", IrType.I32, false), new IrField("Y", IrType.I32, false)]) { Layout = new IrStructLayout(kind, pack, size, offsets) };
    Assert.That(Verified(Module(Function(IrType.Void), [], types: [_pair, laid])), Does.Contain(expected));
  }

  /// <summary>Well-formed layouts of either kind pass, and two layouts that state the same are equal.</summary>
  [Test]
  public void WellFormedLayouts_Pass() {
    var laid = new IrStructType("Laid", [new IrField("X", IrType.I32, false)]) { Layout = new IrStructLayout(IrLayoutKind.Explicit, 8, 16, [4]) };
    Assert.That(Verified(Module(Function(IrType.Void), [], types: [_pair, laid])), Is.Empty);
    Assert.That(new IrStructLayout(IrLayoutKind.Explicit, 8, 16, [4]), Is.EqualTo(laid.Layout));
    Assert.That(new IrStructLayout(IrLayoutKind.Explicit, 8, 16, [4]).GetHashCode(), Is.EqualTo(laid.Layout!.GetHashCode()));
    Assert.That(new IrStructLayout(IrLayoutKind.Explicit, 8, 16, [0]), Is.Not.EqualTo(laid.Layout));
    Assert.That(laid.Layout.Equals(null), Is.False);
  }

  /// <summary>Both fixtures, a laid-out struct and every optional part of a declaration read back from the module format as the same graph.</summary>
  [Test]
  public void TheModuleFormat_KeepsEverything() {
    var laid = new IrStructType("Laid", [new IrField("X", IrType.I32, false), new IrField("Y", IrType.F32, false)]) { Layout = new IrStructLayout(IrLayoutKind.Explicit, 1, 8, [0, 4]) };
    var foreign = new IrForeignFunction("f", false, "lib", IrType.I32, [Parameter(IrType.Bool, new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.I1, Attributed = true }),
      Parameter(IrType.Array(IrType.U8), new IrMarshal(IrMarshalKind.Array, IrMarshalDirection.In) { Directed = true })]) {
      Origin = IrForeignOrigin.LibraryImport, EntryPoint = "e", Convention = IrCallingConvention.StdCall, CharSet = IrStringEncoding.Unicode, SetLastError = false, ExactSpelling = true
    };
    var module = Module(foreign, [new IrReturn(new IrBinary(IrBinaryOp.Add,
      new IrForeignCall("f", IrType.I32, [new IrConst(IrType.Bool, 1), new IrNewArray(IrType.Array(IrType.U8), new IrConst(IrType.I32, 2))]), new IrLastError(true)))], IrType.I32, [_pair, laid]);
    // A second declaration of its own objects, which the format does not share, with the other values of the flags.
    var other = foreign with {
      Name = "h", SetLastError = true, ExactSpelling = false, Result = IrMarshal.Blittable,
      Parameters = [.. foreign.Parameters.Select(parameter => parameter with { Marshal = parameter.Marshal with { } })]
    };
    module = module with { ForeignFunctions = [foreign, other] };
    foreach (var candidate in new[] { module, InteropFixtures.Imported("LibC.Linux.cs"), InteropFixtures.Imported("LibC.Windows.cs"), InteropFixtures.Imported("Missing.cs") }) {
      var bytes = IrModuleFormat.Write(candidate);
      var read = IrModuleFormat.Read(bytes);
      Assert.That(IrGraph.Difference(candidate, read), Is.Null);
      Assert.That(read.Types.OfType<IrStructType>().Select(type => type.Layout), Is.EqualTo(candidate.Types.OfType<IrStructType>().Select(type => type.Layout)));
      Assert.That(IrModuleFormat.Write(read), Is.EqualTo(bytes));
    }
  }

  /// <summary>Test design 13: the interpreter traps InvalidOperation at a foreign call and at the last error.</summary>
  [Test]
  public void TheInterpreter_Traps() {
    var foreign = Function(IrType.I32, Parameter(IrType.I32, IrMarshal.Blittable));
    var call = Module(foreign, [new IrReturn(new IrForeignCall("f", IrType.I32, [new IrConst(IrType.I32, 1)]))], IrType.I32);
    Assert.That(() => IrInterpreter.Call(call, "g", []), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.InvalidOperation));
    var error = Module(foreign, [new IrReturn(new IrLastError(false))], IrType.I32);
    Assert.That(() => IrInterpreter.Call(error, "g", []), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.InvalidOperation));
  }

  /// <summary>
  /// Test design 14: the optimizer neither moves, folds, drops nor merges a foreign call: two calls with the same
  /// constant argument, one of them unused, both stay, in order, at the default level.
  /// </summary>
  [Test]
  public void TheOptimizer_KeepsEveryCallInOrder() {
    var foreign = Function(IrType.I32, Parameter(IrType.I32, IrMarshal.Blittable)) with { IsPublic = false };
    var a = new IrLocal("a", IrType.I32);
    var b = new IrLocal("b", IrType.I32);
    IrExpr Called(int value) => new IrForeignCall("f", IrType.I32, [new IrConst(IrType.I32, value)]);
    var module = new IrModule("M", [new IrFunction("g", true, IrType.I32, [], [a, b], new IrBlock([
      new IrDeclare(a, Called(1)), new IrDeclare(b, Called(2)), new IrExprStmt(Called(1)), new IrReturn(new IrLocalRef(a))]))]) { ForeignFunctions = [foreign] };
    var optimized = Optimizer.Run(module, new OptimizerOptions { Level = OptimizationLevel.Default }).Module;
    optimized = IrPassPipeline.Default.Run(optimized, new OptimizerOptions { Verify = true }).Module;
    var calls = optimized.Functions.Single().Body.Statements.SelectMany(IrTree.Nodes).OfType<IrForeignCall>().ToList();
    Assert.That(calls.Select(call => ((IrConst)call.Arguments[0]).Value), Is.EqualTo(new long[] { 1, 2, 1 }));
    Assert.That(IrShape.IsSideEffectFree(Called(1)), Is.False);
    Assert.That(IrShape.IsSideEffectFree(new IrLastError(true)), Is.False);
    Assert.That(IrShape.IsTrapFree(new IrLastError(true)), Is.False);
  }

  /// <summary>The operands of a foreign call are its arguments, in every walk of the IR, and a rebuilt call keeps its function.</summary>
  [Test]
  public void TheArguments_AreTheOperands() {
    var call = new IrForeignCall("f", IrType.I32, [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2)]);
    IrExpr[] other = [new IrConst(IrType.I32, 3), new IrConst(IrType.I32, 4)];
    Assert.That(IrTree.Operands(call), Is.EqualTo(call.Arguments));
    Assert.That(IrShape.Operands(call), Is.EqualTo(call.Arguments));
    Assert.That(((IrForeignCall)IrTree.With(call, other)).Arguments, Is.EqualTo(other));
    Assert.That(((IrForeignCall)IrShape.With(call, other)).Function, Is.EqualTo("f"));
    Assert.That(IrTree.Operands(new IrLastError(false)), Is.Empty);
    Assert.That(IrForeign.Describe(new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.Out)), Is.EqualTo("ByRef(Out)"));
    Assert.That(new IrForeignFunction("f", true, "lib", IrType.Void, []).Symbol, Is.EqualTo("f"));
    Assert.That(new IrForeignFunction("f", true, "lib", IrType.Void, []) { EntryPoint = "e" }.Symbol, Is.EqualTo("e"));
  }

}
