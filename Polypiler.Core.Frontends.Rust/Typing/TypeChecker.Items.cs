using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// The items of the crate: structs and enums with their fields, derives and discriminants, the
/// signatures of the functions and methods, and the constants (The Rust Reference, "Structs",
/// "Enumerations", "Implementations", "Derive", "Type layout").
/// </summary>
internal sealed partial class TypeChecker {

  /// <summary>The derives the subset imports.</summary>
  private static readonly HashSet<string> _derives = ["Clone", "Copy", "PartialEq", "Eq", "Default", "Debug"];

  private readonly Dictionary<StructItem, StructInfo> _structs = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<EnumItem, EnumInfo> _enums = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<FunctionItem, FunctionSignature> _signatures = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<FunctionSignature> _nestedChecked = new(ReferenceEqualityComparer.Instance);

  /// <summary>The generic functions of the crate outside impls, whose instances the crate may export.</summary>
  private readonly List<FunctionSignature> _templates = [];

  /// <summary>
  /// Checks the generic functions the crate exports, once every body is checked. The IR's functions
  /// have no type parameters, so the exported surface is each instance the crate makes, named after
  /// its type arguments; a public generic function the crate instantiates nowhere has none, and one
  /// instantiated with a type that is not public would show a type no caller can name.
  /// </summary>
  public void CheckExportedGenerics(IReadOnlySet<FunctionItem> exported) {
    foreach (var template in this._templates.Where(template => exported.Contains(template.Item))) {
      if (template.Instances.Count == 0) {
        this._report(RustFrontend.UnsupportedGeneric, $"public generic fn {template.Item.Name}, which the crate instantiates nowhere: its exported surface is the instances the crate makes", template.Item.At);
        continue;
      }

      foreach (var (arguments, instance) in template.Instances)
        if (template.Generics.Select(parameter => instance.Substitution![parameter.Name]).FirstOrDefault(HidesType) is { } hidden)
          this._report(RustFrontend.UnsupportedGeneric, $"public generic fn {template.Item.Name}<{arguments}>, whose exported instance names the private type '{hidden}'", template.Item.At);
    }
  }

  /// <summary>Whether a type names a struct or an enum that is not <c>pub</c>, which the exported surface cannot show.</summary>
  private static bool HidesType(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind == RKind.Struct && !resolved.Struct!.Item.IsPublic || resolved.Kind == RKind.Enum && !resolved.Enum!.Item.IsPublic
      || resolved.Kind is not (RKind.Closure or RKind.Iterator) && ((resolved.Element is { } element && HidesType(element)) || resolved.Elements.Any(HidesType));
  }

  /// <summary>The function being checked, whose path a nested function's path extends.</summary>
  private FunctionSignature? _enclosing;

  /// <summary>Declares the types, the signatures and the constants of the crate; <see langword="false"/> when one of them was refused.</summary>
  public bool Declare() {
    var failed = false;
    void Failed() => failed = true;
    if (!this.DeclareTraits())
      Failed();

    foreach (var (item, module) in this._crate.Types) {
      var path = Mangled(module, item is StructItem s ? s.Name : ((EnumItem)item).Name);
      if (item is StructItem structure) {
        var info = new StructInfo(structure.Name, path, structure) { Module = module };
        this._structs[structure] = info;
        this._typed.Structs.Add(info);
      } else {
        var enumeration = (EnumItem)item;
        var info = new EnumInfo(enumeration.Name, path, enumeration) { Module = module };
        this._enums[enumeration] = info;
        this._typed.Enums.Add(info);
      }
    }

    foreach (var info in this._typed.Structs)
      if (!this.StructFields(info))
        Failed();

    foreach (var info in this._typed.Enums)
      if (!this.Variants(info))
        Failed();

    // A struct must not contain itself, which would make it infinitely large.
    foreach (var info in this._typed.Structs)
      if (Contains(info, info, [])) {
        this._report(RustFrontend.InvalidSource, $"E0072: recursive type `{info.Name}` has infinite size", info.Item.At);
        Failed();
      }

    var impls = new Dictionary<ImplItem, RType?>(ReferenceEqualityComparer.Instance);
    foreach (var (impl, module) in this._crate.Impls)
      impls[impl] = this.ImplType(impl, module);

    foreach (var (item, module, impl) in this._crate.Functions) {
      RType? self = null;
      if (impl is not null) {
        self = impls[impl];
        if (self is null) {
          Failed();
          continue;
        }
      }

      // A method of a generic impl is a template over the impl's type parameters and its own.
      IReadOnlyList<GenericParameter> implGenerics = impl?.Generics ?? [];
      var signature = item.Generics.Count + implGenerics.Count > 0 ? this.Template(item, module, self, [.. implGenerics, .. item.Generics], implGenerics.Count) : this.Signature(item, module, self);
      if (signature is null) {
        Failed();
        continue;
      }

      if (item.Generics.Count > 0 && impl is null)
        this._templates.Add(signature);

      if (impl?.Trait is not null) {
        this.TraitMethod(self!, impl.Trait.Name.Split("::")[^1], signature);
      } else if (self is not null) {
        var methods = self.Resolved.Kind == RKind.Struct ? self.Struct!.Methods : self.Enum!.Methods;
        if (!methods.TryAdd(item.Name, signature)) {
          this._report(RustFrontend.InvalidSource, $"E0592: duplicate definitions with name `{item.Name}`", item.At);
          Failed();
        }
      }
    }

    foreach (var (impl, module) in this._crate.Impls) {
      var self = impls[impl];
      if (self is null)
        continue;

      if (impl.Trait is not null) {
        if (!this.ProvidedMethods(impl, module, self))
          Failed();

        continue;
      }

      foreach (var constant in impl.Items.OfType<ConstItem>()) {
        if (!this.Constant(constant, module, self)) {
          Failed();
          continue;
        }

        var constants = self.Resolved.Kind == RKind.Struct ? self.Struct!.Constants : self.Enum!.Constants;
        constants[constant.Name] = this._constants[constant];
      }
    }

    foreach (var (constant, module) in this._crate.Constants)
      if (!this.Constant(constant, module))
        Failed();

    return !failed;
  }

  /// <summary>
  /// The type an impl is for, its trait impl checked; <see langword="null"/>, reported, when the type
  /// is refused, when an inherent impl is for a type of another crate or the impl is generic.
  /// </summary>
  private RType? ImplType(ImplItem impl, ModuleScope module) {
    // A generic impl is one of a generic struct for all its instances: impl<T> S<T>, its parameters in order.
    var symbol = impl.Generics.Count == 0 ? null : this._crate.Resolve(module, impl.Type.Name.Split("::"), impl.Type.At, false);
    if (impl.Generics.Count > 0 && !(symbol?.Target is StructItem generic && impl.Type.Arguments.Select(argument => argument.Name).SequenceEqual(impl.Generics.Select(parameter => parameter.Name))
        && impl.Type.Arguments.All(argument => argument.Shape == TypeShape.Path && argument.Arguments.Count == 0) && generic.Generics.Count == impl.Generics.Count)) {
      this._report(RustFrontend.UnsupportedGeneric, $"impl<{string.Join(", ", impl.Generics.Select(parameter => parameter.Name))}> for {impl.Type.Text}, which is no generic struct of the crate for all its type arguments in order", impl.At);
      return null;
    }

    var saved = this._typeParameters;
    if (impl.Generics.Count > 0)
      this._typeParameters = impl.Generics.ToDictionary(parameter => parameter.Name, parameter => RType.Param(parameter.Name));

    try {
      return this.ImplSelf(impl, module);
    } finally {
      this._typeParameters = saved;
    }
  }

  /// <summary>The type of an impl, its trait impl checked, in the scope of the impl's type parameters.</summary>
  private RType? ImplSelf(ImplItem impl, ModuleScope module) {
    var self = this.Resolve(impl.Type, false, module, null);
    if (self is null)
      return null;

    if (impl.Trait is null && impl.Items.OfType<AssociatedTypeItem>().FirstOrDefault() is { } associated) {
      this._report(RustFrontend.InvalidSource, "E0658: inherent associated types are unstable", associated.At);
      return null;
    }

    // The methods of a struct are those of all its instances: an inherent impl of one instance is not.
    if (impl.Trait is null && self.Resolved is { Kind: RKind.Struct, Elements.Count: > 0 } instance && !HoldsParameter(instance)) {
      this._report(RustFrontend.UnsupportedGeneric, $"impl {impl.Type.Text}, an inherent impl of one instance of a generic struct", impl.At);
      return null;
    }

    if (impl.Trait is null && self.Resolved.Kind is not (RKind.Struct or RKind.Enum)) {
      this._report(RustFrontend.InvalidSource, $"E0116: cannot define inherent `impl` for a type outside of the crate where the type is defined: `{impl.Type.Text}`", impl.At);
      return null;
    }

    return impl.Trait is null || this.TraitImpl(impl, module, self) ? self : null;
  }

  /// <summary>The name of an item in the IR: its crate path, the separators made underscores; the root's items keep their own names.</summary>
  public static string Mangled(ModuleScope module, string name) => module.Path.Count == 0 ? name : string.Join("_", module.Path) + "_" + name;

  private static bool Contains(StructInfo outer, StructInfo inner, HashSet<StructInfo> seen) {
    if (!seen.Add(inner))
      return false;

    foreach (var (_, type, _) in inner.Fields) {
      var field = type.Resolved;
      while (field.Kind is RKind.Array or RKind.Option)
        field = field.Element!.Resolved;

      if (field.Kind == RKind.Struct && (ReferenceEquals(field.Struct, outer) || Contains(outer, field.Struct!, seen)))
        return true;
    }

    return false;
  }

  private bool Derives(IEnumerable<Attribute> attributes, HashSet<string> derives) {
    var ok = true;
    foreach (var attribute in attributes.Where(attribute => attribute.Name == "derive")) {
      foreach (var argument in attribute.Arguments) {
        var name = argument.Split("::")[^1];
        if (!_derives.Contains(name)) {
          this._report(RustFrontend.UnsupportedDerive, $"derive({argument})", attribute.At);
          ok = false;
          continue;
        }

        derives.Add(name);
      }
    }

    return ok;
  }

  private bool StructFields(StructInfo info) {
    var item = info.Item;
    var ok = this.Derives(item.Attributes, info.Derives);
    foreach (var repr in item.Attributes.Where(attribute => attribute.Name == "repr")) {
      if (repr.Arguments is ["C"]) {
        info.ReprC = true;
        continue;
      }

      this._report(RustFrontend.UnsupportedDerive, $"repr({string.Join(", ", repr.Arguments)})", repr.At);
      ok = false;
    }

    // A generic struct's fields are of its type parameters; each use substitutes its type arguments.
    var self = TemplateOf(info);
    var saved = this._typeParameters;
    this._typeParameters = info.Parameters.ToDictionary(parameter => parameter, RType.Param);
    try {
      ok &= this.Fields(info, self);
    } finally {
      this._typeParameters = saved;
    }

    if (ok && info.Parameters.FirstOrDefault(parameter => info.Fields.All(field => !Mentions(field.Type, parameter))) is { } unused) {
      this._report(RustFrontend.InvalidSource, $"E0392: type parameter `{unused}` is never used", item.At);
      ok = false;
    }

    if (ok && info.Derives.Contains("Copy") && !info.Derives.Contains("Clone")) {
      this._report(RustFrontend.InvalidSource, $"E0277: the trait bound `{info.Name}: Clone` is not satisfied", item.At);
      ok = false;
    }

    // Whether the fields are Copy is known once every enum has its derives; a type parameter is Copy where the struct is.
    if (ok && info.Derives.Contains("Copy"))
      this._deferredDeclarations.Add(() => {
        if (info.Fields.FirstOrDefault(field => !field.Type.IsCopy && !HoldsParameter(field.Type)) is { Name: { } name })
          this._report(RustFrontend.InvalidSource, $"E0204: the trait `Copy` cannot be implemented for this type: field `{name}` does not implement `Copy`", item.At);
      });

    if (ok && info.Derives.Contains("Eq") && !info.Derives.Contains("PartialEq")) {
      this._report(RustFrontend.InvalidSource, $"E0277: can't compare `{info.Name}` with `{info.Name}`", item.At);
      ok = false;
    }

    return ok;
  }

  /// <summary>The type of a struct as its own declaration names it: a generic struct with its type parameters as arguments.</summary>
  private static RType TemplateOf(StructInfo info)
    => info.Parameters.Count == 0 ? RType.StructOf(info) : RType.StructOf(info, [.. info.Parameters.Select(RType.Param)]);

  /// <summary>Whether a type names a type parameter.</summary>
  private static bool Mentions(RType type, string parameter) {
    var resolved = type.Resolved;
    return resolved.Kind == RKind.Param && resolved.Name == parameter || (resolved.Element is { } element && Mentions(element, parameter)) || resolved.Elements.Any(part => Mentions(part, parameter));
  }

  /// <summary>Resolves the fields of a struct; <see langword="false"/> when one is refused.</summary>
  private bool Fields(StructInfo info, RType self) {
    var ok = true;
    var item = info.Item;
    foreach (var field in item.Fields) {
      if (ContainsReference(field.Type)) {
        this._report(RustFrontend.UnsupportedLifetime, $"field '{field.Name}' of reference type '{field.Type.Text}'", field.Type.At);
        ok = false;
        continue;
      }

      var type = this.Resolve(field.Type, false, info.Module!, self);
      if (type is null) {
        ok = false;
        continue;
      }

      // A field of a type parameter is checked for each instance (CheckInstances).
      if (!HoldsParameter(type) && FieldProblem(type) is { } code) {
        this._report(code, code == RustFrontend.UnsupportedTrait ? $"field '{field.Name}' holding the trait object '{field.Type.Text}'" : $"field '{field.Name}' of type '{field.Type.Text}'", field.Type.At);
        ok = false;
        continue;
      }

      info.Fields.Add((field.Name, type, field.IsVisible));
    }

    return ok;
  }

  /// <summary>
  /// The refusal a field of a type gets, as the IR's structs hold no optional value, list, map, set,
  /// string, function, object, or array of anything but numbers, and no tuple of one;
  /// <see langword="null"/> for a field they hold.
  /// </summary>
  private static string? FieldProblem(RType type) {
    var resolved = type.Resolved;
    if (resolved.IsTraitObject)
      return RustFrontend.UnsupportedTrait;

    return resolved.Kind is RKind.Vec or RKind.Function or RKind.Map or RKind.Set or RKind.Option || resolved.IsText || resolved.Kind == RKind.Array && !resolved.Element!.IsNumber
        || resolved.Kind == RKind.Tuple && resolved.Elements.Any(element => TuplePart(element) is not null)
      ? RustFrontend.UnsupportedElement
      : null;
  }

  /// <summary>
  /// The part of a tuple no IR struct holds - a reference (a view or a place) or what <see cref="FieldProblem"/>
  /// names - which a tuple made as a value cannot hold; <see langword="null"/> when it holds every part.
  /// A tuple destructured where it is made is its parts and holds nothing.
  /// </summary>
  public static RType? TuplePart(RType type) {
    var resolved = type.Resolved;
    if (resolved.Kind == RKind.Tuple)
      return resolved.Elements.Select(TuplePart).FirstOrDefault(part => part is not null);

    return resolved.Kind == RKind.Reference || FieldProblem(resolved) is not null ? resolved : null;
  }

  private readonly HashSet<string> _checkedInstances = new(System.StringComparer.Ordinal);

  /// <summary>Checks the fields of every instance of a generic struct a decided type holds, once each: a field of a type argument the IR's structs do not hold is refused.</summary>
  private void CheckInstances(RType type, Span at) {
    var resolved = type.Resolved;
    if (resolved.Kind is RKind.Closure or RKind.Iterator or RKind.Variable)
      return;

    if (resolved.Kind == RKind.Struct && resolved.Elements.Count > 0 && !HoldsParameter(resolved) && this._checkedInstances.Add(resolved.ToString())) {
      foreach (var (name, field, _) in resolved.StructFields)
        if (FieldProblem(field) is { } code)
          this.Refuse(new Refusal(code, $"field '{name}' of type '{field}' of {resolved}", at));

      // The bounds of the struct's parameters, its where clause's included, hold for every instance.
      var info = resolved.Struct!;
      var substitution = info.Parameters.Select((parameter, index) => (parameter, resolved.Elements[index])).ToDictionary(pair => pair.parameter, pair => pair.Item2);
      foreach (var parameter in info.Item.Generics)
        foreach (var bound in parameter.Bounds)
          this.RequireBound(substitution[parameter.Name], bound, info.Module!, substitution, at);
    }

    if (resolved.Element is { } element)
      this.CheckInstances(element, at);

    foreach (var part in resolved.Elements)
      this.CheckInstances(part, at);
  }

  /// <summary>Checks that can only run once every type is known.</summary>
  private readonly List<System.Action> _deferredDeclarations = [];

  /// <summary>Runs the checks of the declarations that waited for every type; what fails is reported.</summary>
  public void FinishDeclarations() {
    foreach (var check in this._deferredDeclarations)
      check();
  }

  private static bool ContainsReference(TypeRef type)
    => type.Shape is TypeShape.Reference || type.Arguments.Any(ContainsReference) || (type.Element is { } element && type.Shape != TypeShape.Function && ContainsReference(element));

  private bool Variants(EnumInfo info) {
    var item = info.Item;
    var ok = this.Derives(item.Attributes, info.Derives);
    foreach (var repr in item.Attributes.Where(attribute => attribute.Name == "repr")) {
      if (repr.Arguments is [var name] && RType.Named(name) is { Kind: RKind.Integer } representation) {
        info.Representation = representation;
        info.HasRepr = true;
        continue;
      }

      this._report(RustFrontend.UnsupportedDerive, $"repr({string.Join(", ", repr.Arguments)})", repr.At);
      ok = false;
    }

    if (item.Variants.Count == 0) {
      this._report(RustFrontend.Unsupported, $"enum '{item.Name}' without variants", item.At);
      return false;
    }

    // Each discriminant is the explicit one, or the one before plus one, the first 0 (The Rust Reference, "Enumerations").
    BigInteger next = 0;
    var values = new HashSet<BigInteger>();
    foreach (var variant in item.Variants) {
      var value = next;
      if (variant.Discriminant is { } discriminant) {
        var (savedModule, savedSelf) = (this._module, this._self);
        this._module = info.Module!;
        var evaluated = Constants.Evaluate(discriminant, this._typed, this.ConstantNamed);
        this._module = savedModule;
        this._self = savedSelf;
        if (evaluated is null) {
          this._report(RustFrontend.Unsupported, $"discriminant of '{item.Name}::{variant.Name}' that is no constant the frontend evaluates", discriminant.At);
          ok = false;
          continue;
        }

        value = evaluated.Value;
      }

      if (value < info.Representation.Minimum || value > info.Representation.Maximum) {
        this._report(RustFrontend.InvalidSource, value > info.Representation.Maximum && variant.Discriminant is null
          ? $"E0370: enum discriminant overflowed: `{item.Name}::{variant.Name}`"
          : $"literal out of range for `{info.Representation}` (overflowing_literals)", variant.At);
        ok = false;
      }

      if (!values.Add(value)) {
        this._report(RustFrontend.InvalidSource, $"E0081: discriminant value `{value}` assigned more than once", variant.At);
        ok = false;
      }

      if (variant.IsDefault)
        info.Default = variant.Name;

      info.Variants.Add((variant.Name, value));
      next = value + 1;
    }

    if (info.Derives.Contains("Default") && info.Default is null) {
      this._report(RustFrontend.InvalidSource, "E0665: `#[derive(Default)]` on enum with no `#[default]`", item.At);
      ok = false;
    }

    return ok;
  }

  /// <summary>The signature of a function: its parameter and result types and, of a method, its receiver.</summary>
  private FunctionSignature? Signature(FunctionItem item, ModuleScope module, RType? self, bool register = true) {
    var parameters = item.Parameters.Select(parameter => this.Resolve(parameter.Type, false, module, self)).ToList();
    var result = this.Resolve(item.ReturnType, true, module, self);
    if (parameters.Any(parameter => parameter is null) || result is null) {
      foreach (var refusal in Refusals.In(item.Body))
        this._report(refusal.Code, refusal.Construct, refusal.At);

      return null;
    }

    if (item.Receiver != ReceiverKind.None && self is null) {
      this._report(RustFrontend.InvalidSource, "E0424: `self` parameter is only allowed in associated functions", item.ReceiverAt);
      return null;
    }

    if (result.Resolved is { Kind: RKind.Reference, Mutable: true }) {
      this._report(RustFrontend.UnsupportedLifetime, $"mutable reference result of '{item.Name}'", item.ReturnType!.At);
      return null;
    }

    RType? receiver = item.Receiver switch {
      ReceiverKind.Value => self,
      ReceiverKind.Shared => RType.Reference(self!, false),
      ReceiverKind.Exclusive => RType.Reference(self!, true),
      _ => null
    };
    var owner = self is null ? null : OwnerName(self);
    var path = owner is null ? item.Name : owner + "::" + item.Name;
    var signature = new FunctionSignature(item, parameters!, result) {
      Receiver = receiver,
      Module = module,
      CratePath = module.CratePath + "::" + path,
      IrName = Mangled(module, owner is null ? item.Name : owner + "_" + item.Name)
    };
    if (self is not null && receiver is null)
      this._selfTypes[signature] = self;

    this._signatures[item] = signature;
    if (register)
      this._typed.Functions.Add(signature);

    return signature;
  }

  /// <summary>A function declared in a block: its signature, and its body checked once, on its own.</summary>
  private FunctionSignature Nested(FunctionItem item) {
    var enclosing = this._self;
    var signature = this.Signature(item, this._module, null) ?? throw new CheckAbort();
    if (this._enclosing is { } outer) {
      signature.IrName = outer.IrName + "_" + item.Name;
      signature = signature with { CratePath = outer.CratePath + "::" + item.Name };
      this._signatures[item] = signature;
      this._typed.Functions[^1] = signature;
    }

    if (this._nestedChecked.Add(signature)) {
      var (variables, undecided, deferred, errors) = (this._variables.ToList(), this._undecided.ToList(), this._deferred.ToList(), this._errors.ToList());
      var expressions = this._expressions.ToList();
      this._variables.Clear();
      this._undecided.Clear();
      this._expressions.Clear();
      this._deferred.Clear();
      this._self = null;
      var outerFunction = this._enclosing;
      this._enclosing = signature;
      try {
        this.Body(signature);
        this.Finish();
        foreach (var check in this._deferred)
          check();

        this.Moves(item);
      } finally {
        this._variables.Clear();
        this._variables.AddRange(variables);
        this._undecided.Clear();
        this._undecided.AddRange(undecided);
        this._expressions.Clear();
        this._expressions.AddRange(expressions);
        this._deferred.Clear();
        this._deferred.AddRange(deferred);
        this._errors.UnionWith(errors);
        this._self = enclosing;
        this._enclosing = outerFunction;
      }
    }

    return signature;
  }

}
