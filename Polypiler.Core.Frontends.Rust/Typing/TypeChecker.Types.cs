using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>The types the source names, and the limits of what the IR can hold.</summary>
internal sealed partial class TypeChecker {

  /// <summary>The standard-library types the subset refuses, by the diagnostic that names them.</summary>
  private static readonly Dictionary<string, string> _refusedTypes = new() {
    ["Box"] = RustFrontend.SmartPointer, ["Rc"] = RustFrontend.SmartPointer, ["Arc"] = RustFrontend.SmartPointer,
    ["Cell"] = RustFrontend.SmartPointer, ["RefCell"] = RustFrontend.SmartPointer, ["Cow"] = RustFrontend.SmartPointer,
    ["Weak"] = RustFrontend.SmartPointer, ["Mutex"] = RustFrontend.SmartPointer, ["RwLock"] = RustFrontend.SmartPointer,
    ["VecDeque"] = RustFrontend.UnsupportedCollection, ["BinaryHeap"] = RustFrontend.UnsupportedCollection,
    ["LinkedList"] = RustFrontend.UnsupportedCollection,
    ["MaybeUninit"] = RustFrontend.UnsafeConstruct, ["ManuallyDrop"] = RustFrontend.UnsafeConstruct, ["NonNull"] = RustFrontend.UnsafeConstruct
  };

  /// <summary>The type a type of the source names, or a refusal when the subset has no such type.</summary>
  public RType? Resolve(TypeRef? type, bool allowUnit) {
    if (type is null)
      return RType.Unit;

    var resolved = this.ResolveShape(type, allowUnit);
    if (resolved is not null && !this.Representable(resolved, type))
      return null;

    return resolved;
  }

  /// <summary>Resolves a type in the scope of a module and of an impl.</summary>
  public RType? Resolve(TypeRef? type, bool allowUnit, ModuleScope module, RType? self) {
    var (savedModule, savedSelf) = (this._module, this._self);
    this._module = module;
    this._self = self;
    try {
      return this.Resolve(type, allowUnit);
    } finally {
      this._module = savedModule;
      this._self = savedSelf;
    }
  }

  private RType? Unsupported(string code, string construct, Span at) {
    this._report(code, construct, at);
    return null;
  }

  private RType? ResolveShape(TypeRef type, bool allowUnit) {
    switch (type.Shape) {
      case TypeShape.Never:
        return allowUnit ? RType.Never : this.Unsupported(RustFrontend.UnsupportedType, "!", type.At);
      case TypeShape.Tuple: {
        if (type.Arguments.Count == 0)
          return RType.Unit;

        var elements = type.Arguments.Select(argument => this.ResolveShape(argument, false)).ToList();
        return elements.Any(element => element is null) ? null : RType.Tuple(elements!);
      }
      case TypeShape.Array: {
        var element = this.ResolveShape(type.Element!, false);
        if (element is null)
          return null;

        var length = Constants.Evaluate(type.Length!, this._typed, this.ConstantNamed);
        if (length is null || length < 0)
          return this.Unsupported(RustFrontend.UnsupportedElement, $"array length '{type.Text}' that is no constant the frontend evaluates", type.At);

        return RType.Array(element, (long)length.Value);
      }
      case TypeShape.Slice:
        return this.Unsupported(RustFrontend.InvalidSource, $"E0277: the size for values of type `{type.Text}` cannot be known at compilation time", type.At);
      case TypeShape.Reference: {
        if (type.Lifetime is { } lifetime && lifetime is not ("static" or "_"))
          return this.Unsupported(RustFrontend.UnsupportedLifetime, $"'{lifetime}", type.At);

        if (type.Element!.Shape == TypeShape.Path && type.Element.Name is "str" or "std::primitive::str" or "core::primitive::str")
          return type.Mutable ? this.Unsupported(RustFrontend.TextType, "&mut str", type.At) : RType.StrRef;

        if (type.Element.Shape == TypeShape.Slice) {
          var element = this.ResolveShape(type.Element.Element!, false);
          return element is null ? null : RType.Reference(RType.Slice(element), type.Mutable);
        }

        if (type.Element.Shape == TypeShape.Trait && type.Element.Name.StartsWith("dyn ", System.StringComparison.Ordinal)) {
          // The IR's object holds a copy of the value it is made from, which a write through it would not reach.
          if (type.Mutable)
            return this.Unsupported(RustFrontend.UnsupportedTrait, $"{type.Text}, a trait object that writes through to a borrowed value", type.At);

          var dyn = this.TraitObject(type.Element);
          return dyn is null ? null : RType.Reference(dyn, false);
        }

        if (type.Element.Shape == TypeShape.Trait)
          return this.ResolveShape(type.Element, false);

        var target = this.ResolveShape(type.Element, false);
        return target is null ? null : RType.Reference(target, type.Mutable);
      }
      case TypeShape.Pointer:
        return this.Unsupported(RustFrontend.UnsafeConstruct, $"raw pointer '{type.Text}'", type.At);
      case TypeShape.Function: {
        var parameters = type.Arguments.Select(argument => this.ResolveShape(argument, false)).ToList();
        var result = type.Element is null ? RType.Unit : this.ResolveShape(type.Element, true);
        return parameters.Any(parameter => parameter is null) || result is null ? null : RType.Function(parameters!, result);
      }
      case TypeShape.Trait:
        return type.Name.StartsWith("dyn ", System.StringComparison.Ordinal)
          ? this.Unsupported(RustFrontend.UnsupportedTrait, type.Text, type.At)
          : this.Unsupported(RustFrontend.UnsupportedFunction, $"'{type.Text}' (impl Trait)", type.At);
    }

    return this.PathType(type, allowUnit);
  }

  /// <summary>Whether <c>_</c> stands for a type to infer: in a <c>let</c>'s type and a turbofish, never in a signature (<c>E0121</c>).</summary>
  private bool _holes;

  /// <summary>A type of a function body, where <c>_</c> is a new inference variable: <c>Vec&lt;_&gt;</c>.</summary>
  private RType? Inferred(TypeRef type, bool allowUnit) {
    var saved = this._holes;
    this._holes = true;
    try {
      return this.Resolve(type, allowUnit);
    } finally {
      this._holes = saved;
    }
  }

  private RType? PathType(TypeRef type, bool allowUnit) {
    var segments = type.Name.Split("::");
    var last = segments[^1];
    if (segments is ["_"] && type.Arguments.Count == 0)
      return this._holes ? this.Fresh(VariableKind.Any) : this.Unsupported(RustFrontend.InvalidSource, "E0121: the placeholder `_` is not allowed within types on item signatures", type.At);

    if (type.Arguments.Count == 0 && this.ParameterType(segments, type.At) is { } parameter)
      return parameter;

    if (segments.Length == 1 && RType.Named(last) is { } primitive && type.Arguments.Count == 0)
      return primitive;

    if (segments.Length == 1 && RType.Unsupported.Contains(last))
      return this.Unsupported(RustFrontend.UnsupportedType, last, type.At);

    if (segments.Length == 1 && last == "Self") {
      if (this._self is null)
        return this.Unsupported(RustFrontend.InvalidSource, "E0411: cannot find type `Self` in this scope", type.At);

      return this._self;
    }

    if (type.Arguments.Any(argument => argument.Lifetime is not null && argument.Element is null))
      return this.Unsupported(RustFrontend.UnsupportedLifetime, type.Text, type.At);

    // The crate's own types first: a struct named Option shadows the prelude's.
    var symbol = segments[0] is "std" or "core" or "alloc" ? null : this._crate.Resolve(this._module, segments, type.At, true);
    if (symbol?.Target is StructItem structure) {
      var info = this._structs[structure];
      if (type.Arguments.Count != info.Parameters.Count)
        return this.Unsupported(RustFrontend.InvalidSource, $"E0107: struct takes {info.Parameters.Count} generic argument{(info.Parameters.Count == 1 ? "" : "s")} but {type.Arguments.Count} generic argument{(type.Arguments.Count == 1 ? " was" : "s were")} supplied: `{type.Text}`", type.At);

      if (info.Parameters.Count == 0)
        return RType.StructOf(info);

      var arguments = type.Arguments.Select(argument => this.ResolveShape(argument, false)).ToList();
      return arguments.Any(argument => argument is null) ? null : RType.StructOf(info, arguments!);
    }

    if (symbol?.Target is EnumItem enumeration)
      return type.Arguments.Count > 0 ? this.Unsupported(RustFrontend.UnsupportedGeneric, type.Text, type.At) : RType.EnumOf(this._enums[enumeration]);

    var library = segments is ["std" or "core" or "alloc", .., var name] ? name : segments.Length == 1 ? last : null;
    if (this.CollectionType(type, segments, symbol) is { } collection)
      return collection;

    // Box<dyn Trait> and Rc<dyn Trait>: a trait object owned or shared; a box of any other type stays refused.
    var pointer = symbol?.Target is LibraryType { Name: "Box" or "Rc" } used ? used.Name : symbol is null && library is "Box" or "Rc" && (segments.Length == 1 || segments is [_, "boxed" or "rc", _]) ? library : null;
    if (pointer is not null && type.Arguments is [{ Shape: TypeShape.Trait } pointee] && pointee.Name.StartsWith("dyn ", System.StringComparison.Ordinal)) {
      var dyn = this.TraitObject(pointee);
      return dyn is null ? null : RType.PointerTo(pointer, dyn);
    }

    if (pointer is not null)
      return this.Unsupported(RustFrontend.SmartPointer, type.Text, type.At);

    if (library is not null && _refusedTypes.TryGetValue(library, out var code))
      return this.Unsupported(code, type.Text, type.At);

    if (library is "String" or "char" && type.Arguments.Count == 0 && segments is [_, "string" or "primitive", _])
      return library == "String" ? RType.String : RType.Char;

    switch (library) {
      case "Vec" when type.Arguments.Count == 1 && (segments.Length == 1 || segments is [_, "vec", _]): {
        var element = this.ResolveShape(type.Arguments[0], false);
        return element is null ? null : RType.Vec(element);
      }
      case "Option" when type.Arguments.Count == 1 && (segments.Length == 1 || segments is [_, "option", _]): {
        var value = this.ResolveShape(type.Arguments[0], false);
        return value is null ? null : RType.Option(value);
      }
      case "Result" when type.Arguments.Count == 2 && (segments.Length == 1 || segments is [_, "result", _]): {
        var value = this.ResolveShape(type.Arguments[0], true);
        var error = this.ResolveShape(type.Arguments[1], true);
        return value is null || error is null ? null : RType.Result(value, error);
      }
    }

    if (segments[0] is "std" or "core" or "alloc")
      return this.Unsupported(RustFrontend.ForeignPath, type.Text, type.At);

    if (segments.Length == 1)
      return this.Unsupported(RustFrontend.InvalidSource, $"E0412: cannot find type `{type.Text}` in this scope", type.At);

    return this.Unsupported(RustFrontend.ForeignPath, type.Text, type.At);
  }

  /// <summary>
  /// Whether the IR can hold values of a type, as the frontend maps it: arrays of numbers and of
  /// structs, slices of numbers, <c>Vec</c>s of numbers, structs and arrays, <c>Option</c>s of
  /// scalars, enums, structs and shared references to them, and <c>Result</c>s of those and of unit.
  /// A type outside is refused here, at the type that names it.
  /// </summary>
  private bool Representable(RType type, TypeRef at) {
    var resolved = type.Resolved;
    // A template's types hold its parameters; each instance's types are checked with the arguments.
    // A `_` is decided by the code.
    if (HoldsParameter(resolved) || Undecided(resolved))
      return true;

    switch (resolved.Kind) {
      case RKind.Array: {
        var element = resolved.Element!.Resolved;
        if (element.Kind == RKind.Array)
          return this.Unsupported(RustFrontend.UnsupportedElement, $"nested array '{at.Text}'", at.At) is not null;

        if (!(element.IsNumber || element.IsText || element.Kind is RKind.Struct or RKind.Enum or RKind.Char or RKind.Bool))
          return this.Unsupported(RustFrontend.UnsupportedElement, $"array of {element}", at.At) is not null;

        return this.Representable(element, at);
      }
      case RKind.Reference when resolved.Element!.Resolved.Kind == RKind.Slice: {
        var element = resolved.Element.Resolved.Element!.Resolved;
        if (!(element.IsNumber || element.IsText || element.Kind is RKind.Char))
          return this.Unsupported(RustFrontend.UnsupportedElement, $"slice of {element}", at.At) is not null;

        return true;
      }
      case RKind.Reference:
        if (resolved.Element!.Resolved.Kind == RKind.Reference)
          return this.Unsupported(RustFrontend.UnsupportedElement, $"reference to a reference '{at.Text}'", at.At) is not null;

        // The IR holds a string in no struct, so no function returns one through a copy-in/copy-out.
        if (resolved.Element.Resolved.Kind == RKind.String && resolved.Mutable)
          return this.Unsupported(RustFrontend.TextType, $"'{at.Text}'", at.At) is not null;

        return this.Representable(resolved.Element, at);
      case RKind.Vec: {
        // A shared reference to a value is the value: Vec<&u8> is a list of the values it borrows.
        var element = resolved.Element!.Resolved is { Kind: RKind.Reference, Mutable: false } borrowed && !borrowed.IsTraitObject ? borrowed.Element!.Resolved : resolved.Element!.Resolved;
        if (!(element.IsNumber || element.IsText || element.IsTraitObject || element.Kind is RKind.Struct or RKind.Enum or RKind.Bool or RKind.Char))
          return this.Unsupported(RustFrontend.UnsupportedElement, $"Vec of {element}", at.At) is not null;

        return this.Representable(element, at);
      }
      case RKind.Option: {
        var value = resolved.Element!.Dereferenced;
        if (!(value.IsScalar || value.Kind is RKind.Struct or RKind.Enum or RKind.Char))
          return this.Unsupported(RustFrontend.UnsupportedOptionMethod, $"Option<{resolved.Element}>", at.At) is not null;

        return true;
      }
      case RKind.Result: {
        foreach (var part in new[] { resolved.Element!, resolved.Error }) {
          var value = part.Resolved;
          if (!(value.IsScalar || value.Kind is RKind.Enum or RKind.Unit or RKind.Char || (value.Kind == RKind.Struct && this.HasDefault(value))))
            return this.Unsupported(RustFrontend.UnsupportedOptionMethod, $"Result<{resolved.Element}, {resolved.Error}>", at.At) is not null;
        }

        return true;
      }
      case RKind.Tuple:
        foreach (var element in resolved.Elements) {
          var part = element.Resolved;
          if (TuplePart(part) is { } held)
            return this.Unsupported(RustFrontend.Unsupported, $"tuple holding {held}", at.At) is not null;

          if (!this.Representable(part, at))
            return false;
        }

        return true;
      default:
        return true;
    }
  }

  /// <summary>Whether a type holds a type parameter of a template.</summary>
  private static bool HoldsParameter(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind == RKind.Param || (resolved.Element is { } element && HoldsParameter(element)) || resolved.Elements.Any(HoldsParameter);
  }

  /// <summary>Whether every field of a struct is a scalar, an enum or a struct of the same kind: the IR gives it a default value.</summary>
  private bool HasDefault(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind switch {
      RKind.Integer or RKind.Float or RKind.Bool or RKind.Enum or RKind.Unit => true,
      RKind.Struct => resolved.StructFields.All(field => this.HasDefault(field.Type)),
      RKind.Tuple => resolved.Elements.All(this.HasDefault),
      _ => false
    };
  }

  /// <summary>The value of a constant a length or a discriminant names, for <see cref="Constants.Evaluate"/>.</summary>
  private ConstItem? ConstantNamed(PathExpr path) {
    if (path.Segments.Count == 1 && this._scopes.Count > 0 && this.Lookup(path.Segments[0]) is { Constant: { } local })
      return local;

    var symbol = this._crate.Resolve(this._module, path.Segments, path.At, false);
    if (symbol?.Target is ConstItem constant)
      return constant;

    // An associated constant: Self::N or S::N.
    if (path.Segments.Count == 2 && this.TypeOfPath([path.Segments[0]], path.At) is { } owner && this.AssociatedConstant(owner, path.Segments[1]) is { Constant: { } associated })
      return associated;

    return null;
  }

}
