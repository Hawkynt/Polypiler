using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>A trait of the crate: its methods, required and provided, and the traits it requires.</summary>
internal sealed class TraitInfo(string name, TraitItem item, ModuleScope module) {

  public string Name { get; } = name;

  public TraitItem Item { get; } = item;

  public ModuleScope Module { get; } = module;

  /// <summary>The methods by name; one without a body every impl provides.</summary>
  public Dictionary<string, FunctionItem> Methods { get; } = item.Items.OfType<FunctionItem>().ToDictionary(method => method.Name);

  /// <summary>The supertraits, resolved after every trait is known.</summary>
  public List<TraitInfo> Supertraits { get; } = [];

}

/// <summary>
/// Traits and generics, both resolved at compile time as rustc's monomorphisation does: a trait
/// method called on a type is the impl's method for that type, a provided method is checked once
/// for every type that does not override it, and a generic function is checked and lowered once
/// for every list of type arguments it is called with, each instance a function of its own. The
/// operator traits of <c>core::ops</c> and <c>PartialEq</c> implemented for the crate's types make
/// the operators call the impl's method.
/// </summary>
internal sealed partial class TypeChecker {

  /// <summary>The traits of <c>core</c> whose methods an operator calls, by the operator.</summary>
  private static readonly Dictionary<string, (string Trait, string Method)> _operatorTraits = new(System.StringComparer.Ordinal) {
    ["+"] = ("Add", "add"), ["-"] = ("Sub", "sub"), ["*"] = ("Mul", "mul"), ["/"] = ("Div", "div"), ["%"] = ("Rem", "rem"),
    ["&"] = ("BitAnd", "bitand"), ["|"] = ("BitOr", "bitor"), ["^"] = ("BitXor", "bitxor"), ["<<"] = ("Shl", "shl"), [">>"] = ("Shr", "shr")
  };

  /// <summary>The traits of the standard library a crate's impl may implement, with the methods each has.</summary>
  private static readonly Dictionary<string, string[]> _libraryTraits = new(System.StringComparer.Ordinal) {
    ["Add"] = ["add"], ["Sub"] = ["sub"], ["Mul"] = ["mul"], ["Div"] = ["div"], ["Rem"] = ["rem"],
    ["BitAnd"] = ["bitand"], ["BitOr"] = ["bitor"], ["BitXor"] = ["bitxor"], ["Shl"] = ["shl"], ["Shr"] = ["shr"], ["Neg"] = ["neg"], ["Not"] = ["not"],
    ["AddAssign"] = ["add_assign"], ["SubAssign"] = ["sub_assign"], ["MulAssign"] = ["mul_assign"], ["DivAssign"] = ["div_assign"], ["RemAssign"] = ["rem_assign"],
    ["PartialEq"] = ["eq", "ne"], ["Default"] = ["default"], ["Clone"] = ["clone"]
  };

  private readonly Dictionary<TraitItem, TraitInfo> _traits = new(ReferenceEqualityComparer.Instance);

  /// <summary>The methods trait impls give a type, by the type's key and the method's name.</summary>
  private readonly Dictionary<string, Dictionary<string, List<(string Trait, FunctionSignature Method)>>> _traitMethods = new(System.StringComparer.Ordinal);

  /// <summary>The traits each type implements, by the type's key: the crate's traits and those of the standard library its impls name.</summary>
  private readonly Dictionary<string, HashSet<string>> _implemented = new(System.StringComparer.Ordinal);

  /// <summary>The associated types of each impl of a type, by the type's key and their name: <c>Output</c>.</summary>
  private readonly Dictionary<string, Dictionary<string, RType>> _associatedTypes = new(System.StringComparer.Ordinal);

  /// <summary>The type parameters in scope and the types they stand for: a template's parameters, an instance's arguments.</summary>
  private IReadOnlyDictionary<string, RType>? _typeParameters;

  /// <summary>The calls of generic functions of the function being checked, instantiated when its types are decided.</summary>
  private readonly List<(CallExpr Call, FunctionSignature Template, IReadOnlyList<RType> Arguments)> _instantiations = [];

  /// <summary>The key of a type for the tables of trait impls: the crate path of a struct or an enum, the name of any other type.</summary>
  private static string TypeKey(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind switch {
      RKind.Struct => "struct " + resolved.Struct!.Path,
      RKind.Enum => "enum " + resolved.Enum!.Path,
      RKind.Str => "str",
      _ => resolved.ToString()
    };
  }

  // ---------------------------------------------------------------- declarations

  /// <summary>The traits of the crate, then their supertraits.</summary>
  private bool DeclareTraits() {
    foreach (var (item, module) in this._crate.Traits)
      this._traits[item] = new TraitInfo(item.Name, item, module);

    var ok = true;
    foreach (var info in this._traits.Values)
      foreach (var supertrait in info.Item.Supertraits) {
        if (this.TraitNamed(supertrait, info.Module) is { } known) {
          info.Supertraits.Add(known);
        } else if (!IsMarker(supertrait.Name)) {
          this._report(RustFrontend.UnsupportedTrait, $"supertrait '{supertrait.Text}'", supertrait.At);
          ok = false;
        }
      }

    return ok;
  }

  /// <summary>The crate's trait a bound names, or <see langword="null"/>.</summary>
  private TraitInfo? TraitNamed(TypeRef bound, ModuleScope module) {
    var symbol = this._crate.Resolve(module, bound.Name.Split("::"), bound.At, false);
    return symbol?.Target is TraitItem trait ? this._traits[trait] : null;
  }

  /// <summary>The traits every type of the subset has or that say nothing a value needs: <c>Sized</c>, <c>Send</c>, <c>Sync</c>, <c>Unpin</c>.</summary>
  private static bool IsMarker(string name) => name.Split("::")[^1] is "Sized" or "Send" or "Sync" or "Unpin";

  /// <summary>
  /// The functions of a trait impl: the impl's methods, which go to the type's table of trait methods,
  /// then the trait's provided methods the impl does not override, each a function of its own for
  /// this type; a required method missing is <c>E0046</c>.
  /// </summary>
  private bool TraitImpl(ImplItem impl, ModuleScope module, RType self) {
    var key = TypeKey(self);
    var traitName = impl.Trait!.Name.Split("::")[^1];
    var trait = this.TraitNamed(impl.Trait, module);
    if (trait is null && !_libraryTraits.ContainsKey(traitName)) {
      this._report(RustFrontend.UnsupportedTrait, $"impl {impl.Trait.Text} for {impl.Type.Text}", impl.At);
      return false;
    }

    if (impl.Trait.Arguments.Count > 0 && !(traitName is "Add" or "Sub" or "Mul" or "Div" or "Rem" or "PartialEq" && impl.Trait.Arguments.Count == 1)) {
      this._report(RustFrontend.UnsupportedGeneric, $"impl {impl.Trait.Text}", impl.Trait.At);
      return false;
    }

    var name = trait?.Name ?? traitName;
    if (!this._implemented.TryGetValue(key, out var implemented))
      this._implemented[key] = implemented = new(System.StringComparer.Ordinal);

    if (!implemented.Add(name)) {
      this._report(RustFrontend.InvalidSource, $"E0119: conflicting implementations of trait `{name}` for type `{self}`", impl.At);
      return false;
    }

    foreach (var associated in impl.Items.OfType<AssociatedTypeItem>()) {
      if (associated.Type is null || this.Resolve(associated.Type, true, module, self) is not { } type) {
        this._report(RustFrontend.UnsupportedTrait, $"associated type {associated.Name}", associated.At);
        return false;
      }

      if (!this._associatedTypes.TryGetValue(key, out var types))
        this._associatedTypes[key] = types = new(System.StringComparer.Ordinal);

      types[associated.Name] = type;
    }

    var ok = true;
    var methods = impl.Items.OfType<FunctionItem>().ToList();
    IEnumerable<string> known = trait is not null ? trait.Methods.Keys : _libraryTraits[traitName];
    foreach (var method in methods.Where(method => !known.Contains(method.Name))) {
      this._report(RustFrontend.InvalidSource, $"E0407: method `{method.Name}` is not a member of trait `{name}`", method.At);
      ok = false;
    }

    if (trait is not null)
      foreach (var required in trait.Methods.Values.Where(required => required.IsRequired && methods.All(method => method.Name != required.Name))) {
        this._report(RustFrontend.InvalidSource, $"E0046: not all trait items implemented, missing: `{required.Name}`", impl.At);
        ok = false;
      }

    return ok;
  }

  /// <summary>Records a method of a trait impl in the type's table.</summary>
  private void TraitMethod(RType self, string trait, FunctionSignature method) {
    var key = TypeKey(self);
    if (!this._traitMethods.TryGetValue(key, out var methods))
      this._traitMethods[key] = methods = new(System.StringComparer.Ordinal);

    if (!methods.TryGetValue(method.Item.Name, out var list))
      methods[method.Item.Name] = list = [];

    list.Add((trait, method));
  }

  /// <summary>The provided methods of a trait that an impl does not override, each a function for the impl's type, checked with the type as <c>Self</c>.</summary>
  private bool ProvidedMethods(ImplItem impl, ModuleScope module, RType self) {
    if (this.TraitNamed(impl.Trait!, module) is not { } trait)
      return true;

    var ok = true;
    foreach (var provided in trait.Methods.Values.Where(method => !method.IsRequired && impl.Items.OfType<FunctionItem>().All(own => own.Name != method.Name))) {
      var fresh = this._crate.Fresh(provided, trait.Module);
      var signature = this.Signature(fresh, trait.Module, self);
      if (signature is null) {
        ok = false;
        continue;
      }

      signature.IrName = Mangled(trait.Module, $"{OwnerName(self)}_{trait.Name}_{provided.Name}");
      this.TraitMethod(self, trait.Name, signature);
    }

    return ok;
  }

  /// <summary>The name of a type as a word of a generated name: <c>Point</c>, <c>u32</c>, <c>str</c>.</summary>
  private static string OwnerName(RType type) => Regex.Replace(type.Resolved.Kind switch {
    RKind.Struct => type.Resolved.Struct!.Name,
    RKind.Enum => type.Resolved.Enum!.Name,
    _ => type.ToString()
  }, "[^A-Za-z0-9]+", "_").Trim('_');

  /// <summary>A trait method of a type by name; <see langword="null"/> when no impl gives one, <c>E0034</c> when two do.</summary>
  private FunctionSignature? TraitMethodOf(RType type, string name, Span at) {
    if (!this._traitMethods.TryGetValue(TypeKey(type), out var methods) || !methods.TryGetValue(name, out var candidates))
      return null;

    if (candidates.Count > 1)
      throw this.Invalid($"E0034: multiple applicable items in scope: `{name}` of traits {string.Join(" and ", candidates.Select(candidate => $"`{candidate.Trait}`"))}", at);

    return candidates[0].Method;
  }

  /// <summary>The methods of text the subset has, which a trait method of the same name does not hide.</summary>
  private static readonly HashSet<string> _textMethods = [
    "len", "is_empty", "as_str", "to_string", "to_owned", "to_ascii_uppercase", "to_ascii_lowercase", "clone", "as_bytes",
    "trim", "trim_start", "trim_end", "repeat", "contains", "starts_with", "ends_with", "find", "push_str", "push", "clear",
    "chars", "bytes", "split", "parse"
  ];

  /// <summary>The methods of <c>char</c> the subset has besides the ASCII predicates.</summary>
  private static readonly HashSet<string> _charMethods = ["to_ascii_uppercase", "to_ascii_lowercase", "clone", "len_utf8", "to_string", "to_digit", "is_digit"];

  /// <summary>Whether a primitive or text has a method of its own by the name, which wins over a trait's (The Rust Reference, "Method call expressions").</summary>
  private static bool Builtin(RType target, string name) => target.Kind switch {
    RKind.String or RKind.Str => _textMethods.Contains(name),
    RKind.Char => _charMethods.Contains(name) || _asciiPredicates.Contains(name),
    _ => Methods.Of(target, name) is not null || name is "to_le_bytes" or "to_be_bytes" or "to_ne_bytes" or "clone" or "to_string"
  };

  /// <summary>Whether a type implements a trait of the crate or one of the standard library through an impl of the crate.</summary>
  private bool Implements(RType type, string trait) => this._implemented.TryGetValue(TypeKey(type), out var traits) && traits.Contains(trait);

  // ---------------------------------------------------------------- operators

  /// <summary>An operator on a struct or an enum whose impl of the operator's trait gives the method it calls; <see langword="null"/> for any other operand.</summary>
  private RType? TraitOperator(BinaryExpr binary, RType left, RType right) {
    var type = left.Resolved;
    if (type.Kind is not (RKind.Struct or RKind.Enum))
      return null;

    string method;
    if (binary.Op is "==" or "!=") {
      if (!this.Implements(type, "PartialEq"))
        return null;

      method = "eq";
    } else if (_operatorTraits.TryGetValue(binary.Op, out var trait) && this.Implements(type, trait.Trait)) {
      method = trait.Method;
    } else {
      return null;
    }

    var signature = this.TraitMethodOf(type, method, binary.At)!;
    this._typed.Operators[binary] = signature;
    // `a == b` is PartialEq::eq(&a, &b): both sides are borrowed.
    if (method == "eq")
      this.Unify(signature.Parameters[0].Dereferenced, StripShared(right), binary.Right.At);
    else
      this.Coerce(signature.Parameters[0], right, binary.Right.At);
    return binary.Op is "==" or "!=" ? RType.Bool : signature.Result;
  }

  /// <summary><c>-x</c> and <c>!x</c> of a struct or an enum whose impl of <c>Neg</c> or <c>Not</c> gives the method; <see langword="null"/> for any other operand.</summary>
  private RType? TraitUnary(UnaryExpr unary, RType operand) {
    var type = operand.Resolved;
    var (trait, method) = unary.Op == "-" ? ("Neg", "neg") : ("Not", "not");
    if (type.Kind is not (RKind.Struct or RKind.Enum) || !this.Implements(type, trait))
      return null;

    var signature = this.TraitMethodOf(type, method, unary.At)!;
    this._typed.Operators[unary] = signature;
    return signature.Result;
  }

  /// <summary><c>a op= b</c> of a struct or an enum whose impl of <c>AddAssign</c> or its relatives gives the method; <see langword="null"/> for any other place.</summary>
  private bool TraitAssign(AssignExpr assign, RType place, RType value) {
    var type = place.Resolved;
    if (type.Kind is not (RKind.Struct or RKind.Enum) || !_operatorTraits.TryGetValue(assign.Op, out var trait))
      return false;

    if (!this.Implements(type, trait.Trait + "Assign"))
      throw this.Invalid($"E0368: binary assignment operation `{assign.Op}=` cannot be applied to type `{type}`", assign.At);

    var signature = this.TraitMethodOf(type, trait.Method + "_assign", assign.At)!;
    this._typed.Operators[assign] = signature;
    this.Coerce(signature.Parameters[0], value, assign.Value.At);
    return true;
  }

  // ---------------------------------------------------------------- generics

  /// <summary>The template of a generic function: its signature over the type parameters, which no body is checked against; its instances are.</summary>
  private FunctionSignature? Template(FunctionItem item, ModuleScope module, RType? self) {
    var saved = this._typeParameters;
    this._typeParameters = item.Generics.ToDictionary(parameter => parameter.Name, parameter => RType.Param(parameter.Name));
    try {
      var template = this.Signature(item, module, self, false);
      if (template is not null)
        template.Generics = item.Generics;

      return template;
    } finally {
      this._typeParameters = saved;
    }
  }

  /// <summary>A call of a generic function: the type parameters are new inference variables, or the turbofish's types; the instance is made when the caller's types are decided.</summary>
  private RType GenericCall(CallExpr call, PathExpr path, FunctionSignature template) {
    var generics = template.Generics;
    var explicitTypes = path.Generics.TryGetValue(path.Segments.Count - 1, out var given) ? given : null;
    if (explicitTypes is not null && explicitTypes.Count != generics.Count)
      throw this.Invalid($"E0107: function takes {generics.Count} generic argument{(generics.Count == 1 ? "" : "s")} but {explicitTypes.Count} were supplied", path.At);

    var arguments = generics.Select((parameter, index) => explicitTypes is null ? this.Fresh(VariableKind.Any) : this.Resolve(explicitTypes[index], false) ?? this.ErrorType()).ToList();
    var substitution = generics.Select((parameter, index) => (parameter.Name, arguments[index])).ToDictionary(pair => pair.Name, pair => pair.Item2);
    IReadOnlyList<RType> parameters = template.Receiver is null ? template.Parameters : [template.Receiver, .. template.Parameters];
    this.Arguments(call, [.. parameters.Select(parameter => Substitute(parameter, substitution))], "function");
    this._instantiations.Add((call, template, arguments));
    return Substitute(template.Result, substitution);
  }

  /// <summary>Makes the instances the calls of the function just checked need and binds each call to its instance.</summary>
  private void Instantiate() {
    var pending = this._instantiations.ToList();
    this._instantiations.Clear();
    foreach (var (call, template, arguments) in pending) {
      if (arguments.Any(argument => this.IsError(argument)))
        continue;

      var undecided = arguments.FirstOrDefault(Undecided);
      if (undecided is not null)
        throw this.Invalid($"E0282: type annotations needed: cannot infer the type of the type parameter of `{template.Item.Name}`", call.At);

      this._typed.Callees[call] = this.Instance(template, [.. arguments.Select(argument => argument.Resolved)], call.At);
    }
  }

  /// <summary>The instance of a template for type arguments, made once: the template's item parsed anew, its types resolved with the arguments, its bounds checked.</summary>
  private FunctionSignature Instance(FunctionSignature template, IReadOnlyList<RType> arguments, Span at) {
    var key = string.Join(", ", arguments.Select(argument => argument.ToString()));
    var substitution = template.Generics.Select((parameter, index) => (parameter.Name, arguments[index])).ToDictionary(pair => pair.Name, pair => pair.Item2);
    foreach (var parameter in template.Generics)
      foreach (var bound in parameter.Bounds)
        this.RequireBound(substitution[parameter.Name], bound, template.Module!, substitution, at);

    if (template.Instances.TryGetValue(key, out var known))
      return known;

    var fresh = this._crate.Fresh(template.Item, template.Module!);
    var saved = this._typeParameters;
    this._typeParameters = substitution;
    try {
      var self = template.Receiver?.Dereferenced ?? this.SelfOf(template);
      var instance = this.Signature(fresh, template.Module!, self) ?? throw new CheckAbort();
      instance.IrName = template.IrName + "_" + string.Join("_", arguments.Select(OwnerName));
      instance.Substitution = substitution;
      template.Instances[key] = instance;
      return instance;
    } finally {
      this._typeParameters = saved;
    }
  }

  /// <summary>Requires a type to satisfy a bound: the standard traits by what the subset knows of the type, the crate's traits by its impls.</summary>
  private void RequireBound(RType type, TypeRef bound, ModuleScope module, IReadOnlyDictionary<string, RType> substitution, Span at) {
    var name = bound.Name.Split("::")[^1];
    if (IsMarker(bound.Name))
      return;

    bool satisfied;
    if (this.TraitNamed(bound, module) is { } trait) {
      satisfied = this.Implements(type, trait.Name);
    } else {
      var resolved = type.Resolved;
      satisfied = name switch {
        "Copy" => resolved.IsCopy,
        "Clone" => resolved.IsCopy || resolved.IsText || resolved.Kind == RKind.Vec || this.Implements(resolved, "Clone") || (resolved.Kind is RKind.Struct && resolved.Struct!.Derives.Contains("Clone")) || (resolved.Kind == RKind.Enum && resolved.Enum!.Derives.Contains("Clone")),
        "PartialEq" or "Eq" => (name == "PartialEq" || resolved.Kind != RKind.Float) && (Equatable(resolved) || this.Implements(resolved, "PartialEq")),
        "PartialOrd" or "Ord" => resolved.Kind is RKind.Integer or RKind.Char or RKind.Bool || resolved.IsText || (name == "PartialOrd" && resolved.Kind == RKind.Float),
        "Default" => resolved.Kind is RKind.Integer or RKind.Float or RKind.Bool or RKind.Char or RKind.String or RKind.Vec || this.Implements(resolved, "Default")
          || (resolved.Kind == RKind.Struct && resolved.Struct!.Derives.Contains("Default")) || (resolved.Kind == RKind.Enum && resolved.Enum!.Derives.Contains("Default")),
        "Display" => resolved.Kind is RKind.Integer or RKind.Bool or RKind.Char || resolved.IsText,
        "Debug" => resolved.Kind is RKind.Integer or RKind.Bool,
        "Add" or "Sub" or "Mul" or "Div" or "Rem" => resolved.IsNumber && OutputIsSelf(bound, resolved, substitution, this) || this.Implements(resolved, name),
        "BitAnd" or "BitOr" or "BitXor" => resolved.Kind is RKind.Integer or RKind.Bool && OutputIsSelf(bound, resolved, substitution, this) || this.Implements(resolved, name),
        "Shl" or "Shr" or "Not" => resolved.Kind == RKind.Integer && OutputIsSelf(bound, resolved, substitution, this),
        "Neg" => (resolved.IsSigned && resolved.Kind == RKind.Integer || resolved.Kind == RKind.Float) && OutputIsSelf(bound, resolved, substitution, this) || this.Implements(resolved, "Neg"),
        "AddAssign" or "SubAssign" or "MulAssign" or "DivAssign" or "RemAssign" => resolved.IsNumber || this.Implements(resolved, name),
        _ => this.UnknownBound(bound)
      };
    }

    if (!satisfied)
      throw this.Invalid($"E0277: the trait bound `{type}: {bound.Text}` is not satisfied", at);
  }

  /// <summary>A bound on a trait of the standard library the subset does not know: refused, and taken as satisfied.</summary>
  private bool UnknownBound(TypeRef bound) {
    this.Refuse(new Refusal(RustFrontend.UnsupportedTrait, $"bound '{bound.Text}'", bound.At));
    return true;
  }

  /// <summary>Whether the <c>Output</c> a bound like <c>Add&lt;Output = T&gt;</c> binds is the type itself, as it is for the operators of the primitives.</summary>
  private static bool OutputIsSelf(TypeRef bound, RType type, IReadOnlyDictionary<string, RType> substitution, TypeChecker checker) {
    var output = bound.Arguments.FirstOrDefault(argument => argument.Binding == "Output");
    if (output is null)
      return bound.Arguments.Count == 0;

    var saved = checker._typeParameters;
    checker._typeParameters = substitution;
    try {
      return checker.Resolve(output.Element, false) is { } wanted && wanted.Resolved == type.Resolved;
    } finally {
      checker._typeParameters = saved;
    }
  }

  /// <summary>A type with the type parameters replaced by the types they stand for.</summary>
  private static RType Substitute(RType type, IReadOnlyDictionary<string, RType> substitution) {
    var resolved = type.Resolved;
    return resolved.Kind switch {
      RKind.Param => substitution[resolved.Name],
      RKind.Reference => RType.Reference(Substitute(resolved.Element!, substitution), resolved.Mutable),
      RKind.Option => RType.Option(Substitute(resolved.Element!, substitution)),
      RKind.Vec => RType.Vec(Substitute(resolved.Element!, substitution)),
      RKind.Array => RType.Array(Substitute(resolved.Element!, substitution), resolved.Length),
      RKind.Slice => RType.Slice(Substitute(resolved.Element!, substitution)),
      RKind.Tuple => RType.Tuple([.. resolved.Elements.Select(element => Substitute(element, substitution))]),
      RKind.Result => RType.Result(Substitute(resolved.Element!, substitution), Substitute(resolved.Error, substitution)),
      RKind.Function => RType.Function([.. resolved.Elements.Select(element => Substitute(element, substitution))], Substitute(resolved.Element!, substitution)),
      _ => resolved
    };
  }

  /// <summary>
  /// <c>T::name(...)</c> in an instance, <c>T</c> a type argument: the associated function of the
  /// type's own impl or of a trait impl, or <c>default</c> of a type that has one.
  /// </summary>
  private RType ParameterCall(CallExpr call, RType type, string name) {
    var resolved = type.Resolved;
    var function = resolved.Kind is RKind.Struct or RKind.Enum ? this.Member(resolved, name, call.At) as FunctionSignature : this.TraitMethodOf(resolved, name, call.At);
    if (function is not null) {
      this._typed.Callees[call] = function;
      IReadOnlyList<RType> parameters = function.Receiver is null ? function.Parameters : [function.Receiver, .. function.Parameters];
      this.Arguments(call, parameters, "function");
      return function.Result;
    }

    if (name == "default" && call.Arguments.Count == 0 && resolved.Kind is RKind.Integer or RKind.Float or RKind.Bool or RKind.Char or RKind.String or RKind.Struct or RKind.Enum) {
      this._typed.Resolutions[call] = "default";
      return resolved;
    }

    foreach (var argument in call.Arguments)
      this.Expression(argument);

    throw this.Invalid($"E0599: no function or associated item named `{name}` found for `{type}`", call.At);
  }

  /// <summary>The type a path type names when it is a type parameter, <c>Self::Output</c> or <c>T::Output</c>; <see langword="null"/> for any other path.</summary>
  private RType? ParameterType(IReadOnlyList<string> segments, Span at) {
    if (segments.Count == 1 && this._typeParameters is { } parameters && parameters.TryGetValue(segments[0], out var parameter))
      return parameter;

    if (segments.Count != 2)
      return null;

    var owner = segments[0] == "Self" ? this._self : this._typeParameters?.GetValueOrDefault(segments[0]);
    if (owner is null)
      return null;

    if (owner.Resolved.Kind == RKind.Param)
      return this.Unsupported(RustFrontend.UnsupportedTrait, $"associated type {string.Join("::", segments)} of a type parameter", at) ?? this.ErrorType();

    if (this._associatedTypes.TryGetValue(TypeKey(owner), out var types) && types.TryGetValue(segments[1], out var associated))
      return associated;

    // The operators of the primitives yield their own type.
    return owner.IsNumber && segments[1] == "Output" ? owner.Resolved : null;
  }

}
