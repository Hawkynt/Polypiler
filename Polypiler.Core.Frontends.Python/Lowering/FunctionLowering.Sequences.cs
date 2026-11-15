using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.Frontends.Python.Typing;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// Lists, tuples, slices and the built-ins over sequences. A list is a list of the IR, a reference
/// with an identity, so that <c>b = a</c> aliases and <c>[row] * h</c> repeats the same row; bytes are
/// an array of <c>u8</c>; a tuple is a struct, which no one can change. An index counts from the end
/// where it is negative and raises <c>IndexError</c> outside the sequence; a slice clamps its bounds
/// as <c>slice.indices</c> does and never raises, except <c>ValueError</c> for a zero step.
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary>
  /// <c>[e1, e2, …]</c>: a new list, its elements evaluated left to right; an empty one takes its
  /// element type from the place it goes to.
  /// </summary>
  private (IrExpr, PyType) ListDisplay(PyNode display, PyType? expected) {
    var type = this.TypeOf(display);
    if (type.IsIncomplete && expected is { Kind: PyKind.List } && PyTypes.Join(type, expected) is { IsIncomplete: false } joined)
      type = joined;

    if (type.IsIncomplete)
      throw this.Refuse(display, PythonFrontend.MissingTypeDescriptor, $"the type of the elements of '{this.Quote(display)}' is not known; add a type hint such as 'list[int]' to what holds it");

    var element = type.Element!;
    var values = display.Nodes("elts").Select(item => this.Coerce(this.Value(item, element), element, item)).ToList();
    return (this.ListOf(values, type), type);
  }

  /// <summary>A new list of values, evaluated in order.</summary>
  private IrExpr ListOf(IReadOnlyList<IrExpr> values, PyType type) {
    var listType = PyTypes.Ir(type);
    var element = listType.Element!;
    if (values.Count == 0)
      return new IrCollection(IrCollectionOp.ListNew, listType, []);

    // An array holds numbers, strings and structs; a list of lists or of objects is built by appending.
    if (IrType.Array(element).IsStorable)
      return new IrCollection(IrCollectionOp.ListFromArray, listType, [Intrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(element), values)]);

    return this.Effect(this.Helpers.ListOf(values, listType));
  }

  /// <summary><c>(e1, e2)</c>: a new tuple, its items evaluated left to right.</summary>
  private (IrExpr, PyType) TupleDisplay(PyNode display, PyType? expected) {
    var items = display.Nodes("elts");
    var types = new List<PyType>();
    var values = new List<IrExpr>();
    for (var index = 0; index < items.Count; ++index) {
      var wanted = expected is { Kind: PyKind.Tuple } tuple && tuple.Items.Count == items.Count ? tuple.Items[index] : null;
      var value = this.Value(items[index], wanted);
      var type = wanted is not null && !wanted.IsIncomplete ? wanted : value.Type;
      values.Add(this.Coerce(value, type, items[index]));
      types.Add(type);
    }

    var result = PyType.Tuple(types);
    if (types.Any(type => !IsTupleItem(type)))
      throw this.Unsupported(display, $"a tuple of the types {string.Join(", ", types)}; a tuple holds numbers, bytes, enum members, objects and tuples of them");

    return (MakeStruct(PyTypes.Ir(result), values), result);
  }

  /// <summary>Whether a value of a type can be an item of a tuple: what a field of a struct can hold.</summary>
  private static bool IsTupleItem(PyType type) => type.IsNumber || type.IsBytes || type.Kind is PyKind.Tuple or PyKind.Enum or PyKind.Table
    || (type.Kind == PyKind.Optional && type.Element!.IsNumber);

  /// <summary><c>a[i]</c> and <c>a[i:j:k]</c>.</summary>
  private (IrExpr, PyType) Subscript(PyNode subscript) {
    var index = subscript.Node("slice")!;
    var container = this.Value(subscript.Node("value")!);
    if (index.Is("Slice"))
      return this.Slice(subscript, container, index);

    if (container.Type.Kind == PyKind.Tuple) {
      if (Constants.Integer(index) is { } position && position >= -container.Type.Items.Count && position < container.Type.Items.Count) {
        var at = (int)(position < 0 ? position + container.Type.Items.Count : position);
        return (new IrFieldLoad(container.Value, PyTypes.Ir(container.Type).StructType!.Fields[at]), container.Type.Items[at]);
      }

      throw this.Unsupported(subscript, $"the index {this.Quote(index)} of a tuple of {container.Type.Items.Count} items; a tuple is indexed by a constant within it");
    }

    if (container.Type.Kind == PyKind.Dict)
      return this.DictItem(subscript, container, this.Value(index, container.Type.Key));

    if (container.Type.Kind == PyKind.Object) {
      var getter = container.Type.Class!.IrMethods.GetValueOrDefault("__getitem__")
        ?? throw this.Unsupported(subscript, $"{this.Quote(subscript)} of an object of {container.Type.Class.Name}, which has no __getitem__, and raises TypeError");
      var method = container.Type.Class.Method("__getitem__")!;
      var argument = this.Coerce(this.Value(index, method.Parameters[1].Type), method.Parameters[1].Type, index);
      return (this.Effect(new IrMethodCall(PyTypes.Ir(method.Returns), getter, container.Value, [argument])), method.Returns);
    }

    return this.Index(subscript, container, this.Value(index));
  }

  /// <summary>The element at an index of a sequence: a negative index counts from the end, one outside raises <c>IndexError</c>.</summary>
  private (IrExpr Value, PyType Type) Index(PyNode at, (IrExpr Value, PyType Type) container, (IrExpr Value, PyType Type) index) {
    if (container.Type.Kind == PyKind.Dict)
      return this.DictItem(at, container, index);

    var position = ExpressionTypes.Value(index.Type);
    if (!position.IsNumber || position.Kind == PyKind.Float)
      throw this.Unsupported(at, $"an index of the type {index.Type}, which raises TypeError,");

    var integer = this.Integer(this.Plain(index, at)).Item1;
    return container.Type.Kind switch {
      PyKind.List => (this.Effect(this.Helpers.ListGet(container.Value, integer, PyTypes.Ir(container.Type))), container.Type.Element!),
      PyKind.Bytes or PyKind.ByteArray => (this.Effect(this.Helpers.BytesGet(container.Value, integer)), PyType.Int),
      PyKind.Table => (this.Effect(this.Helpers.ArrayGet(container.Value, integer, PyTypes.Ir(container.Type))), container.Type.Element!),
      PyKind.Str => (this.Effect(this.Helpers.StrGet(container.Value, integer)), PyType.Str),
      _ => throw this.Unsupported(at, $"an index of a value of the type {container.Type}")
    };
  }

  /// <summary>The element at an index known to lie within the sequence, as a loop reads it: no check, no conversion of the index.</summary>
  private (IrExpr Value, PyType Type) ElementAt((IrExpr Value, PyType Type) sequence, IrExpr index) {
    switch (sequence.Type.Kind) {
      case PyKind.List:
        return (new IrCollection(IrCollectionOp.ListGet, PyTypes.Ir(sequence.Type.Element!), [sequence.Value, index]), sequence.Type.Element!);
      case PyKind.Bytes or PyKind.ByteArray:
        return (Convert(IrType.I64, new IrArrayLoad(sequence.Value, index)), PyType.Int);
      case PyKind.Table:
        return (new IrArrayLoad(sequence.Value, index), sequence.Type.Element!);
      default:
        // A tuple of items of one type: the item of the index.
        var fields = PyTypes.Ir(sequence.Type).StructType!.Fields;
        IrExpr chosen = new IrFieldLoad(sequence.Value, fields[^1]);
        for (var field = fields.Count - 2; field >= 0; --field)
          chosen = Choose(Equal(index, Int32(field)), new IrFieldLoad(sequence.Value, fields[field]), chosen);

        return (chosen, sequence.Type.Items[0]);
    }
  }

  /// <summary>The number of elements of a sequence as an <c>i32</c>; of a str its code units, which only its emptiness is read from.</summary>
  private IrExpr Length32((IrExpr Value, PyType Type) sequence) => sequence.Type.Kind switch {
    PyKind.List => new IrCollection(IrCollectionOp.ListCount, IrType.I32, [sequence.Value]),
    PyKind.Str => Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, sequence.Value),
    PyKind.Tuple => Int32(sequence.Type.Items.Count),
    PyKind.Dict or PyKind.Set => CollectionCount(sequence),
    _ => new IrArrayLength(sequence.Value)
  };

  /// <summary><c>len(x)</c>: the elements of a sequence, the code points of a str, the <c>__len__</c> of an object.</summary>
  private (IrExpr, PyType) Len(PyNode at, (IrExpr Value, PyType Type) value) {
    switch (value.Type.Kind) {
      case PyKind.Str:
        return (this.Effect(this.Helpers.CodePointCount(value.Value)), PyType.Int);
      case PyKind.List or PyKind.Bytes or PyKind.ByteArray or PyKind.Table or PyKind.Dict or PyKind.Set:
        return (Convert(IrType.I64, this.Length32(value)), PyType.Int);
      case PyKind.Tuple when value.Value is IrLocalRef or IrStructNew { Arguments.Count: 0 }:
        return (Int(value.Type.Items.Count), PyType.Int);
      case PyKind.Object when value.Type.Class!.IrMethods.TryGetValue("__len__", out var length):
        return (this.Effect(new IrMethodCall(PyTypes.Ir(value.Type.Class.Method("__len__")!.Returns), length, value.Value, [])), PyType.Int);
      default:
        throw this.Unsupported(at, $"len of a value of the type {value.Type}, which raises TypeError,");
    }
  }

  /// <summary>The length of an object with <c>__len__</c>, or of a sequence, as an <c>int</c>.</summary>
  private IrExpr Length64((IrExpr Value, PyType Type) value, IrExpr? _) => Exactly(this.Len(this.Function.Node, value)).Value;

  /// <summary><c>a[i] = v</c>: the value first, then the sequence and the index, then the store.</summary>
  private List<IrStmt> StoreElement(PyNode target, (IrExpr Value, PyType Type) value, (IrExpr Value, PyType Type)? heldContainer = null, (IrExpr Value, PyType Type)? heldIndex = null) {
    var result = new List<IrStmt>();
    if (heldContainer is null && value.Value is not (IrConst or IrLocalRef or IrStrConst))
      value = this.Once(value, result);

    var container = heldContainer ?? this.Value(target.Node("value")!);
    var indexNode = target.Node("slice")!;
    switch (container.Type.Kind) {
      case PyKind.List:
        var element = container.Type.Element!;
        var index = heldIndex ?? this.Value(indexNode);
        result.Add(new IrExprStmt(this.Effect(this.Helpers.ListSet(container.Value, this.Integer(this.Plain(index, indexNode)).Item1, this.Coerce(value, element, target), PyTypes.Ir(container.Type)))));
        return result;
      case PyKind.Dict:
        result.Add(this.DictStore(target, container, heldIndex ?? this.Value(indexNode, container.Type.Key), value));
        return result;
      case PyKind.ByteArray:
        var position = heldIndex ?? this.Value(indexNode);
        var stored = ExpressionTypes.Value(value.Type);
        if (stored.Kind is not (PyKind.Int or PyKind.Bool))
          throw this.Unsupported(target, $"storing a value of the type {value.Type} into a bytearray, which raises TypeError,");

        result.Add(new IrExprStmt(this.Effect(this.Helpers.ByteArraySet(container.Value, this.Integer(this.Plain(position, indexNode)).Item1, this.Integer(this.Plain(value, target)).Item1))));
        return result;
      case PyKind.Object when container.Type.Class!.IrMethods.TryGetValue("__setitem__", out var setter):
        var method = container.Type.Class.Method("__setitem__")!;
        var key = heldIndex ?? this.Value(indexNode, method.Parameters[1].Type);
        result.Add(new IrExprStmt(this.Effect(new IrMethodCall(IrType.Void, setter, container.Value,
          [this.Coerce(key, method.Parameters[1].Type, indexNode), this.Coerce(value, method.Parameters[2].Type, target)]))));
        return result;
      default:
        throw this.Unsupported(target, $"assignment to an element of a value of the type {container.Type}, which raises TypeError,");
    }
  }

  /// <summary><c>a[i:j:k]</c>: a new sequence of the same type, its bounds clamped as <c>slice.indices</c> clamps them; a zero step raises <c>ValueError</c>.</summary>
  private (IrExpr, PyType) Slice(PyNode at, (IrExpr Value, PyType Type) container, PyNode slice) {
    var bounds = new List<IrExpr>();
    foreach (var field in new[] { "lower", "upper", "step" }) {
      if (slice.Node(field) is not { } bound || (bound.Is("Constant") && bound.Text("kind") == "none")) {
        bounds.Add(Bool(false));
        bounds.Add(Int(0));
        continue;
      }

      var value = this.Value(bound);
      if (ExpressionTypes.Value(value.Type) is not { Kind: PyKind.Int or PyKind.Bool })
        throw this.Unsupported(bound, $"a slice bound of the type {value.Type}, which raises TypeError,");

      bounds.Add(Bool(true));
      bounds.Add(this.Integer(this.Plain(value, bound)).Item1);
    }

    var type = container.Type.Kind == PyKind.Table ? PyType.List(container.Type.Element!) : container.Type;
    if (container.Type.Kind is not (PyKind.List or PyKind.Str or PyKind.Bytes or PyKind.ByteArray or PyKind.Table))
      throw this.Refuse(at, PythonFrontend.UnsupportedSliceDescriptor, $"slice form '{this.Quote(at)}' of a value of the type {container.Type} is not supported; slices are imported as values only");

    return (this.Effect(this.Helpers.Slice(container.Value, PyTypes.Ir(container.Type), PyTypes.Ir(type), bounds)), type);
  }

  /// <summary><c>+</c> and <c>*</c> of sequences: a new list, str, bytes or tuple of both, or of one repeated.</summary>
  private (IrExpr Value, PyType Type) SequenceOperation(PyNode node, string op, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right) {
    var type = ExpressionTypes.Binary(op, left.Type, right.Type, node.Node("right") ?? node);
    if (left.Type.Kind is PyKind.Dict or PyKind.Set || right.Type.Kind is PyKind.Dict or PyKind.Set)
      throw this.DictRefusal(node, $"{_symbols.GetValueOrDefault(op, op)} of the types {left.Type} and {right.Type}");

    if (type.Kind == PyKind.Unknown)
      throw this.Unsupported(node, $"{_symbols.GetValueOrDefault(op, op)} of the types {left.Type} and {right.Type}, which raises TypeError,");

    if (op == "Add" && type.Kind == PyKind.Tuple) {
      var setup = new List<IrStmt>();
      var (a, b) = (this.Kept(node, left, this._effects), this.Kept(node, right, this._effects));
      _ = setup;
      var leftFields = PyTypes.Ir(left.Type).StructType!.Fields.Select(field => (IrExpr)new IrFieldLoad(a.Value, field));
      var rightFields = PyTypes.Ir(right.Type).StructType!.Fields.Select(field => (IrExpr)new IrFieldLoad(b.Value, field));
      return (MakeStruct(PyTypes.Ir(type), [.. leftFields, .. rightFields]), type);
    }

    if (op == "Add") {
      return type.Kind switch {
        PyKind.Str => (Intrinsic(IrIntrinsicOp.StrConcat, IrType.String, left.Value, right.Value), type),
        PyKind.List => (this.Effect(this.Helpers.ListConcat(this.Coerce(left, type, node), this.Coerce(right, type, node), PyTypes.Ir(type))), type),
        _ => (this.Effect(this.Helpers.BytesConcat(left.Value, right.Value)), type)
      };
    }

    // Repetition: the sequence and the count in the order written.
    var sequenceFirst = left.Type.Kind is PyKind.List or PyKind.Str or PyKind.Bytes or PyKind.ByteArray;
    var (sequence, count) = sequenceFirst ? (left, right) : (right, left);
    var times = this.Integer(this.Plain(count, node)).Item1;
    return (this.Effect(this.Helpers.Repeat(sequence.Value, times, PyTypes.Ir(type), sequenceFirst)), type);
  }

  /// <summary>The methods of lists: <c>append</c>, <c>pop()</c>, <c>extend</c>, <c>copy</c>; <see langword="null"/> for any other method or receiver.</summary>
  private (IrExpr, PyType)? ListMethod(PyNode call, string name, (IrExpr Value, PyType Type) list) {
    if (list.Type.Kind != PyKind.List)
      return null;

    var arguments = call.Nodes("args");
    var element = list.Type.Element!;
    var listType = PyTypes.Ir(list.Type);
    switch (name) {
      case "append" when arguments.Count == 1 && call.Nodes("keywords").Count == 0:
        var appended = this.Coerce(this.Value(arguments[0], element), element, arguments[0]);
        return (new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [list.Value, appended]), PyType.None);
      case "pop" when arguments.Count == 0:
        return (this.Effect(this.Helpers.Pop(list.Value, listType)), element);
      case "extend" when arguments.Count == 1:
        var source = this.Value(arguments[0]);
        if (ExpressionTypes.Element(source.Type) != element && !(source.Type.Kind == PyKind.List && PyTypes.Join(source.Type, list.Type) is not null))
          throw this.Unsupported(call, $"extend of a list of {element} by a value of the type {source.Type}");

        return (this.Effect(this.Helpers.Extend(list.Value, this.AsList(call, source, list.Type), listType)), PyType.None);
      case "copy" when arguments.Count == 0:
        return (this.Effect(this.Helpers.ListCopy(list.Value, listType)), list.Type);
      case "remove" or "clear":
        throw this.Refuse(call, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{name}' is not supported by the Python frontend");
      default:
        throw this.Refuse(call, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{name}' with these arguments is not supported by the Python frontend");
    }
  }

  /// <summary>A sequence as a list of the import: a list itself, anything else copied into a new one.</summary>
  private IrExpr AsList(PyNode at, (IrExpr Value, PyType Type) source, PyType listType) {
    if (source.Type.Kind == PyKind.List)
      return this.Coerce(source, listType, at);

    return new IrSequence(IrSequenceOp.ToList, PyTypes.Ir(listType), [this.Sequence(at, source)]);
  }

  /// <summary>
  /// A value as a lazy sequence of its elements, in the order a <c>for</c> takes them: a list by an
  /// index that reads the length anew at every step, bytes and tables by their arrays, a str by its
  /// code points, a generator as it is.
  /// </summary>
  private IrExpr Sequence(PyNode at, (IrExpr Value, PyType Type) source) {
    switch (source.Type.Kind) {
      case PyKind.Iterator:
        return source.Value;
      case PyKind.List:
        return this.Effect(this.Helpers.ListIterator(source.Value, PyTypes.Ir(source.Type)));
      case PyKind.Table:
        return this.ExactElements(new IrSequence(IrSequenceOp.FromArray, IrType.Sequence(PyTypes.Held(source.Type.Element!), true), [source.Value]));
      case PyKind.Bytes or PyKind.ByteArray:
        return this.ExactElements(new IrSequence(IrSequenceOp.FromArray, IrType.Sequence(IrType.U8, true), [source.Value]));
      case PyKind.Str:
        return this.Effect(this.Helpers.CodePointIterator(source.Value));
      case PyKind.Dict:
        return this.DictIterator(source, "keys", at).Value;
      case PyKind.Set:
        throw this.DictRefusal(at, "iteration over a set, whose order Python leaves to the hash,", "; sorted(s) iterates it in order");
      case PyKind.Tuple when source.Type.Items.Count > 0 && source.Type.Items.All(item => item == source.Type.Items[0]) && IrType.Array(PyTypes.Held(source.Type.Items[0])).IsStorable:
        var fields = PyTypes.Ir(source.Type).StructType!.Fields;
        var element = PyTypes.Held(source.Type.Items[0]);
        var held = this.Kept(at, source, this._effects);
        return this.ExactElements(new IrSequence(IrSequenceOp.FromArray, IrType.Sequence(element, true), [Intrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(element), [.. fields.Select(field => (IrExpr)new IrFieldLoad(held.Value, field))])]));
      default:
        throw this.Refuse(at, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(at)}' of the type {source.Type} is not supported");
    }
  }

  /// <summary>A sequence of integers of a fixed width - of a table, of bytes, of a tuple - as the exact <c>int</c>s Python's iteration gives; any other as it is.</summary>
  private IrExpr ExactElements(IrExpr sequence) {
    var element = sequence.Type.Element!;
    if (!element.IsInteger || element.IsEnum)
      return sequence;

    var held = new IrLocal(this.Unique("v"), element);
    var exact = IrLambda.Of([held], IrType.BigInt, [], Block(Return(Exact(Ref(held)))));
    return new IrSequence(IrSequenceOp.Select, IrType.Sequence(IrType.BigInt, sequence.Type.Restartable), [sequence, exact]);
  }

  /// <summary>
  /// An iterable as a lazy sequence, the built-ins that make one included: <c>range</c>,
  /// <c>enumerate</c>, <c>zip</c>, <c>reversed</c>, <c>map</c>, <c>filter</c>, an enum class and a
  /// generator expression. Its arguments are evaluated now, its elements when it is enumerated.
  /// </summary>
  private (IrExpr Value, PyType Type) Iterable(PyNode iterable) {
    var arguments = iterable.Nodes("args");
    switch (this.BuiltinCall(iterable)) {
      case "range" when arguments.Count is >= 1 and <= 3 && iterable.Nodes("keywords").Count == 0:
        var bounds = arguments.Select(argument => {
          var value = this.Value(argument);
          return ExpressionTypes.Value(value.Type) is { Kind: PyKind.Int or PyKind.Bool }
            ? this.Integer(this.Plain(value, argument)).Item1
            : throw this.Refuse(argument, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(iterable)}' is not supported: a {value.Type} argument raises TypeError");
        }).ToList();
        var (start, stop, step) = bounds.Count switch {
          1 => (Big(0), bounds[0], (IrExpr)Big(1)),
          2 => (bounds[0], bounds[1], Big(1)),
          _ => (bounds[0], bounds[1], bounds[2])
        };
        return (this.Effect(this.Helpers.Range(start, stop, step)), PyType.Iterator(PyType.Int));
      case "enumerate" when arguments.Count is 1 or 2 && iterable.Nodes("keywords").Count == 0:
        var source = this.Iterable(arguments[0]);
        var first = arguments.Count == 2 ? this.Integer(this.Plain(this.Value(arguments[1]), arguments[1])).Item1 : Big(0);
        var pair = PyType.Tuple([PyType.Int, source.Type.Element!]);
        return (this.Effect(this.Helpers.Enumerate(source.Value, first, PyTypes.Ir(source.Type), PyTypes.Ir(pair))), PyType.Iterator(pair));
      case "zip" when arguments.Count == 2 && iterable.Nodes("keywords").Count == 0:
        var left = this.Iterable(arguments[0]);
        var right = this.Value(arguments[1]);
        var second = right.Type.Kind == PyKind.List ? right
          : throw this.Refuse(arguments[1], PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation 'zip with a second argument of the type {right.Type}' is not supported by the Python frontend");
        var zipped = PyType.Tuple([left.Type.Element!, second.Type.Element!]);
        var (x, y) = (new IrLocal(this.Unique("x"), PyTypes.Ir(left.Type.Element!)), new IrLocal(this.Unique("y"), PyTypes.Ir(second.Type.Element!)));
        var make = IrLambda.Of([x, y], PyTypes.Ir(zipped), [], Block(Return(MakeStruct(PyTypes.Ir(zipped), [Ref(x), Ref(y)]))));
        var rightSequence = new IrSequence(IrSequenceOp.FromList, IrType.Sequence(PyTypes.Ir(second.Type.Element!), true), [second.Value]);
        return (new IrSequence(IrSequenceOp.Zip, IrType.Sequence(PyTypes.Ir(zipped), left.Value.Type.Restartable), [left.Value, rightSequence, make]), PyType.Iterator(zipped));
      case "reversed" when arguments.Count == 1:
        var reversed = this.Value(arguments[0]);
        return reversed.Type.Kind == PyKind.List
          ? (this.Effect(this.Helpers.ReversedIterator(reversed.Value, PyTypes.Ir(reversed.Type))), PyType.Iterator(reversed.Type.Element!))
          : throw this.Refuse(arguments[0], PythonFrontend.UnsupportedIterationDescriptor, $"iteration over 'reversed of the type {reversed.Type}' is not supported");
      case "map" or "filter" when arguments.Count == 2:
        return this.MapFilter(iterable, this.BuiltinCall(iterable) == "map");
    }

    if (iterable.Is("Name") && this.Scope.Resolve(iterable) == Binding.Class && this.Scope.Class(iterable) is { IsEnum: true } enumeration) {
      var table = this.MemberTable(enumeration);
      return (this.ExactElements(new IrSequence(IrSequenceOp.FromArray, IrType.Sequence(PyTypes.Held(table.Type.Element!), true), [table.Value])), PyType.Iterator(table.Type.Element!));
    }

    if (iterable.Is("GeneratorExp"))
      return this.GeneratorExpression(iterable);

    if (this.DictView(iterable) is { } view)
      return view;

    var value = this.Value(iterable);
    var element = ExpressionTypes.Element(value.Type) ?? throw this.Refuse(iterable, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(iterable)}' of the type {value.Type} is not supported");
    return (this.Sequence(iterable, value), PyType.Iterator(element));
  }

  /// <summary><c>map(f, xs)</c> and <c>filter(f, xs)</c>: the function applied to every element as it is pulled.</summary>
  private (IrExpr, PyType) MapFilter(PyNode call, bool map) {
    var arguments = call.Nodes("args");
    var source = this.Iterable(arguments[1]);
    var element = source.Type.Element!;
    var function = this.FunctionValue(arguments[0], [element], out var result);
    if (map)
      return (new IrSequence(IrSequenceOp.Select, IrType.Sequence(PyTypes.Ir(result), source.Value.Type.Restartable), [source.Value, function]), PyType.Iterator(result));

    var test = result.Kind == PyKind.Bool ? function : this.Predicate(function, element, result);
    return (new IrSequence(IrSequenceOp.Where, IrType.Sequence(PyTypes.Ir(element), source.Value.Type.Restartable), [source.Value, test]), PyType.Iterator(element));
  }

  /// <summary>A function value that is the truth of what another one gives.</summary>
  private IrExpr Predicate(IrExpr function, PyType element, PyType result) {
    var parameter = new IrLocal(this.Unique("v"), PyTypes.Ir(element));
    return IrLambda.Of([parameter], IrType.Bool, [], Block(Return(this.Truth((new IrInvoke(PyTypes.Ir(result), function, [Ref(parameter)]), result)))));
  }

  /// <summary>
  /// A function value passed to <c>map</c>, <c>filter</c> or as <c>key=</c>: a lambda, lowered with its
  /// parameter bound to the elements' type, or a function of the module or a nested one.
  /// </summary>
  private IrExpr FunctionValue(PyNode function, IReadOnlyList<PyType> parameters, out PyType result) {
    if (function.Is("Lambda")) {
      var arguments = function.Node("args")!.Nodes("args");
      var variables = arguments.Select(argument => this._context.Bound[argument]).ToList();
      var body = function.Node("body")!;
      var resultType = PyType.Unknown;
      var lambda = this.LambdaOf(variables, () => {
        var value = this.Value(body);
        resultType = value.Type;
        return value.Value;
      }, () => PyTypes.Ir(resultType));
      result = resultType;
      return lambda;
    }

    var named = this.Scope.FunctionNamed(function)!;
    result = named.Returns;
    if (named.Parent is not null)
      return Ref(this.NestedLocal(named));

    var signature = IrType.Function(new IrFunctionType([.. named.Parameters.Select(parameter => PyTypes.Ir(parameter.Type))], PyTypes.Ir(named.Returns)));
    return new IrFunctionRef(signature, named.IrName);
  }

  /// <summary>
  /// A lambda whose parameters are variables of the source - of a comprehension or a lambda - and whose
  /// body is an expression lowered inside it: its temporaries are its own locals.
  /// </summary>
  private IrLambda LambdaOf(IReadOnlyList<Variable> variables, System.Func<IrExpr> body, System.Func<IrType> result) {
    var parameters = variables.Select(variable => new IrLocal(this.Unique(variable.Name), PyTypes.Ir(variable.Type))).ToList();
    var saved = (this._prefix, this._locals, this._effects, this._statementEffects, this._conditional, this._heldEffects);
    var previous = variables.Select(variable => this._variables.GetValueOrDefault(variable)).ToList();
    this._prefix = [];
    this._locals = [];
    this._statementEffects = this._effects;
    this._heldEffects = this._effects;
    this._conditional = 0;
    for (var index = 0; index < variables.Count; ++index)
      this._variables[variables[index]] = parameters[index];

    try {
      var value = body();
      var statements = new List<IrStmt>(this._prefix) { Return(value) };
      return IrLambda.Of(parameters, result(), this._locals, Block(statements));
    } finally {
      for (var index = 0; index < variables.Count; ++index) {
        if (previous[index] is { } restored)
          this._variables[variables[index]] = restored;
        else
          this._variables.Remove(variables[index]);
      }

      (this._prefix, this._locals, this._effects, this._statementEffects, this._conditional, this._heldEffects) = saved;
    }
  }

  /// <summary>
  /// A generator expression: its first iterable evaluated now, in the scope it stands in; every
  /// <c>if</c> a <c>Where</c>, every further <c>for</c> a <c>SelectMany</c>, the element a
  /// <c>Select</c> - pulled element by element, in the order Python's nested loops run (#1329).
  /// </summary>
  private (IrExpr Value, PyType Type) GeneratorExpression(PyNode expression) {
    var generators = expression.Nodes("generators");
    var element = this.TypeOf(expression.Node("elt")!);
    if (element.IsIncomplete)
      throw this.Refuse(expression, PythonFrontend.MissingTypeDescriptor, $"the type of the elements of '{this.Quote(expression)}' is not known");

    var source = this.ComprehensionSource(generators[0]);
    var elt = expression.Node("elt")!;
    return (this.Pipeline(generators, 0, source, () => this.Coerce(this.Value(elt, element), element, elt), PyTypes.Ir(element)), PyType.Iterator(element));
  }

  /// <summary>
  /// The clauses of a comprehension from one on, over the sequence of its iterable: every element the
  /// innermost clause reaches made by <paramref name="produce"/>, of the type <paramref name="produced"/>.
  /// </summary>
  private IrExpr Pipeline(IReadOnlyList<PyNode> generators, int index, (IrExpr Value, PyType Type) source, System.Func<IrExpr> produce, IrType produced) {
    var generator = generators[index];
    var target = generator.Node("target")!;
    var item = source.Type.Element!;
    var (variables, bind) = this._keyedItems.TryGetValue(source.Value, out var dict) ? this.KeyedItems(target, dict) : this.TargetVariables(target, item);
    var current = source.Value;
    foreach (var condition in generator.Nodes("ifs"))
      current = new IrSequence(IrSequenceOp.Where, IrType.Sequence(current.Type.Element!, current.Type.Restartable),
        [current, this.LambdaOf(variables, () => { this._prefix.AddRange(bind()); return this.Truth(this.Value(condition)); }, () => IrType.Bool)]);

    if (index == generators.Count - 1)
      return new IrSequence(IrSequenceOp.Select, IrType.Sequence(produced, current.Type.Restartable),
        [current, this.LambdaOf(variables, () => { this._prefix.AddRange(bind()); return produce(); }, () => produced)]);

    IrType? inner = null;
    var flattened = this.LambdaOf(variables, () => {
      this._prefix.AddRange(bind());
      var pipeline = this.Pipeline(generators, index + 1, this.ComprehensionSource(generators[index + 1]), produce, produced);
      inner = pipeline.Type;
      return pipeline;
    }, () => inner!);
    return new IrSequence(IrSequenceOp.SelectMany, IrType.Sequence(produced, current.Type.Restartable), [current, flattened]);
  }

  /// <summary>
  /// The variables a comprehension target binds and the statements that bind them from the lambda's
  /// parameter: a name is the parameter itself; the names of a tuple are locals of the lambda taken
  /// from the items of its tuple parameter.
  /// </summary>
  private (IReadOnlyList<Variable> Parameters, System.Func<List<IrStmt>> Bind) TargetVariables(PyNode target, PyType item) {
    if (target.Is("Name"))
      return ([this._context.Bound[target]], () => []);

    var names = target.Nodes("elts");
    if (item.Kind != PyKind.Tuple || item.Items.Count != names.Count)
      throw this.Unsupported(target, $"unpacking a value of the type {item} into {names.Count} names");

    var carrier = new Variable("item", target) { Type = item };
    return ([carrier], () => {
      var statements = new List<IrStmt>();
      var fields = PyTypes.Ir(item).StructType!.Fields;
      for (var index = 0; index < names.Count; ++index) {
        var variable = this._context.Bound[names[index]];
        var local = this.Fresh(variable.Name, PyTypes.Ir(variable.Type));
        this._variables[variable] = local;
        statements.Add(new IrDeclare(local, Fit(new IrFieldLoad(Ref(this._variables[carrier]), fields[index]), local.Type)));
      }

      return statements;
    });
  }

  /// <summary><c>[e for x in it if c]</c>: the generator expression of the same clauses, collected into a new list.</summary>
  private (IrExpr, PyType) ListComprehension(PyNode expression, PyType? expected) {
    var (sequence, type) = this.GeneratorExpression(expression);
    var list = PyType.List(type.Element!);
    return (new IrSequence(IrSequenceOp.ToList, PyTypes.Ir(list), [sequence]), list);
  }

  /// <summary>A sequence or iterable as a sequence of the IR together with its element type.</summary>
  private (IrExpr Value, PyType Element) Elements(PyNode argument) {
    var iterable = this.Iterable(argument);
    return (iterable.Value, iterable.Type.Element!);
  }

  /// <summary>The built-ins over sequences; <see langword="null"/> for any other built-in.</summary>
  private (IrExpr, PyType)? SequenceBuiltin(PyNode call, string name, PyType? expected) {
    var arguments = call.Nodes("args");
    var keywords = call.Nodes("keywords");
    switch (name) {
      case "list" when arguments.Count == 0:
        return expected is { Kind: PyKind.List, IsIncomplete: false } ? (new IrCollection(IrCollectionOp.ListNew, PyTypes.Ir(expected), []), expected)
          : throw this.Refuse(call, PythonFrontend.MissingTypeDescriptor, $"the type of the elements of '{this.Quote(call)}' is not known; add a type hint such as 'list[int]' to what holds it");
      case "list" when arguments.Count == 1:
        var (sequence, element) = this.Elements(arguments[0]);
        var list = PyType.List(element);
        return (new IrSequence(IrSequenceOp.ToList, PyTypes.Ir(list), [sequence]), list);
      case "tuple" when arguments.Count == 1:
        var tuple = this.Value(arguments[0]);
        return tuple.Type.Kind == PyKind.Tuple ? tuple : throw this.Refuse(call, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation 'tuple of a value of the type {tuple.Type}, whose length is not known' is not supported by the Python frontend");
      case "dict" or "set":
        return this.NewCollection(call, name, expected);
      case "sorted":
        return this.Sorted(call);
      case "reversed" when arguments.Count == 1:
        // reversed() is an iterator, never a list: python indexes none.
        return this.Iterable(call);
      case "sum" when keywords.Count == 0 && arguments.Count is 1 or 2:
        return this.Sum(call);
      case "min" or "max":
        return this.Extreme(call, name == "min");
      case "any" or "all" when arguments.Count == 1 && keywords.Count == 0:
        var (values, type) = this.Elements(arguments[0], ExpressionTypes.Element(this.TypeOf(arguments[0])) is { } truth && (truth.IsNumber || truth.Kind == PyKind.Str));
        var item = new Variable("x", arguments[0]) { Type = type };
        var predicate = this.LambdaOf([item], () => this.Truth((Ref(this._variables[item]), type)), () => IrType.Bool);
        return name == "any"
          ? (new IrSequence(IrSequenceOp.AnyWhere, IrType.Bool, [values, predicate]), PyType.Bool)
          : (new IrSequence(IrSequenceOp.All, IrType.Bool, [values, predicate]), PyType.Bool);
      default:
        return null;
    }
  }

  /// <summary>
  /// <c>sum(it, start)</c> of integers: a left fold from <c>start</c>, every partial sum exact or
  /// <c>OverflowError</c>. A sum of floats is refused: CPython 3.12 and later add floats with
  /// compensation, which no single rounding per step reproduces.
  /// </summary>
  private (IrExpr, PyType) Sum(PyNode call) {
    var arguments = call.Nodes("args");
    var (values, element) = this.Elements(arguments[0], true);
    var start = arguments.Count == 2 ? this.Value(arguments[1]) : (Big(0), PyType.Int);
    if (ExpressionTypes.Value(element) is not { Kind: PyKind.Int or PyKind.Bool } || ExpressionTypes.Value(start.Type) is not { Kind: PyKind.Int or PyKind.Bool })
      throw this.Refuse(call, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation 'sum of {element} values', which CPython adds with compensation, is not supported by the Python frontend");

    var integers = element.Kind == PyKind.Int ? values : this.Widened(values, element);
    return (this.Effect(this.Helpers.Sum(integers, this.Integer(this.Plain(start, call)).Item1)), PyType.Int);
  }

  /// <summary>A sequence of truth values or enum members as a sequence of integers.</summary>
  private IrExpr Widened(IrExpr values, PyType element) {
    var parameter = new IrLocal(this.Unique("v"), PyTypes.Ir(element));
    var widen = IrLambda.Of([parameter], IrType.BigInt, [], Block(Return(this.Integer((Ref(parameter), element)).Item1)));
    return new IrSequence(IrSequenceOp.Select, IrType.Sequence(IrType.BigInt, values.Type.Restartable), [values, widen]);
  }

  /// <summary>
  /// <c>min</c> and <c>max</c> of a sequence or of several arguments, with <c>key=</c> and
  /// <c>default=</c>: the first element whose key is smaller (larger) than every one before it, as
  /// CPython compares - <c>&lt;</c> for <c>min</c>, <c>&gt;</c> for <c>max</c>; an empty sequence
  /// raises <c>ValueError</c> unless there is a default.
  /// </summary>
  private (IrExpr, PyType) Extreme(PyNode call, bool minimum) {
    var arguments = call.Nodes("args");
    var keywords = call.Nodes("keywords");
    if (arguments.Count == 0 || keywords.Any(keyword => keyword.Text("arg") is not ("key" or "default")) || (arguments.Count > 1 && keywords.Any(keyword => keyword.Text("arg") == "default")))
      throw this.Refuse(call, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{this.Quote(call)}' is not supported by the Python frontend");

    IrExpr values;
    PyType element;
    if (arguments.Count == 1) {
      (values, element) = this.Elements(arguments[0], KeyFunction(keywords) is null && ExpressionTypes.Element(this.TypeOf(arguments[0])) is { } member && TotallyOrdered(member));
    } else {
      element = this.TypeOf(call);
      var items = arguments.Select(argument => this.Coerce(this.Value(argument, element), element, argument)).ToList();
      values = this.Sequence(call, (this.ListOf(items, PyType.List(element)), PyType.List(element)));
    }

    var keyNode = KeyFunction(keywords);
    var keyType = element;
    IrExpr? key = keyNode is null ? null : this.FunctionValue(keyNode, [element], out keyType);
    var fallback = keywords.FirstOrDefault(keyword => keyword.Text("arg") == "default")?.Node("value");
    IrExpr? fallbackValue = fallback is null ? null : this.Coerce(this.Value(fallback, element), element, fallback);
    var less = this.LessThan(call, keyType);
    return (this.Effect(this.Helpers.Extreme(values, PyTypes.Ir(element), key, PyTypes.Ir(keyType), minimum, fallbackValue, less, minimum ? "min() iterable argument is empty" : "max() iterable argument is empty")), element);
  }

  /// <summary><c>sorted(it, key=…, reverse=…)</c>: a new list, stably sorted by Python's <c>&lt;</c> of the keys, each key computed once, in order.</summary>
  private (IrExpr, PyType) Sorted(PyNode call) {
    var arguments = call.Nodes("args");
    if (arguments.Count != 1)
      throw this.Refuse(call, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{this.Quote(call)}' is not supported by the Python frontend");

    var keywords = call.Nodes("keywords");
    var (values, element) = this.Elements(arguments[0], KeyFunction(keywords) is null && ExpressionTypes.Element(this.TypeOf(arguments[0])) is { } member && TotallyOrdered(member));
    var keyNode = KeyFunction(keywords);
    var keyType = element;
    IrExpr? key = keyNode is null ? null : this.FunctionValue(keyNode, [element], out keyType);
    var reverseNode = keywords.FirstOrDefault(keyword => keyword.Text("arg") == "reverse")?.Node("value");
    var reverse = reverseNode is null ? Bool(false) : this.Truth(this.Value(reverseNode));
    if (keyType.Kind == PyKind.Float || (keyType.Kind == PyKind.Tuple && keyType.Items.Any(item => item.Kind == PyKind.Float)))
      this.Notes.SortsFloats = true;

    var list = PyType.List(element);
    return (this.Effect(this.Helpers.Sorted(values, PyTypes.Ir(list), key, PyTypes.Ir(keyType), reverse, this.LessThan(call, keyType))), list);
  }

  /// <summary>The function of a <c>key=</c> keyword, or <see langword="null"/> where there is none or it is <c>None</c>, which compares the elements themselves.</summary>
  private static PyNode? KeyFunction(IReadOnlyList<PyNode> keywords)
    => keywords.FirstOrDefault(keyword => keyword.Text("arg") == "key")?.Node("value") is { } key && !(key.Is("Constant") && key.Text("kind") == "none") ? key : null;

  /// <summary>Python's <c>a &lt; b</c> of two values of one type, for the helpers that compare elements and keys.</summary>
  private System.Func<IrExpr, IrExpr, IrExpr> LessThan(PyNode at, PyType type) => type.Kind switch {
    PyKind.Bool or PyKind.Int or PyKind.Float => (a, b) => Binary(IrBinaryOp.Less, a, b),
    PyKind.Enum when type.Class!.Kind == ClassKind.IntEnum => (a, b) => Binary(IrBinaryOp.Less, Convert(IrType.I64, a), Convert(IrType.I64, b)),
    PyKind.Str => (a, b) => this.Helpers.StrLess(a, b),
    PyKind.Tuple => (a, b) => this.Helpers.TupleLess(a, b, PyTypes.Ir(type), type.Items.Select(item => this.LessThan(at, item)).ToList(),
      type.Items.Select(item => (System.Func<IrExpr, IrExpr, IrExpr>)((x, y) => this.ElementEquality(at, x, y, item))).ToList()),
    _ => throw this.Unsupported(at, $"ordering of values of the type {type}, which raises TypeError,")
  };

}
