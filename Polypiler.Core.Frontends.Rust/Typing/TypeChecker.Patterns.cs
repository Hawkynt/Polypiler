using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary><c>for</c>, <c>match</c> and the patterns of every binding construct.</summary>
internal sealed partial class TypeChecker {

  // ---------------------------------------------------------------- for

  private RType For(ForExpr loop) {
    var iterator = this.Iterable(loop.Iterable);
    if (iterator is null) {
      this._scopes.Add([]);
      this._contexts.Add(new Context(loop, loop.Label, true, false) { ClosureDepth = this._closures.Count });
      this.Block(loop.Body);
      this._contexts.RemoveAt(this._contexts.Count - 1);
      this._scopes.RemoveAt(this._scopes.Count - 1);
      return RType.Unit;
    }

    if (iterator is { Kind: SourceKind.Range } && iterator.Adapters.All(adapter => adapter.Name is "rev" or "step_by"))
      this._typed.Ranges[loop] = iterator.Range!;
    else
      this._typed.Iterations[loop] = new Iteration(iterator.Kind, iterator.Range, iterator.Source, iterator.Adapters, iterator.Item) { RangeElement = iterator.RangeElement };

    this._scopes.Add([]);
    if (loop.Pattern is RefusedPattern refused)
      this.Refuse(refused.Refusal);
    else
      this.Irrefutable(loop.Pattern, iterator.Item, true, "for");

    this._contexts.Add(new Context(loop, loop.Label, true, false) { ClosureDepth = this._closures.Count });
    var body = this.Block(loop.Body);
    this._contexts.RemoveAt(this._contexts.Count - 1);
    this._scopes.RemoveAt(this._scopes.Count - 1);
    this.Unify(RType.Unit, body, loop.Body.Tail?.At ?? loop.At);
    return RType.Unit;
  }

  /// <summary>
  /// What a <c>for</c> iterates over: an integer range, an iterator, an array, a <c>Vec</c> or a
  /// reference to an array, a slice or a <c>Vec</c>. <see langword="null"/> when it was refused.
  /// </summary>
  private IteratorInfo? Iterable(Expr iterable) {
    if (iterable is RangeExpr range) {
      if (range.Start is null || range.End is null) {
        this.Refuse(new Refusal(RustFrontend.UnsupportedIterable, range.Start is null ? ".." : "a..", range.At));
        return null;
      }

      var info = this.RangeIterator(range, iterable);
      this._typed.Types[range] = RType.Unit;
      return info;
    }

    var type = this.Value(iterable);
    var resolved = type.Resolved;
    if (resolved.Kind == RKind.Iterator)
      return resolved.IteratorInfo;

    if (this.IsError(resolved))
      return null;

    var target = resolved.Dereferenced;
    if (target.Kind is RKind.Array or RKind.Slice or RKind.Vec) {
      var access = resolved.Kind == RKind.Reference ? resolved.Mutable ? ElementAccess.Exclusive : ElementAccess.Shared : ElementAccess.Value;
      var container = iterable is ReferenceExpr reference ? reference.Operand : iterable;
      var source = new ElementSource(container, target.Element!, access);
      return new IteratorInfo(SourceKind.Elements, null, source, [], ItemOf(target.Element!, access));
    }

    if (resolved.Kind == RKind.Variable)
      throw this.Unresolved(Describe(iterable), iterable.At);

    throw this.Invalid($"E0277: `{type}` is not an iterator", iterable.At);
  }

  // ---------------------------------------------------------------- match

  private RType Match(MatchExpr match) {
    var scrutinee = this.Value(match.Scrutinee);
    RType? result = null;
    foreach (var arm in match.Arms) {
      this._scopes.Add([]);
      this.Pattern(arm.Pattern, scrutinee, false);
      if (arm.Guard is not null)
        this.Unify(RType.Bool, this.Value(arm.Guard), arm.Guard.At);

      var body = this.Value(arm.Body);
      this._scopes.RemoveAt(this._scopes.Count - 1);
      result = result is null ? body : this.Join(result, body, arm.Body.At);
    }

    this._deferred.Add(() => {
      if (Floats(scrutinee))
        throw this.Refusal(new Refusal(RustFrontend.UnsupportedPattern, $"match on {scrutinee}", match.Scrutinee.At));

      if (!Exhaustiveness.Covers([.. match.Arms.Where(arm => arm.Guard is null).Select(arm => arm.Pattern)], scrutinee, this._typed))
        throw this.Invalid("E0004: non-exhaustive patterns", match.Scrutinee.At);
    });
    return result ?? RType.Never;
  }

  /// <summary>Whether a scrutinee is or holds a float, which a pattern does not test in the subset.</summary>
  private static bool Floats(RType type) {
    var resolved = type.Dereferenced;
    return resolved.Kind == RKind.Float || (resolved.Kind == RKind.Tuple && resolved.Elements.Any(Floats));
  }

  private CheckAbort Refusal(Refusal refusal) {
    this.Refuse(refusal);
    return new CheckAbort();
  }

  // ---------------------------------------------------------------- patterns

  /// <summary>
  /// Checks a pattern against the type of the value it matches and declares its bindings. A
  /// reference matched by a pattern that is no reference pattern is looked through, and the
  /// bindings inside then bind references (the default binding mode of The Rust Reference,
  /// "Patterns", "Binding modes").
  /// </summary>
  private void Pattern(Pattern pattern, RType type, bool inAlternative, bool? byReference = null) {
    var resolved = type.Resolved;
    // Look through references for a pattern that is not one and not a binding: a constant like `None` is looked through too.
    var isConstant = pattern is IdentifierPattern { Mutable: false } named && this.PatternConstant(named) is not null;
    // A string literal is a &str itself: it matches a &str, not the str behind it.
    var textLiteral = pattern is LiteralPattern { Value: StringLiteral } && resolved.Element?.Resolved.Kind == RKind.Str;
    if (resolved.Kind == RKind.Reference && !textLiteral && (isConstant || pattern is not (ReferencePattern or WildcardPattern or IdentifierPattern or RefusedPattern or OrPattern))) {
      var mutable = resolved.Mutable && byReference != false;
      this.Pattern(pattern, resolved.Element!, inAlternative, mutable);
      return;
    }

    this._typed.PatternTypes[pattern] = type;
    switch (pattern) {
      case WildcardPattern:
        return;
      case RestPattern:
        throw this.Invalid("`..` patterns are not allowed here", pattern.At);
      case IdentifierPattern identifier: {
        if (!identifier.Mutable && this.PatternConstant(identifier) is { } constant) {
          this.ConstantPattern(identifier, constant, type);
          return;
        }

        if (inAlternative) {
          this.Refuse(new Refusal(RustFrontend.UnsupportedPattern, $"binding '{identifier.Name}' in an or-pattern", identifier.At));
          return;
        }

        // In the mutable binding mode a binding is a `&mut` to a part of the place matched.
        var bound = byReference is { } mutable ? RType.Reference(type, mutable) : type;
        this.Declare(identifier.Name, bound, identifier.Mutable, identifier, identifier.At, true);
        return;
      }
      case LiteralPattern literal:
        this.LiteralPattern(literal, type);
        return;
      case RangePattern range:
        if (range.Low is not null)
          this.Unify(type, this.PatternValue(range.Low), range.Low.At);

        if (range.High is not null)
          this.Unify(type, this.PatternValue(range.High), range.High.At);

        this._deferred.Add(() => {
          if (type.Resolved.Kind is not (RKind.Integer or RKind.Float or RKind.Char) && !this.IsError(type))
            throw this.Invalid($"E0029: only `char` and numeric types are allowed in range patterns, not `{type}`", range.At);
        });
        return;
      case OrPattern alternatives:
        foreach (var alternative in alternatives.Alternatives)
          this.Pattern(alternative, type, true, byReference);

        return;
      case TuplePattern tuple:
        this.TuplePattern(tuple, type, inAlternative, byReference);
        return;
      case TupleStructPattern tupleStruct:
        this.TupleStructPattern(tupleStruct, type, inAlternative, byReference);
        return;
      case StructPattern structure:
        this.StructPattern(structure, type, inAlternative, byReference);
        return;
      case ReferencePattern reference: {
        if (resolved.Kind != RKind.Reference) {
          if (this.IsError(resolved))
            return;

          if (resolved.Kind == RKind.Variable)
            throw this.Unresolved("the value of a reference pattern", reference.At);

          throw this.Invalid($"E0308: mismatched types: expected `{type}`, found `&_`", reference.At);
        }

        if (reference.Mutable && !resolved.Mutable)
          throw this.Invalid($"E0308: mismatched types: expected `{type}`, found `&mut _`", reference.At);

        this.Pattern(reference.Inner, resolved.Element!, inAlternative, null);
        return;
      }
      default:
        this.Refuse(((RefusedPattern)pattern).Refusal);
        return;
    }
  }

  /// <summary>
  /// What an identifier pattern or a path names when it is no new binding: a constant, a unit
  /// struct, a variant of an enum, <c>None</c>; <see langword="null"/> for a binding.
  /// </summary>
  private object? PatternConstant(IdentifierPattern identifier) {
    var name = identifier.Name;
    for (var index = this._scopes.Count - 1; index >= 0; --index)
      if (this._scopes[index].TryGetValue(name, out var binding) && binding.Constant is not null)
        return binding;

    if (name == "None" && this._crate.Lookup(this._module, name, new(ReferenceEqualityComparer.Instance)) is null)
      return "None";

    return this._crate.Lookup(this._module, name, new(ReferenceEqualityComparer.Instance)) is { } symbol
      && this.TargetOf(symbol) is var target && target is Binding or StructInfo { Item.Form: StructForm.Unit } ? target : null;
  }

  private void ConstantPattern(Pattern pattern, object constant, RType type) {
    this._typed.Resolutions[pattern] = constant;
    switch (constant) {
      case Binding binding:
        if (pattern is IdentifierPattern identifier)
          this._typed.Bindings[identifier] = binding;

        this.Unify(type, binding.Type, pattern.At);
        return;
      case "None":
        this.Unify(type, RType.Option(this.Fresh(VariableKind.Any)), pattern.At);
        return;
      case StructInfo structure:
        this.Unify(type, RType.StructOf(structure), pattern.At);
        return;
      case (EnumInfo enumeration, string):
        this.Unify(type, RType.EnumOf(enumeration), pattern.At);
        return;
    }
  }

  private void LiteralPattern(LiteralPattern literal, RType type) {
    if (literal.Value is PathExpr path && path.Segments.Count >= 1) {
      var target = path.Segments is ["None"] ? "None" : this.PathTarget(path);
      if (target is "None" or Binding or (EnumInfo, string) || target is StructInfo { Item.Form: StructForm.Unit }) {
        this.ConstantPattern(literal, target, type);
        this._typed.Types[path] = type;
        return;
      }

      if (path.Segments.Count == 1 && this.Lookup(path.Segments[0]) is { Constant: null }) {
        this.Refuse(new Refusal(RustFrontend.UnsupportedPattern, $"variable '{path.Text}' in a range pattern", path.At));
        return;
      }
    }

    this.Unify(type, this.PatternValue(literal.Value), literal.At);
  }

  /// <summary>A literal, a negated literal or a path to a constant in a pattern.</summary>
  private RType PatternValue(Expr value) {
    if (value is PathExpr { Segments.Count: 1 } path && this.Lookup(path.Segments[0]) is { Constant: null }) {
      this.Refuse(new Refusal(RustFrontend.UnsupportedPattern, $"variable '{path.Text}' in a range pattern", path.At));
      return this.ErrorType();
    }

    return this.Expression(value);
  }

  /// <summary>The patterns of a list with an optional <c>..</c>, each with the type of the element it stands for.</summary>
  private List<(Pattern Pattern, RType Type)> Elements(IReadOnlyList<Pattern> patterns, IReadOnlyList<RType> types, Span at, string what) {
    var rest = patterns.Select((pattern, index) => (pattern, index)).Where(pair => pair.pattern is RestPattern).Select(pair => pair.index).ToList();
    if (rest.Count > 1)
      throw this.Invalid("`..` can only be used once per tuple pattern", at);

    if (rest.Count == 0) {
      if (patterns.Count != types.Count)
        throw this.Invalid($"E0308: mismatched types: expected a {what} with {types.Count} element{(types.Count == 1 ? "" : "s")}, found one with {patterns.Count}", at);

      return [.. patterns.Zip(types)];
    }

    var before = rest[0];
    var after = patterns.Count - before - 1;
    if (before + after > types.Count)
      throw this.Invalid($"E0308: mismatched types: expected a {what} with {types.Count} elements, found one with at least {before + after}", at);

    return [.. patterns.Take(before).Zip(types.Take(before)), .. patterns.Skip(before + 1).Zip(types.Skip(types.Count - after))];
  }

  private void TuplePattern(TuplePattern tuple, RType type, bool inAlternative, bool? byReference) {
    var resolved = type.Resolved;
    if (resolved.Kind == RKind.Variable && !this.IsError(resolved) && tuple.Elements.All(element => element is not RestPattern)) {
      var fresh = tuple.Elements.Select(_ => this.Fresh(VariableKind.Any)).ToList();
      this.Unify(type, fresh.Count == 0 ? RType.Unit : RType.Tuple(fresh), tuple.At);
      resolved = type.Resolved;
    }

    if (resolved.Kind == RKind.Unit && tuple.Elements.Count == 0)
      return;

    if (resolved.Kind != RKind.Tuple) {
      if (this.IsError(resolved))
        return;

      throw this.Invalid("E0308: mismatched types: a tuple pattern for a value that is no tuple", tuple.At);
    }

    foreach (var (element, elementType) in this.Elements(tuple.Elements, resolved.Elements, tuple.At, "tuple"))
      this.Pattern(element, elementType, inAlternative, byReference);
  }

  private void TupleStructPattern(TupleStructPattern pattern, RType type, bool inAlternative, bool? byReference) {
    var path = pattern.Path;
    if (path.Segments is [var name] && name is "Some" or "Ok" or "Err" && this._crate.Lookup(this._module, name, new(ReferenceEqualityComparer.Instance)) is null) {
      this._typed.Resolutions[pattern] = name;
      if (pattern.Elements.Count != 1)
        throw this.Invalid($"E0023: this pattern has {pattern.Elements.Count} fields, but the corresponding tuple variant has 1 field", pattern.At);

      RType payload;
      if (name == "Some") {
        var option = type.Resolved.Kind == RKind.Option ? type.Resolved : RType.Option(this.Fresh(VariableKind.Any));
        this.Unify(type, option, pattern.At);
        payload = option.Element!;
      } else {
        var result = type.Resolved.Kind == RKind.Result ? type.Resolved : RType.Result(this.Fresh(VariableKind.Any), this.Fresh(VariableKind.Any));
        this.Unify(type, result, pattern.At);
        payload = name == "Ok" ? result.Element! : result.Error;
      }

      this.Pattern(pattern.Elements[0], payload, inAlternative, byReference);
      return;
    }

    var target = this.PathTarget(path);
    if (target is StructInfo { Item.Form: StructForm.Tuple } structure) {
      this._typed.Resolutions[pattern] = structure;
      this.Unify(type, RType.StructOf(structure), pattern.At);
      foreach (var (element, elementType) in this.Elements(pattern.Elements, [.. structure.Fields.Select(field => field.Type)], pattern.At, "tuple struct"))
        this.Pattern(element, elementType, inAlternative, byReference);

      return;
    }

    if (target is (EnumInfo, string))
      throw this.Invalid($"E0532: expected tuple struct or tuple variant, found unit variant `{path.Text}`", pattern.At);

    if (path.Segments[0] is "std" or "core")
      this.Refuse(new Refusal(RustFrontend.ForeignPath, path.Text, path.At));
    else
      throw this.Invalid($"E0531: cannot find tuple struct or tuple variant `{path.Text}` in this scope", pattern.At);
  }

  private void StructPattern(StructPattern pattern, RType type, bool inAlternative, bool? byReference) {
    var owner = this.TypeOfPath(pattern.Path.Segments, pattern.Path.At);
    if (owner?.Resolved.Kind != RKind.Struct)
      throw this.Invalid($"E0422: cannot find struct `{pattern.Path.Text}` in this scope", pattern.At);

    var info = owner.Struct!;
    this._typed.Resolutions[pattern] = info;
    this.Unify(type, owner, pattern.At);
    var named = new HashSet<string>();
    foreach (var (name, field) in pattern.Fields) {
      var index = info.Fields.FindIndex(candidate => candidate.Name == name);
      if (index < 0)
        throw this.Invalid($"E0026: struct `{info.Name}` does not have a field named `{name}`", field.At);

      named.Add(name);
      this.Pattern(field, info.Fields[index].Type, inAlternative, byReference);
    }

    if (!pattern.Rest && named.Count != info.Fields.Count)
      throw this.Invalid($"E0027: pattern does not mention field `{info.Fields.First(field => !named.Contains(field.Name)).Name}`", pattern.At);
  }

}
