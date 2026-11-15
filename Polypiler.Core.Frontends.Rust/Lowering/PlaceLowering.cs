using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>A place a value lives in: a local, an element of an array, a view or a list, a field of a place.</summary>
/// <param name="Type">The IR type of the value.</param>
internal abstract record Place(IrType Type);

/// <summary>A local or a parameter.</summary>
internal sealed record LocalPlace(IrLocal Local) : Place(Local.Type);

/// <summary>An element of an array or a view; the container and the index are read once, before.</summary>
internal sealed record ElementPlace(IrExpr Container, IrExpr Index) : Place(Container.Type.Element!);

/// <summary>An element of a list; the list and the index are read once, before.</summary>
internal sealed record ListElementPlace(IrExpr List, IrExpr Index) : Place(List.Type.Element!);

/// <summary>A field of a place.</summary>
internal sealed record FieldPlace(Place Parent, IrField Field) : Place(Field.Type);

/// <summary>A value no one else can see, borrowed as a place: a temporary.</summary>
internal sealed record TemporaryPlace(IrLocal Local) : Place(Local.Type);

/// <summary>What an iteration or a destructuring hands to a pattern: a value, a place a reference reaches, or the parts of a tuple.</summary>
internal abstract record Item;

/// <summary>A value, also a shared reference to a value, which is the value.</summary>
internal sealed record ValueItem(IrExpr Value) : Item;

/// <summary>A reference to a place, through which a <c>&amp;mut</c> writes.</summary>
internal sealed record PlaceItem(Place Place) : Item;

/// <summary>The parts of a tuple that was never made: of <c>zip</c>, <c>enumerate</c>, a tuple expression.</summary>
internal sealed record TupleItem(IReadOnlyList<Item> Parts) : Item;

/// <summary>Places: reading, writing, borrowing, and binding patterns to what they match.</summary>
internal sealed partial class FunctionLowering {

  // ---------------------------------------------------------------- reading and writing

  /// <summary>The value of a place.</summary>
  private IrExpr Read(Place place) => place switch {
    LocalPlace local => new IrLocalRef(local.Local),
    TemporaryPlace temporary => new IrLocalRef(temporary.Local),
    ElementPlace element => new IrArrayLoad(element.Container, element.Index),
    ListElementPlace element => new IrCollection(IrCollectionOp.ListGet, element.Type, [element.List, element.Index]),
    FieldPlace field => new IrFieldLoad(this.Target(field.Parent), field.Field),
    _ => throw new System.InvalidOperationException()
  };

  /// <summary>The target of a field load or store: the place itself where the IR can name it, else its value.</summary>
  private IrExpr Target(Place place) => this.Read(place);

  /// <summary>Whether the IR writes a field of a place directly: a field of a local, an array element or a field of one.</summary>
  private static bool Addressable(Place place) => place switch {
    LocalPlace or TemporaryPlace or ElementPlace => true,
    FieldPlace field => Addressable(field.Parent),
    _ => false
  };

  /// <summary>Writes a value to a place; a field of a list element is read, changed and written back.</summary>
  private void Write(Place place, IrExpr value) {
    switch (place) {
      case LocalPlace local:
        this.Emit(new IrAssign(local.Local, value));
        return;
      case TemporaryPlace temporary:
        this.Emit(new IrAssign(temporary.Local, value));
        return;
      case ElementPlace element:
        this.Emit(new IrStore(element.Container, element.Index, value));
        return;
      case ListElementPlace element:
        this.Emit(new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [element.List, element.Index, value])));
        return;
      case FieldPlace field when Addressable(field.Parent):
        this.Emit(new IrFieldStore(this.Target(field.Parent), field.Field, value));
        return;
      case FieldPlace field: {
        var stored = this.Stable(value, "field");
        var copy = this.Temporary(this.Read(field.Parent), "element");
        this.Emit(new IrFieldStore(copy, field.Field, stored));
        this.Write(field.Parent, copy);
        return;
      }
    }
  }

  // ---------------------------------------------------------------- place expressions

  /// <summary>
  /// The place an expression names, its index and container read into locals: a variable, what a
  /// <c>&amp;mut</c> variable reaches, a field or an element of a place, <c>*r</c>. A temporary is
  /// borrowed as a local of its own. <see langword="null"/> after an expression that never ends.
  /// </summary>
  private Place? PlaceOf(Expr expression) {
    switch (expression) {
      case PathExpr path when this._typed.Bindings.TryGetValue(path, out var binding) && binding.Constant is null:
        if (this._places.TryGetValue(binding, out var aliased))
          return aliased;

        if (this._values[binding] is IrLocalRef local)
          return new LocalPlace(local.Local);

        return new TemporaryPlace(this.Temporary(this._values[binding], binding.Name).Local);
      case DerefExpr deref:
        return this.PlaceOf(deref.Operand);
      case FieldExpr field: {
        var parent = this.PlaceOf(field.Target);
        if (parent is null)
          return null;

        return new FieldPlace(parent, this.FieldOf(this._typed.TypeOf(field.Target).Dereferenced, field.Name));
      }
      case IndexExpr { Index: not RangeExpr } index: {
        var container = this.Container(index.Target);
        var position = this.Position(this.Value(index.Index));
        if (this._diverged)
          return null;

        return container.Type.IsList ? new ListElementPlace(container, position) : new ElementPlace(container, position);
      }
      default: {
        var value = this.Lower(expression);
        if (value is null || this._diverged)
          return null;

        return new TemporaryPlace(this.Temporary(value, "borrowed").Local);
      }
    }
  }

  /// <summary>The place a <c>&amp;mut place</c> expression borrows, or a <c>&amp;mut</c> variable passed on; <see langword="null"/> for anything else.</summary>
  private Place? PlaceOfReference(Expr expression) => expression switch {
    ReferenceExpr { Mutable: true } reference => this.PlaceOf(reference.Operand),
    PathExpr path when this._typed.Bindings.TryGetValue(path, out var binding) && this._places.ContainsKey(binding) => this._places[binding],
    _ => null
  };

  /// <summary>The field of a struct or a tuple by its name or position.</summary>
  private IrField FieldOf(RType type, string name) {
    var declaration = this.Ir(type).StructType!;
    return type.Resolved.Kind == RKind.Tuple ? declaration.Fields[int.Parse(name, System.Globalization.CultureInfo.InvariantCulture)] : declaration.Field(ModuleLowering.FieldName(name))!;
  }

  /// <summary>An array, a view or a list an element is read from or written to, read once into a local.</summary>
  private IrExpr Container(Expr expression) {
    // A constant table is read where it is.
    if (expression is PathExpr path && this.Table(path) is { } table)
      return new IrConstRef(table);

    var value = this.Lower(expression) ?? this.Zero(this.Ir(this._typed.TypeOf(expression)));
    return this.Stable(value, "container");
  }

  /// <summary>An index of type <c>usize</c> as the <c>i32</c> the IR indexes with: beyond <c>i32::MAX</c> it saturates, which is outside every array.</summary>
  private IrExpr Position(IrExpr index) {
    if (index is IrConst constant)
      return new IrConst(IrType.I32, (ulong)constant.Value > int.MaxValue ? int.MaxValue : constant.Value);

    var stable = this.Stable(index, "index");
    return Cast(new IrIntrinsic(IrIntrinsicOp.Min, IrType.U64, [stable, new IrConst(IrType.U64, int.MaxValue)]), IrType.I32);
  }

  // ---------------------------------------------------------------- copies and conversions

  /// <summary>A copy of a value that holds an array, so that the copy and the original never share it.</summary>
  private IrExpr Duplicate(IrExpr value, RType type) {
    var resolved = type.Resolved;
    switch (resolved.Kind) {
      case RKind.Array: {
        var source = value is IrConstRef ? value : this.Stable(value, "source");
        var length = this.Stable(new IrArrayLength(source), "length");
        var copy = this.Temporary(new IrNewArray(source.Type.IsArray ? source.Type : IrType.Array(this.Ir(resolved.Element!)), length), "copy");
        if (source is IrConstRef || !resolved.Element!.IsNumber) {
          var index = this.NewLocal("i", IrType.I32);
          this.Emit(new IrDeclare(index, new IrConst(IrType.I32, 0)));
          var element = new IrArrayLoad(source, new IrLocalRef(index));
          this.Emit(new IrFor(Binary(IrBinaryOp.Less, new IrLocalRef(index), length),
            new IrBlock([new IrStore(copy, new IrLocalRef(index), element)]),
            new IrBlock([new IrAssign(index, Binary(IrBinaryOp.Add, new IrLocalRef(index), new IrConst(IrType.I32, 1)))])));
        } else {
          this.Emit(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [source, new IrConst(IrType.I32, 0), copy, new IrConst(IrType.I32, 0), length])));
        }

        return copy;
      }
      default: {
        // A struct or a tuple: field by field, the arrays in it copied.
        var source = this.Stable(value, "source");
        var declaration = source.Type.StructType!;
        var parts = resolved.Kind == RKind.Struct ? resolved.Struct!.Fields.Select(field => field.Type).ToList() : resolved.Elements.ToList();
        var fields = declaration.Fields.Select((field, index) => {
          IrExpr load = new IrFieldLoad(source, field);
          return HoldsArray(parts[index]) ? this.Duplicate(load, parts[index]) : load;
        }).ToList();
        return new IrStructNew(source.Type, fields);
      }
    }
  }

  /// <summary>
  /// A value converted to the IR type a coercion site wants (The Rust Reference, "Type coercions"):
  /// an array to a view of it, a writable view to a read-only one, a list to a read-only view of a
  /// copy of its elements (which a shared borrow cannot tell from the list).
  /// </summary>
  private IrExpr Converted(IrExpr value, RType target, RType? source) {
    var wanted = this.Ir(target);
    if (value.Type == wanted || !wanted.IsView || this._diverged)
      return value;

    if (value.Type.IsList)
      value = new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(value.Type.Element!), [value]);

    return new IrIntrinsic(IrIntrinsicOp.ViewOf, wanted, [value]);
  }

  // ---------------------------------------------------------------- binding patterns

  /// <summary>The value of an item: a place read, a tuple made.</summary>
  private IrExpr ValueOf(Item item, RType type) {
    switch (item) {
      case ValueItem value:
        return value.Value;
      case PlaceItem place:
        return this.Read(place.Place);
      default: {
        var tuple = (TupleItem)item;
        var resolved = type.Resolved;
        var parts = tuple.Parts.Select((part, index) => this.ValueOf(part, resolved.Elements[index])).ToList();
        return new IrStructNew(this.Ir(resolved), parts);
      }
    }
  }

  /// <summary>
  /// Binds an irrefutable pattern to an item of a type: a name to a local holding the value (or, for
  /// a <c>&amp;mut</c> to a value, to the place), a tuple, a struct or a reference pattern to its parts.
  /// </summary>
  private void Bind(Pattern pattern, Item item, RType type) {
    var resolved = type.Resolved;
    switch (pattern) {
      case WildcardPattern or RestPattern:
        if (item is ValueItem { Value: var value } && !IsSimple(value))
          this.Effect(value);

        return;
      case IdentifierPattern identifier when this._typed.Bindings.TryGetValue(identifier, out var binding) && binding.Constant is null && !this._typed.Resolutions.ContainsKey(identifier): {
        if (IsExclusiveValue(binding.Type)) {
          if (item is PlaceItem place) {
            this._places[binding] = place.Place;
          } else {
            this.Refuse(RustFrontend.UnsupportedPattern, $"binding '{identifier.Name}' by mutable reference into a value", identifier.At);
            this._values[binding] = this.ValueOf(item, binding.Type.Dereferenced);
          }

          return;
        }

        if (binding.Type.Resolved.Kind == RKind.Reference && binding.Type.Resolved.Element!.Resolved.Kind is RKind.Slice && item is ValueItem view) {
          this.Declared(identifier, view.Value);
          return;
        }

        this.Declared(identifier, this.ValueOf(item, binding.Type.Resolved.Kind == RKind.Reference && resolved.Kind != RKind.Reference ? resolved : binding.Type.Dereferenced.Kind == RKind.Tuple ? binding.Type.Dereferenced : type));
        return;
      }
      case IdentifierPattern or LiteralPattern:
        // A constant, a unit struct or a variant: an irrefutable pattern of it matches without a test.
        return;
      case ReferencePattern reference:
        this.Bind(reference.Inner, item is PlaceItem place2 ? new ValueItem(this.Read(place2.Place)) : item, resolved.Kind == RKind.Reference ? resolved.Element! : type);
        return;
    }

    // A pattern that is no reference pattern looks through a reference.
    if (resolved.Kind == RKind.Reference) {
      this.Bind(pattern, item is PlaceItem place3 && !IsExclusiveValue(type) ? new ValueItem(this.Read(place3.Place)) : item, resolved.Element!);
      return;
    }

    switch (pattern) {
      case TuplePattern tuple when resolved.Kind == RKind.Unit:
        return;
      case TuplePattern tuple: {
        var parts = this.Parts(item, resolved, resolved.Elements.Count);
        foreach (var (element, index) in Spread(tuple.Elements, resolved.Elements.Count))
          this.Bind(element, parts[index], resolved.Elements[index]);

        return;
      }
      case TupleStructPattern tupleStruct: {
        var info = resolved.Struct!;
        var parts = this.Parts(item, resolved, info.Fields.Count);
        foreach (var (element, index) in Spread(tupleStruct.Elements, info.Fields.Count))
          this.Bind(element, parts[index], info.Fields[index].Type);

        return;
      }
      case StructPattern structure: {
        var info = resolved.Struct!;
        var parts = this.Parts(item, resolved, info.Fields.Count);
        foreach (var (name, field) in structure.Fields) {
          var index = info.Fields.FindIndex(candidate => candidate.Name == name);
          this.Bind(field, parts[index], info.Fields[index].Type);
        }

        return;
      }
    }
  }

  /// <summary>The parts of a tuple or a struct item: its own parts, the fields of its place, or the fields of its value read once.</summary>
  private List<Item> Parts(Item item, RType type, int count) {
    if (item is TupleItem tuple)
      return [.. tuple.Parts];

    var declaration = this.Ir(type).StructType!;
    if (item is PlaceItem place)
      return [.. declaration.Fields.Select(field => (Item)new PlaceItem(new FieldPlace(place.Place, field)))];

    var value = this.Stable(((ValueItem)item).Value, "destructured");
    return [.. declaration.Fields.Select(field => (Item)new ValueItem(new IrFieldLoad(value, field)))];
  }

  /// <summary>The patterns of a list with an optional <c>..</c>, each with the position of the element it stands for.</summary>
  private static IEnumerable<(Pattern Pattern, int Index)> Spread(IReadOnlyList<Pattern> patterns, int count) {
    var rest = patterns.ToList().FindIndex(pattern => pattern is RestPattern);
    if (rest < 0)
      return patterns.Select((pattern, index) => (pattern, index));

    var after = patterns.Count - rest - 1;
    return patterns.Take(rest).Select((pattern, index) => (pattern, index))
      .Concat(patterns.Skip(rest + 1).Select((pattern, index) => (pattern, count - after + index)));
  }

}
