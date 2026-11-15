using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// The sequence operations of LINQ in the IR (#1329) on modules built by hand: the typing rule, the
/// verifier, the byte format, what the interpreter computes, and that <see cref="SequenceFusion"/>
/// computes the same with loops.
/// </summary>
[TestFixture]
public sealed class IrSequenceOpsTests {

  private static readonly IrType Ints = IrType.Sequence(IrType.I32, true);

  private static readonly IrType IntArray = IrType.Array(IrType.I32);

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrLambda Lambda(IrType result, Func<IrLocal[], IrExpr> body, params IrType[] parameters) {
    var locals = parameters.Select((type, index) => new IrLocal("p" + index, type)).ToArray();
    return IrLambda.Of(locals, result, [], new IrBlock([new IrReturn(body(locals))]));
  }

  private static IrSequence Of(IrSequenceOp op, params IrExpr[] arguments) => IrSequenceOps.Of(op, arguments);

  /// <summary>A module of one public function of an int array that returns an expression.</summary>
  private static IrModule Module(IrType result, Func<IrExpr, IrExpr> body, IrType? parameter = null) {
    var a = new IrLocal("a", parameter ?? IntArray);
    return new IrModule("M", [new IrFunction("F", true, result, [a], [], new IrBlock([new IrReturn(body(R(a)))]))]);
  }

  /// <summary>What the interpreter computes of the module, and the same of the fused module, which must agree.</summary>
  private static object? Both(IrModule module, params object[] arguments) {
    Assert.That(IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message), Is.Empty);
    var fused = SequenceFusion.Run(module);
    Assert.That(IrVerifier.Verify(fused).Select(diagnostic => diagnostic.Message), Is.Empty);
    Assert.That(IrTree.Nodes(fused.Functions[0].Body).OfType<IrSequence>(), Is.Empty);
    var (direct, lowered) = (Outcome(module, arguments), Outcome(fused, arguments));
    Assert.That(lowered, Is.EqualTo(direct));
    return direct;
  }

  private static object? Outcome(IrModule module, object[] arguments) {
    try {
      var result = IrInterpreter.Call(module, "F", arguments);
      return result is Array array ? PrototypeFixture.Show(array) : result;
    } catch (IrException exception) {
      return exception.Class.Name;
    }
  }

  private static IEnumerable<TestCaseData> IllTyped() {
    var f = Lambda(IrType.I32, p => R(p[0]), IrType.I32);
    var predicate = Lambda(IrType.Bool, p => new IrConst(IrType.Bool, 1), IrType.I32);
    var array = new IrLocalRef(new IrLocal("a", IntArray));
    var sequence = Of(IrSequenceOp.FromArray, array);
    var doubles = Of(IrSequenceOp.FromArray, new IrLocalRef(new IrLocal("d", IrType.Array(IrType.F64))));
    var strings = Of(IrSequenceOp.FromArray, new IrLocalRef(new IrLocal("s", IrType.Array(IrType.String))));
    var lists = Of(IrSequenceOp.FromArray, new IrLocalRef(new IrLocal("l", IrType.Array(IrType.List(IrType.I32)))));
    var bytes = Of(IrSequenceOp.FromArray, new IrLocalRef(new IrLocal("b", IrType.Array(IrType.U8))));
    IEnumerable<(IrSequenceOp, IrExpr[], string)> cases = [
      (IrSequenceOp.FromArray, [I(1)], "'FromArray' takes an array"),
      (IrSequenceOp.FromList, [array], "'FromList' takes a list"),
      (IrSequenceOp.Range, [I(1)], "'Range' takes two i32"),
      (IrSequenceOp.Select, [sequence], "'Select' takes 2 arguments, not 1"),
      (IrSequenceOp.Select, [array, f], "'Select' takes a sequence first"),
      (IrSequenceOp.Select, [sequence, new IrLocalRef(new IrLocal("v", IrType.Function(IrType.Void, IrType.I32)))], "fn(i32) -> U"),
      (IrSequenceOp.Select, [sequence, I(1)], "fn(i32) -> U"),
      (IrSequenceOp.SelectIndexed, [sequence, f], "fn(i32, i32) -> U"),
      (IrSequenceOp.Where, [sequence, f], "fn(i32) -> bool"),
      (IrSequenceOp.WhereIndexed, [sequence, predicate], "fn(i32, i32) -> bool"),
      (IrSequenceOp.SelectMany, [sequence, f], "fn(i32) -> seq<U>"),
      (IrSequenceOp.SelectManyResult, [sequence, f, f], "fn(i32) -> seq<U> and fn(i32, U) -> V"),
      (IrSequenceOp.Take, [sequence, new IrConst(IrType.I64, 1)], "'Take' takes an i32 count"),
      (IrSequenceOp.Zip, [sequence, I(1), f], "a second sequence"),
      (IrSequenceOp.Zip, [sequence, sequence, f], "a second sequence"),
      (IrSequenceOp.OrderBy, [lists, Lambda(IrType.List(IrType.I32), p => R(p[0]), IrType.List(IrType.I32))], "fn(list<i32>) -> K"),
      (IrSequenceOp.CountWhere, [sequence, f], "fn(i32) -> bool"),
      (IrSequenceOp.FirstOrDefault, [strings], "has no default value"),
      (IrSequenceOp.FirstOrDefaultWhere, [strings, Lambda(IrType.Bool, p => new IrConst(IrType.Bool, 1), IrType.String)], "has no default value"),
      (IrSequenceOp.Sum, [bytes], "'Sum' of a sequence of u8"),
      (IrSequenceOp.Min, [strings], "'Min' of a sequence of str"),
      (IrSequenceOp.Aggregate, [sequence, predicate], "fn(i32, i32) -> i32"),
      (IrSequenceOp.Aggregate, [lists, Lambda(IrType.List(IrType.I32), p => R(p[0]), IrType.List(IrType.I32), IrType.List(IrType.I32))], "fn(list<i32>, list<i32>) -> list<i32>"),
      (IrSequenceOp.AggregateSeed, [sequence, I(0), predicate], "'AggregateSeed' takes a seed"),
      (IrSequenceOp.Contains, [sequence, new IrConst(IrType.F64, 0)], "'Contains' takes a value of i32"),
      (IrSequenceOp.IndexOf, [lists, R(new IrLocal("x", IrType.List(IrType.I32)))], "'IndexOf' takes a value"),
      (IrSequenceOp.Count, [doubles, I(1)], "'Count' takes 1 arguments, not 2")
    ];
    return cases.Select(entry => new TestCaseData(entry.Item1, entry.Item2, entry.Item3).SetName($"{entry.Item1} {entry.Item3}"));
  }

  /// <summary>Every operation refuses operands that do not fit it, saying what it takes; the verifier and the factory say the same.</summary>
  [TestCaseSource(nameof(IllTyped))]
  public void Signature_RefusesOperandsThatDoNotFit(IrSequenceOp op, IrExpr[] arguments, string problem) {
    var (type, message) = IrSequenceOps.Signature(op, [.. arguments.Select(argument => argument.Type)]);
    Assert.That(type, Is.Null);
    Assert.That(message, Does.Contain(problem));
    Assert.That(() => IrSequenceOps.Of(op, arguments), Throws.ArgumentException.With.Message.Contains(problem));
  }

  [Test]
  public void Classification_SaysSourcesTerminalsAndOrderings() {
    Assert.That(Enum.GetValues<IrSequenceOp>().Where(IrSequenceOps.IsSource), Is.EqualTo(new[] { IrSequenceOp.FromArray, IrSequenceOp.FromList, IrSequenceOp.Range }));
    Assert.That(IrSequenceOps.IsTerminal(IrSequenceOp.ToArray) && IrSequenceOps.IsTerminal(IrSequenceOp.FindIndex) && !IrSequenceOps.IsTerminal(IrSequenceOp.ThenByDescending), Is.True);
    Assert.That(Enum.GetValues<IrSequenceOp>().Count(IrSequenceOps.IsOrdering), Is.EqualTo(4));
    Assert.That(IrSequenceOps.IsKey(IrType.String) && IrSequenceOps.IsKey(IrType.Bool) && !IrSequenceOps.IsKey(IntArray), Is.True);
    var sequence = Of(IrSequenceOp.Range, I(0), I(1));
    Assert.That(sequence.IsSource && !sequence.IsTerminal && Of(IrSequenceOp.Count, sequence).IsTerminal, Is.True);
  }

  [Test]
  public void Verifier_RefusesAWrongTypeAFurtherKeyWithoutOrderingAndAZipOfASequence() {
    var a = new IrLocal("a", IntArray);
    var key = Lambda(IrType.I32, p => R(p[0]), IrType.I32);
    var source = Of(IrSequenceOp.FromArray, R(a));
    var pair = Lambda(IrType.I32, p => R(p[0]), IrType.I32, IrType.I32);
    IrExpr[] bodies = [
      new IrSequence(IrSequenceOp.Count, IrType.I64, [source]),
      new IrSequence(IrSequenceOp.Count, IrType.I32, [new IrSequence(IrSequenceOp.ThenBy, Ints, [source, key])]),
      new IrSequence(IrSequenceOp.Count, IrType.I32, [new IrSequence(IrSequenceOp.Zip, Ints, [source, new IrArraySequence(R(a)), pair])]),
      new IrSequence(IrSequenceOp.Count, IrType.I32, [R(a)])
    ];
    var problems = bodies.Select(body => IrVerifier.Verify(new IrModule("M", [new IrFunction("F", true, body.Type, [a], [], new IrBlock([new IrReturn(body)]))]))
      .Select(diagnostic => diagnostic.Message).ToList()).ToList();
    Assert.That(problems[0], Has.Some.Contains("Count has the type i64, expected i32"));
    Assert.That(problems[1], Has.Some.Contains("ThenBy of a sequence that is not ordered"));
    Assert.That(problems[2], Has.Some.Contains("a Zip whose second sequence is no array and no list"));
    Assert.That(problems[3], Has.Some.Contains("'Count' takes a sequence first"));
  }

  [Test]
  public void Format_KeepsTheOperations() {
    var module = Module(IrType.I32, a => Of(IrSequenceOp.Sum, Of(IrSequenceOp.Select, Of(IrSequenceOp.FromArray, a), Lambda(IrType.I32, p => R(p[0]), IrType.I32))));
    var bytes = IrModuleFormat.Write(module);
    Assert.That(IrModuleFormat.Write(IrModuleFormat.Read(bytes)), Is.EqualTo(bytes));
  }

  [Test]
  public void List_TrapsWhenItChangesDuringTheEnumeration() {
    var a = new IrLocal("a", IntArray);
    var list = new IrLocal("l", IrType.List(IrType.I32));
    var add = IrLambda.Of([new IrLocal("x", IrType.I32)], IrType.I32, [], new IrBlock([
      new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(list), I(9)])),
      new IrReturn(I(1))]));
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [a], [list], new IrBlock([
      new IrDeclare(list, new IrCollection(IrCollectionOp.ListFromArray, IrType.List(IrType.I32), [R(a)])),
      new IrReturn(Of(IrSequenceOp.Sum, Of(IrSequenceOp.Select, Of(IrSequenceOp.FromList, R(list)), add)))]))]);
    Assert.That(Both(module, new[] { 1, 2 }), Is.EqualTo("InvalidOperationException"));
    Assert.That(Both(module, Array.Empty<int>()), Is.EqualTo(0));
  }

  [Test]
  public void ToList_AndZipOfAList_KeepTheElements() {
    var list = new IrLocal("l", IrType.List(IrType.I32));
    var a = new IrLocal("a", IntArray);
    var pair = Lambda(IrType.I32, p => new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, R(p[0]), I(10)), R(p[1])), IrType.I32, IrType.I32);
    var zipped = Of(IrSequenceOp.ToList, Of(IrSequenceOp.Zip, Of(IrSequenceOp.FromArray, R(a)), Of(IrSequenceOp.FromList, R(list)), pair));
    var module = new IrModule("M", [new IrFunction("F", true, IntArray, [a], [list], new IrBlock([
      new IrDeclare(list, new IrCollection(IrCollectionOp.ListFromArray, IrType.List(IrType.I32), [R(a)])),
      new IrReturn(new IrCollection(IrCollectionOp.ListCopyToArray, IntArray, [zipped]))]))]);
    Assert.That(Both(module, new[] { 1, 2, 3 }), Is.EqualTo("[11,22,33]"));
  }

  [TestCase(new[] { 3, 1, 2 }, "0")]
  [TestCase(new int[0], "InvalidOperationException")]
  public void Aggregate_FoldsFromTheFirstElement(int[] input, string expected)
    => Assert.That(Both(Module(IrType.I32, a => Of(IrSequenceOp.Aggregate, Of(IrSequenceOp.FromArray, a), Lambda(IrType.I32, p => new IrBinary(IrBinaryOp.Subtract, R(p[0]), R(p[1])), IrType.I32, IrType.I32))), input)?.ToString(),
      Is.EqualTo(expected));

  [Test]
  public void Searches_FindTheIndexAndTheElement() {
    var even = Lambda(IrType.Bool, p => new IrBinary(IrBinaryOp.Equal, new IrBinary(IrBinaryOp.Remainder, R(p[0]), I(2)), I(0)), IrType.I32);
    Assert.That(Both(Module(IrType.I32, a => Of(IrSequenceOp.IndexOf, Of(IrSequenceOp.FromArray, a), I(3))), new[] { 1, 3, 3 }), Is.EqualTo(1));
    Assert.That(Both(Module(IrType.I32, a => Of(IrSequenceOp.IndexOf, Of(IrSequenceOp.FromArray, a), I(4))), new[] { 1, 3 }), Is.EqualTo(-1));
    Assert.That(Both(Module(IrType.I32, a => Of(IrSequenceOp.FindIndex, Of(IrSequenceOp.FromArray, a), even)), new[] { 1, 4 }), Is.EqualTo(1));
    Assert.That(Both(Module(IrType.I32, a => Of(IrSequenceOp.FirstOrDefaultWhere, Of(IrSequenceOp.FromArray, a), even)), new[] { 1, 3 }), Is.EqualTo(0));
    Assert.That(Both(Module(IrType.Bool, a => Of(IrSequenceOp.Contains, Of(IrSequenceOp.FromArray, a), I(3))), new[] { 1, 3 }), Is.EqualTo(true));
  }

  [Test]
  public void Strings_CompareByCodeUnit() {
    var strings = IrType.Array(IrType.String);
    var self = Lambda(IrType.String, p => R(p[0]), IrType.String);
    Assert.That(Both(Module(IrType.String, a => Of(IrSequenceOp.First, Of(IrSequenceOp.OrderByDescending, Of(IrSequenceOp.FromArray, a), self)), strings), new object[] { new[] { "B", "a", "b" } }), Is.EqualTo("b"));
    Assert.That(Both(Module(IrType.Bool, a => Of(IrSequenceOp.Contains, Of(IrSequenceOp.FromArray, a), new IrStrConst("a")), strings), new object[] { new[] { "B", "a" } }), Is.EqualTo(true));
    Assert.That(Both(Module(IrType.String, a => Of(IrSequenceOp.Aggregate, Of(IrSequenceOp.FromArray, a),
      Lambda(IrType.String, p => new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [R(p[1]), R(p[0])]), IrType.String, IrType.String)), strings), new object[] { new[] { "a", "b" } }), Is.EqualTo("ba"));
  }

  [TestCase(new[] { 2f, float.NaN, 1f }, "NaN", "2")]
  [TestCase(new[] { float.NaN, 3f }, "NaN", "3")]
  [TestCase(new[] { float.NaN }, "NaN", "NaN")]
  [TestCase(new[] { 1f, 2f }, "1", "2")]
  public void SingleExtremes_TreatNaNAsTheSmallest(float[] input, string min, string max) {
    static string Show(object? value) => PrototypeFixture.Show((double)(float)value!);
    Assert.That(Show(Both(Module(IrType.F32, a => Of(IrSequenceOp.Min, Of(IrSequenceOp.FromArray, a)), IrType.Array(IrType.F32)), input)), Is.EqualTo(min));
    Assert.That(Show(Both(Module(IrType.F32, a => Of(IrSequenceOp.Max, Of(IrSequenceOp.FromArray, a)), IrType.Array(IrType.F32)), input)), Is.EqualTo(max));
  }

  [Test]
  public void SelectManyWithAResult_PairsEveryInnerElement() {
    var inner = Lambda(Ints, p => Of(IrSequenceOp.Range, I(0), R(p[0])), IrType.I32);
    var pair = Lambda(IrType.I32, p => new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, R(p[0]), I(10)), R(p[1])), IrType.I32, IrType.I32);
    Assert.That(Both(Module(IntArray, a => Of(IrSequenceOp.ToArray, Of(IrSequenceOp.SelectManyResult, Of(IrSequenceOp.FromArray, a), inner, pair))), new[] { 1, 2 }), Is.EqualTo("[10,20,21]"));
  }

  /// <summary>An escaping sequence of an array alone is the sequence of its elements; one of a list an iterator of its own.</summary>
  [TestCase(false)]
  [TestCase(true)]
  public void EscapingSources_BecomeSequences(bool list) {
    var a = new IrLocal("a", IntArray);
    var l = new IrLocal("l", IrType.List(IrType.I32));
    var s = new IrLocal("s", Ints);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [a], [l, s], new IrBlock([
      new IrDeclare(l, new IrCollection(IrCollectionOp.ListFromArray, IrType.List(IrType.I32), [R(a)])),
      new IrDeclare(s, list ? Of(IrSequenceOp.FromList, R(l)) : Of(IrSequenceOp.FromArray, R(a))),
      new IrReturn(Of(IrSequenceOp.Sum, R(s)))]))]);
    Assert.That(Both(module, new[] { 1, 2 }), Is.EqualTo(3));
    Assert.That(IrTree.Nodes(SequenceFusion.Run(module).Functions[0].Body).OfType<IrArraySequence>().Any(), Is.EqualTo(!list));
  }

  /// <summary>Range traps where it is evaluated, before anything enumerates it; a start that is no constant is evaluated once.</summary>
  [TestCase(0, -1, "ArgumentOutOfRangeException")]
  [TestCase(int.MaxValue, 2, "ArgumentOutOfRangeException")]
  [TestCase(int.MaxValue, 1, "1")]
  [TestCase(-5, 3, "1")]
  public void Range_TrapsWhenItIsBuilt(int start, int count, string expected) {
    var counter = new IrLocal("calls", IrType.I32);
    var s = new IrLocal("s", Ints);
    var x = new IrLocal("x", IrType.I32);
    var y = new IrLocal("y", IrType.I32);
    var stepped = new IrStep(R(counter), true, false, false);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [x, y], [counter, s], new IrBlock([
      new IrDeclare(counter, I(0)),
      new IrDeclare(s, Of(IrSequenceOp.Range, new IrBinary(IrBinaryOp.Add, R(x), new IrBinary(IrBinaryOp.Subtract, stepped, I(1))), R(y))),
      new IrReturn(R(counter))]))]);
    Assert.That(Both(module, start, count)?.ToString(), Is.EqualTo(expected));
  }

  [Test]
  public void Fusion_LeavesAModuleWithoutSequencesAsItIs() {
    var module = Module(IrType.I32, a => new IrArrayLength(a));
    Assert.That(SequenceFusion.Run(module), Is.SameAs(module));
    Assert.That(() => SequenceFusion.Run(null!), Throws.ArgumentNullException);
  }

  /// <summary>A chain inside a lambda becomes a function of its own, called from the lambda; a range there keeps its temporary in the lambda.</summary>
  [Test]
  public void Fusion_RewritesTheChainsOfLambdas() {
    var inner = Lambda(IrType.I32, p => Of(IrSequenceOp.Count, Of(IrSequenceOp.Range, new IrBinary(IrBinaryOp.Add, R(p[0]), I(1)), R(p[0]))), IrType.I32);
    Assert.That(Both(Module(IntArray, a => Of(IrSequenceOp.ToArray, Of(IrSequenceOp.Select, Of(IrSequenceOp.FromArray, a), inner))), new[] { 0, 2 }), Is.EqualTo("[0,2]"));
  }

  /// <summary>Every node rebuilt with its own operands has them again: the rewrite of a pass keeps what it does not replace.</summary>
  [Test]
  public void With_RebuildsEveryNodeOfTheFixtures()
    => Rebuilt([LinqFixtures.Imported("Pipelines"), SequenceFixtures.Imported(), PrototypeFixture.Imported("Statements")]);

  /// <summary>The same over every prototype fixture that imports, so that every kind of node is rebuilt, and fusion leaves each without sequences as it is.</summary>
  [Test, Category("Slow")]
  public void With_RebuildsEveryNodeOfEveryFixture() {
    var root = System.IO.Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");
    var modules = System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories)
      .Select(path => System.IO.Path.GetExtension(path) switch {
        ".cs" => Polypiler.Core.Frontends.CSharp.CSharpFrontend.Import(System.IO.Path.GetFileName(path), System.IO.File.ReadAllText(path)).Module,
        ".js" or ".mjs" => Polypiler.Core.Frontends.JavaScript.JavaScriptFrontend.Import(System.IO.Path.GetFileName(path), System.IO.File.ReadAllText(path)).Module,
        _ => null
      })
      .OfType<IrModule>().ToList();
    Assert.That(modules, Has.Count.GreaterThan(50));
    Rebuilt(modules);
    foreach (var module in modules.Where(module => !module.Functions.Any(function => IrTree.Nodes(function.Body).OfType<IrSequence>().Any())))
      Assert.That(SequenceFusion.Run(module), Is.SameAs(module));
  }

  private static void Rebuilt(IReadOnlyList<IrModule> modules) {
    foreach (var function in modules.SelectMany(module => module.Functions)) {
      foreach (var node in IrTree.Nodes(function.Body)) {
        var operands = IrTree.Operands(node);
        var rebuilt = IrTree.With(node, operands);
        Assert.That(rebuilt.GetType(), Is.EqualTo(node.GetType()));
        Assert.That(IrTree.Operands(rebuilt), Is.EqualTo(operands));
      }

      foreach (var statement in IrClosures.Within(function.Body).Where(statement => statement is not IrBlock)) {
        var rebuilt = IrTree.With(statement, IrTree.Expressions(statement), IrTree.Blocks(statement));
        Assert.That(rebuilt.GetType(), Is.EqualTo(statement.GetType()));
        Assert.That(IrTree.Expressions(rebuilt), Is.EqualTo(IrTree.Expressions(statement)));
        Assert.That(IrTree.Blocks(rebuilt), Is.EqualTo(IrTree.Blocks(statement)));
      }
    }
  }

}
