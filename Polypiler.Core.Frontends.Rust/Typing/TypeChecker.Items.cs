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

  /// <summary>The function being checked, whose path a nested function's path extends.</summary>
  private FunctionSignature? _enclosing;

  /// <summary>The signature of a function of the crate, by its item.</summary>
  public FunctionSignature? SignatureOf(FunctionItem item) => this._signatures.GetValueOrDefault(item);

  /// <summary>Declares the types, the signatures and the constants of the crate; <see langword="false"/> when one of them was refused.</summary>
  public bool Declare() {
    var failed = false;
    void Failed() => failed = true;

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

    foreach (var (item, module, impl) in this._crate.Functions) {
      RType? self = null;
      if (impl is not null) {
        self = this.Resolve(impl.Type, false, module, null);
        if (self is null) {
          Failed();
          continue;
        }

        if (self.Resolved.Kind is not (RKind.Struct or RKind.Enum)) {
          this._report(RustFrontend.InvalidSource, $"E0116: cannot define inherent `impl` for a type outside of the crate where the type is defined: `{impl.Type.Text}`", impl.At);
          Failed();
          continue;
        }
      }

      var signature = this.Signature(item, module, self);
      if (signature is null) {
        Failed();
        continue;
      }

      if (self is not null) {
        var methods = self.Resolved.Kind == RKind.Struct ? self.Struct!.Methods : self.Enum!.Methods;
        if (!methods.TryAdd(item.Name, signature)) {
          this._report(RustFrontend.InvalidSource, $"E0592: duplicate definitions with name `{item.Name}`", item.At);
          Failed();
        }
      }
    }

    foreach (var (impl, module) in this._crate.Impls) {
      var self = this.Resolve(impl.Type, false, module, null);
      if (self is null)
        continue;

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

    var self = RType.StructOf(info);
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

      var resolved = type.Resolved;
      if (resolved.Kind is RKind.Vec or RKind.Function || resolved.Kind == RKind.Array && !resolved.Element!.IsNumber) {
        this._report(RustFrontend.UnsupportedElement, $"field '{field.Name}' of type '{field.Type.Text}'", field.Type.At);
        ok = false;
        continue;
      }

      info.Fields.Add((field.Name, type, field.IsVisible));
    }

    if (ok && info.Derives.Contains("Copy") && !info.Derives.Contains("Clone")) {
      this._report(RustFrontend.InvalidSource, $"E0277: the trait bound `{info.Name}: Clone` is not satisfied", item.At);
      ok = false;
    }

    if (ok && info.Derives.Contains("Copy") && info.Fields.FirstOrDefault(field => !field.Type.IsCopy) is { Name: { } name })
      this._deferredDeclarations.Add(() => this._report(RustFrontend.InvalidSource, $"E0204: the trait `Copy` cannot be implemented for this type: field `{name}` does not implement `Copy`", item.At));

    if (ok && info.Derives.Contains("Eq") && !info.Derives.Contains("PartialEq")) {
      this._report(RustFrontend.InvalidSource, $"E0277: can't compare `{info.Name}` with `{info.Name}`", item.At);
      ok = false;
    }

    return ok;
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
  private FunctionSignature? Signature(FunctionItem item, ModuleScope module, RType? self) {
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
    var owner = self is null ? null : self.Resolved.Kind == RKind.Struct ? self.Struct!.Name : self.Enum!.Name;
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
    this._typed.Functions.Add(signature);
    return signature;
  }

  /// <summary>A function declared in a block: its signature, and its body checked once, on its own.</summary>
  private FunctionSignature Nested(FunctionItem item) {
    if (this._signatures.TryGetValue(item, out var known))
      return known;

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
      this._variables.Clear();
      this._undecided.Clear();
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
