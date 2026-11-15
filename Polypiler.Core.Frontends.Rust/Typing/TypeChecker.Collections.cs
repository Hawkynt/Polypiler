using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// <c>HashMap</c>, <c>BTreeMap</c>, <c>HashSet</c> and <c>BTreeSet</c> onto the IR's maps and sets:
/// insertion, lookup, removal, counting and <c>entry(k).or_insert(v)</c>. The order a <c>HashMap</c>
/// or a <c>HashSet</c> enumerates in is unspecified and differs from one run to the next, so no
/// iteration is imported; a <c>BTreeMap</c>'s sorted iteration is refused too, until the IR has a
/// sorted map.
/// </summary>
internal sealed partial class TypeChecker {

  private static readonly HashSet<string> _maps = ["HashMap", "BTreeMap"];
  private static readonly HashSet<string> _sets = ["HashSet", "BTreeSet"];

  /// <summary>The map or set type a path names when <c>use</c> brought it into scope or the path is <c>std::collections::X</c>; <see langword="null"/> for any other path.</summary>
  private RType? CollectionType(TypeRef type, string[] segments, Symbol? symbol) {
    var name = symbol?.Target is LibraryType library ? library.Name : segments is ["std", "collections", var named] ? named : null;
    if (name is null || !(_maps.Contains(name) || _sets.Contains(name)))
      return null;

    var map = _maps.Contains(name);
    if (type.Arguments.Count != (map ? 2 : 1))
      return this.Unsupported(RustFrontend.InvalidSource, $"E0107: missing generics for `{name}`", type.At) ?? this.ErrorType();

    var key = this.ResolveShape(type.Arguments[0], false);
    var value = map ? this.ResolveShape(type.Arguments[1], false) : null;
    if (key is null || (map && value is null))
      return null;

    if (!KeyType(key))
      return this.Unsupported(RustFrontend.UnsupportedCollection, $"{name} keyed by '{key}'", type.At);

    if (map && !ValueType(value!))
      return this.Unsupported(RustFrontend.UnsupportedCollection, $"{name} of '{value}' values", type.At);

    return map ? RType.Map(name, key, value!) : RType.Set(name, key);
  }

  /// <summary>Whether a type can be a key of an IR map or an element of an IR set as Rust hashes or orders it: an integer, <c>bool</c>, <c>char</c> or text.</summary>
  private static bool KeyType(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind is RKind.Integer or RKind.Bool or RKind.Char || resolved.IsText;
  }

  /// <summary>
  /// Whether a type can be a value of an IR map that <c>get</c>, <c>insert</c> and <c>remove</c> hand
  /// back as an IR optional value: a number, <c>bool</c>, <c>char</c>, a struct, an enum.
  /// </summary>
  private static bool ValueType(RType type) {
    var resolved = type.Resolved;
    return resolved.IsScalar || resolved.Kind is RKind.Char or RKind.Struct or RKind.Enum;
  }

  /// <summary>Checks the keys and values of a map or set type once they are decided.</summary>
  private void CollectionParts(RType collection, Span at) {
    this._deferred.Add(() => {
      var resolved = collection.Resolved;
      var key = resolved.Kind == RKind.Map ? resolved.Key : resolved.Element!;
      if (!this.IsError(key) && !KeyType(key))
        this.Refuse(new Refusal(RustFrontend.UnsupportedCollection, $"{resolved.Name} keyed by '{key}'", at));

      if (resolved.Kind == RKind.Map && !this.IsError(resolved.Element!) && !ValueType(resolved.Element!))
        this.Refuse(new Refusal(RustFrontend.UnsupportedCollection, $"{resolved.Name} of '{resolved.Element}' values", at));
    });
  }

  /// <summary><c>HashMap::new()</c>, <c>HashSet::with_capacity(n)</c> and their relatives; <see langword="null"/> for any other path.</summary>
  private RType? CollectionCall(CallExpr call, PathExpr path, RType? expected) {
    var owner = path.Segments[^2];
    var name = path.Segments[^1];
    var prefix = path.Segments.Take(path.Segments.Count - 2).ToList();
    // A use may rename the type: the name is the library's.
    if (prefix.Count == 0 && this._crate.Lookup(this._module, owner, new(ReferenceEqualityComparer.Instance)) is { Target: LibraryType library })
      owner = library.Name;
    else if (prefix is not ["std", "collections"])
      return null;

    if (!(_maps.Contains(owner) || _sets.Contains(owner)) || name is not ("new" or "with_capacity"))
      return null;

    this._typed.Resolutions[call] = "collection::" + name;
    this.ArgumentCount(call, name == "new" ? 0 : 1);
    if (name == "with_capacity")
      this.Unify(RType.Usize, this.Value(call.Arguments[0], RType.Usize), call.Arguments[0].At);

    var generics = path.Generics.TryGetValue(path.Segments.Count - 2, out var given) ? given : null;
    var map = _maps.Contains(owner);
    var wanted = expected?.Resolved;
    RType Part(int index) => generics is { } types && types.Count > index ? this.Resolve(types[index], false) ?? this.ErrorType()
      : wanted is { Kind: RKind.Map or RKind.Set } && wanted.Name == owner ? (index == 0 ? (map ? wanted.Key : wanted.Element!) : wanted.Element!)
      : this.Fresh(VariableKind.Any);
    var collection = map ? RType.Map(owner, Part(0), Part(1)) : RType.Set(owner, Part(0));
    this.CollectionParts(collection, call.At);
    return collection;
  }

  /// <summary>A key or an element passed by reference, as <c>get(&amp;k)</c> takes it: a reference to the key type, or <c>&amp;str</c> for a key of text.</summary>
  private void KeyArgument(Expr argument, RType key) {
    var given = this.Value(argument, RType.Reference(key, false));
    if (key.Resolved.IsText && given.IsText)
      return;

    this.Coerce(RType.Reference(key, false), given, argument.At);
  }

  private RType MapMethod(MethodCallExpr call, RType receiver, RType map) {
    var key = map.Key;
    var value = map.Element!;
    this._typed.Methods[call] = "map::" + call.Name;
    switch (call.Name) {
      case "len":
        this.Count(call, 0);
        return RType.Usize;
      case "is_empty":
        this.Count(call, 0);
        return RType.Bool;
      case "clear":
        this.Count(call, 0);
        this.RequireMutable(call, receiver);
        return RType.Unit;
      case "insert":
        this.Count(call, 2);
        this.Coerce(key, this.Value(call.Arguments[0], key), call.Arguments[0].At);
        this.Coerce(value, this.Value(call.Arguments[1], value), call.Arguments[1].At);
        this.RequireMutable(call, receiver);
        return RType.Option(value);
      case "get" or "contains_key" or "remove":
        this.Count(call, 1);
        this.KeyArgument(call.Arguments[0], key);
        if (call.Name == "remove")
          this.RequireMutable(call, receiver);

        return call.Name switch {
          "get" => RType.Option(RType.Reference(value, false)),
          "contains_key" => RType.Bool,
          _ => RType.Option(value)
        };
    }

    foreach (var argument in call.Arguments)
      this.Expression(argument);

    this._typed.Methods.Remove(call);
    return call.Name is "iter" or "iter_mut" or "keys" or "values" or "values_mut" or "into_iter" or "into_keys" or "into_values" or "drain"
      ? this.Refused(RustFrontend.UnsupportedCollection, $"iteration of a {map.Name}{(map.Name == "HashMap" ? ", whose order is unspecified" : "")}", call.At)
      : this.Refused(RustFrontend.UnsupportedCollection, $"{map.Name}::{call.Name}", call.At);
  }

  private RType SetMethod(MethodCallExpr call, RType receiver, RType set) {
    var element = set.Element!;
    this._typed.Methods[call] = "set::" + call.Name;
    switch (call.Name) {
      case "len":
        this.Count(call, 0);
        return RType.Usize;
      case "is_empty":
        this.Count(call, 0);
        return RType.Bool;
      case "clear":
        this.Count(call, 0);
        this.RequireMutable(call, receiver);
        return RType.Unit;
      case "insert":
        this.Count(call, 1);
        this.Coerce(element, this.Value(call.Arguments[0], element), call.Arguments[0].At);
        this.RequireMutable(call, receiver);
        return RType.Bool;
      case "contains" or "remove":
        this.Count(call, 1);
        this.KeyArgument(call.Arguments[0], element);
        if (call.Name == "remove")
          this.RequireMutable(call, receiver);

        return RType.Bool;
    }

    foreach (var argument in call.Arguments)
      this.Expression(argument);

    this._typed.Methods.Remove(call);
    return call.Name is "iter" or "into_iter" or "drain"
      ? this.Refused(RustFrontend.UnsupportedCollection, $"iteration of a {set.Name}{(set.Name == "HashSet" ? ", whose order is unspecified" : "")}", call.At)
      : this.Refused(RustFrontend.UnsupportedCollection, $"{set.Name}::{call.Name}", call.At);
  }

  /// <summary>
  /// <c>map.entry(k).or_insert(v)</c> and <c>or_default()</c>: the value of the key, inserted first
  /// when the key is absent, as a <c>&amp;mut V</c> the caller writes through; <see langword="null"/>
  /// for any other call.
  /// </summary>
  private RType? Entry(MethodCallExpr call) {
    if (call is not { Name: "or_insert" or "or_default", Receiver: MethodCallExpr { Name: "entry" } entry })
      return null;

    var receiver = this.Value(entry.Receiver);
    if (receiver.Dereferenced is not { Kind: RKind.Map } map)
      return null;

    if (entry.Arguments.Count != 1)
      throw this.Invalid($"E0061: this method takes 1 argument but {entry.Arguments.Count} were supplied", entry.At);

    this.Coerce(map.Key, this.Value(entry.Arguments[0], map.Key), entry.Arguments[0].At);
    this.Count(call, call.Name == "or_insert" ? 1 : 0);
    if (call.Name == "or_insert")
      this.Coerce(map.Element!, this.Value(call.Arguments[0], map.Element), call.Arguments[0].At);

    this.RequireMutable(entry, receiver);
    this._typed.Types[entry] = RType.Unit;
    this._typed.Methods[call] = "map::" + call.Name;
    return RType.Reference(map.Element!, true);
  }

}
