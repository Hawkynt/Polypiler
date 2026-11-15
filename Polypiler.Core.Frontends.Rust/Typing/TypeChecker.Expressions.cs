using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>Paths, calls, references, fields, indices, tuples, arrays, struct expressions, <c>?</c>, closures and macros.</summary>
internal sealed partial class TypeChecker {

  /// <summary>The functions of <c>core</c> the subset imports, by their path after <c>std::</c> or <c>core::</c>.</summary>
  public static readonly IReadOnlySet<string> LibraryFunctions = new HashSet<string> { "cmp::min", "cmp::max", "mem::swap" };

  // ---------------------------------------------------------------- paths

  private RType Path(PathExpr path, RType? expected) {
    if (path.Segments.Count == 1) {
      var name = path.Segments[0];
      if (this.Lookup(name) is { } binding) {
        this._typed.Bindings[path] = binding;
        // A closure variable is called where the call names it, or handed to an iterator method; anything else lets it escape.
        if (binding.Type.Resolved.Kind == RKind.Closure && !this._closureArgument && this._escaped.Add(binding))
          this.Refuse(new Refusal(RustFrontend.EscapingClosure, $"closure '{name}' used other than called", path.At));

        return binding.Type;
      }

      if (this.LocalFunction(name) is { } local) {
        this._typed.Callees[path] = local;
        return RType.Function(local.Parameters, local.Result);
      }

      if (name == "None") {
        this._typed.Resolutions[path] = "None";
        if (path.Generics.TryGetValue(0, out var generics) && generics.Count == 1)
          return RType.Option(this.Resolve(generics[0], false) ?? this.ErrorType());

        return expected?.Resolved is { Kind: RKind.Option } option ? option : RType.Option(this.Fresh(VariableKind.Any));
      }
    }

    if (path.Segments.Count == 2 && RType.Named(path.Segments[0]) is { } owner && !path.Segments[0].Equals("Self", StringComparison.Ordinal)) {
      if (owner.Kind == RKind.Integer && path.Segments[1] is "MIN" or "MAX")
        return owner;

      if (owner.Kind == RKind.Integer && path.Segments[1] == "BITS")
        return RType.U32;

      if (owner.Kind == RKind.Float && path.Segments[1] is "MIN" or "MAX" or "NAN" or "INFINITY" or "NEG_INFINITY" or "EPSILON" or "MIN_POSITIVE")
        return owner;

      return this.Refused(RustFrontend.Unsupported, path.Text, path.At);
    }

    if (path.Segments.Count == 2 && RType.Unsupported.Contains(path.Segments[0]))
      return this.Refused(RustFrontend.UnsupportedType, path.Segments[0], path.At);

    if (path.Segments.Count == 2 && _refusedTypes.TryGetValue(path.Segments[0], out var refusedType))
      return this.Refused(refusedType, path.Text, path.At);

    var target = this.PathTarget(path);
    switch (target) {
      case FunctionSignature function:
        this._typed.Callees[path] = function;
        return RType.Function(function.Receiver is null ? function.Parameters : [function.Receiver, .. function.Parameters], function.Result);
      case Binding constant:
        this._typed.Bindings[path] = constant;
        return constant.Type;
      case (EnumInfo enumeration, string variant):
        this._typed.Resolutions[path] = (enumeration, variant);
        return RType.EnumOf(enumeration);
      case StructInfo { Item.Form: StructForm.Unit } structure:
        this._typed.Resolutions[path] = structure;
        return RType.StructOf(structure);
      case StructInfo structure:
        return this.Refused(RustFrontend.Unsupported, $"constructor of '{structure.Name}' as a value", path.At);
      case string library:
        return this.Refused(RustFrontend.Unsupported, $"'{library}' as a value", path.At);
    }

    return this.UnknownPath(path);
  }

  private RType UnknownPath(PathExpr path) {
    // Box::new, Rc::new and their relatives: the prelude's smart pointers and collections, refused by kind.
    if (_refusedTypes.TryGetValue(path.Segments[0], out var refused) && this._crate.Lookup(this._module, path.Segments[0], new(ReferenceEqualityComparer.Instance)) is null)
      return this.Refused(refused, path.Text, path.At);

    if (path.Segments[0] is "std" or "core" or "alloc")
      return this.Refused(RustFrontend.ForeignPath, path.Text, path.At);

    if (path.Segments.Count == 1)
      throw this.Invalid($"E0425: cannot find value `{path.Segments[0]}` in this scope", path.At);

    if (this._crate.Resolve(this._module, [.. path.Segments.Take(path.Segments.Count - 1)], path.At, false) is not null
        || this.TypeOfPath([.. path.Segments.Take(path.Segments.Count - 1)], path.At) is not null)
      throw this.Invalid($"E0425: cannot find `{path.Segments[^1]}` in `{string.Join("::", path.Segments.Take(path.Segments.Count - 1))}`", path.At);

    // An unknown first segment: a type of that name would be the crate's own (E0433); a lowercase one names another crate.
    if (this._crate.Lookup(this._module, path.Segments[0], new(ReferenceEqualityComparer.Instance)) is null && path.Segments[0] is not ("crate" or "self" or "super" or "Self")) {
      if (path.Segments[0].Length > 0 && char.IsUpper(path.Segments[0][0]))
        throw this.Invalid($"E0433: failed to resolve: use of undeclared type `{path.Segments[0]}`", path.At);

      return this.Refused(RustFrontend.ForeignPath, path.Text, path.At);
    }

    throw this.Invalid($"E0433: failed to resolve: `{path.Text}`", path.At);
  }

  /// <summary>
  /// What a path of the crate names: a function, a constant (as its <see cref="Binding"/>), a
  /// variant of an enum (the enum and the variant), a struct, an associated function or constant,
  /// a function of <c>core</c> (its path), or <see langword="null"/>.
  /// </summary>
  private object? PathTarget(PathExpr path) {
    var segments = path.Segments;
    if (segments[0] is "std" or "core" && segments.Count == 3 && LibraryFunctions.Contains(segments[1] + "::" + segments[2]))
      return segments[1] + "::" + segments[2];

    if (segments.Count == 2 && this._crate.Lookup(this._module, segments[0], new(ReferenceEqualityComparer.Instance)) is { Target: LibraryModule library }) {
      var function = library.Name + "::" + segments[1];
      return LibraryFunctions.Contains(function) ? function : null;
    }

    var symbol = this._crate.Resolve(this._module, segments, path.At, true);
    if (symbol is not null)
      return this.TargetOf(symbol);

    // A type and one of its members: an associated function or constant, a variant.
    if (segments.Count >= 2 && this.TypeOfPath([.. segments.Take(segments.Count - 1)], path.At) is { } owner)
      return this.Member(owner, segments[^1], path.At);

    return null;
  }

  private object? TargetOf(Symbol symbol) => symbol.Target switch {
    FunctionItem function => this._signatures.GetValueOrDefault(function),
    ConstItem constant => this._constants.GetValueOrDefault(constant) ?? throw this.Invalid($"E0391: cycle detected when evaluating the constant `{constant.Name}`", constant.At),
    StructItem structure => this._structs[structure],
    EnumItem enumeration => this._enums[enumeration],
    string library => library,
    _ => null
  };

  /// <summary>The struct or enum type a path names, <c>Self</c> included, or <see langword="null"/>.</summary>
  private RType? TypeOfPath(IReadOnlyList<string> segments, Span at) {
    if (segments is ["Self"])
      return this._self;

    var symbol = this._crate.Resolve(this._module, segments, at, false);
    return symbol?.Target switch {
      StructItem structure => RType.StructOf(this._structs[structure]),
      EnumItem enumeration => RType.EnumOf(this._enums[enumeration]),
      _ => null
    };
  }

  /// <summary>A member of a struct or an enum: an associated function or constant, a variant.</summary>
  private object? Member(RType owner, string name, Span at) {
    var resolved = owner.Resolved;
    if (resolved.Kind == RKind.Enum && resolved.Enum!.Variants.Any(variant => variant.Name == name))
      return (resolved.Enum, name);

    var methods = resolved.Kind == RKind.Struct ? resolved.Struct!.Methods : resolved.Enum!.Methods;
    if (methods.TryGetValue(name, out var method))
      return method;

    if (this.TraitMethodOf(resolved, name, at) is { } traitMethod)
      return traitMethod;

    if (this.AssociatedConstant(owner, name) is { } constant)
      return constant;

    return null;
  }

  private Binding? AssociatedConstant(RType owner, string name) {
    var resolved = owner.Resolved;
    var constants = resolved.Kind == RKind.Struct ? resolved.Struct!.Constants : resolved.Kind == RKind.Enum ? resolved.Enum!.Constants : null;
    return constants?.GetValueOrDefault(name);
  }

  // ---------------------------------------------------------------- references

  private RType Reference(ReferenceExpr reference, RType? expected) {
    var target = expected?.Resolved is { Kind: RKind.Reference } wanted ? wanted.Element : null;
    var operand = this.Value(reference.Operand, target);
    if (reference.Mutable) {
      this._deferred.Add(() => {
        if (!this.MutablePlace(reference.Operand))
          throw this.Invalid($"E0596: cannot borrow `{Describe(reference.Operand)}` as mutable, as it is not declared as mutable", reference.At);
      });
    }

    if (reference.Operand is IndexExpr { Index: RangeExpr } slice && reference.Mutable && this._typed.TypeOf(slice.Target).Dereferenced.Kind == RKind.Vec)
      return this.Refused(RustFrontend.UnsupportedElement, "&mut Vec<T> as a mutable slice", reference.At);

    if (operand.Resolved.Kind == RKind.Reference && operand.Resolved.Element!.Resolved.Kind != RKind.Slice && reference.Operand is not IndexExpr)
      return RType.Reference(operand, reference.Mutable);

    return RType.Reference(operand, reference.Mutable);
  }

  /// <summary>Whether a place may be borrowed mutably: a mutable variable, a field or an element of one, or what a <c>&amp;mut</c> reaches.</summary>
  private bool MutablePlace(Expr place) {
    switch (place) {
      case PathExpr path when this._typed.Bindings.TryGetValue(path, out var binding):
        return binding.Mutable || binding.Type.Resolved is { Kind: RKind.Reference, Mutable: true } && binding.Type.Resolved.Element!.Resolved.Kind is RKind.Slice or RKind.Array or RKind.Vec;
      case FieldExpr field:
        return this.ThroughMutable(field.Target);
      case IndexExpr index:
        return this.ThroughMutable(index.Target);
      case DerefExpr deref:
        return this._typed.TypeOf(deref.Operand) is { Kind: RKind.Reference, Mutable: true };
      default:
        // Any other expression is a temporary, which may be borrowed mutably: `&mut [1, 2, 3]`, `&mut 10`.
        return place is not PathExpr;
    }
  }

  private RType Deref(DerefExpr deref) {
    var operand = this.Value(deref.Operand).Resolved;
    if (operand.Kind == RKind.Reference)
      return operand.Element!;

    if (this.IsError(operand))
      return operand;

    if (operand.Kind == RKind.Variable)
      throw this.Unresolved(Describe(deref.Operand), deref.At);

    throw this.Invalid($"E0614: type `{operand}` cannot be dereferenced", deref.At);
  }

  // ---------------------------------------------------------------- calls

  private bool _closureArgument;

  /// <summary>The closure variables already refused for escaping: one refusal for each.</summary>
  private readonly HashSet<Binding> _escaped = new(ReferenceEqualityComparer.Instance);

  private RType Call(CallExpr call, RType? expected) {
    if (call.Callee is not PathExpr path) {
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      return this.Refused(call.Callee is RefusedExpr refused ? refused.Refusal : new Refusal(RustFrontend.Unsupported, "call of a value", call.Callee.At));
    }

    // Some, Ok and Err: the constructors of the prelude.
    if (path.Segments is [var constructor] && constructor is "Some" or "Ok" or "Err" && this.Lookup(constructor) is null && this.LocalFunction(constructor) is null
        && this._crate.Lookup(this._module, constructor, new(ReferenceEqualityComparer.Instance)) is null)
      return this.Constructor(call, constructor, expected);

    if (path.Segments.Count == 1 && this.Intrinsic(call, path.Segments[0]) is { } intrinsic)
      return intrinsic;

    // A variable: a closure or a function pointer.
    if (path.Segments.Count == 1 && this.Lookup(path.Segments[0]) is { } variable) {
      this._typed.Bindings[path] = variable;
      this._typed.Types[path] = variable.Type;
      var callee = variable.Type.Resolved;
      if (callee.Kind is RKind.Closure or RKind.Function) {
        if (callee.Kind == RKind.Closure)
          this._typed.Closures[call] = (ClosureInfo)callee.Declaration!;

        this.Arguments(call, callee.Elements, "closure");
        return callee.Element!;
      }

      throw this.Invalid($"E0618: expected function, found `{callee}`", path.At);
    }

    if (path.Segments.Count == 2 && this._typeParameters?.GetValueOrDefault(path.Segments[0]) is { } parameter)
      return this.ParameterCall(call, parameter, path.Segments[1]);

    var target = path.Segments.Count == 1 && this.LocalFunction(path.Segments[0]) is { } local ? local : this.PathTarget(path);
    switch (target) {
      case FunctionSignature { Generics.Count: > 0 } template:
        this.Uses(template.Module);
        return this.GenericCall(call, path, template);
      case FunctionSignature function: {
        this._typed.Callees[call] = function;
        IReadOnlyList<RType> parameters = function.Receiver is null ? function.Parameters : [function.Receiver, .. function.Parameters];
        this.Arguments(call, parameters, "function");
        this.Uses(function.Module);
        return function.Result;
      }
      case StructInfo { Item.Form: StructForm.Tuple } structure: {
        this._typed.Resolutions[call] = structure;
        this.Arguments(call, [.. structure.Fields.Select(field => field.Type)], "struct");
        this.Uses(structure.Module);
        return RType.StructOf(structure);
      }
      case string library:
        return this.LibraryCall(call, library);
    }

    // The associated functions of the standard library the subset has.
    if (path.Segments.Count >= 2 && (this.TextCall(call, path) ?? this.CollectionCall(call, path, expected) ?? this.StandardCall(call, path, expected)) is { } standard)
      return standard;


    foreach (var argument in call.Arguments)
      this.Expression(argument);

    return this.UnknownPath(path);
  }

  /// <summary>Records that the current module uses an item of another, which orders the modules.</summary>
  private void Uses(ModuleScope? module) {
    if (module is not null && !ReferenceEquals(module, this._module))
      this.ModuleUses.Add((this._module, module));
  }

  /// <summary>The pairs of a module and a module whose items it uses.</summary>
  public HashSet<(ModuleScope User, ModuleScope Used)> ModuleUses { get; } = [];

  /// <summary>Checks the arguments of a call against the parameter types, each a coercion site.</summary>
  private void Arguments(CallExpr call, IReadOnlyList<RType> parameters, string what) {
    if (call.Arguments.Count != parameters.Count)
      throw this.Invalid($"E0061: this {what} takes {parameters.Count} argument{(parameters.Count == 1 ? "" : "s")} but {call.Arguments.Count} were supplied", call.At);

    for (var index = 0; index < parameters.Count; ++index)
      this.Coerce(parameters[index], this.Value(call.Arguments[index], parameters[index]), call.Arguments[index].At);
  }

  private RType Constructor(CallExpr call, string name, RType? expected) {
    this._typed.Resolutions[call] = name;
    if (call.Arguments.Count != 1)
      throw this.Invalid($"E0061: this enum variant takes 1 argument but {call.Arguments.Count} were supplied", call.At);

    var wanted = expected?.Resolved;
    switch (name) {
      case "Some": {
        var value = wanted is { Kind: RKind.Option } option ? option.Element! : this.Fresh(VariableKind.Any);
        this.Coerce(value, this.Value(call.Arguments[0], value), call.Arguments[0].At);
        return RType.Option(value);
      }
      default: {
        var result = wanted is { Kind: RKind.Result } known ? known : RType.Result(this.Fresh(VariableKind.Any), this.Fresh(VariableKind.Any));
        var part = name == "Ok" ? result.Element! : result.Error;
        this.Coerce(part, this.Value(call.Arguments[0], part), call.Arguments[0].At);
        return result;
      }
    }
  }

  /// <summary><c>min</c>, <c>max</c> and <c>swap</c> of <c>core</c>.</summary>
  private RType LibraryCall(CallExpr call, string library) {
    this._typed.Resolutions[call] = library;
    if (call.Arguments.Count != 2)
      throw this.Invalid($"E0061: this function takes 2 arguments but {call.Arguments.Count} were supplied", call.At);

    var first = this.Value(call.Arguments[0]);
    var second = this.Value(call.Arguments[1]);
    this.Unify(first, second, call.Arguments[1].At);
    if (library == "mem::swap") {
      this._deferred.Add(() => {
        if (first.Resolved is not { Kind: RKind.Reference, Mutable: true })
          throw this.Invalid($"E0308: mismatched types: expected `&mut _`, found `{first}`", call.Arguments[0].At);

        if (call.Arguments.Any(argument => argument is not ReferenceExpr { Mutable: true }))
          this.Refuse(new Refusal(RustFrontend.Unsupported, "mem::swap of references that are not '&mut place'", call.At));
      });
      return RType.Unit;
    }

    this._deferred.Add(() => {
      if (!first.Resolved.IsScalar && !this.IsError(first))
        this.Refuse(new Refusal(RustFrontend.Unsupported, $"{library} of '{first}'", call.At));
    });
    return first;
  }

  /// <summary><c>Vec::new</c>, <c>Vec::with_capacity</c>, <c>u32::from_be_bytes</c> and their relatives, <c>f32::from_bits</c>, <c>S::default</c>.</summary>
  private RType? StandardCall(CallExpr call, PathExpr path, RType? expected) {
    var owner = path.Segments[^2];
    var name = path.Segments[^1];
    var prefix = path.Segments.Take(path.Segments.Count - 2).ToList();
    var library = prefix.Count == 0 || prefix is ["std" or "alloc", "vec"] || prefix is ["std" or "core", _];
    if (owner == "Vec" && library && name is "new" or "with_capacity") {
      this._typed.Resolutions[call] = "Vec::" + name;
      if (call.Arguments.Count != (name == "new" ? 0 : 1))
        throw this.Invalid($"E0061: this function takes {(name == "new" ? 0 : 1)} argument{(name == "new" ? "s" : "")} but {call.Arguments.Count} were supplied", call.At);

      if (name == "with_capacity")
        this.Unify(RType.Usize, this.Value(call.Arguments[0], RType.Usize), call.Arguments[0].At);

      var element = path.Generics.TryGetValue(path.Segments.Count - 2, out var generics) && generics.Count == 1
        ? this.Resolve(generics[0], false) ?? this.ErrorType()
        : expected?.Resolved is { Kind: RKind.Vec } vector ? vector.Element! : this.Fresh(VariableKind.Any);
      return RType.Vec(element);
    }

    if (RType.Named(owner) is { } primitive && prefix.Count == 0) {
      if (primitive.Kind == RKind.Float && name == "from_bits" && call.Arguments.Count == 1) {
        this._typed.Resolutions[call] = "from_bits";
        this.Unify(primitive.Bits == 32 ? RType.U32 : RType.U64, this.Value(call.Arguments[0]), call.Arguments[0].At);
        return primitive;
      }

      if (name is "from_le_bytes" or "from_be_bytes" && primitive.Kind is RKind.Integer or RKind.Float && call.Arguments.Count == 1) {
        this._typed.Resolutions[call] = name;
        var bytes = RType.Array(RType.U8, primitive.Bits / 8);
        this.Unify(bytes, this.Value(call.Arguments[0], bytes), call.Arguments[0].At);
        return primitive;
      }

      if (name == "from_ne_bytes")
        return this.Refused(RustFrontend.UnsupportedSliceMethod, $"{owner}::{name}", call.At);

      return null;
    }

    if (name == "default" && call.Arguments.Count == 0 && this.TypeOfPath([.. path.Segments.Take(path.Segments.Count - 1)], path.At) is { } type) {
      var derives = type.Resolved.Kind == RKind.Struct ? type.Struct!.Derives : type.Enum!.Derives;
      if (!derives.Contains("Default"))
        throw this.Invalid($"E0599: no function or associated item named `default` found for `{type}`", call.At);

      this._typed.Resolutions[call] = "default";
      return type;
    }

    return null;
  }

  // ---------------------------------------------------------------- fields and indices

  private RType Field(FieldExpr field) {
    var target = this.Value(field.Target).Dereferenced;
    if (target.Kind == RKind.Tuple && int.TryParse(field.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position)) {
      if (position >= target.Elements.Count)
        throw this.Invalid($"E0609: no field `{field.Name}` on type `{target}`", field.At);

      return target.Elements[position];
    }

    if (target.Kind == RKind.Struct) {
      var info = target.Struct!;
      var index = info.Fields.FindIndex(candidate => candidate.Name == field.Name);
      if (index < 0)
        throw this.Invalid($"E0609: no field `{field.Name}` on type `{target}`", field.At);

      if (!info.Fields[index].IsVisible && !this._module.IsWithin(info.Module!))
        throw this.Invalid($"E0616: field `{field.Name}` of struct `{info.Name}` is private", field.At);

      return info.Fields[index].Type;
    }

    if (this.IsError(target))
      return target;

    if (target is { Kind: RKind.Variable, VariableKind: VariableKind.Any })
      throw this.Unresolved(Describe(field.Target), field.Target.At);

    throw this.Invalid($"E0610: `{target}` is a primitive type and therefore doesn't have fields", field.At);
  }

  private RType Index(IndexExpr index) {
    var container = this.Value(index.Target).Dereferenced;
    if (container.Kind == RKind.Variable)
      throw this.Unresolved(Describe(index.Target), index.Target.At);

    if (this.IsError(container))
      return container;

    if (container.Kind == RKind.Map) {
      this.KeyArgument(index.Index, container.Key);
      return container.Element!;
    }

    if (container.Kind is RKind.String or RKind.Str && index.Index is not RangeExpr) {
      this.Value(index.Index);
      throw this.Invalid($"E0277: the type `str` cannot be indexed by `{this._typed.TypeOf(index.Index)}`", index.At);
    }

    if (container.Kind is not (RKind.Array or RKind.Slice or RKind.Vec or RKind.String or RKind.Str))
      throw this.Invalid($"E0608: cannot index into a value of type `{container}`", index.At);

    if (index.Index is RangeExpr range) {
      foreach (var bound in new[] { range.Start, range.End })
        if (bound is not null) {
          this.Unify(RType.Usize, this.Value(bound, RType.Usize), bound.At);
          this._typed.Types[bound] = this._typed.Types[bound];
        }

      if (range is { Inclusive: true, End: null })
        throw this.Invalid("E0586: inclusive range with no end", range.At);

      this._typed.Types[range] = RType.Unit;
      return container.Kind is RKind.String or RKind.Str ? RType.Str : RType.Slice(container.Element!);
    }

    this.Unify(RType.Usize, this.Value(index.Index, RType.Usize), index.Index.At);
    this._deferred.Add(() => this.ConstantIndex(index, container));
    return container.Element!;
  }

  /// <summary>The deny-by-default lint <c>unconditional_panic</c>: a constant index outside an array.</summary>
  private void ConstantIndex(IndexExpr index, RType container) {
    if (container.Kind != RKind.Array || Constants.Evaluate(index.Index, this._typed) is not { } value)
      return;

    if (value >= container.Length)
      throw this.Invalid($"this operation will panic at runtime: index out of bounds: the length is {container.Length} but the index is {value} (unconditional_panic)", index.At);
  }

  // ---------------------------------------------------------------- tuples, arrays and structs

  private RType Tuple(TupleExpr tuple, RType? expected) {
    if (tuple.Elements.Count == 0)
      return RType.Unit;

    var wanted = expected?.Resolved is { Kind: RKind.Tuple } known && known.Elements.Count == tuple.Elements.Count ? known.Elements : null;
    var elements = tuple.Elements.Select((element, index) => this.Value(element, wanted?[index])).ToList();
    return RType.Tuple(elements);
  }

  private RType Array(ArrayExpr array, RType? expected) {
    var element = expected?.Resolved is { Kind: RKind.Array } known ? known.Element! : this.Fresh(VariableKind.Any);
    foreach (var item in array.Elements)
      this.Coerce(element, this.Value(item, element), item.At);

    if (array.Elements.Count == 0 && expected is null)
      return RType.Array(element, 0);

    return RType.Array(element, array.Elements.Count);
  }

  private RType ArrayRepeat(ArrayRepeatExpr array, RType? expected) {
    var element = expected?.Resolved is { Kind: RKind.Array } known ? known.Element! : this.Fresh(VariableKind.Any);
    this.Coerce(element, this.Value(array.Value, element), array.Value.At);
    this.Unify(RType.Usize, this.Value(array.Count, RType.Usize), array.Count.At);
    var count = Constants.Evaluate(array.Count, this._typed, this.ConstantNamed);
    if (count is null)
      return this.Refused(RustFrontend.UnsupportedElement, "array length that is no constant the frontend evaluates", array.Count.At);

    this._deferred.Add(() => {
      if (!element.IsCopy && array.Value is not (PathExpr { Segments.Count: > 1 }))
        throw this.Invalid($"E0277: the trait bound `{element}: Copy` is not satisfied", array.Value.At);
    });
    return RType.Array(element, (long)count.Value);
  }

  private RType StructExpression(StructExpr structure) {
    var type = this.TypeOfPath(structure.Path.Segments, structure.Path.At);
    if (type?.Resolved.Kind != RKind.Struct) {
      foreach (var (_, value, _) in structure.Fields)
        this.Expression(value);

      if (structure.Path.Segments[0] is "std" or "core")
        return this.Refused(RustFrontend.ForeignPath, structure.Path.Text, structure.Path.At);

      throw this.Invalid($"E0422: cannot find struct `{structure.Path.Text}` in this scope", structure.Path.At);
    }

    var info = type.Struct!;
    this.Uses(info.Module);
    var named = new HashSet<string>();
    foreach (var (name, value, at) in structure.Fields) {
      var index = info.Fields.FindIndex(field => field.Name == name);
      if (index < 0)
        throw this.Invalid($"E0560: struct `{info.Name}` has no field named `{name}`", at);

      if (!named.Add(name))
        throw this.Invalid($"E0062: field `{name}` specified more than once", at);

      if (!info.Fields[index].IsVisible && !this._module.IsWithin(info.Module!))
        throw this.Invalid($"E0451: field `{name}` of struct `{info.Name}` is private", at);

      if (value is PathExpr { Segments.Count: 1 } shorthand && shorthand.At == at && this._typed.Types.ContainsKey(value) is false) {
        // `S { a }` names the variable `a`.
      }

      this.Coerce(info.Fields[index].Type, this.Value(value, info.Fields[index].Type), value.At);
    }

    if (structure.Base is { } @base) {
      this.Unify(type, this.Value(@base, type), @base.At);
    } else if (info.Fields.Count != named.Count) {
      var missing = info.Fields.First(field => !named.Contains(field.Name)).Name;
      throw this.Invalid($"E0063: missing field `{missing}` in initializer of `{info.Name}`", structure.At);
    }

    this._typed.Resolutions[structure] = info;
    return type;
  }

  // ---------------------------------------------------------------- ?, closures and macros

  private RType Try(TryExpr attempt) {
    var operand = this.Value(attempt.Operand).Resolved;
    var result = (this._closures.Count > 0 ? this._closures[^1].Result : this._result).Resolved;
    if (this.IsError(operand))
      return operand;

    switch (operand.Kind) {
      case RKind.Option:
        if (result.Kind == RKind.Variable)
          this.Unify(result, RType.Option(this.Fresh(VariableKind.Any)), attempt.At);
        else if (result.Kind != RKind.Option)
          throw this.Invalid($"E0277: the `?` operator can only be used on `Option`s in a function that returns `Option`", attempt.At);

        return operand.Element!;
      case RKind.Result:
        if (result.Kind == RKind.Variable)
          this.Unify(result, RType.Result(this.Fresh(VariableKind.Any), operand.Error), attempt.At);
        else if (result.Kind != RKind.Result)
          throw this.Invalid($"E0277: the `?` operator can only be used on `Result`s in a function that returns `Result`", attempt.At);
        else if (!this.TryUnify(result.Error, operand.Error))
          throw this.Invalid($"E0277: `?` couldn't convert the error to `{result.Error}`: the subset converts no error type", attempt.At);

        return operand.Element!;
      case RKind.Variable:
        throw this.Unresolved(Describe(attempt.Operand), attempt.At);
      default:
        throw this.Invalid($"E0277: the `?` operator can only be applied to values that implement `Try`, not `{operand}`", attempt.At);
    }
  }

  /// <summary>
  /// A closure: its parameters take the types the annotations or the place it is passed to give,
  /// its body is checked in a scope of its own, and every variable of the enclosing function it
  /// names is a capture. A <c>move</c> closure may capture only <c>Copy</c> values.
  /// </summary>
  private RType Closure(ClosureExpr closure, RType? expected) {
    var wanted = expected?.Resolved is { Kind: RKind.Function } signature && signature.Elements.Count == closure.Parameters.Count ? signature : null;
    var parameters = closure.Parameters.Select((parameter, index) => parameter.Type is null
      ? wanted?.Elements[index] ?? this.Fresh(VariableKind.Any)
      : this.Resolve(parameter.Type, false) ?? this.ErrorType()).ToList();
    var result = closure.ReturnType is null ? wanted?.Element ?? this.Fresh(VariableKind.Any) : this.Resolve(closure.ReturnType, true) ?? this.ErrorType();
    var info = new ClosureInfo(closure, parameters, result);
    this._typed.Closures[closure] = info;
    this._closures.Add(info);
    this._scopes.Add([]);
    try {
      for (var index = 0; index < parameters.Count; ++index)
        this.Irrefutable(closure.Parameters[index].Pattern, parameters[index], true, "parameter");

      var body = this.Value(closure.Body, result);
      this.Coerce(result, body, closure.Body.At);
    } finally {
      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._closures.RemoveAt(this._closures.Count - 1);
    }

    if (closure.Move) {
      this._deferred.Add(() => {
        foreach (var captured in info.Captures)
          if (!captured.Type.IsCopy && !this.IsError(captured.Type))
            this.Refuse(new Refusal(RustFrontend.EscapingClosure, $"move of the non-Copy value `{captured.Name}`", closure.At));
      });
    }

    return RType.Closure(info);
  }

  private RType Macro(MacroExpr macro, RType? expected) {
    switch (macro.Name) {
      case "format":
        this.Format(macro, 0);
        return RType.String;
      case "vec": {
        var element = expected?.Resolved is { Kind: RKind.Vec } known ? known.Element! : this.Fresh(VariableKind.Any);
        if (macro.Repeat) {
          this.Coerce(element, this.Value(macro.Arguments[0], element), macro.Arguments[0].At);
          this.Unify(RType.Usize, this.Value(macro.Arguments[1], RType.Usize), macro.Arguments[1].At);
          this._deferred.Add(() => {
            if (!element.IsCopy && !this.IsError(element))
              this.Refuse(new Refusal(RustFrontend.UnsupportedElement, $"vec![value; n] of the non-Copy type '{element}'", macro.At));
          });
        } else {
          foreach (var item in macro.Arguments)
            this.Coerce(element, this.Value(item, element), item.At);
        }

        return RType.Vec(element);
      }
      case "assert" or "debug_assert": {
        this.DebugSetting(macro);
        if (macro.Arguments.Count == 0)
          throw this.Invalid("macro requires a boolean expression as an argument", macro.At);

        this.Unify(RType.Bool, this.Value(macro.Arguments[0]), macro.Arguments[0].At);
        this.Message(macro, 1);
        return RType.Unit;
      }
      case "assert_eq" or "assert_ne" or "debug_assert_eq" or "debug_assert_ne": {
        this.DebugSetting(macro);
        if (macro.Arguments.Count < 2)
          throw this.Invalid($"unexpected end of macro invocation: {macro.Name}! takes two values", macro.At);

        var left = this.Value(macro.Arguments[0]);
        var right = this.Value(macro.Arguments[1]);
        this.Unify(left, right, macro.Arguments[1].At);
        this._deferred.Add(() => {
          if (!Equatable(left) && !this.IsError(left))
            throw this.Invalid($"E0369: binary operation `==` cannot be applied to type `{left}`", macro.At);
        });
        this.Message(macro, 2);
        return RType.Unit;
      }
      default:
        // panic!, unreachable!, todo!, unimplemented!.
        this.Message(macro, 0);
        return RType.Never;
    }
  }

  /// <summary>A <c>debug_assert*!</c> needs to know whether debug assertions are on.</summary>
  private void DebugSetting(MacroExpr macro) {
    if (macro.Name.StartsWith("debug_", StringComparison.Ordinal) && this.DebugAssertions is null)
      this.Refuse(new Refusal(RustFrontend.MissingDebugAssertions, macro.Name + "!", macro.At));
  }

  /// <summary>Whether <c>debug_assert*!</c> asserts; <see langword="null"/> when the profile does not say.</summary>
  public bool? DebugAssertions { get; init; }

  /// <summary>The message of a panicking macro, after its operands: a string literal without format arguments, or none.</summary>
  private void Message(MacroExpr macro, int first) {
    if (macro.Arguments.Count <= first)
      return;

    if (macro.Arguments.Count == first + 1 && macro.Arguments[first] is StringLiteral literal && !Formats(literal.Value))
      return;

    foreach (var argument in macro.Arguments.Skip(first).Where(argument => argument is not StringLiteral))
      this.Expression(argument);

    this.Refuse(new Refusal(RustFrontend.UnsupportedMacro, $"format arguments in '{macro.Name}!'", macro.At));
  }

  /// <summary>Whether a format string has a placeholder: a <c>{</c> that is not <c>{{</c>.</summary>
  private static bool Formats(string text) {
    for (var index = 0; index < text.Length; ++index) {
      if (text[index] != '{')
        continue;

      if (index + 1 < text.Length && text[index + 1] == '{') {
        ++index;
        continue;
      }

      return true;
    }

    return false;
  }

}
