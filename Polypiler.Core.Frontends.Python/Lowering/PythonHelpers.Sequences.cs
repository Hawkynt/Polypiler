using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// The operations of Python's sequences that no single IR operation computes, each a private function
/// of the module made once per type of its sequence: indexing that counts from the end and raises
/// <c>IndexError</c>, slicing as <c>slice.indices</c> clamps, concatenation and repetition, the
/// iterators of lists that read the length anew at every step, <c>min</c>, <c>max</c>, <c>sorted</c>
/// and the element-wise <c>==</c> of lists and tuples. Written from the Python Library Reference
/// ("Sequence Types", "Built-in Functions") and the data model's rules for slices, with CPython as
/// the oracle the differential tests compare against.
/// </summary>
internal sealed partial class PythonHelpers {

  /// <summary>A part of a helper's name for an IR type: its letters and digits.</summary>
  private static string Name(IrType type) {
    var text = new StringBuilder();
    foreach (var character in type.ToString())
      text.Append(char.IsLetterOrDigit(character) ? character : '_');

    return text.ToString().Trim('_');
  }

  private static IrExpr Count(IrExpr list) => new IrCollection(IrCollectionOp.ListCount, IrType.I32, [list]);

  /// <summary>A value of a type that no path reads, so that a local is assigned where it is declared: zero, false, empty, none or no reference.</summary>
  private static IrExpr DefaultOf(IrType type) => type switch {
    { IsOptional: true } => new IrNone(type),
    { IsStruct: true } => new IrStructDefault(type),
    { IsString: true } => new IrStrConst(""),
    { IsList: true } => NewList(type), { IsNullable: true } or { IsLazySequence: true } => new IrNull(type),
    _ when type == IrType.Bool => Bool(false),
    _ when type.IsBigInt => Big(0),
    _ => new IrConst(type, 0)
  };

  private static IrExpr Get(IrExpr list, IrExpr index, IrType element) => new IrCollection(IrCollectionOp.ListGet, element, [list, index]);

  private static IrStmt Add(IrExpr list, IrExpr value) => new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [list, value]));

  private static IrExpr NewList(IrType type) => new IrCollection(IrCollectionOp.ListNew, type, []);

  private static IrExpr Less(IrExpr left, IrExpr right) => Binary(IrBinaryOp.Less, left, right);

  private static IrExpr Plus(IrExpr left, IrExpr right) => Binary(IrBinaryOp.Add, left, right);

  private static IrExpr And(IrExpr left, IrExpr right) => Binary(IrBinaryOp.LogicalAnd, left, right);

  private static IrExpr Or(IrExpr left, IrExpr right) => Binary(IrBinaryOp.LogicalOr, left, right);

  /// <summary>A counted loop over an <c>i32</c> counter from 0 while it is below a bound.</summary>
  private static IrStmt Counted(IrLocal counter, IrExpr bound, params IReadOnlyList<IrStmt> body)
    => Block(Assign(counter, Int32(0)), new IrFor(Less(Ref(counter), bound), Block(body), Block(Assign(counter, Plus(Ref(counter), Int32(1))))));

  /// <summary>The raise of a Python exception with a message held by a local.</summary>
  private static IrThrow RaiseWith(IrClassType type, IrExpr message)
    => new(new IrNew(IrType.Object(type), IrExceptionClass.Constructor(type, 1), [message]));

  /// <summary>The value of an optional, or the raise of an exception where it holds none: arithmetic on <c>None</c> raises <c>TypeError</c>, an attribute of it <c>AttributeError</c>.</summary>
  public IrCall Unwrap(IrExpr value, IrType underlying, DeclaredException error, string message) {
    var reference = underlying.IsNullable;
    var parameter = reference ? underlying : IrType.Optional(underlying);
    return this.Call($"PyUnwrap_{Name(underlying)}_{error.Type.Name}", underlying, [value, new IrStrConst(message)], name => {
      var (v, text) = (Local("value", parameter), Local("message", IrType.String));
      IrExpr none = reference ? new IrIsNull(Ref(v)) : new IrIsNone(Ref(v));
      return Function(name, underlying, [v, text], [], If(none, error.Raise(Ref(text))), Return(reference ? Ref(v) : new IrUnwrap(Ref(v))));
    });
  }

  /// <summary>The truth of an optional: it holds a value, and that value is true.</summary>
  public IrCall OptionalTruth(IrExpr value, IrType underlying, Func<IrExpr, IrExpr> truth) {
    var reference = underlying.IsNullable;
    var parameter = reference ? underlying : IrType.Optional(underlying);
    return this.Call($"PyTruthOptional_{Name(underlying)}", IrType.Bool, [value], name => {
      var v = Local("value", parameter);
      IrExpr held = reference ? Not(new IrIsNull(Ref(v))) : Not(new IrIsNone(Ref(v)));
      return Function(name, IrType.Bool, [v], [], Return(And(held, truth(reference ? Ref(v) : new IrUnwrap(Ref(v))))));
    });
  }

  /// <summary>A new list of the values given, in order.</summary>
  public IrCall ListOf(IReadOnlyList<IrExpr> values, IrType listType)
    => this.Call($"PyListOf{values.Count}_{Name(listType)}", listType, values, name => {
      var parameters = values.Select((_, index) => Local("v" + index, listType.Element!)).ToList();
      var list = Local("list", listType);
      return Function(name, listType, parameters, [list], [new IrDeclare(list, NewList(listType)), .. parameters.Select(parameter => Add(Ref(list), Ref(parameter))), Return(Ref(list))]);
    });

  /// <summary>
  /// The statements that turn an <c>i64</c> index into a position within a length: a negative index
  /// counts from the end; outside the sequence <c>IndexError</c> with python's message.
  /// </summary>
  private static IEnumerable<IrStmt> Normalised(IrLocal index, IrExpr length, string message) => [
    If(Less(Ref(index), Int(0)), Assign(index, Plus(Ref(index), Convert(IrType.I64, length)))),
    If(Or(Less(Ref(index), Int(0)), Binary(IrBinaryOp.GreaterOrEqual, Ref(index), Convert(IrType.I64, length))), Raise(IrExceptionClass.IndexOutOfRangeException, message))
  ];

  /// <summary><c>a[i]</c> of a list.</summary>
  public IrCall ListGet(IrExpr list, IrExpr index, IrType listType)
    => this.Call($"PyListGet_{Name(listType)}", listType.Element!, [list, index], name => {
      var (l, i) = (Local("list", listType), Local("index", IrType.I64));
      return Function(name, listType.Element!, [l, i], [], [.. Normalised(i, Count(Ref(l)), "list index out of range"), Return(Get(Ref(l), Convert(IrType.I32, Ref(i)), listType.Element!))]);
    });

  /// <summary><c>a[i] = v</c> of a list.</summary>
  public IrCall ListSet(IrExpr list, IrExpr index, IrExpr value, IrType listType)
    => this.Call($"PyListSet_{Name(listType)}", IrType.Void, [list, index, value], name => {
      var (l, i, v) = (Local("list", listType), Local("index", IrType.I64), Local("value", listType.Element!));
      return Function(name, IrType.Void, [l, i, v], [], [.. Normalised(i, Count(Ref(l)), "list assignment index out of range"),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [Ref(l), Convert(IrType.I32, Ref(i)), Ref(v)]))]);
    });

  /// <summary><c>b[i]</c> of bytes: the byte as an <c>int</c>.</summary>
  public IrCall BytesGet(IrExpr bytes, IrExpr index)
    => this.Call("PyBytesGet", IrType.I64, [bytes, index], name => {
      var (b, i) = (Local("bytes", IrType.Array(IrType.U8)), Local("index", IrType.I64));
      return Function(name, IrType.I64, [b, i], [], [.. Normalised(i, new IrArrayLength(Ref(b)), "index out of range"), Return(Convert(IrType.I64, new IrArrayLoad(Ref(b), Convert(IrType.I32, Ref(i)))))]);
    });

  /// <summary><c>t[i]</c> of a table of the module.</summary>
  public IrCall ArrayGet(IrExpr array, IrExpr index, IrType arrayType)
    => this.Call($"PyTableGet_{Name(arrayType)}", arrayType.Element!, [array, index], name => {
      var (a, i) = (Local("table", arrayType), Local("index", IrType.I64));
      return Function(name, arrayType.Element!, [a, i], [], [.. Normalised(i, new IrArrayLength(Ref(a)), "tuple index out of range"), Return(new IrArrayLoad(Ref(a), Convert(IrType.I32, Ref(i))))]);
    });

  /// <summary><c>b[i] = v</c> of a bytearray: <c>ValueError</c> for a value outside 0 to 255, then <c>IndexError</c> outside it.</summary>
  public IrCall ByteArraySet(IrExpr bytes, IrExpr index, IrExpr value)
    => this.Call("PyByteArraySet", IrType.Void, [bytes, index, value], name => {
      var (b, i, v) = (Local("bytes", IrType.Array(IrType.U8)), Local("index", IrType.I64), Local("value", IrType.I64));
      return Function(name, IrType.Void, [b, i, v], [],
        [If(Or(Less(Ref(v), Int(0)), Binary(IrBinaryOp.Greater, Ref(v), Int(255))), Raise(IrExceptionClass.ArgumentException, "byte must be in range(0, 256)")),
          .. Normalised(i, new IrArrayLength(Ref(b)), "bytearray index out of range"),
          new IrStore(Ref(b), Convert(IrType.I32, Ref(i)), Convert(IrType.U8, Ref(v)))]);
    });

  /// <summary>The number of code points of a str: a pair of surrogates counts once.</summary>
  public IrCall CodePointCount(IrExpr text)
    => this.Call("PyStrLength", IrType.I64, [text], name => {
      var (s, at, count) = (Local("text", IrType.String), Local("at", IrType.I32), Local("count", IrType.I64));
      return Function(name, IrType.I64, [s], [at, count],
        new IrDeclare(at, Int32(0)),
        new IrDeclare(count, Int(0)),
        new IrWhile(Less(Ref(at), Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, Ref(s))), Block(
          Assign(at, Plus(Ref(at), Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(s), Ref(at)))),
          Assign(count, Plus(Ref(count), Int(1))))),
        Return(Ref(count)));
    });

  /// <summary><c>s[i]</c> of a str: the code point at a position counted in code points, as a str of its own.</summary>
  public IrCall StrGet(IrExpr text, IrExpr index)
    => this.Call("PyStrGet", IrType.String, [text, index], name => {
      var (s, i, at, k) = (Local("text", IrType.String), Local("index", IrType.I64), Local("at", IrType.I32), Local("k", IrType.I64));
      return Function(name, IrType.String, [s, i], [at, k],
        [.. Normalised(i, Convert(IrType.I32, this.CodePointCount(Ref(s))), "string index out of range"),
          new IrDeclare(at, Int32(0)),
          new IrDeclare(k, Int(0)),
          new IrWhile(Less(Ref(k), Ref(i)), Block(
            Assign(at, Plus(Ref(at), Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(s), Ref(at)))),
            Assign(k, Plus(Ref(k), Int(1))))),
          Return(Intrinsic(IrIntrinsicOp.StrSubstring, IrType.String, Ref(s), Ref(at), Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(s), Ref(at))))]);
    });

  /// <summary>Whether a str holds another one: <c>x in s</c>, the needle evaluated first.</summary>
  public IrCall StrContains(IrExpr needle, IrExpr haystack)
    => this.Call("PyStrContains", IrType.Bool, [needle, haystack], name => {
      var (n, h) = (Local("needle", IrType.String), Local("haystack", IrType.String));
      return Function(name, IrType.Bool, [n, h], [], Return(Intrinsic(IrIntrinsicOp.StrContains, IrType.Bool, Ref(h), Ref(n))));
    });

  /// <summary><c>x in b</c> of an <c>int</c> and bytes: <c>ValueError</c> for a value outside 0 to 255.</summary>
  public IrCall BytesContains(IrExpr value, IrExpr bytes)
    => this.Call("PyBytesContains", IrType.Bool, [value, bytes], name => {
      var (v, b, i) = (Local("value", IrType.I64), Local("bytes", IrType.Array(IrType.U8)), Local("i", IrType.I32));
      return Function(name, IrType.Bool, [v, b], [i],
        If(Or(Less(Ref(v), Int(0)), Binary(IrBinaryOp.Greater, Ref(v), Int(255))), Raise(IrExceptionClass.ArgumentException, "byte must be in range(0, 256)")),
        new IrDeclare(i, Int32(0)),
        Counted(i, new IrArrayLength(Ref(b)), If(Equal(Convert(IrType.I64, new IrArrayLoad(Ref(b), Ref(i))), Ref(v)), Return(Bool(true)))),
        Return(Bool(false)));
    });

  /// <summary><c>x in a</c> of a list or table: the first element <c>==</c> to the value, from the start, the length read anew at every step.</summary>
  public IrCall Contains(IrExpr value, IrExpr sequence, IrType element, IrType sequenceType, Func<IrExpr, IrExpr, IrExpr> equal)
    => this.Call($"PyContains_{Name(sequenceType)}", IrType.Bool, [value, sequence], name => {
      var (v, s, i) = (Local("value", element), Local("sequence", sequenceType), Local("i", IrType.I32));
      var list = sequenceType.IsList;
      var length = list ? Count(Ref(s)) : new IrArrayLength(Ref(s));
      IrExpr at = list ? Get(Ref(s), Ref(i), element) : new IrArrayLoad(Ref(s), Ref(i));
      return Function(name, IrType.Bool, [v, s], [i], new IrDeclare(i, Int32(0)), Counted(i, length, If(equal(Ref(v), at), Return(Bool(true)))), Return(Bool(false)));
    });

  /// <summary><c>x in t</c> of a tuple of one item type: the items tried in order.</summary>
  public IrCall TupleContains(IrExpr value, IrExpr tuple, IrType tupleType, Func<IrExpr, IrExpr, IrExpr> equal)
    => this.Call($"PyTupleContains_{Name(tupleType)}", IrType.Bool, [value, tuple], name => {
      var (v, t) = (Local("value", tupleType.StructType!.Fields[0].Type), Local("tuple", tupleType));
      var found = tupleType.StructType.Fields.Select(field => equal(Ref(v), new IrFieldLoad(Ref(t), field))).Aggregate((IrExpr)Bool(false), Or);
      return Function(name, IrType.Bool, [v, t], [], Return(found));
    });

  /// <summary><c>a == b</c> of two byte arrays: the same length and the same bytes.</summary>
  public IrCall BytesEqual(IrExpr left, IrExpr right)
    => this.Call("PyBytesEqual", IrType.Bool, [left, right], name => {
      var (a, b, i) = (Local("a", IrType.Array(IrType.U8)), Local("b", IrType.Array(IrType.U8)), Local("i", IrType.I32));
      return Function(name, IrType.Bool, [a, b], [i],
        If(Binary(IrBinaryOp.NotEqual, new IrArrayLength(Ref(a)), new IrArrayLength(Ref(b))), Return(Bool(false))),
        new IrDeclare(i, Int32(0)),
        Counted(i, new IrArrayLength(Ref(a)), If(Binary(IrBinaryOp.NotEqual, Convert(IrType.I32, new IrArrayLoad(Ref(a), Ref(i))), Convert(IrType.I32, new IrArrayLoad(Ref(b), Ref(i)))), Return(Bool(false)))),
        Return(Bool(true)));
    });

  /// <summary><c>a == b</c> of two lists: the same length and every element <c>==</c> to its counterpart.</summary>
  public IrCall ListEqual(IrExpr left, IrExpr right, IrType listType, Func<IrExpr, IrExpr, IrExpr> equal)
    => this.Call($"PyListEqual_{Name(listType)}", IrType.Bool, [left, right], name => {
      var (a, b, i) = (Local("a", listType), Local("b", listType), Local("i", IrType.I32));
      var element = listType.Element!;
      return Function(name, IrType.Bool, [a, b], [i],
        If(Binary(IrBinaryOp.NotEqual, Count(Ref(a)), Count(Ref(b))), Return(Bool(false))),
        new IrDeclare(i, Int32(0)),
        new IrFor(And(Less(Ref(i), Count(Ref(a))), Less(Ref(i), Count(Ref(b)))), Block(If(Not(equal(Get(Ref(a), Ref(i), element), Get(Ref(b), Ref(i), element))), Return(Bool(false)))),
          Block(Assign(i, Plus(Ref(i), Int32(1))))),
        Return(Equal(Count(Ref(a)), Count(Ref(b)))));
    });

  /// <summary><c>a == b</c> of two references that may be <c>None</c>: <c>None</c> equals only <c>None</c>, two objects compare by the equality given.</summary>
  public IrCall OptionalEqual(IrExpr left, IrExpr right, IrType type, Func<IrExpr, IrExpr, IrExpr> equal)
    => this.Call($"PyOptionalEqual_{Name(type)}", IrType.Bool, [left, right], name => {
      var (a, b) = (Local("a", type), Local("b", type));
      return Function(name, IrType.Bool, [a, b], [],
        If(Or(new IrIsNull(Ref(a)), new IrIsNull(Ref(b))), Return(new IrReferenceEquals(Ref(a), Ref(b)))),
        Return(equal(Ref(a), Ref(b))));
    });

  /// <summary><c>a == b</c> of two tuples of one type: every item <c>==</c> to its counterpart.</summary>
  public IrCall TupleEqual(IrExpr left, IrExpr right, IrType tupleType, Func<IrExpr, IrExpr, IrExpr> equal)
    => this.Call($"PyTupleEqual_{Name(tupleType)}", IrType.Bool, [left, right], name => {
      var (a, b) = (Local("a", tupleType), Local("b", tupleType));
      return Function(name, IrType.Bool, [a, b], [], Return(equal(Ref(a), Ref(b))));
    });

  /// <summary><c>a + b</c> of two lists: a new list of the elements of both.</summary>
  public IrCall ListConcat(IrExpr left, IrExpr right, IrType listType)
    => this.Call($"PyListConcat_{Name(listType)}", listType, [left, right], name => {
      var (a, b, result, i) = (Local("a", listType), Local("b", listType), Local("result", listType), Local("i", IrType.I32));
      var (na, nb) = (Local("na", IrType.I32), Local("nb", IrType.I32));
      return Function(name, listType, [a, b], [result, i, na, nb],
        new IrDeclare(result, NewList(listType)),
        new IrDeclare(na, Count(Ref(a))),
        new IrDeclare(nb, Count(Ref(b))),
        new IrDeclare(i, Int32(0)),
        Counted(i, Ref(na), Add(Ref(result), Get(Ref(a), Ref(i), listType.Element!))),
        Counted(i, Ref(nb), Add(Ref(result), Get(Ref(b), Ref(i), listType.Element!))),
        Return(Ref(result)));
    });

  /// <summary><c>a + b</c> of two byte arrays: a new array of the bytes of both.</summary>
  public IrCall BytesConcat(IrExpr left, IrExpr right)
    => this.Call("PyBytesConcat", IrType.Array(IrType.U8), [left, right], name => {
      var bytes = IrType.Array(IrType.U8);
      var (a, b, result, i) = (Local("a", bytes), Local("b", bytes), Local("result", bytes), Local("i", IrType.I32));
      return Function(name, bytes, [a, b], [result, i],
        new IrDeclare(result, new IrNewArray(bytes, Plus(new IrArrayLength(Ref(a)), new IrArrayLength(Ref(b))))),
        new IrDeclare(i, Int32(0)),
        Counted(i, new IrArrayLength(Ref(a)), new IrStore(Ref(result), Ref(i), new IrArrayLoad(Ref(a), Ref(i)))),
        Counted(i, new IrArrayLength(Ref(b)), new IrStore(Ref(result), Plus(Ref(i), new IrArrayLength(Ref(a))), new IrArrayLoad(Ref(b), Ref(i)))),
        Return(Ref(result)));
    });

  /// <summary>
  /// <c>s * n</c> and <c>n * s</c>: a new sequence of the elements of <c>s</c> <c>n</c> times, the same
  /// elements each time; empty for <c>n ≤ 0</c>; <c>OverflowError</c> where the result would exceed the
  /// largest length, where CPython runs out of memory.
  /// </summary>
  public IrCall Repeat(IrExpr sequence, IrExpr times, IrType type, bool sequenceFirst) {
    var key = $"PyRepeat_{Name(type)}{(sequenceFirst ? "" : "_CountFirst")}";
    return this.Call(key, type, sequenceFirst ? [sequence, times] : [times, sequence], name => {
      var (s, n, length, k, i) = (Local("sequence", type), Local("times", IrType.I64), Local("length", IrType.I64), Local("k", IrType.I64), Local("i", IrType.I32));
      var result = Local("result", type);
      var body = new List<IrStmt> {
        If(Binary(IrBinaryOp.LessOrEqual, Ref(n), Int(0)), Assign(n, Int(0))),
        new IrDeclare(length, Convert(IrType.I64, type.IsList ? Count(Ref(s)) : type.IsString ? Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, Ref(s)) : new IrArrayLength(Ref(s)))),
        If(And(Binary(IrBinaryOp.Greater, Ref(length), Int(0)), Binary(IrBinaryOp.Greater, Ref(n), Binary(IrBinaryOp.Divide, Int(int.MaxValue), Ref(length)))),
          Raise(IrExceptionClass.OverflowException, "repeated sequence is too long"))
      };
      if (type.IsString) {
        body.Add(Return(Choose(Equal(Ref(n), Int(0)), new IrStrConst(""), Intrinsic(IrIntrinsicOp.StrRepeat, IrType.String, Ref(s), Convert(IrType.I32, Ref(n))))));
        return Function(name, type, sequenceFirst ? [s, n] : [n, s], [length], body);
      }

      body.Add(new IrDeclare(k, Int(0)));
      body.Add(new IrDeclare(i, Int32(0)));
      if (type.IsList) {
        body.Add(new IrDeclare(result, NewList(type)));
        body.Add(new IrFor(Less(Ref(k), Ref(n)), Block(Counted(i, Convert(IrType.I32, Ref(length)), Add(Ref(result), Get(Ref(s), Ref(i), type.Element!)))), Block(Assign(k, Plus(Ref(k), Int(1))))));
      } else {
        body.Add(new IrDeclare(result, new IrNewArray(type, Convert(IrType.I32, Binary(IrBinaryOp.Multiply, Ref(n), Ref(length))))));
        body.Add(new IrFor(Less(Ref(k), Ref(n)), Block(Counted(i, Convert(IrType.I32, Ref(length)),
          new IrStore(Ref(result), Plus(Convert(IrType.I32, Binary(IrBinaryOp.Multiply, Ref(k), Ref(length))), Ref(i)), new IrArrayLoad(Ref(s), Ref(i))))), Block(Assign(k, Plus(Ref(k), Int(1))))));
      }

      body.Add(Return(Ref(result)));
      return Function(name, type, sequenceFirst ? [s, n] : [n, s], [length, k, i, result], body);
    });
  }

  /// <summary><c>a.pop()</c>: the last element, removed; <c>IndexError</c> for an empty list.</summary>
  public IrCall Pop(IrExpr list, IrType listType)
    => this.Call($"PyPop_{Name(listType)}", listType.Element!, [list], name => {
      var (l, last) = (Local("list", listType), Local("last", listType.Element!));
      return Function(name, listType.Element!, [l], [last],
        If(Equal(Count(Ref(l)), Int32(0)), Raise(IrExceptionClass.IndexOutOfRangeException, "pop from empty list")),
        new IrDeclare(last, Get(Ref(l), Binary(IrBinaryOp.Subtract, Count(Ref(l)), Int32(1)), listType.Element!)),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [Ref(l), Binary(IrBinaryOp.Subtract, Count(Ref(l)), Int32(1))])),
        Return(Ref(last)));
    });

  /// <summary><c>a.extend(b)</c>: the elements <c>b</c> has when the call starts appended, so that <c>a.extend(a)</c> doubles <c>a</c>.</summary>
  public IrCall Extend(IrExpr list, IrExpr other, IrType listType)
    => this.Call($"PyExtend_{Name(listType)}", IrType.Void, [list, other], name => {
      var (a, b, n, i) = (Local("list", listType), Local("other", listType), Local("n", IrType.I32), Local("i", IrType.I32));
      return Function(name, IrType.Void, [a, b], [n, i],
        new IrDeclare(n, Count(Ref(b))),
        new IrDeclare(i, Int32(0)),
        Counted(i, Ref(n), Add(Ref(a), Get(Ref(b), Ref(i), listType.Element!))));
    });

  /// <summary><c>a.copy()</c> and <c>a[:]</c>: a new list of the same elements.</summary>
  public IrCall ListCopy(IrExpr list, IrType listType)
    => this.Call($"PyListCopy_{Name(listType)}", listType, [list], name => {
      var (a, result, i) = (Local("list", listType), Local("result", listType), Local("i", IrType.I32));
      return Function(name, listType, [a], [result, i],
        new IrDeclare(result, NewList(listType)),
        new IrDeclare(i, Int32(0)),
        Counted(i, Count(Ref(a)), Add(Ref(result), Get(Ref(a), Ref(i), listType.Element!))),
        Return(Ref(result)));
    });

  /// <summary>
  /// <c>a[i:j:k]</c> of a list, bytes, a table or a str: the bounds as <c>slice.indices</c> clamps them
  /// - an absent start is the first element (the last for a negative step), a negative bound counts
  /// from the end, a bound beyond either end stops there - a zero step raises <c>ValueError</c>.
  /// </summary>
  public IrCall Slice(IrExpr sequence, IrType type, IrType resultType, IReadOnlyList<IrExpr> bounds)
    => this.Call($"PySlice_{Name(type)}", resultType, [sequence, .. bounds], name => {
      var s = Local("sequence", type);
      var (hasStart, start, hasStop, stop, hasStep, step) = (Local("hasStart", IrType.Bool), Local("start", IrType.I64), Local("hasStop", IrType.Bool), Local("stop", IrType.I64), Local("hasStep", IrType.Bool), Local("step", IrType.I64));
      var (length, count, k, at) = (Local("length", IrType.I64), Local("count", IrType.I64), Local("k", IrType.I64), Local("at", IrType.I64));
      var (result, offsets, i, text) = (Local("result", resultType), Local("offsets", IrType.Array(IrType.I32)), Local("i", IrType.I32), Local("text", IrType.String));
      IrStmt Adjust(IrLocal bound) => Block(
        If(Less(Ref(bound), Int(0)),
          Assign(bound, Plus(Ref(bound), Ref(length))),
          If(Less(Ref(bound), Int(0)), Assign(bound, Choose(Less(Ref(step), Int(0)), Int(-1), Int(0))))),
        If(Binary(IrBinaryOp.GreaterOrEqual, Ref(bound), Ref(length)), Assign(bound, Choose(Less(Ref(step), Int(0)), Binary(IrBinaryOp.Subtract, Ref(length), Int(1)), Ref(length)))));
      var body = new List<IrStmt> {
        If(Not(Ref(hasStep)), Assign(step, Int(1))),
        If(Equal(Ref(step), Int(0)), Raise(IrExceptionClass.ArgumentException, "slice step cannot be zero")),
        If(Less(Ref(step), Int(-long.MaxValue)), Assign(step, Int(-long.MaxValue))),
        If(Not(Ref(hasStart)), Assign(start, Choose(Less(Ref(step), Int(0)), Int(long.MaxValue), Int(0)))),
        If(Not(Ref(hasStop)), Assign(stop, Choose(Less(Ref(step), Int(0)), Int(long.MinValue), Int(long.MaxValue))))
      };
      var locals = new List<IrLocal> { length, count, k, at, result };
      if (type.IsString) {
        // The code-unit offset of every code point, and of the end.
        locals.Add(i);
        locals.Add(offsets);
        locals.Add(text);
        body.Add(new IrDeclare(length, this.CodePointCount(Ref(s))));
        body.Add(new IrDeclare(offsets, new IrNewArray(IrType.Array(IrType.I32), Plus(Convert(IrType.I32, Ref(length)), Int32(1)))));
        body.Add(new IrDeclare(i, Int32(0)));
        body.Add(new IrDeclare(at, Int(0)));
        body.Add(new IrFor(Less(Ref(i), Convert(IrType.I32, Ref(length))), Block(
          new IrStore(Ref(offsets), Ref(i), Convert(IrType.I32, Ref(at))),
          Assign(at, Plus(Ref(at), Convert(IrType.I64, Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(s), Convert(IrType.I32, Ref(at))))))),
          Block(Assign(i, Plus(Ref(i), Int32(1))))));
        body.Add(new IrStore(Ref(offsets), Convert(IrType.I32, Ref(length)), Convert(IrType.I32, Ref(at))));
      } else {
        body.Add(new IrDeclare(length, Convert(IrType.I64, type.IsList ? Count(Ref(s)) : new IrArrayLength(Ref(s)))));
        body.Add(new IrDeclare(at, Int(0)));
      }

      body.Add(Adjust(start));
      body.Add(Adjust(stop));
      body.Add(new IrDeclare(count, Choose(Less(Ref(step), Int(0)),
        Choose(Less(Ref(stop), Ref(start)), Plus(Binary(IrBinaryOp.Divide, Binary(IrBinaryOp.Subtract, Binary(IrBinaryOp.Subtract, Ref(start), Ref(stop)), Int(1)), Binary(IrBinaryOp.Subtract, Int(0), Ref(step))), Int(1)), Int(0)),
        Choose(Less(Ref(start), Ref(stop)), Plus(Binary(IrBinaryOp.Divide, Binary(IrBinaryOp.Subtract, Binary(IrBinaryOp.Subtract, Ref(stop), Ref(start)), Int(1)), Ref(step)), Int(1)), Int(0)))));
      body.Add(new IrDeclare(k, Int(0)));
      body.Add(Assign(at, Ref(start)));
      var next = Block(Assign(k, Plus(Ref(k), Int(1))), Assign(at, Plus(Ref(at), Ref(step))));
      var position = Convert(IrType.I32, Ref(at));
      if (type.IsString) {
        body.Add(new IrDeclare(result, new IrStrConst("")));
        body.Add(new IrFor(Less(Ref(k), Ref(count)), Block(Assign(result, Intrinsic(IrIntrinsicOp.StrConcat, IrType.String, Ref(result),
          Intrinsic(IrIntrinsicOp.StrSubstring, IrType.String, Ref(s), new IrArrayLoad(Ref(offsets), position),
            Binary(IrBinaryOp.Subtract, new IrArrayLoad(Ref(offsets), Plus(position, Int32(1))), new IrArrayLoad(Ref(offsets), position)))))), next));
      } else if (type.IsList || resultType.IsList) {
        IrExpr element = type.IsList ? Get(Ref(s), position, type.Element!) : new IrArrayLoad(Ref(s), position);
        body.Add(new IrDeclare(result, NewList(resultType)));
        body.Add(new IrFor(Less(Ref(k), Ref(count)), Block(Add(Ref(result), element)), next));
      } else {
        body.Add(new IrDeclare(result, new IrNewArray(resultType, Convert(IrType.I32, Ref(count)))));
        body.Add(new IrFor(Less(Ref(k), Ref(count)), Block(new IrStore(Ref(result), Convert(IrType.I32, Ref(k)), new IrArrayLoad(Ref(s), position))), next));
      }

      body.Add(Return(Ref(result)));
      return Function(name, resultType, [s, hasStart, start, hasStop, stop, hasStep, step], locals, body);
    });

  /// <summary>The elements of a list as a sequence, read by an index that is tested against the length as it is at every step, as CPython's list iterator does.</summary>
  public IrCall ListIterator(IrExpr list, IrType listType)
    => this.Call($"PyListIterator_{Name(listType)}", IrType.Sequence(listType.Element!, false), [list], name => {
      var (l, i) = (Local("list", listType), Local("i", IrType.I32));
      return Function(name, IrType.Sequence(listType.Element!, false), [l], [i],
        new IrDeclare(i, Int32(0)),
        new IrWhile(Less(Ref(i), Count(Ref(l))), Block(new IrYield(Get(Ref(l), Ref(i), listType.Element!)), Assign(i, Plus(Ref(i), Int32(1))))));
    });

  /// <summary>The elements of a list from the last down, as a sequence: CPython's reverse iterator, which stops where the list shrank below its index.</summary>
  public IrCall ReversedIterator(IrExpr list, IrType listType)
    => this.Call($"PyReversedIterator_{Name(listType)}", IrType.Sequence(listType.Element!, false), [list], name => {
      var (l, i) = (Local("list", listType), Local("i", IrType.I32));
      return Function(name, IrType.Sequence(listType.Element!, false), [l], [i],
        new IrDeclare(i, Binary(IrBinaryOp.Subtract, Count(Ref(l)), Int32(1))),
        new IrWhile(And(Binary(IrBinaryOp.GreaterOrEqual, Ref(i), Int32(0)), Less(Ref(i), Count(Ref(l)))),
          Block(new IrYield(Get(Ref(l), Ref(i), listType.Element!)), Assign(i, Binary(IrBinaryOp.Subtract, Ref(i), Int32(1))))));
    });

  /// <summary>The code points of a str as a sequence of strs.</summary>
  public IrCall CodePointIterator(IrExpr text)
    => this.Call("PyStrIterator", IrType.Sequence(IrType.String, false), [text], name => {
      var (s, at, width) = (Local("text", IrType.String), Local("at", IrType.I32), Local("width", IrType.I32));
      return Function(name, IrType.Sequence(IrType.String, false), [s], [at, width],
        new IrDeclare(at, Int32(0)),
        new IrDeclare(width, Int32(0)),
        new IrWhile(Less(Ref(at), Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, Ref(s))), Block(
          Assign(width, Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(s), Ref(at))),
          new IrYield(Intrinsic(IrIntrinsicOp.StrSubstring, IrType.String, Ref(s), Ref(at), Ref(width))),
          Assign(at, Plus(Ref(at), Ref(width))))));
    });

  /// <summary><c>range(start, stop, step)</c> as a sequence: <c>ValueError</c> for a zero step when it is made, the numbers when it is enumerated.</summary>
  public IrCall Range(IrExpr start, IrExpr stop, IrExpr step) {
    // The step is checked where the range is made, as an argument: the iterator's body runs only when it is enumerated.
    var checkedStep = step is IrBigConst { Value.IsZero: false } ? step : this.Call("PyRangeStep", IrType.BigInt, [step], name => {
      var c = Local("step", IrType.BigInt);
      return Function(name, IrType.BigInt, [c], [], If(Equal(Ref(c), Big(0)), Raise(IrExceptionClass.ArgumentException, "range() arg 3 must not be zero")), Return(Ref(c)));
    });
    return this.Call("PyRange", IrType.Sequence(IrType.BigInt, false), [start, stop, checkedStep], name => {
      var (a, b, c, k, more) = (Local("start", IrType.BigInt), Local("stop", IrType.BigInt), Local("step", IrType.BigInt), Local("k", IrType.BigInt), Local("more", IrType.Bool));
      // The numbers are exact at every magnitude, so whether another one follows is the test of the bound by the sign of the step.
      IrExpr Inside() => Choose(Binary(IrBinaryOp.Greater, Ref(c), Big(0)), Less(Ref(k), Ref(b)), Binary(IrBinaryOp.Greater, Ref(k), Ref(b)));
      return Function(name, IrType.Sequence(IrType.BigInt, false), [a, b, c], [k, more],
        new IrDeclare(k, Ref(a)),
        new IrDeclare(more, Inside()),
        new IrWhile(Ref(more), Block(
          new IrYield(Ref(k)),
          Assign(k, Binary(IrBinaryOp.Add, Ref(k), Ref(c))),
          Assign(more, Inside()))));
    });
  }

  /// <summary><c>enumerate(it, start)</c> as a sequence of pairs: the count goes up by one per element, exact; the pair holds it as a tuple holds an <c>int</c>.</summary>
  public IrCall Enumerate(IrExpr sequence, IrExpr start, IrType sequenceType, IrType pairType)
    => this.Call($"PyEnumerate_{Name(pairType)}", IrType.Sequence(pairType, false), [sequence, start], name => {
      var (s, n, item, first) = (Local("sequence", sequenceType), Local("n", IrType.BigInt), Local("item", sequenceType.Element!), Local("first", IrType.Bool));
      return Function(name, IrType.Sequence(pairType, false), [s, n], [item, first],
        new IrDeclare(first, Bool(true)),
        new IrForEachSequence(item, Ref(s), Block(
          If(Not(Ref(first)), Assign(n, Binary(IrBinaryOp.Add, Ref(n), Big(1)))),
          Assign(first, Bool(false)),
          new IrYield(MakeStruct(pairType, [Ref(n), Ref(item)])))));
    });

  /// <summary><c>sum(it, start)</c> of integers: a left fold from <c>start</c>, every partial sum exact.</summary>
  public IrCall Sum(IrExpr sequence, IrExpr start)
    => this.Call($"PySum_{Name(sequence.Type)}", IrType.BigInt, [sequence, start], name => {
      var (s, total, item) = (Local("sequence", sequence.Type), Local("total", IrType.BigInt), Local("item", IrType.BigInt));
      return Function(name, IrType.BigInt, [s, total], [item], new IrForEachSequence(item, Ref(s), Block(Assign(total, Binary(IrBinaryOp.Add, Ref(total), Ref(item))))), Return(Ref(total)));
    });

  /// <summary>
  /// <c>min</c> or <c>max</c> of a sequence: every key computed once, in order; the first element whose
  /// key is smaller (larger) than the one held replaces it; an empty sequence gives the default or
  /// raises <c>ValueError</c>.
  /// </summary>
  public IrCall Extreme(IrExpr sequence, IrType element, IrExpr? key, IrType keyType, bool minimum, IrExpr? fallback, Func<IrExpr, IrExpr, IrExpr> less, string message) {
    var sequenceType = sequence.Type.IsLazySequence ? sequence.Type : IrType.Sequence(element, true);
    var name = $"Py{(minimum ? "Min" : "Max")}_{Name(element)}{(key is null ? "" : "_By_" + Name(keyType))}{(fallback is null ? "" : "_Or")}";
    List<IrExpr> arguments = [sequence];
    if (key is not null)
      arguments.Add(key);

    if (fallback is not null)
      arguments.Add(fallback);

    return this.Call(name + "_" + Name(sequenceType), element, arguments, made => {
      var s = Local("sequence", sequenceType);
      var function = Local("key", IrType.Function(keyType, element));
      var otherwise = Local("fallback", element);
      var (item, best, value, bestKey, found) = (Local("item", element), Local("best", element), Local("value", keyType), Local("bestKey", keyType), Local("found", IrType.Bool));
      List<IrLocal> parameters = [s];
      if (key is not null)
        parameters.Add(function);

      if (fallback is not null)
        parameters.Add(otherwise);

      IrExpr KeyOf(IrExpr x) => key is null ? x : new IrInvoke(keyType, Ref(function), [x]);
      var better = minimum ? less(Ref(value), Ref(bestKey)) : less(Ref(bestKey), Ref(value));
      return Function(made, element, parameters, [item, best, value, bestKey, found],
        new IrDeclare(found, Bool(false)),
        new IrDeclare(best, DefaultOf(element)),
        new IrDeclare(bestKey, DefaultOf(keyType)),
        new IrForEachSequence(item, Ref(s), Block(
          new IrDeclare(value, KeyOf(Ref(item))),
          new IrIf(Binary(IrBinaryOp.LogicalOr, Not(Ref(found)), better), Block(Assign(best, Ref(item)), Assign(bestKey, Ref(value)), Assign(found, Bool(true))), null))),
        If(Not(Ref(found)), fallback is null ? Raise(IrExceptionClass.ArgumentException, message) : Return(Ref(otherwise))),
        Return(Ref(best)));
    });
  }

  /// <summary>
  /// <c>sorted(it, key=…, reverse=…)</c>: the elements into a new list, every key computed once and in
  /// order, then a stable merge sort by Python's <c>&lt;</c> of the keys; with <c>reverse</c> the
  /// comparison is turned round, so that equal elements keep their order, as CPython keeps it.
  /// </summary>
  public IrCall Sorted(IrExpr sequence, IrType listType, IrExpr? key, IrType keyType, IrExpr reverse, Func<IrExpr, IrExpr, IrExpr> less) {
    var element = listType.Element!;
    var sequenceType = sequence.Type;
    var keyList = IrType.List(keyType);
    List<IrExpr> arguments = [sequence];
    if (key is not null)
      arguments.Add(key);

    arguments.Add(reverse);
    return this.Call($"PySorted_{Name(element)}{(key is null ? "" : "_By_" + Name(keyType))}_{Name(sequenceType)}", listType, arguments, name => {
      var s = Local("sequence", sequenceType);
      var function = Local("key", IrType.Function(keyType, element));
      var descending = Local("reverse", IrType.Bool);
      var (items, keys, order, buffer, result) = (Local("items", listType), Local("keys", keyList), Local("order", IrType.Array(IrType.I32)), Local("buffer", IrType.Array(IrType.I32)), Local("result", listType));
      var (n, width, low, middle, high, i, j, k) = (Local("n", IrType.I32), Local("width", IrType.I32), Local("low", IrType.I32), Local("middle", IrType.I32), Local("high", IrType.I32),
        Local("i", IrType.I32), Local("j", IrType.I32), Local("k", IrType.I32));
      var swap = Local("swap", IrType.Array(IrType.I32));
      List<IrLocal> parameters = [s];
      if (key is not null)
        parameters.Add(function);

      parameters.Add(descending);
      IrExpr KeyAt(IrExpr index) => Get(Ref(keys), new IrArrayLoad(Ref(order), index), keyType);
      // The right element goes first only where its key is strictly before the left one's: a merge that keeps equal keys in order.
      var rightFirst = Choose(Ref(descending), less(KeyAt(Ref(i)), KeyAt(Ref(j))), less(KeyAt(Ref(j)), KeyAt(Ref(i))));
      IrExpr Min(IrExpr a, IrExpr b) => Choose(Less(a, b), a, b);
      return Function(name, listType, parameters, [items, keys, order, buffer, result, n, width, low, middle, high, i, j, k, swap],
        new IrDeclare(items, new IrSequence(IrSequenceOp.ToList, listType, [Ref(s)])),
        new IrDeclare(n, Count(Ref(items))),
        new IrDeclare(keys, NewList(keyList)),
        new IrDeclare(i, Int32(0)),
        Counted(i, Ref(n), Add(Ref(keys), key is null ? Get(Ref(items), Ref(i), element) : new IrInvoke(keyType, Ref(function), [Get(Ref(items), Ref(i), element)]))),
        new IrDeclare(order, new IrNewArray(IrType.Array(IrType.I32), Ref(n))),
        new IrDeclare(buffer, new IrNewArray(IrType.Array(IrType.I32), Ref(n))),
        new IrDeclare(swap, new IrNull(IrType.Array(IrType.I32))),
        Counted(i, Ref(n), new IrStore(Ref(order), Ref(i), Ref(i))),
        new IrDeclare(width, Int32(1)),
        new IrDeclare(low, Int32(0)),
        new IrDeclare(middle, Int32(0)),
        new IrDeclare(high, Int32(0)),
        new IrDeclare(j, Int32(0)),
        new IrDeclare(k, Int32(0)),
        new IrWhile(Less(Ref(width), Ref(n)), Block(
          Assign(low, Int32(0)),
          new IrWhile(Less(Ref(low), Ref(n)), Block(
            Assign(middle, Min(Plus(Ref(low), Ref(width)), Ref(n))),
            Assign(high, Min(Plus(Ref(middle), Ref(width)), Ref(n))),
            Assign(i, Ref(low)),
            Assign(j, Ref(middle)),
            Assign(k, Ref(low)),
            new IrWhile(Less(Ref(k), Ref(high)), Block(
              new IrIf(And(Less(Ref(j), Ref(high)), Or(Binary(IrBinaryOp.GreaterOrEqual, Ref(i), Ref(middle)), rightFirst)),
                Block(new IrStore(Ref(buffer), Ref(k), new IrArrayLoad(Ref(order), Ref(j))), Assign(j, Plus(Ref(j), Int32(1)))),
                Block(new IrStore(Ref(buffer), Ref(k), new IrArrayLoad(Ref(order), Ref(i))), Assign(i, Plus(Ref(i), Int32(1))))),
              Assign(k, Plus(Ref(k), Int32(1))))),
            Assign(low, Ref(high)))),
          Assign(swap, Ref(order)),
          Assign(order, Ref(buffer)),
          Assign(buffer, Ref(swap)),
          Assign(width, Binary(IrBinaryOp.Multiply, Ref(width), Int32(2))))),
        new IrDeclare(result, NewList(listType)),
        Counted(i, Ref(n), Add(Ref(result), Get(Ref(items), new IrArrayLoad(Ref(order), Ref(i)), element))),
        Return(Ref(result)));
    });
  }

  /// <summary>Python's <c>a &lt; b</c> of two strs: by code points, a proper prefix first.</summary>
  public IrCall StrLess(IrExpr left, IrExpr right)
    => this.Call("PyStrLess", IrType.Bool, [left, right], name => {
      var (a, b, i, j, x, y) = (Local("a", IrType.String), Local("b", IrType.String), Local("i", IrType.I32), Local("j", IrType.I32), Local("x", IrType.I32), Local("y", IrType.I32));
      return Function(name, IrType.Bool, [a, b], [i, j, x, y],
        new IrDeclare(i, Int32(0)),
        new IrDeclare(j, Int32(0)),
        new IrDeclare(x, Int32(0)),
        new IrDeclare(y, Int32(0)),
        new IrWhile(And(Less(Ref(i), Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, Ref(a))), Less(Ref(j), Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, Ref(b)))), Block(
          Assign(x, Intrinsic(IrIntrinsicOp.StrCodePointAt, IrType.I32, Ref(a), Ref(i))),
          Assign(y, Intrinsic(IrIntrinsicOp.StrCodePointAt, IrType.I32, Ref(b), Ref(j))),
          If(Binary(IrBinaryOp.NotEqual, Ref(x), Ref(y)), Return(Less(Ref(x), Ref(y)))),
          Assign(i, Plus(Ref(i), Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(a), Ref(i)))),
          Assign(j, Plus(Ref(j), Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(b), Ref(j)))))),
        Return(And(Binary(IrBinaryOp.GreaterOrEqual, Ref(i), Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, Ref(a))), Less(Ref(j), Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, Ref(b))))));
    });

  /// <summary>Python's <c>a &lt; b</c> of two tuples of one type: by the first items that are not <c>==</c>.</summary>
  public IrCall TupleLess(IrExpr left, IrExpr right, IrType tupleType, IReadOnlyList<Func<IrExpr, IrExpr, IrExpr>> less, IReadOnlyList<Func<IrExpr, IrExpr, IrExpr>> equal)
    => this.Call($"PyTupleLess_{Name(tupleType)}", IrType.Bool, [left, right], name => {
      var (a, b) = (Local("a", tupleType), Local("b", tupleType));
      var body = new List<IrStmt>();
      var fields = tupleType.StructType!.Fields;
      for (var index = 0; index < fields.Count; ++index) {
        var (x, y) = (new IrFieldLoad(Ref(a), fields[index]), new IrFieldLoad(Ref(b), fields[index]));
        body.Add(If(Not(equal[index](x, y)), Return(less[index](x, y))));
      }

      body.Add(Return(Bool(false)));
      return Function(name, IrType.Bool, [a, b], [], body);
    });

}
