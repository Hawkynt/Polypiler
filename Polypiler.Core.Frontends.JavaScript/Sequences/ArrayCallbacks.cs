using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The functions that run JavaScript's array callbacks (#1329), one per call, written in the IR as the
/// loop ECMA-262 §23.1.3 specifies: the length is read once before the first call, every element is
/// read when it is reached, the callback receives the element, its index and the array as far as its
/// parameters reach, and the method runs over the whole array before anything after it - eager, as
/// JavaScript is. A list (an array whose length changes, #1328) skips an index it no longer has, as
/// <c>HasProperty</c> does.
/// </summary>
internal sealed class ArrayCallbacks {

  private readonly List<IrLocal> _parameters = [];
  private readonly List<IrLocal> _locals = [];

  private ArrayCallbacks() { }

  /// <summary>The message JavaScript's <c>reduce</c> of an empty array without an initial value throws with, as V8 words it.</summary>
  public const string EmptyReduce = "Reduce of empty array with no initial value";

  private IrLocal Parameter(string name, IrType type) {
    var local = new IrLocal(name, type);
    this._parameters.Add(local);
    return local;
  }

  private IrLocal Local(string name, IrType type) {
    var unique = name;
    for (var index = 2; this._locals.Concat(this._parameters).Any(local => local.Name == unique); ++index)
      unique = name + index;

    var local = new IrLocal(unique, type);
    this._locals.Add(local);
    return local;
  }

  private static IrExpr I32(long value) => new IrConst(IrType.I32, value);

  private static IrExpr Ref(IrLocal local) => new IrLocalRef(local);

  private static IrExpr Less(IrExpr left, IrExpr right) => new IrBinary(IrBinaryOp.Less, left, right);

  private static IrStmt Step(IrLocal index, long by = 1) => new IrAssign(index, new IrBinary(IrBinaryOp.Add, Ref(index), I32(by)));

  /// <summary>A number converted to another number type; any other value as it is.</summary>
  public static IrExpr Converted(IrExpr value, IrType type)
    => value.Type == type || !value.Type.IsNumeric || !type.IsNumeric ? value : new IrConvert(type, value);

  /// <summary>Whether a value converts to a type the way <see cref="Converted"/> converts it.</summary>
  public static bool Converts(IrType from, IrType to) => from == to || (from.IsNumeric && to.IsNumeric);

  /// <summary>
  /// What JavaScript's <c>ToBoolean</c> makes of a callback's result: a truth value as it is, a number
  /// true unless it is a zero or a NaN; <see langword="null"/> for a type the IR does not test.
  /// </summary>
  public static IrExpr? Truthy(IrExpr value) => value.Type switch {
    { Kind: IrTypeKind.Bool } => value,
    { IsInteger: true } => new IrBinary(IrBinaryOp.NotEqual, value, new IrConst(value.Type, 0)),
    { IsFloat: true } => new IrBinary(IrBinaryOp.Greater, new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [Converted(value, IrType.F64)]), new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 0.0))),
    _ => null
  };

  /// <summary>The length of an array or of a list.</summary>
  private static IrExpr Length(IrExpr source)
    => source.Type.IsArray ? new IrArrayLength(source) : new IrCollection(IrCollectionOp.ListCount, IrType.I32, [source]);

  /// <summary>The element of an array or of a list at an index.</summary>
  private static IrExpr Element(IrExpr source, IrExpr index)
    => source.Type.IsArray ? new IrArrayLoad(source, index) : new IrCollection(IrCollectionOp.ListGet, source.Type.Element!, [source, index]);

  /// <summary>The call of the callback with the arguments its parameters take, each converted to its parameter.</summary>
  private static IrExpr Call(IrExpr callback, params IrExpr[] arguments) {
    var signature = callback.Type.Signature!;
    return new IrInvoke(signature.Result, callback, [.. signature.Parameters.Select((parameter, index) => Converted(arguments[index], parameter))]);
  }

  /// <summary>
  /// The loop over the indices below the length read once: for a list, an index the list no longer
  /// has is skipped - <c>HasProperty</c> - unless the method reads every index.
  /// </summary>
  private IrStmt Loop(IrExpr source, IrLocal length, Func<IrExpr, IrExpr, List<IrStmt>> pass, bool skips = true) {
    var index = this.Local("i", IrType.I32);
    var each = pass(Ref(index), Element(source, Ref(index)));
    List<IrStmt> kept = source.Type.IsList && skips ? [new IrIf(Less(Ref(index), Length(source)), new IrBlock(each), null)] : each;
    return new IrBlock([
      new IrDeclare(index, I32(0)),
      new IrWhile(Less(Ref(index), Ref(length)), new IrBlock([.. kept, Step(index)]))
    ]);
  }

  /// <summary>
  /// The function that runs one call of an array method with a callback: its parameters are the
  /// array, the callback and - for <c>reduce</c> with one - the initial value.
  /// </summary>
  /// <param name="name">The name of the function.</param>
  /// <param name="method">The method: map, filter, forEach, some, every, find, findIndex, flatMap, reduce, reduceRight or sort.</param>
  /// <param name="source">The type of the array: an array or a list.</param>
  /// <param name="callback">The type of the callback.</param>
  /// <param name="result">The type of the value of the call.</param>
  /// <param name="initial">The type of the initial value of <c>reduce</c>, or <see langword="null"/> for none.</param>
  /// <returns>The function.</returns>
  public static IrFunction Build(string name, string method, IrType source, IrType callback, IrType result, IrType? initial) {
    var builder = new ArrayCallbacks();
    var array = builder.Parameter("array", source);
    var function = builder.Parameter("callback", callback);
    var seed = initial is null ? null : builder.Parameter("initial", initial);
    var body = builder.Body(method, Ref(array), Ref(function), seed is null ? null : Ref(seed), result);
    return new IrFunction(name, false, result, builder._parameters, builder._locals, new IrBlock(body));
  }

  private List<IrStmt> Body(string method, IrExpr array, IrExpr callback, IrExpr? initial, IrType result) {
    var statements = new List<IrStmt>();
    var length = this.Local("n", IrType.I32);
    statements.Add(new IrDeclare(length, Length(array)));
    IrStmt Return(IrExpr value) => new IrReturn(value);
    switch (method) {
      case "map": {
        // An array of numbers is made at its length; one of any other element - a string array has no elements to start with - is collected in a list.
        if (result.IsArray && (result.Element!.IsNumeric || result.Element == IrType.Bool)) {
          var mapped = this.Local("result", result);
          statements.Add(new IrDeclare(mapped, new IrNewArray(result, Ref(length))));
          statements.Add(this.Loop(array, length, (index, element) => [new IrStore(Ref(mapped), index, Converted(Call(callback, element, index, array), result.Element!))]));
          statements.Add(Return(Ref(mapped)));
          break;
        }

        var list = IrType.List(result.Element!);
        var collected = this.Local("result", list);
        statements.Add(new IrDeclare(collected, new IrCollection(IrCollectionOp.ListNew, list, [])));
        statements.Add(this.Loop(array, length, (index, element) => [new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [Ref(collected), Converted(Call(callback, element, index, array), list.Element!)]))]));
        statements.Add(Return(result.IsArray ? new IrCollection(IrCollectionOp.ListCopyToArray, result, [Ref(collected)]) : Ref(collected)));
        break;
      }
      case "filter" or "flatMap": {
        var list = IrType.List(result.Element!);
        var kept = this.Local("result", list);
        statements.Add(new IrDeclare(kept, new IrCollection(IrCollectionOp.ListNew, list, [])));
        IrStmt Add(IrExpr value) => new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [Ref(kept), Converted(value, list.Element!)]));
        statements.Add(this.Loop(array, length, (index, element) => {
          var value = this.Local("x", element.Type);
          var pass = new List<IrStmt> { new IrDeclare(value, element) };
          if (method == "filter") {
            pass.Add(new IrIf(Truthy(Call(callback, Ref(value), index, array))!, new IrBlock([Add(Ref(value))]), null));
            return pass;
          }

          // flatMap: the elements of the array the callback returns, one level deep.
          var inner = this.Local("inner", callback.Type.Signature!.Result);
          var at = this.Local("j", IrType.I32);
          pass.Add(new IrDeclare(inner, Call(callback, Ref(value), index, array)));
          pass.Add(new IrDeclare(at, I32(0)));
          pass.Add(new IrWhile(Less(Ref(at), Length(Ref(inner))), new IrBlock([Add(Element(Ref(inner), Ref(at))), Step(at)])));
          return pass;
        }));
        statements.Add(Return(result.IsArray ? new IrCollection(IrCollectionOp.ListCopyToArray, result, [Ref(kept)]) : Ref(kept)));
        break;
      }
      case "forEach":
        statements.Add(this.Loop(array, length, (index, element) => [new IrExprStmt(Call(callback, element, index, array))]));
        break;
      case "some" or "every": {
        var some = method == "some";
        statements.Add(this.Loop(array, length, (index, element) => {
          var test = Truthy(Call(callback, element, index, array))!;
          return [new IrIf(some ? test : new IrUnary(IrUnaryOp.LogicalNot, test), new IrBlock([Return(new IrConst(IrType.Bool, some ? 1 : 0))]), null)];
        }));
        statements.Add(Return(new IrConst(IrType.Bool, some ? 0 : 1)));
        break;
      }
      case "find" or "findIndex": {
        statements.Add(this.Loop(array, length, (index, element) => {
          var value = this.Local("x", element.Type);
          IrExpr found = method == "findIndex" ? index
            : result.IsOptional ? new IrSome(Converted(Ref(value), result.Underlying!)) : Converted(Ref(value), result);
          return [new IrDeclare(value, element), new IrIf(Truthy(Call(callback, Ref(value), index, array))!, new IrBlock([Return(found)]), null)];
        }));
        statements.Add(Return(method == "findIndex" ? I32(-1) : result.IsOptional ? new IrNone(result) : new IrNull(result)));
        break;
      }
      case "reduce" or "reduceRight":
        this.Reduce(method == "reduceRight", array, callback, initial, result, length, statements);
        break;
      default:
        this.Sort(array, callback, length, statements);
        statements.Add(Return(array));
        break;
    }

    return statements;
  }

  /// <summary>
  /// <c>reduce</c> and <c>reduceRight</c>: from the initial value, or from the first element present -
  /// a <c>TypeError</c> where there is none - the accumulator through every further element.
  /// </summary>
  private void Reduce(bool right, IrExpr array, IrExpr callback, IrExpr? initial, IrType result, IrLocal length, List<IrStmt> statements) {
    var accumulator = this.Local("result", result);
    var index = this.Local("k", IrType.I32);
    var first = right ? (IrExpr)new IrBinary(IrBinaryOp.Subtract, Ref(length), I32(1)) : I32(0);
    IrExpr Within() => right ? new IrBinary(IrBinaryOp.GreaterOrEqual, Ref(index), I32(0)) : Less(Ref(index), Ref(length));
    IrExpr Present() => array.Type.IsList ? new IrBinary(IrBinaryOp.LogicalAnd, Within(), Less(Ref(index), Length(array))) : Within();
    var thrown = new IrThrow(new IrNew(IrType.Object(IrExceptionClass.NullReferenceException), IrExceptionClass.Constructor(IrExceptionClass.NullReferenceException, 1), [new IrStrConst(EmptyReduce)]));
    statements.Add(new IrDeclare(index, first));
    if (initial is not null) {
      statements.Add(new IrDeclare(accumulator, initial));
    } else {
      // The first element present starts the accumulator.
      statements.Add(new IrDeclare(accumulator, Default(result)));
      var found = this.Local("found", IrType.Bool);
      statements.Add(new IrDeclare(found, new IrConst(IrType.Bool, 0)));
      statements.Add(new IrWhile(new IrBinary(IrBinaryOp.LogicalAnd, new IrUnary(IrUnaryOp.LogicalNot, Ref(found)), Within()), new IrBlock([
        new IrIf(array.Type.IsList ? Less(Ref(index), Length(array)) : new IrConst(IrType.Bool, 1),
          new IrBlock([new IrAssign(accumulator, Converted(Element(array, Ref(index)), result)), new IrAssign(found, new IrConst(IrType.Bool, 1))]), null),
        Step(index, right ? -1 : 1)])));
      statements.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, Ref(found)), new IrBlock([thrown]), null));
    }

    var step = new IrAssign(accumulator, Converted(Call(callback, Ref(accumulator), Element(array, Ref(index)), Ref(index), array), result));
    statements.Add(new IrWhile(Within(), new IrBlock([
      array.Type.IsList ? new IrIf(Less(Ref(index), Length(array)), new IrBlock([step]), null) : step,
      Step(index, right ? -1 : 1)])));
    statements.Add(new IrReturn(Ref(accumulator)));
  }

  /// <summary>The value a variable of a type starts with before the loop gives it one.</summary>
  private static IrExpr Default(IrType type) => type switch {
    { IsFloat: true } => new IrConst(type, IrOperators.Bits(type, 0.0)),
    { IsString: true } => new IrStrConst(""),
    { IsStruct: true } => new IrStructDefault(type),
    { IsOptional: true } => new IrNone(type),
    { IsNullable: true } => new IrNull(type),
    _ => new IrConst(type, 0)
  };

  /// <summary>
  /// <c>sort(comparator)</c>, stably as ECMA-262 2019 and later require: the elements are copied, their
  /// indices sorted by the sign of the comparator - a NaN counts as 0 - and the elements written back
  /// in that order.
  /// </summary>
  private void Sort(IrExpr array, IrExpr comparator, IrLocal length, List<IrStmt> statements) {
    var element = array.Type.Element!;
    var copy = this.Local("copy", IrType.List(element));
    var order = this.Local("order", IrType.Array(IrType.I32));
    statements.Add(new IrDeclare(copy, new IrCollection(IrCollectionOp.ListNew, IrType.List(element), [])));
    statements.Add(new IrDeclare(order, new IrNewArray(IrType.Array(IrType.I32), Ref(length))));
    statements.Add(this.Loop(array, length, (index, value) => [
      new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [Ref(copy), value])),
      new IrStore(Ref(order), index, index)], false));

    var a = new IrLocal("a", IrType.I32);
    var b = new IrLocal("b", IrType.I32);
    var compared = new IrLocal("c", comparator.Type.Signature!.Result);
    IrExpr Copied(IrLocal at) => new IrCollection(IrCollectionOp.ListGet, element, [Ref(copy), Ref(at)]);
    var zero = compared.Type.IsFloat ? new IrConst(compared.Type, IrOperators.Bits(compared.Type, 0.0)) : new IrConst(compared.Type, 0);
    var sign = IrLambda.Of([a, b], IrType.I32, [compared], new IrBlock([
      new IrDeclare(compared, Call(comparator, Copied(a), Copied(b))),
      new IrIf(new IrBinary(IrBinaryOp.Less, Ref(compared), zero), new IrBlock([new IrReturn(I32(-1))]), null),
      new IrIf(new IrBinary(IrBinaryOp.Greater, Ref(compared), zero), new IrBlock([new IrReturn(I32(1))]), null),
      new IrReturn(I32(0))]));
    statements.Add(new IrExprStmt(new IrCall(SequenceLibrary.StableSortName, IrType.Void, [Ref(order), sign])));

    var position = this.Local("k", IrType.I32);
    var sorted = new IrCollection(IrCollectionOp.ListGet, element, [Ref(copy), new IrArrayLoad(Ref(order), Ref(position))]);
    statements.Add(new IrDeclare(position, I32(0)));
    statements.Add(new IrWhile(Less(Ref(position), Ref(length)), new IrBlock([
      array.Type.IsArray ? new IrStore(array, Ref(position), sorted) : new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [array, Ref(position), sorted])),
      Step(position)])));
  }

}
