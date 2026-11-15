using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>The Python name of every member of every enum, by identity.</summary>
    private readonly Dictionary<IrEnumMember, string> _enumMembers = new(ReferenceEqualityComparer.Instance);

    /// <summary>The tables of the members of the enums whose names the module asks for, by enum.</summary>
    private readonly Dictionary<IrEnumType, string> _enumTables = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// An enum as an <c>enum.IntEnum</c>, its members in UPPER_SNAKE_CASE with their values in the
    /// order of the source. A value of the enum is an <c>int</c>: a member where the IR names one, any
    /// other number where it computes one, which an <c>IntEnum</c> compares and computes with alike.
    /// </summary>
    private PyClassDef Enumeration(IrEnumType type) {
      var names = new HashSet<string>(StringComparer.Ordinal) { "name", "value" };
      var body = new List<PyStatement>();
      foreach (var member in type.Members) {
        var snake = PythonNaming.Snake(member.Name).Trim('_').ToUpperInvariant();
        var name = PythonNaming.Free(PythonNaming.Usable(snake.Length == 0 ? "MEMBER" : snake), candidate => names.Contains(candidate) || candidate.StartsWith('_'));
        names.Add(name);
        this._enumMembers[member] = name;
        body.Add(new PyAssign(Name(name), Integer(Big(IrOperators.Number(type.Underlying, member.Value)))));
      }

      return new PyClassDef(this._typeNames[type], [new PyAttribute(Name("enum"), "IntEnum")], body);
    }

    /// <summary>A constant of an enum type: the member that has the value, or the number.</summary>
    private PyExpression EnumLiteral(IrEnumType type, long value)
      => type.MemberOf(value) is { } member ? new PyAttribute(Name(this.TypeNameOf(type)), this._enumMembers.GetValueOrDefault(member) ?? PythonNaming.Snake(member.Name).ToUpperInvariant())
        : Integer(Big(IrOperators.Number(type.Underlying, value)));

    /// <summary>The table of an enum's members as .NET names them, in the order of their unsigned values: <c>((value, "Name"), …)</c>.</summary>
    private PyExpression EnumTable(IrEnumType type) {
      if (!this._enumTables.TryGetValue(type, out var name))
        this._enumTables[type] = name = this.Take("_" + PythonNaming.Snake(this.TypeNameOf(type)).Trim('_').ToUpperInvariant() + "_MEMBERS");

      return Name(name);
    }

    /// <summary>The module-level tables the enums of the module need, after the enums.</summary>
    private IEnumerable<PyStatement> EnumTables() {
      foreach (var (type, name) in this._enumTables)
        yield return new PyAssign(Name(name), new PyTuple([.. type.Ordered.Select(member => new PyTuple([Integer(Big(IrOperators.Number(type.Underlying, member.Value))), new PyString(member.Name)]))]) { Tabulated = true });
    }

    /// <summary>The operations on enums.</summary>
    private PyExpression EnumOperation(IrIntrinsic intrinsic) {
      var type = EnumOperations.Of(intrinsic);
      var bits = Integer(type.Underlying.Bits);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.EnumToString:
          return this.Helper(PythonHelpers.EnumFormat, this.Expression(intrinsic.Arguments[0]), this.EnumTable(type), new PyBool(type.IsFlags), bits);
        case IrIntrinsicOp.EnumIsDefined:
          return this.Helper(PythonHelpers.EnumDefined, this.Expression(intrinsic.Arguments[0]), this.EnumTable(type));
        case IrIntrinsicOp.EnumParse:
          return this.Helper(PythonHelpers.EnumParse, this.Expression(intrinsic.Arguments[0]), this.Expression(intrinsic.Arguments[1]), this.EnumTable(type),
            Integer(Big(IrOperators.Minimum(type.Underlying))), Integer(Big(IrOperators.Maximum(type.Underlying))));
        case IrIntrinsicOp.EnumHasFlag:
          var flag = this.Expression(intrinsic.Arguments[1]);
          if (flag is PyAttribute or PyInt) {
            var value = this.Expression(intrinsic.Arguments[0]);
            return Compare(Binary(value, "&", flag), "==", flag);
          }

          return this.Helper(PythonHelpers.HasFlag, this.Expression(intrinsic.Arguments[0]), flag);
        case IrIntrinsicOp.EnumNames:
          return new PyList([.. type.Ordered.Select(member => new PyString(member.Name))]);
        default:
          return new PyList([.. type.Ordered.Select(member => this.EnumLiteral(type, member.Value))]);
      }
    }

    /// <summary>
    /// The expressions of optional values: <c>None</c> for none, the value itself for some, tests
    /// with <c>is None</c> - never by truth, which <c>0</c> and <c>""</c> fail - and the unwrap that
    /// traps with <c>RuntimeError</c>.
    /// </summary>
    private PyExpression? OptionalExpression(IrExpr expression) {
      switch (expression) {
        case IrNone:
          return new PyNone();
        case IrSome some:
          return this.Expression(some.Value);
        case IrIsNone test:
          return Compare(this.Expression(test.Value), "is", new PyNone());
        case IrUnwrap unwrap:
          return this.Helper(PythonHelpers.Unwrap, this.Expression(unwrap.Value));
        case IrCoalesce coalesce:
          var value = this.Expression(coalesce.Value);
          if (coalesce.Value is IrLocalRef { Local: var local } && !this.IsCell(local))
            return new PyConditional(Compare(value, "is not", new PyNone()), value, this.Expression(coalesce.Fallback));

          var held = this.Temporary();
          return new PyConditional(Compare(new PyNamedExpression(held, value), "is not", new PyNone()), Name(held), this.Expression(coalesce.Fallback));
        case IrThrowExpression thrown:
          return this.Helper(PythonHelpers.Throw, this.Expression(thrown.Exception));
        default:
          return null;
      }
    }

    /// <summary>
    /// A string operation. A string is a Python <c>str</c> whose every character is one UTF-16 code
    /// unit - a character outside the BMP is its two surrogates - so that lengths, indexes and the
    /// ordinal comparison are those of the IR; where the IR traps and Python would count from the end
    /// or clamp, a helper checks first.
    /// </summary>
    private PyExpression StringOperation(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      PyExpression Arg(int index) => this.Expression(arguments[index]);
      PyExpression Text(int index) => arguments[index].Type.IsString ? this.Expression(arguments[index]) : new PyCall(Name("chr"), [this.Expression(arguments[index])]);
      PyExpression Method(string name, params PyExpression[] values) => new PyCall(new PyAttribute(Arg(0), name), values);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.StrLength:
          return new PyCall(Name("len"), [Arg(0)]);
        case IrIntrinsicOp.StrCodeUnitAt:
          return this.NonNegative(arguments[1])
            ? new PyCall(Name("ord"), [new PySubscript(Arg(0), Arg(1))])
            : this.Helper(PythonHelpers.UnitAt, Arg(0), Arg(1));
        case IrIntrinsicOp.StrConcat:
          return arguments.Skip(1).Aggregate(Arg(0), (sum, next) => Binary(sum, "+", this.Expression(next)));
        case IrIntrinsicOp.StrFromCodeUnit:
          return new PyCall(Name("chr"), [Arg(0)]);
        case IrIntrinsicOp.StrFromCodeUnits when arguments.Count == 1:
          return new PyCall(new PyAttribute(new PyString(""), "join"), [new PyCall(Name("map"), [Name("chr"), Arg(0)])]);
        case IrIntrinsicOp.StrFromCodeUnits:
          return this.Helper(PythonHelpers.FromUnits, Arg(0), Arg(1), Arg(2));
        case IrIntrinsicOp.StrToCodeUnits:
          return new PyCall(Name("list"), [new PyCall(Name("map"), [Name("ord"), Arg(0)])]);
        case IrIntrinsicOp.StrRepeat:
          return arguments[1] is IrConst { Value: >= 0 } ? Binary(Text(0), "*", Arg(1)) : this.Helper(PythonHelpers.Repeat, Text(0), Arg(1));
        case IrIntrinsicOp.StrSubstring when arguments.Count == 2:
          return this.Helper(PythonHelpers.SubstringFrom, Arg(0), Arg(1));
        case IrIntrinsicOp.StrSubstring:
          return this.Helper(PythonHelpers.Substring, Arg(0), Arg(1), Arg(2));
        case IrIntrinsicOp.StrEquals:
          return Compare(Arg(0), "==", Arg(1));
        case IrIntrinsicOp.StrCompareOrdinal:
          return this.Helper(PythonHelpers.CompareOrdinal, Arg(0), Arg(1));
        case IrIntrinsicOp.StrIndexOf when arguments.Count == 2 || arguments[2] is IrConst { Value: 0 }:
          return Method("find", Text(1));
        case IrIntrinsicOp.StrIndexOf:
          return this.Helper(PythonHelpers.IndexOf, Arg(0), Text(1), Arg(2));
        case IrIntrinsicOp.StrLastIndexOf when arguments.Count == 2:
          return Method("rfind", Text(1));
        case IrIntrinsicOp.StrLastIndexOf:
          return this.Helper(PythonHelpers.LastIndexOf, Arg(0), Text(1), Arg(2));
        case IrIntrinsicOp.StrStartsWith:
          return Method("startswith", Text(1));
        case IrIntrinsicOp.StrEndsWith:
          return Method("endswith", Text(1));
        case IrIntrinsicOp.StrContains:
          return new PyCompare([Text(1), Arg(0)], ["in"]);
        case IrIntrinsicOp.StrReplaceAll when arguments[1] is IrStrConst { Value.Length: > 0 } || !arguments[1].Type.IsString:
          return Method("replace", Text(1), Text(2));
        case IrIntrinsicOp.StrReplaceAll:
          return this.Helper(PythonHelpers.ReplaceAll, Arg(0), Arg(1), Arg(2));
        case IrIntrinsicOp.StrReplaceFirst:
          return Method("replace", Text(1), Text(2), Integer(1));
        case IrIntrinsicOp.StrSplit:
          return this.Helper(PythonHelpers.Split, Arg(0), Text(1), Arg(2));
        case IrIntrinsicOp.StrJoin:
          return new PyCall(new PyAttribute(Arg(0), "join"), [Arg(1)]);
        case IrIntrinsicOp.StrTrim or IrIntrinsicOp.StrTrimStart or IrIntrinsicOp.StrTrimEnd:
          var helper = StringOperations.SetOf(intrinsic) == WhitespaceSet.Ecma ? PythonHelpers.TrimEcma : PythonHelpers.TrimDotNet;
          return this.Helper(helper, Arg(0), new PyBool(intrinsic.Op != IrIntrinsicOp.StrTrimEnd), new PyBool(intrinsic.Op != IrIntrinsicOp.StrTrimStart));
        case IrIntrinsicOp.StrPadStart or IrIntrinsicOp.StrPadEnd:
          return this.Helper(PythonHelpers.Pad, Arg(0), Arg(1), Arg(2), new PyBool(intrinsic.Op == IrIntrinsicOp.StrPadStart));
        case IrIntrinsicOp.StrCodePointAt:
          return this.Helper(PythonHelpers.CodePointAt, Arg(0), Arg(1));
        default:
          return this.Helper(PythonHelpers.NextCodePoint, Arg(0), Arg(1));
      }
    }

    /// <summary>Whether the lists of the module count their changes, because a checked loop over one has a body that may change a list.</summary>
    private bool? _versioned;

    private bool Versioned => this._versioned ??= this._module.Functions
      .SelectMany(function => IrClosures.Within(function.Body)).OfType<IrForEachCollection>()
      .Any(loop => loop is { Mode: EnumerationMode.Checked, Collection.Type.IsList: true } && MayChangeAList(loop.Body));

    /// <summary>Whether a block changes a list itself or calls what may.</summary>
    private static bool MayChangeAList(IrBlock body) => IrTree.Nodes(body).Any(node =>
      node is IrCall or IrMethodCall or IrNew or IrInvoke || node is IrCollection { Arguments: [{ Type.IsList: true }, ..] } operation && IrCollections.Mutates(operation.Op));

    /// <summary>
    /// An operation on a collection. A list is a <c>list</c> - a <c>_PlyList</c> that counts its
    /// changes where a checked loop needs that - a map a <c>dict</c>, a set a <c>dict</c> of keys to
    /// <c>None</c>, both of which keep the order of insertion. Where the IR traps and Python would
    /// count from the end, a helper checks first.
    /// </summary>
    private PyExpression CollectionOperation(IrCollection operation) {
      var arguments = operation.Arguments;
      PyExpression Arg(int index) => this.Expression(arguments[index]);
      PyExpression Key(int index) => this.Key(arguments[index]);
      PyExpression Method(string name, params PyExpression[] values) => new PyCall(new PyAttribute(Arg(0), name), values);
      var element = (IrCollections.Constructs(operation.Op) ? operation.Type : arguments[0].Type).Element;
      switch (operation.Op) {
        case IrCollectionOp.ListNew:
          return this.Versioned ? this.ListHelper([]) : new PyList([]);
        case IrCollectionOp.ListFromArray:
          var copied = this.CopiedElements(arguments[0], element);
          return this.Versioned ? this.ListHelper([copied]) : copied;
        case IrCollectionOp.ListCount or IrCollectionOp.MapCount or IrCollectionOp.SetCount:
          return new PyCall(Name("len"), [Arg(0)]);
        case IrCollectionOp.ListGet:
          return this.Helper(PythonHelpers.ListGet, Arg(0), Arg(1));
        case IrCollectionOp.ListSet:
          return this.Helper(PythonHelpers.ListSet, Arg(0), Arg(1), Arg(2));
        case IrCollectionOp.ListAdd:
          return Method("append", Arg(1));
        case IrCollectionOp.ListInsert:
          return this.Helper(PythonHelpers.ListInsert, Arg(0), Arg(1), Arg(2));
        case IrCollectionOp.ListRemoveAt:
          return this.Helper(PythonHelpers.ListRemoveAt, Arg(0), Arg(1));
        case IrCollectionOp.ListRemoveLast:
          return this.Helper(PythonHelpers.ListPop, Arg(0));
        case IrCollectionOp.ListClear or IrCollectionOp.MapClear or IrCollectionOp.SetClear:
          return Method("clear");
        case IrCollectionOp.ListContains:
          return element!.IsFloat ? Compare(this.Helper(PythonHelpers.IndexOfFloat, Arg(0), Arg(1)), ">=", Integer(0)) : new PyCompare([Arg(1), Arg(0)], ["in"]);
        case IrCollectionOp.ListIncludesSameValueZero:
          return element!.IsFloat ? Compare(this.Helper(PythonHelpers.IndexOfFloat, Arg(0), Arg(1)), ">=", Integer(0)) : this.Helper(PythonHelpers.Includes, Arg(0), Arg(1));
        case IrCollectionOp.ListIndexOfEquals:
          return this.Helper(element!.IsFloat ? PythonHelpers.IndexOfFloat : PythonHelpers.IndexOfValue, Arg(0), Arg(1));
        case IrCollectionOp.ListIndexOfStrict:
          return this.Helper(element!.IsFloat ? PythonHelpers.IndexOfStrict : PythonHelpers.IndexOfIdentity, Arg(0), Arg(1));
        case IrCollectionOp.ListCopyToArray:
          return this.CopiedElements(arguments[0], element);
        case IrCollectionOp.MapNew or IrCollectionOp.SetNew when operation.Type.Order == MapOrder.SlotReuse && this.Removes(operation.Type):
          this._helpers.Add(PythonHelpers.Slots);
          return new PyCall(Name(PythonHelpers.Slots), []);
        case IrCollectionOp.MapNew or IrCollectionOp.SetNew:
          return new PyDict([], []);
        case IrCollectionOp.MapGet:
          // Python raises KeyError for an absent key, the class of the trap.
          return new PySubscript(Arg(0), Key(1));
        case IrCollectionOp.MapTryGet:
          return Method("get", Key(1));
        case IrCollectionOp.MapContainsKey or IrCollectionOp.SetContains:
          return new PyCompare([Key(1), Arg(0)], ["in"]);
        case IrCollectionOp.MapPut:
          return this.Helper(PythonHelpers.MapPut, Arg(0), Key(1), Arg(2));
        case IrCollectionOp.MapAdd:
          return this.Helper(PythonHelpers.MapAdd, Arg(0), Key(1), Arg(2));
        case IrCollectionOp.MapTryAdd:
          return this.Helper(PythonHelpers.MapTryAdd, Arg(0), Key(1), Arg(2));
        case IrCollectionOp.MapRemove or IrCollectionOp.SetRemove:
          return this.Helper(PythonHelpers.MapRemove, Arg(0), Key(1));
        case IrCollectionOp.SetAdd:
          return this.Helper(PythonHelpers.MapTryAdd, Arg(0), Key(1), new PyNone());
        default:
          return this.Inexpressible(operation.Op.ToString());
      }
    }

    /// <summary>
    /// Whether the module removes keys from maps or sets of a type, whose order of .NET's slots then
    /// differs from the order of insertion a <c>dict</c> keeps.
    /// </summary>
    private bool Removes(IrType type) => this._module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrCollection>()
      .Any(operation => operation.Op is IrCollectionOp.MapRemove or IrCollectionOp.SetRemove && operation.Arguments[0].Type == type);

    /// <summary>A key of a map or a set: a floating-point key with every NaN made the one NaN, which then finds itself.</summary>
    private PyExpression Key(IrExpr key) => key.Type.IsFloat ? this.Helper(PythonHelpers.FloatKey, this.Expression(key)) : this.Expression(key);

    /// <summary>A new list of the elements of a list or an array, each struct copied.</summary>
    private PyExpression CopiedElements(IrExpr source, IrType? element) {
      var items = this.Expression(source);
      if (element?.StructType is { } type && this._mutableStructs.Contains(type))
        return new PyCall(Name("list"), [new PyCall(Name("map"), [new PyAttribute(Name("copy"), "copy"), items])]);

      return new PyCall(Name("list"), [items]);
    }

    /// <summary>A list that counts its changes.</summary>
    private PyExpression ListHelper(IReadOnlyList<PyExpression> arguments) {
      this._helpers.Add(PythonHelpers.VersionedList);
      return new PyCall(Name(PythonHelpers.VersionedList), arguments);
    }

    /// <summary>
    /// A loop over a list, a map or a set. A list is walked by index up to its length at every step;
    /// a checked loop of a module whose lists count their changes traps at the step after a change. A
    /// map or a set is walked over a snapshot of its keys where the body may change it, which a live
    /// loop then checks again, and as it is where it may not.
    /// </summary>
    private void ForEachCollection(IrForEachCollection loop, List<PyStatement> written) {
      var collection = this.Expression(loop.Collection);
      var type = loop.Collection.Type;
      var frame = new LoopFrame(null, null);
      if (type.IsList) {
        var element = loop.Element!;
        var variable = this.Temporary("_item");
        // Python walks a list by index up to its length at every step, as a live loop does.
        PyExpression items = loop.Mode == EnumerationMode.Checked && this.Versioned ? this.Helper(PythonHelpers.EachChecked, collection) : collection;
        var body = new List<PyStatement>();
        if (this.IsCell(element) || element.Type.StructType is { } copied && this._mutableStructs.Contains(copied)) {
          PyExpression value = Name(variable);
          if (element.Type.StructType is { } structType && this._mutableStructs.Contains(structType))
            value = new PyCall(new PyAttribute(Name("copy"), "copy"), [value]);

          body.Add(this.Declare(element, value));
          body.AddRange(this.Inside(frame, loop.Body.Statements));
          written.Add(new PyFor(variable, items, body));
          return;
        }

        this._localNames.Remove(variable);
        var name = this.LocalName(element);
        this._annotated.Add(name);
        written.Add(new PyFor(name, items, this.Inside(frame, loop.Body.Statements)));
        return;
      }

      // A map or a set: the keys, and for a map the value of each.
      var changes = MayChangeACollection(loop.Body);
      PyExpression keys = !changes ? collection
        : loop.Mode == EnumerationMode.Live ? this.Helper(PythonHelpers.EachLiveKey, collection)
        : this.Helper(PythonHelpers.EachKey, collection);
      var pass = new List<PyStatement>();
      var key = loop.Element is { } named && !this.IsCell(named) ? this.LocalName(named) : this.Temporary("_key");
      this._annotated.Add(key);
      if (loop.Element is { } cell && this.IsCell(cell))
        pass.Add(this.Declare(cell, Name(key)));

      if (loop.Value is { } entry) {
        if (!changes && !this.IsCell(entry) && loop.Element is not null) {
          var valueName = this.LocalName(entry);
          this._annotated.Add(valueName);
          written.Add(new PyFor($"{key}, {valueName}", new PyCall(new PyAttribute(collection, "items"), []), [.. pass, .. this.Inside(frame, loop.Body.Statements)]));
          return;
        }

        if (!changes && !this.IsCell(entry)) {
          var valueName = this.LocalName(entry);
          this._annotated.Add(valueName);
          written.Add(new PyFor(valueName, new PyCall(new PyAttribute(collection, "values"), []), this.Inside(frame, loop.Body.Statements)));
          return;
        }

        var holder = this.Temporary("_map");
        written.Add(new PyAssign(Name(holder), collection));
        keys = !changes ? Name(holder)
          : loop.Mode == EnumerationMode.Live ? this.Helper(PythonHelpers.EachLiveKey, Name(holder))
          : this.Helper(PythonHelpers.EachKey, Name(holder));
        pass.Add(this.Declare(entry, new PySubscript(Name(holder), Name(key))));
      }

      pass.AddRange(this.Inside(frame, loop.Body.Statements));
      written.Add(new PyFor(key, keys, pass));
    }

    /// <summary>Whether a block changes a collection itself or calls what may.</summary>
    private static bool MayChangeACollection(IrBlock body) => IrTree.Nodes(body).Any(node =>
      node is IrCall or IrMethodCall or IrNew or IrInvoke || node is IrCollection operation && IrCollections.Mutates(operation.Op));

  }

}
