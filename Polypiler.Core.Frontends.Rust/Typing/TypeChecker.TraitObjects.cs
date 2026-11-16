using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// Trait objects: <c>&amp;dyn Trait</c>, <c>Box&lt;dyn Trait&gt;</c> and <c>Rc&lt;dyn Trait&gt;</c> of
/// the crate's traits, made from a value of a type that implements the trait where the source expects
/// one (The Rust Reference, "Trait objects", "Type coercions": unsized coercion), and the calls of
/// their methods, which dispatch on the value's type at run time. A trait is dyn compatible as the
/// reference says: no associated function without <c>self</c>, no generic method, no <c>Self</c> in
/// a signature (<c>E0038</c>). A <c>&amp;mut dyn Trait</c> is refused: the IR's object holds a copy of
/// the value it was made from, which writing through it would leave behind.
/// </summary>
internal sealed partial class TypeChecker {

  /// <summary>The trait object a <c>dyn</c> type names, its trait checked for dyn compatibility; <see langword="null"/>, reported, otherwise.</summary>
  private RType? TraitObject(TypeRef type) {
    var traits = type.Arguments.Where(bound => !IsMarker(bound.Name)).ToList();
    if (traits.Count == 0)
      return this.Unsupported(RustFrontend.UnsupportedTrait, $"{type.Text} without a trait of the crate", type.At);

    if (traits.Count > 1) {
      this._report(RustFrontend.InvalidSource, $"E0225: only auto traits can be used as additional traits in a trait object: `{type.Text}`", type.At);
      return null;
    }

    if (this.TraitNamed(traits[0], this._module) is not { } trait)
      return this.Unsupported(RustFrontend.UnsupportedTrait, $"{type.Text}, a trait object of a trait outside the crate", type.At);

    if (trait.Parameters.Count > 0)
      return this.Unsupported(RustFrontend.UnsupportedTrait, $"{type.Text}, a trait object of a generic trait", type.At);

    return this.DynMethods(trait, type.At) is null ? null : RType.DynOf(trait);
  }

  /// <summary>The methods a trait object of a trait dispatches, made once; <see langword="null"/>, reported, when the trait is not dyn compatible or the IR cannot dispatch it.</summary>
  private IReadOnlyList<DynMethod>? DynMethods(TraitInfo trait, Span at) {
    if (this._typed.TraitObjects.TryGetValue(trait, out var known))
      return known;

    var methods = new List<DynMethod>();
    foreach (var owner in Closure(trait)) {
      if (owner.Item.Items.OfType<AssociatedTypeItem>().FirstOrDefault() is { } associated) {
        this._report(RustFrontend.UnsupportedTrait, $"dyn {trait.Name}, whose trait {owner.Name} has the associated type {associated.Name}", at);
        return null;
      }

      foreach (var method in owner.Methods.Values) {
        var problem = method.Generics.Count > 0 ? $"method `{method.Name}` has generic type parameters"
          : method.Receiver == ReceiverKind.None ? $"associated function `{method.Name}` has no `self` parameter"
          : method.Parameters.Any(parameter => NamesSelf(parameter.Type)) || NamesSelf(method.ReturnType) ? $"method `{method.Name}` references the `Self` type in its parameters or return type"
          : null;
        if (problem is not null) {
          this._report(RustFrontend.InvalidSource, $"E0038: the trait `{trait.Name}` is not dyn compatible: {problem}", at);
          return null;
        }

        // A method that takes self by value is part of the trait but cannot be called on `dyn Trait`.
        if (method.Receiver == ReceiverKind.Value)
          continue;

        if (methods.Any(other => other.Item.Name == method.Name)) {
          this._report(RustFrontend.UnsupportedTrait, $"dyn {trait.Name}, whose traits share the method name '{method.Name}'", at);
          return null;
        }

        var parameters = method.Parameters.Select(parameter => this.Resolve(parameter.Type, false, owner.Module, null)).ToList();
        var result = this.Resolve(method.ReturnType, true, owner.Module, null);
        if (parameters.Any(parameter => parameter is null) || result is null)
          return null;

        if (parameters.Any(parameter => parameter!.Resolved is { Kind: RKind.Reference, Mutable: true } reference && reference.Element!.Resolved.Kind is not (RKind.Slice or RKind.Array or RKind.Vec))) {
          this._report(RustFrontend.UnsupportedTrait, $"dyn {trait.Name}, whose method '{method.Name}' takes a &mut parameter", at);
          return null;
        }

        methods.Add(new DynMethod(owner, method, parameters!, result));
      }
    }

    this._typed.TraitObjects[trait] = methods;
    return methods;
  }

  /// <summary>A trait and its supertraits, each once, the trait first.</summary>
  private static List<TraitInfo> Closure(TraitInfo trait) {
    var all = new List<TraitInfo>();
    var pending = new Stack<TraitInfo>([trait]);
    while (pending.TryPop(out var next)) {
      if (all.Contains(next))
        continue;

      all.Add(next);
      foreach (var supertrait in Enumerable.Reverse(next.Supertraits))
        pending.Push(supertrait);
    }

    return all;
  }

  /// <summary>Whether a type of a signature names <c>Self</c>.</summary>
  private static bool NamesSelf(TypeRef? type)
    => type is not null && (type.Name.Split("::")[0] == "Self" || type.Arguments.Any(NamesSelf) || NamesSelf(type.Element));

  /// <summary>
  /// A value where a trait object is expected: <c>&amp;T</c> where <c>&amp;dyn Trait</c> is, <c>T</c>
  /// implementing the trait, becomes the object (recorded for the lowering); <see langword="null"/>
  /// for any other pair, which the caller unifies. A <c>&amp;Box&lt;dyn Trait&gt;</c> is unsized too, as
  /// rustc does it, and no box implements a trait; <c>&amp;*b</c> reaches the object.
  /// </summary>
  private RType? Unsize(Expr expression, RType found, RType expected) {
    var wanted = expected.Resolved;
    if (wanted is not { Kind: RKind.Reference, Mutable: false } || wanted.Element!.Resolved.Kind != RKind.Dyn)
      return null;

    var source = found.Resolved;
    if (source.Kind != RKind.Reference)
      return null;

    var target = source.Element!.Resolved;
    if (target.Kind is RKind.Dyn or RKind.Variable || this.IsError(target))
      return null;

    this._typed.Unsized[expression] = this.Unsizing(target, wanted.Trait!, expression.At);
    return expected;
  }

  /// <summary>Checks that a type may become a trait object of a trait and finds the impl's method for each method the object dispatches.</summary>
  private Unsizing Unsizing(RType concrete, TraitInfo trait, Span at) {
    var resolved = concrete.Resolved;
    if (resolved.Kind is RKind.Str or RKind.Slice or RKind.Dyn)
      throw this.Invalid($"E0277: the size for values of type `{resolved}` cannot be known at compilation time", at);

    foreach (var required in Closure(trait))
      if (!this.Implements(resolved, required.Name))
        throw this.Invalid($"E0277: the trait bound `{resolved}: {required.Name}` is not satisfied", at);

    // A generic struct's type arguments may be decided only once the function is (W(5) is a W<i32>):
    // the impl's methods, of the instance for them, are found then.
    var unsizing = new Unsizing(trait, resolved, []);
    void Find() {
      var decided = Decided(resolved);
      foreach (var method in this._typed.TraitObjects[trait]) {
        var impl = this.TraitMethods(decided, method.Item.Name).First(candidate => candidate.Trait == method.Owner.Name).Method;
        unsizing.Impls.Add(impl.Generics.Count > 0 ? this.Instance(impl, decided.Elements, at) : impl);
      }
    }

    if (Undecided(resolved))
      this._deferred.Add(Find);
    else
      Find();

    return unsizing;
  }

  /// <summary>
  /// <c>Box::new(x)</c> and <c>Rc::new(x)</c> where a <c>Box&lt;dyn Trait&gt;</c> or an <c>Rc&lt;dyn Trait&gt;</c>
  /// is expected, and <c>Rc::clone(&amp;r)</c>; <see langword="null"/> for any other path. A box of a
  /// value that does not become a trait object is refused: the subset has no other.
  /// </summary>
  private RType? SmartPointerCall(CallExpr call, PathExpr path, RType? expected) {
    var segments = path.Segments;
    var used = segments.Count == 2 ? this._crate.Lookup(this._module, segments[0], new(ReferenceEqualityComparer.Instance)) : null;
    var (name, function) = segments switch {
      [_, var member] when used?.Target is LibraryType { Name: "Box" or "Rc" } library => (library.Name, member),
      [var pointer, var member] when pointer is "Box" or "Rc" && used is null => (pointer, member),
      ["std" or "alloc", "boxed", "Box", var member] => ("Box", member),
      ["std" or "alloc", "rc", "Rc", var member] => ("Rc", member),
      _ => (null, null)
    };
    if (name is null)
      return null;

    if (function == "clone" && name == "Rc" && call.Arguments.Count == 1) {
      var argument = this.Value(call.Arguments[0]).Resolved;
      if (argument is { Kind: RKind.Reference } reference && reference.Element!.Resolved is { Kind: RKind.Pointer, Name: "Rc" } shared) {
        this._typed.Resolutions[call] = "pointer::clone";
        return shared;
      }

      throw this.Invalid($"E0308: mismatched types: expected `&Rc<_>`, found `{argument}`", call.Arguments[0].At);
    }

    var wanted = expected?.Resolved;
    if (function != "new" || call.Arguments.Count != 1 || wanted is not { Kind: RKind.Pointer } || wanted.Name != name) {
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      return this.Refused(RustFrontend.SmartPointer, $"{path.Text} of a value that becomes no trait object", path.At);
    }

    var value = this.Value(call.Arguments[0]);
    if (value.Resolved.Kind == RKind.Variable)
      return this.IsError(value) ? value : throw this.Unresolved(Describe(call.Arguments[0]), call.Arguments[0].At);

    this._typed.Resolutions[call] = "pointer::new";
    this._typed.Unsized[call] = this.Unsizing(value, wanted.Trait!, call.At);
    return wanted;
  }

  /// <summary>
  /// A method call on a trait object: a method of its trait or a supertrait, dispatched on the value's
  /// type. <c>&amp;mut self</c> needs a <c>Box</c> the caller may change; a method taking <c>self</c> by
  /// value cannot be called on an unsized <c>dyn Trait</c>.
  /// </summary>
  private RType DynMethodCall(MethodCallExpr call, RType receiver, RType target) {
    var trait = target.Trait!;
    var method = this._typed.TraitObjects[trait].FirstOrDefault(candidate => candidate.Item.Name == call.Name);
    if (method is null) {
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      if (Closure(trait).Any(owner => owner.Methods.ContainsKey(call.Name)))
        throw this.Invalid($"E0161: cannot move a value of type `dyn {trait.Name}`", call.At);

      throw this.Invalid($"E0599: no method named `{call.Name}` found for `{receiver}` in the current scope", call.At);
    }

    if (method.Item.Receiver == ReceiverKind.Exclusive) {
      var through = receiver.Resolved;
      var pointer = through.Kind == RKind.Reference ? through.Element!.Resolved : through;
      if (pointer.Kind == RKind.Dyn)
        throw this.Invalid($"E0596: cannot borrow `*{Describe(call.Receiver)}` as mutable, as it is behind a `&` reference", call.Receiver.At);

      if (pointer.Name == "Rc")
        throw this.Invalid($"E0596: cannot borrow data in an `Rc` as mutable", call.Receiver.At);

      if (through.Kind == RKind.Reference && !through.Mutable)
        throw this.Invalid($"E0596: cannot borrow `**{Describe(call.Receiver)}` as mutable, as it is behind a `&` reference", call.Receiver.At);

      this._deferred.Add(() => {
        if (through.Kind == RKind.Pointer && !this.MutablePlace(call.Receiver) && !IsTemporary(call.Receiver))
          throw this.Invalid($"E0596: cannot borrow `{Describe(call.Receiver)}` as mutable, as it is not declared as mutable", call.Receiver.At);
      });
    }

    this._typed.DynamicCalls[call] = method;
    this.Uses(trait.Module);
    this.Count(call, method.Parameters.Count);
    for (var index = 0; index < method.Parameters.Count; ++index)
      this.Coerce(method.Parameters[index], this.Value(call.Arguments[index], method.Parameters[index]), call.Arguments[index].At);

    return method.Result;
  }

  /// <summary>A type without the references in front of it.</summary>
  private static RType StripReferences(RType type) {
    var resolved = type.Resolved;
    while (resolved.Kind == RKind.Reference)
      resolved = resolved.Element!.Resolved;

    return resolved;
  }

  /// <summary>The expected type a block, an <c>if</c> or a <c>match</c> passes on to the values it yields: a trait object, which those values become.</summary>
  private static RType? Carried(RType? expected) => expected is not null && expected.IsTraitObject ? expected : null;

}
