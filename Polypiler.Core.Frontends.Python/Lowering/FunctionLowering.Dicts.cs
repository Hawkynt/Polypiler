using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// Dicts and sets. A dict is a map of the IR in the order its keys were first inserted, which is the
/// order Python iterates a dict in; a set is a set of the IR, which Python iterates in the order of
/// the hash, so a set is iterated only where no order can change the result. A key and an element of a
/// set are held as a field holds them: an <c>int</c> one is an <c>i64</c>, and one beyond 64 bits raises
/// <c>OverflowError</c> (<c>py-int-held-64</c>). A missing key raises <c>KeyError</c>; a dict whose size
/// changes while it is iterated raises <c>RuntimeError</c> at the next step, as CPython's iterator does.
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary>The type of the keys of a dict, or of the elements of a set.</summary>
  private static PyType KeyType(PyType collection) => collection.Kind == PyKind.Dict ? collection.Key! : collection.Element!;

  /// <summary>A refusal of a dict or set operation, named.</summary>
  private Refusal DictRefusal(PyNode at, string operation, string why = "")
    => this.Refuse(at, PythonFrontend.UnsupportedDictDescriptor, $"dict or set operation '{operation}' is not supported by the Python frontend{why}");

  /// <summary>
  /// The type of a display, a comprehension or a construction of a dict or a set: its own, completed by
  /// the type of the place it goes to; one that stays incomplete asks for a hint. A key the IR keeps no
  /// map of, and a value no map holds, are refused by name.
  /// </summary>
  private PyType CollectionType(PyNode node, PyType? expected) {
    var type = this.TypeOf(node);
    if (type.IsIncomplete && expected is not null && expected.Kind == type.Kind && PyTypes.Join(type, expected) is { IsIncomplete: false } joined)
      type = joined;

    var dict = type.Kind == PyKind.Dict;
    if (type.IsIncomplete)
      throw this.Refuse(node, PythonFrontend.MissingTypeDescriptor, dict
        ? $"the type of the keys and values of '{this.Quote(node)}' is not known; add a type hint such as 'dict[str, int]' to what holds it"
        : $"the type of the elements of '{this.Quote(node)}' is not known; add a type hint such as 'set[int]' to what holds it");

    if (!PyTypes.Keyable(KeyType(type)))
      throw this.DictRefusal(node, dict ? $"a dict keyed by {type.Key}" : $"a set of {type.Element}",
        ": a key or an element of a set is an int, a bool, a str or an object of a plain class without __eq__");

    if (dict && !PyTypes.Mappable(type.Element!))
      throw this.DictRefusal(node, $"a dict of {type.Element} values", ": a value is no optional and no generator");

    return type;
  }

  /// <summary>A key of a dict, or an element of a set, as the collection holds it.</summary>
  private IrExpr HeldKey((IrExpr Value, PyType Type) key, PyType collection, PyNode at)
    => Fit(this.Coerce(key, KeyType(collection), at), PyTypes.Held(KeyType(collection)));

  /// <summary>A key or an element lowered where it stands, as the collection holds it.</summary>
  private IrExpr HeldKey(PyNode key, PyType collection) => this.HeldKey(this.Value(key, KeyType(collection)), collection, key);

  /// <summary><c>{k: v, …}</c>: a new dict, the keys and values evaluated left to right, inserted in order.</summary>
  private (IrExpr, PyType) DictDisplay(PyNode display, PyType? expected) {
    var type = this.CollectionType(display, expected);
    var values = display.Nodes("values");
    var pairs = new List<IrExpr>();
    for (var index = 0; index < values.Count; ++index) {
      pairs.Add(this.HeldKey(display.Item("keys", index)!, type));
      pairs.Add(this.Coerce(this.Value(values[index], type.Element), type.Element!, values[index]));
    }

    return pairs.Count == 0 ? (new IrCollection(IrCollectionOp.MapNew, PyTypes.Ir(type), []), type) : (this.Effect(this.Helpers.DictOf(pairs, PyTypes.Ir(type))), type);
  }

  /// <summary><c>{e, …}</c>: a new set, the elements evaluated left to right, inserted in order.</summary>
  private (IrExpr, PyType) SetDisplay(PyNode display, PyType? expected) {
    var type = this.CollectionType(display, expected);
    var elements = display.Nodes("elts").Select(element => this.HeldKey(element, type)).ToList();
    return (this.Effect(this.Helpers.SetOf(elements, PyTypes.Ir(type))), type);
  }

  /// <summary>
  /// <c>{k: v for …}</c> and <c>{e for …}</c>: the clauses of a generator expression whose every step
  /// puts its key and value - the key evaluated first - or adds its element into a new collection,
  /// pulled to the end; the collection is the value.
  /// </summary>
  private (IrExpr, PyType) CollectionComprehension(PyNode expression, PyType? expected) {
    var type = this.CollectionType(expression, expected);
    var irType = PyTypes.Ir(type);
    var dict = type.Kind == PyKind.Dict;
    var target = this.Fresh(dict ? "dict" : "set", irType);
    this._prefix.Add(new IrDeclare(target, new IrCollection(dict ? IrCollectionOp.MapNew : IrCollectionOp.SetNew, irType, [])));
    var generators = expression.Nodes("generators");
    var source = this.ComprehensionSource(generators[0]);
    var sequence = this.Pipeline(generators, 0, source, () => {
      var key = this.HeldKey(expression.Node(dict ? "key" : "elt")!, type);
      this._prefix.Add(new IrExprStmt(dict
        ? new IrCollection(IrCollectionOp.MapPut, IrType.Void, [Ref(target), key, this.Coerce(this.Value(expression.Node("value")!, type.Element), type.Element!, expression.Node("value")!)])
        : new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [Ref(target), key])));
      return Bool(true);
    }, IrType.Bool);
    return (this.Effect(this.Helpers.Drain(sequence, Ref(target))), type);
  }

  /// <summary><c>dict()</c>, <c>dict(d)</c>, <c>set()</c>, <c>set(s)</c> and <c>set(it)</c>; any other form is refused.</summary>
  private (IrExpr, PyType) NewCollection(PyNode call, string name, PyType? expected) {
    var arguments = call.Nodes("args");
    if (call.Nodes("keywords").Count > 0 || arguments.Count > 1)
      throw this.DictRefusal(call, this.Quote(call));

    if (arguments.Count == 0) {
      var type = this.CollectionType(call, expected);
      return (new IrCollection(name == "dict" ? IrCollectionOp.MapNew : IrCollectionOp.SetNew, PyTypes.Ir(type), []), type);
    }

    var source = this.TypeOf(arguments[0]);
    if (name == "dict") {
      if (source.Kind != PyKind.Dict)
        throw this.DictRefusal(call, this.Quote(call), $": dict of a value of the type {source}");

      var copied = this.Value(arguments[0]);
      return (this.Effect(this.Helpers.DictCopy(copied.Value, PyTypes.Ir(copied.Type))), copied.Type);
    }

    if (source.Kind == PyKind.Set) {
      var copied = this.Value(arguments[0]);
      return (this.Effect(this.Helpers.SetCopy(copied.Value, PyTypes.Ir(copied.Type))), copied.Type);
    }

    var made = this.CollectionType(call, expected);
    var (values, _) = this.Elements(arguments[0]);
    return (this.Effect(this.Helpers.SetFrom(values, PyTypes.Ir(made))), made);
  }

  /// <summary><c>d[k]</c>: the value of a key; <c>KeyError</c> where it is absent.</summary>
  private (IrExpr, PyType) DictItem(PyNode at, (IrExpr Value, PyType Type) dict, (IrExpr Value, PyType Type) key)
    => (this.Effect(new IrCollection(IrCollectionOp.MapGet, PyTypes.Ir(dict.Type.Element!), [dict.Value, this.HeldKey(key, dict.Type, at)])), dict.Type.Element!);

  /// <summary><c>d[k] = v</c>: the key inserted with its value, or the value of the key present replaced.</summary>
  private IrStmt DictStore(PyNode target, (IrExpr Value, PyType Type) dict, (IrExpr Value, PyType Type) key, (IrExpr Value, PyType Type) value)
    => new IrExprStmt(this.Effect(new IrCollection(IrCollectionOp.MapPut, IrType.Void,
      [dict.Value, this.HeldKey(key, dict.Type, target.Node("slice")!), this.Coerce(value, dict.Type.Element!, target)])));

  /// <summary><c>del d[k]</c>: the dict and the key in order, then the removal; <c>KeyError</c> where the key is absent. Of anything but a dict it is refused.</summary>
  private IEnumerable<IrStmt> Delete(PyNode statement) {
    var result = new List<IrStmt>();
    foreach (var target in statement.Nodes("targets")) {
      var container = this.Value(target.Node("value")!);
      if (container.Type.Kind != PyKind.Dict)
        throw this.Refuse(target, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation 'del {this.Quote(target)}' is not supported by the Python frontend");

      var key = this.HeldKey(target.Node("slice")!, container.Type);
      result.Add(new IrExprStmt(this.Effect(this.Helpers.DictDelete(container.Value, key, PyTypes.Ir(container.Type)))));
    }

    return result;
  }

  /// <summary><c>k in d</c> and <c>x in s</c>: whether the key or the element is present.</summary>
  private IrExpr Contains(PyNode node, (IrExpr Value, PyType Type) element, (IrExpr Value, PyType Type) collection)
    => this.Effect(new IrCollection(collection.Type.Kind == PyKind.Dict ? IrCollectionOp.MapContainsKey : IrCollectionOp.SetContains, IrType.Bool,
      [collection.Value, this.HeldKey(element, collection.Type, node)]));

  /// <summary>The number of entries of a dict or elements of a set, an <c>i32</c>.</summary>
  private static IrExpr CollectionCount((IrExpr Value, PyType Type) collection)
    => new IrCollection(collection.Type.Kind == PyKind.Dict ? IrCollectionOp.MapCount : IrCollectionOp.SetCount, IrType.I32, [collection.Value]);

  /// <summary>
  /// The methods of dicts - <c>get</c>, <c>pop</c>, <c>clear</c>, <c>copy</c> - and of sets - <c>add</c>,
  /// <c>remove</c>, <c>discard</c>, <c>clear</c>, <c>copy</c>; <see langword="null"/> for a receiver that is
  /// neither. Any other method is refused by name.
  /// </summary>
  private (IrExpr, PyType)? CollectionMethod(PyNode call, string name, (IrExpr Value, PyType Type) receiver) {
    if (receiver.Type.Kind is not (PyKind.Dict or PyKind.Set))
      return null;

    var arguments = call.Nodes("args");
    var type = receiver.Type;
    var irType = PyTypes.Ir(type);
    var dict = type.Kind == PyKind.Dict;
    if (call.Nodes("keywords").Count > 0)
      throw this.DictRefusal(call, $"{name} with keyword arguments");

    switch (name) {
      case "clear" when arguments.Count == 0:
        return (this.Effect(new IrCollection(dict ? IrCollectionOp.MapClear : IrCollectionOp.SetClear, IrType.Void, [receiver.Value])), PyType.None);
      case "copy" when arguments.Count == 0:
        return (this.Effect(dict ? this.Helpers.DictCopy(receiver.Value, irType) : this.Helpers.SetCopy(receiver.Value, irType)), type);
      case "keys" or "values" or "items" when dict && arguments.Count == 0:
        throw this.DictRefusal(call, $"{name}() as a value", "; a view is imported where a for, a comprehension or a built-in iterates it");
      case "get" or "pop" when dict && arguments.Count is 1 or 2:
        var key = this.HeldKey(arguments[0], type);
        var value = type.Element!;
        if (arguments.Count == 2 && !(arguments[1].Is("Constant") && arguments[1].Text("kind") == "none" && name == "get")) {
          var fallback = this.Value(arguments[1], value);
          if (PyTypes.Join(fallback.Type, value) != value)
            throw this.DictRefusal(call, $"{name} with a default of the type {fallback.Type} from a dict of {value} values");

          var given = this.Coerce(fallback, value, arguments[1]);
          return (this.Effect(name == "get" ? this.Helpers.DictGetOr(receiver.Value, key, given, irType) : this.Helpers.DictPopOr(receiver.Value, key, given, irType)), value);
        }

        if (name == "pop")
          return (this.Effect(this.Helpers.DictPop(receiver.Value, key, irType)), value);

        if (!PyTypes.Optionable(value))
          throw this.DictRefusal(call, $"get without a default from a dict of {value} values", $", which gives {value} | None, an optional the import holds no value of; give a default");

        var optional = PyType.Optional(value);
        return (this.Effect(this.Helpers.DictGet(receiver.Value, key, irType, PyTypes.Ir(optional))), optional);
      case "add" or "discard" when !dict && arguments.Count == 1:
        var element = this.HeldKey(arguments[0], type);
        return (this.Effect(new IrCollection(name == "add" ? IrCollectionOp.SetAdd : IrCollectionOp.SetRemove, IrType.Bool, [receiver.Value, element])), PyType.None);
      case "remove" when !dict && arguments.Count == 1:
        return (this.Effect(this.Helpers.SetRemove(receiver.Value, this.HeldKey(arguments[0], type), irType)), PyType.None);
      default:
        throw this.DictRefusal(call, dict ? $"dict.{name}" : $"set.{name}", " with these arguments");
    }
  }

  /// <summary>
  /// <c>d.keys()</c>, <c>d.values()</c> and <c>d.items()</c> where a <c>for</c>, a comprehension or a
  /// built-in iterates them: the iterator of the dict; <see langword="null"/> for any other expression.
  /// </summary>
  private (IrExpr Value, PyType Type)? DictView(PyNode node) {
    foreach (var part in new[] { "keys", "values", "items" })
      if (this.IsView(node, part))
        return this.DictIterator(this.Value(node.Node("func")!.Node("value")!), part, node);

    return null;
  }

  /// <summary>Whether an expression is <c>d.keys()</c>, <c>d.values()</c> or <c>d.items()</c> - the one named - of a dict.</summary>
  private bool IsView(PyNode node, string part)
    => node.Is("Call") && node.Node("func") is { Kind: "Attribute" } callee && callee.Text("attr") == part
      && node.Nodes("args").Count == 0 && node.Nodes("keywords").Count == 0 && this.TypeOf(callee.Node("value")!).Kind == PyKind.Dict;

  /// <summary>
  /// <c>for k, v in d.items()</c>: the keys as the iterator of the dict gives them, the value of each
  /// read when its step begins - what CPython's item iterator reads there - with no tuple between,
  /// so that keys and values of every type are taken apart.
  /// </summary>
  private List<IrStmt> Items(PyNode target, PyNode call, System.Func<List<IrStmt>> body) {
    var setup = new List<IrStmt>();
    var dict = this.Once(this.Value(call.Node("func")!.Node("value")!), setup);
    var keys = this.DictIterator(dict, "keys", call);
    var key = this.Fresh("key", PyTypes.Ir(dict.Type.Key!));
    var value = this.Fresh("value", PyTypes.Ir(dict.Type.Element!));
    var names = target.Nodes("elts");
    var pass = new List<IrStmt> {
      new IrDeclare(value, new IrCollection(IrCollectionOp.MapGet, value.Type, [dict.Value, Fit(Ref(key), PyTypes.Held(dict.Type.Key!))]))
    };
    pass.AddRange(this.Store(names[0], (Ref(key), dict.Type.Key!)));
    pass.AddRange(this.Store(names[1], (Ref(value), dict.Type.Element!)));
    pass.AddRange(body());
    return [.. setup, new IrForEachSequence(key, keys.Value, new(pass))];
  }

  /// <summary>
  /// The iterable of a clause of a comprehension: <c>d.items()</c> taken apart by two names, of keys or
  /// values no tuple holds, is the keys of the dict, and the clause reads the value of each (<see cref="KeyedItems"/>);
  /// any other iterable as <see cref="Iterable"/> makes it.
  /// </summary>
  private (IrExpr Value, PyType Type) ComprehensionSource(PyNode generator) {
    var iterable = generator.Node("iter")!;
    if (!(generator.Node("target") is { Kind: "Tuple" } target && target.Nodes("elts").Count == 2 && this.IsView(iterable, "items")
        && this.TypeOf(iterable.Node("func")!.Node("value")!) is var type && !(IsTupleItem(type.Key!) && IsTupleItem(type.Element!))))
      return this.Iterable(iterable);

    var before = this._effects;
    var dict = this.Kept(iterable, this.Value(iterable.Node("func")!.Node("value")!), before);
    var keys = this.DictIterator(dict, "keys", iterable);
    this._keyedItems[keys.Value] = dict;
    return (keys.Value, PyType.Iterator(PyType.Tuple([type.Key!, type.Element!])));
  }

  /// <summary>
  /// The variables of <c>k, v</c> in a clause over the keys <see cref="ComprehensionSource"/> made of
  /// <c>d.items()</c>: the key is the lambda's parameter, the value a local of the lambda read from the dict.
  /// </summary>
  private (IReadOnlyList<Variable> Parameters, System.Func<List<IrStmt>> Bind) KeyedItems(PyNode target, (IrExpr Value, PyType Type) dict) {
    var names = target.Nodes("elts");
    var key = this._context.Bound[names[0]];
    return ([key], () => {
      var variable = this._context.Bound[names[1]];
      var local = this.Fresh(variable.Name, PyTypes.Ir(variable.Type));
      this._variables[variable] = local;
      return [new IrDeclare(local, new IrCollection(IrCollectionOp.MapGet, local.Type, [dict.Value, Fit(Ref(this._variables[key]), PyTypes.Held(dict.Type.Key!))]))];
    }
    );
  }

  /// <summary><c>for i, k in enumerate(d, start)</c>: the keys as the iterator of the dict gives them, numbered from the start, which is evaluated once, after the dict.</summary>
  private List<IrStmt> EnumerateDict(PyNode target, PyNode call, System.Func<List<IrStmt>> body) {
    var arguments = call.Nodes("args");
    if (!target.Is("Tuple") || target.Nodes("elts").Count != 2 || arguments.Count > 2 || call.Nodes("keywords").Count > 0)
      throw this.Refuse(call, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(call)}' is not supported: enumerate takes one sequence and a start, its target two names");

    var setup = new List<IrStmt>();
    var dict = this.Once(this.Value(arguments[0]), setup);
    var keys = this.DictIterator(dict, "keys", call);
    var counter = this.Fresh("i", IrType.BigInt);
    setup.Add(new IrDeclare(counter, arguments.Count == 2 ? this.Integer(this.Plain(this.Value(arguments[1]), arguments[1])).Item1 : Big(0)));
    var key = this.Fresh("key", PyTypes.Ir(dict.Type.Key!));
    var names = target.Nodes("elts");
    var pass = new List<IrStmt>();
    pass.AddRange(this.Store(names[0], (Ref(counter), PyType.Int)));
    pass.Add(Assign(counter, Binary(IrBinaryOp.Add, Ref(counter), Big(1))));
    pass.AddRange(this.Store(names[1], (Ref(key), dict.Type.Key!)));
    pass.AddRange(body());
    return [.. setup, new IrForEachSequence(key, keys.Value, new(pass))];
  }

  /// <summary>The keys, values or pairs of a dict as a sequence, which raises <c>RuntimeError</c> once the dict changed its size.</summary>
  private (IrExpr Value, PyType Type) DictIterator((IrExpr Value, PyType Type) dict, string part, PyNode at) {
    var (key, value) = (dict.Type.Key!, dict.Type.Element!);
    var mapType = PyTypes.Ir(dict.Type);
    var element = part switch {
      "keys" => key,
      "values" => value,
      _ => PyType.Tuple([key, value])
    };
    // A struct holds no str, list or object: items() of those is taken apart only by a for of two names.
    if (part == "items" && !(IsTupleItem(key) && IsTupleItem(value)))
      throw this.DictRefusal(at, $"items() of a dict of the types {key} and {value} outside 'for k, v in d.items()'", ", as a tuple of the IR holds no str, list or object");

    var irElement = PyTypes.Ir(element);
    System.Func<IrExpr, IrExpr, IrExpr> make = part switch {
      "keys" => (k, _) => Fit(k, irElement),
      "values" => (_, v) => v,
      _ => (k, v) => MakeStruct(irElement, [k, v])
    };
    // The size is taken where the iterator is made when the dict is a variable; otherwise at its first step, which a for and a comprehension take at once.
    var size = dict.Value is IrLocalRef ? CollectionCount(dict) : Int32(-1);
    return (this.Effect(this.Helpers.DictIterator(dict.Value, size, mapType, part, irElement, make)), PyType.Iterator(element));
  }

  /// <summary>
  /// The elements of a set for a built-in whose result no order can change - <c>sorted</c>, <c>min</c>,
  /// <c>max</c> without a key, <c>sum</c>, <c>any</c>, <c>all</c> - or of any other iterable as a <c>for</c> takes them.
  /// </summary>
  private (IrExpr Value, PyType Element) Elements(PyNode argument, bool orderFree) {
    if (!orderFree || this.TypeOf(argument).Kind != PyKind.Set)
      return this.Elements(argument);

    var set = this.Value(argument);
    var element = set.Type.Element!;
    return (this.Effect(this.Helpers.SetElements(set.Value, PyTypes.Ir(set.Type), PyTypes.Ir(element))), element);
  }

  /// <summary>Whether the elements of a set of a type sort, and pick a smallest or largest one, alike in every order: distinct ints, bools and strs.</summary>
  private static bool TotallyOrdered(PyType element) => element.Kind is PyKind.Int or PyKind.Bool or PyKind.Str;

}
