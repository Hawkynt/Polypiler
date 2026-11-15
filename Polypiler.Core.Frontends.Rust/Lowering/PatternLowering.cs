using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// <c>match</c>, <c>if let</c>, <c>while let</c> and <c>let ... else</c>: the scrutinee is evaluated
/// once; the arms are tried top to bottom, a guard only after its pattern matched, and the first arm
/// whose pattern and guard hold runs - a chain of <c>if</c>s, a chain of conditional expressions when
/// every arm is a plain value, or a <c>switch</c> when every arm compares an integer or an enum with
/// constants. The checker has made sure the arms cover every value, so the last arm without a guard
/// is the final <c>else</c> and the arms after it are never reached; no default arm is invented.
/// A pattern's test reads the parts of the scrutinee it needs - a field, the payload of a
/// <c>Some</c> or an <c>Ok</c> - only once the tests before it held.
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary>One arm, lowered: its test (<see langword="null"/> for always), its guard and its body.</summary>
  private sealed record LoweredArm(IrExpr? Test, (List<IrStmt> Statements, IrExpr? Value, bool Diverged) Guard, (List<IrStmt> Statements, IrExpr? Value, bool Diverged) Body);

  /// <summary>The scrutinee of a pattern test, read once: a tuple expression stays its parts; a value a pattern binds is a local of its own.</summary>
  private Item Scrutinee(Expr expression, params Pattern[] patterns) {
    var binds = patterns.Any(this.Binds);
    if (expression is TupleExpr { Elements.Count: > 0 } tuple)
      return new TupleItem([.. this.Operands([.. tuple.Elements]).Select(value => (Item)new ValueItem(binds && value is not IrConst ? this.Temporary(value, "scrutinee") : this.Stable(value, "scrutinee")))]);

    if (this.IsExclusiveScrutinee(expression) is { } place)
      return new PlaceItem(place);

    var value = this.Lower(expression) ?? this.Zero(this.Ir(this._typed.TypeOf(expression)));
    return new ValueItem(binds && value is not IrConst ? this.Temporary(value, "scrutinee") : this.Stable(value, "scrutinee"));
  }

  /// <summary>The place a <c>&amp;mut</c> scrutinee reaches, whose bindings write through; <see langword="null"/> for any other scrutinee.</summary>
  private Place? IsExclusiveScrutinee(Expr expression) => IsExclusiveValue(this._typed.TypeOf(expression)) ? this.PlaceOfReference(expression) : null;

  /// <summary>Whether a pattern binds a name anywhere in it.</summary>
  private bool Binds(Pattern pattern) => pattern switch {
    IdentifierPattern identifier => this.IsBinding(identifier),
    OrPattern or => or.Alternatives.Any(this.Binds),
    TuplePattern tuple => tuple.Elements.Any(this.Binds),
    TupleStructPattern tupleStruct => tupleStruct.Elements.Any(this.Binds),
    StructPattern structure => structure.Fields.Any(field => this.Binds(field.Pattern)),
    ReferencePattern reference => this.Binds(reference.Inner),
    _ => false
  };

  private bool IsBinding(IdentifierPattern pattern) => this._typed.Bindings.TryGetValue(pattern, out var binding) && binding.Constant is null && !this._typed.Resolutions.ContainsKey(pattern);

  // ---------------------------------------------------------------- tests

  /// <summary>The test of a pattern on an item of a type; <see langword="null"/> when it matches every value.</summary>
  private IrExpr? Test(Pattern pattern, Item item, RType type) {
    var resolved = type.Resolved;
    // A pattern that is no reference pattern and no binding looks through a reference, which is its value.
    if (resolved.Kind == RKind.Reference && pattern is not (ReferencePattern or WildcardPattern or IdentifierPattern or OrPattern))
      return this.Test(pattern, this.Dereferenced(item), resolved.Element!);

    switch (pattern) {
      case WildcardPattern or RestPattern:
        return null;
      case IdentifierPattern identifier when !this._typed.Resolutions.ContainsKey(identifier):
        return null;
      case IdentifierPattern or LiteralPattern when this._typed.Resolutions.TryGetValue(pattern, out var resolution):
        return this.ConstantTest(resolution, this.ItemValue(item, type), type);
      case LiteralPattern literal:
        return Equal(this.ItemValue(item, type), this.Value(literal.Value));
      case RangePattern range: {
        var value = this.ItemValue(item, type);
        var low = range.Low is null ? null : Compare(IrBinaryOp.LessOrEqual, this.Value(range.Low), value);
        var high = range.High is null ? null : Compare(range.Inclusive ? IrBinaryOp.LessOrEqual : IrBinaryOp.Less, value, this.Value(range.High));
        return low is null ? high : high is null ? low : And(low, high);
      }
      case OrPattern alternatives:
        return Any(alternatives.Alternatives.Select(alternative => this.Test(alternative, item, type)));
      case ReferencePattern reference:
        return this.Test(reference.Inner, this.Dereferenced(item), resolved.Kind == RKind.Reference ? resolved.Element! : type);
      case TuplePattern tuple when resolved.Kind == RKind.Unit:
        return null;
      case TuplePattern tuple: {
        var parts = this.TestParts(item, resolved, resolved.Elements.Count);
        return All(Spread(tuple.Elements, resolved.Elements.Count).Select(pair => this.Test(pair.Pattern, parts[pair.Index], resolved.Elements[pair.Index])));
      }
      case TupleStructPattern tupleStruct when this._typed.Resolutions[tupleStruct] is string variant:
        return this.VariantTest(variant, tupleStruct.Elements[0], item, resolved);
      case TupleStructPattern tupleStruct: {
        var fields = resolved.Struct!.Fields;
        var parts = this.TestParts(item, resolved, fields.Count);
        return All(Spread(tupleStruct.Elements, fields.Count).Select(pair => this.Test(pair.Pattern, parts[pair.Index], fields[pair.Index].Type)));
      }
      case StructPattern structure: {
        var fields = resolved.Struct!.Fields;
        var parts = this.TestParts(item, resolved, fields.Count);
        return All(structure.Fields.Select(field => {
          var index = fields.FindIndex(candidate => candidate.Name == field.Name);
          return this.Test(field.Pattern, parts[index], fields[index].Type);
        }));
      }
      default:
        return null;
    }
  }

  /// <summary>The value of an item for a test that compares it.</summary>
  private IrExpr ItemValue(Item item, RType type) => this.ValueOf(item, type.Dereferenced);

  /// <summary>The parts of a tuple or a struct item for tests, each read only where its test runs.</summary>
  private List<Item> TestParts(Item item, RType type, int count) {
    if (item is TupleItem tuple)
      return [.. tuple.Parts];

    var declaration = this.Ir(type).StructType!;
    if (item is PlaceItem place)
      return [.. declaration.Fields.Select(field => (Item)new PlaceItem(new FieldPlace(place.Place, field)))];

    var value = ((ValueItem)item).Value;
    return [.. declaration.Fields.Select(field => (Item)new ValueItem(new IrFieldLoad(value, field)))];
  }

  /// <summary>The test of a constant pattern: a constant, a variant, <c>None</c>, a unit struct (always).</summary>
  private IrExpr? ConstantTest(object resolution, IrExpr value, RType type) => resolution switch {
    "None" => new IrIsNone(value),
    (EnumInfo enumeration, string variant) => Compare(IrBinaryOp.Equal, Integral(value), Integral(this.Variant(enumeration, variant))),
    StructInfo => null,
    Binding { Constant: { } constant } when type.Dereferenced.IsScalar => Equal(value, this.Value(constant.Value)),
    Binding { Constant: { } constant } => this.Equal(value, this.Value(constant.Value), type),
    _ => null
  };

  private static IrExpr Integral(IrExpr value) => value.Type.IsEnum ? new IrConvert(value.Type.Integral, value, IrConversion.Wrap) : value;

  /// <summary>The test of <c>Some(p)</c>, <c>Ok(p)</c> or <c>Err(p)</c>: the variant, then the payload's pattern.</summary>
  private IrExpr? VariantTest(string variant, Pattern payload, Item item, RType type) {
    var value = this.ItemValue(item, type);
    var (tag, inner, payloadType) = this.Variant(variant, value, type);
    var test = this.Test(payload, inner is null ? new ValueItem(new IrConst(IrType.I32, 0)) : new ValueItem(inner), payloadType);
    return test is null ? tag : And(tag, test);
  }

  /// <summary>The tag test of a variant of <c>Option</c> or <c>Result</c>, its payload and the payload's type.</summary>
  private (IrExpr Tag, IrExpr? Payload, RType Type) Variant(string variant, IrExpr value, RType type) => variant switch {
    "Some" => (Not(new IrIsNone(value)), new IrUnwrap(value) { IsTested = true }, type.Element!),
    "Ok" => (IsOk(value), Payload(value, "ok"), type.Element!),
    _ => (Not(IsOk(value)), Payload(value, "err"), type.Error)
  };

  /// <summary>The disjunction of tests, <see langword="null"/> (always) when one of them always holds.</summary>
  private static IrExpr? Any(IEnumerable<IrExpr?> tests) {
    IrExpr? any = null;
    foreach (var test in tests) {
      if (test is null)
        return null;

      any = any is null ? test : Or(any, test);
    }

    return any;
  }

  /// <summary>The conjunction of tests in order; <see langword="null"/> (always) when there is none.</summary>
  private static IrExpr? All(IEnumerable<IrExpr?> tests) {
    IrExpr? all = null;
    foreach (var test in tests.Where(test => test is not null))
      all = all is null ? test : And(all, test!);

    return all;
  }

  // ---------------------------------------------------------------- bindings

  /// <summary>The bindings of a pattern that matched, each with the item it names.</summary>
  private IEnumerable<(IdentifierPattern Pattern, Item Item)> Bindings(Pattern pattern, Item item, RType type) {
    var resolved = type.Resolved;
    if (resolved.Kind == RKind.Reference && pattern is not (ReferencePattern or WildcardPattern or IdentifierPattern or OrPattern))
      return this.Bindings(pattern, this.Dereferenced(item), resolved.Element!);

    switch (pattern) {
      case IdentifierPattern identifier when this.IsBinding(identifier):
        return [(identifier, item)];
      case ReferencePattern reference:
        return this.Bindings(reference.Inner, this.Dereferenced(item), resolved.Kind == RKind.Reference ? resolved.Element! : type);
      case TuplePattern tuple when resolved.Kind == RKind.Tuple: {
        var parts = this.TestParts(item, resolved, resolved.Elements.Count);
        return Spread(tuple.Elements, resolved.Elements.Count).SelectMany(pair => this.Bindings(pair.Pattern, parts[pair.Index], resolved.Elements[pair.Index])).ToList();
      }
      case TupleStructPattern tupleStruct when this._typed.Resolutions[tupleStruct] is string variant: {
        var (_, payload, payloadType) = this.Variant(variant, this.ItemValue(item, type), resolved);
        return payload is null ? [] : this.Bindings(tupleStruct.Elements[0], new ValueItem(payload), payloadType);
      }
      case TupleStructPattern tupleStruct: {
        var fields = resolved.Struct!.Fields;
        var parts = this.TestParts(item, resolved, fields.Count);
        return Spread(tupleStruct.Elements, fields.Count).SelectMany(pair => this.Bindings(pair.Pattern, parts[pair.Index], fields[pair.Index].Type)).ToList();
      }
      case StructPattern structure: {
        var fields = resolved.Struct!.Fields;
        var parts = this.TestParts(item, resolved, fields.Count);
        return structure.Fields.SelectMany(field => {
          var index = fields.FindIndex(candidate => candidate.Name == field.Name);
          return this.Bindings(field.Pattern, parts[index], fields[index].Type);
        }).ToList();
      }
      default:
        return [];
    }
  }

  /// <summary>
  /// Declares the bindings of a pattern that matched: an immutable one names the part of the
  /// scrutinee, which no arm can change; a mutable one is a local copy; a <c>&amp;mut</c> one the place.
  /// </summary>
  private void Declare(IEnumerable<(IdentifierPattern Pattern, Item Item)> bindings) {
    foreach (var (pattern, item) in bindings) {
      var binding = this._typed.Bindings[pattern];
      if (IsExclusiveValue(binding.Type)) {
        if (item is PlaceItem place) {
          this._places[binding] = place.Place;
        } else {
          this.Refuse(RustFrontend.UnsupportedPattern, $"binding '{pattern.Name}' by mutable reference into an Option or a Result", pattern.At);
          this._values[binding] = this.ValueOf(item, binding.Type.Dereferenced);
        }

        continue;
      }

      var value = this.ValueOf(item, binding.Type.Dereferenced);
      if (pattern.Mutable || !IsSimple(value) && value is not (IrFieldLoad or IrUnwrap))
        this.Declared(pattern, value);
      else
        this._values[binding] = value;
    }
  }

  // ---------------------------------------------------------------- match

  private IrExpr? Match(MatchExpr match) {
    var type = this._typed.TypeOf(match);
    var wanted = type.Kind is not (RKind.Unit or RKind.Never);
    var scrutineeType = this._typed.TypeOf(match.Scrutinee);
    var scrutinee = this.Scrutinee(match.Scrutinee, [.. match.Arms.Select(arm => arm.Pattern)]);
    var last = match.Arms.ToList().FindLastIndex(arm => arm.Guard is null);
    if (this.Switchable(match, scrutineeType, last) && scrutinee is ValueItem { Value: var switched })
      return this.Switch(match, switched, scrutineeType, last, wanted ? this.Ir(type) : null);

    var arms = new List<LoweredArm>();
    for (var index = 0; index <= last; ++index) {
      var arm = match.Arms[index];
      var test = index == last ? null : this.Test(arm.Pattern, scrutinee, scrutineeType);
      var bindings = this.Bindings(arm.Pattern, scrutinee, scrutineeType).ToList();
      // The guard sees the bindings as the parts of the scrutinee; the body gets its own copies of the mutable ones.
      foreach (var (pattern, item) in bindings.Where(binding => !binding.Pattern.Mutable))
        if (!(IsExclusiveValue(this._typed.Bindings[pattern].Type) && item is PlaceItem))
          this._values[this._typed.Bindings[pattern]] = this.ValueOf(item, this._typed.Bindings[pattern].Type.Dereferenced);

      var guard = this.Capture(() => {
        foreach (var (pattern, item) in bindings.Where(binding => binding.Pattern.Mutable))
          this._values[this._typed.Bindings[pattern]] = this.ValueOf(item, this._typed.Bindings[pattern].Type.Dereferenced);

        return arm.Guard is null ? null : this.Value(arm.Guard);
      });
      var body = this.Capture(() => {
        this.Declare(bindings);
        return this.Lower(arm.Body);
      });
      arms.Add(new LoweredArm(test, guard, body));
    }

    if (arms.Any(arm => arm.Guard.Statements.Count > 0 || arm.Guard.Diverged))
      return this.Flagged(arms, wanted ? this.Ir(type) : null);

    if (wanted && arms.All(arm => arm.Body.Statements.Count == 0 && !arm.Body.Diverged)) {
      var value = arms[^1].Body.Value!;
      for (var index = arms.Count - 2; index >= 0; --index)
        value = new IrConditional(Condition(arms[index])!, arms[index].Body.Value!, value);

      return value;
    }

    var slot = wanted ? this.Slot(this.Ir(type), "result") : null;
    IrBlock? otherwise = null;
    for (var index = arms.Count - 1; index >= 0; --index) {
      var block = Assigned(arms[index].Body, slot);
      var condition = Condition(arms[index]);
      otherwise = condition is null ? block : new IrBlock([new IrIf(condition, block, otherwise)]);
    }

    this.Splice([.. otherwise!.Statements], arms.All(arm => arm.Body.Diverged));
    return slot is null ? null : new IrLocalRef(slot);
  }

  /// <summary>Whether an arm is taken: its pattern and then its guard.</summary>
  private static IrExpr? Condition(LoweredArm arm) => arm.Guard.Value is null ? arm.Test : arm.Test is null ? arm.Guard.Value : And(arm.Test, arm.Guard.Value);

  /// <summary>The statements of an arm, ending with the assignment of its value.</summary>
  private static IrBlock Assigned((List<IrStmt> Statements, IrExpr? Value, bool Diverged) body, IrLocal? slot) {
    var statements = new List<IrStmt>(body.Statements);
    if (slot is not null && !body.Diverged)
      statements.Add(new IrAssign(slot, body.Value!));

    return new IrBlock(statements);
  }

  /// <summary>A match with a guard that needs statements: each arm runs when no earlier one did and its pattern holds, then tests its guard.</summary>
  private IrExpr? Flagged(List<LoweredArm> arms, IrType? type) {
    var slot = type is null ? null : this.Slot(type, "result");
    var matched = this.Slot(IrType.Bool, "matched");
    foreach (var arm in arms) {
      var taken = new List<IrStmt>(arm.Guard.Statements);
      var body = Assigned(arm.Body, slot).Statements.Prepend(new IrAssign(matched, Truth(true))).ToList();
      if (!arm.Guard.Diverged)
        taken.Add(arm.Guard.Value is null ? new IrBlock(body) : new IrIf(arm.Guard.Value, new IrBlock(body), null));

      var test = arm.Test is null ? Not(new IrLocalRef(matched)) : And(Not(new IrLocalRef(matched)), arm.Test);
      this.Emit(new IrIf(test, new IrBlock(taken), null));
    }

    return slot is null ? null : new IrLocalRef(slot);
  }

  // ---------------------------------------------------------------- switch

  /// <summary>
  /// Whether a match is a <c>switch</c>: an integer or an enum compared with constants, no guard and
  /// no binding, and no arm that leaves a loop around the match - the IR's <c>break</c> would leave
  /// the switch - nor one whose value is a plain expression all of them are, which a conditional
  /// chain says shorter.
  /// </summary>
  private bool Switchable(MatchExpr match, RType type, int last) {
    var resolved = type.Resolved;
    if (resolved.Kind is not (RKind.Integer or RKind.Enum) || last != match.Arms.Count - 1 || match.Arms.Count < 2)
      return false;

    for (var index = 0; index < match.Arms.Count; ++index) {
      var arm = match.Arms[index];
      if (arm.Guard is not null || this.Binds(arm.Pattern) || Jumps(arm.Body, this._closureFrames.Count > 0))
        return false;

      if (this.Labels(arm.Pattern, resolved) is null && !(index == last && arm.Pattern is WildcardPattern or IdentifierPattern))
        return false;
    }

    // Plain values in every arm read better as a conditional chain.
    return !match.Arms.All(arm => arm.Body is IntegerLiteral or BoolLiteral or PathExpr or FloatLiteral);
  }

  /// <summary>The constants a pattern compares with, or <see langword="null"/> when it is no list of constants.</summary>
  private List<BigInteger>? Labels(Pattern pattern, RType type) {
    switch (pattern) {
      case OrPattern or: {
        var all = new List<BigInteger>();
        foreach (var alternative in or.Alternatives) {
          if (this.Labels(alternative, type) is not { } labels)
            return null;

          all.AddRange(labels);
        }

        return all;
      }
      case IdentifierPattern or LiteralPattern when this._typed.Resolutions.TryGetValue(pattern, out var resolution):
        return resolution switch {
          (EnumInfo enumeration, string variant) => [enumeration.Variants.First(candidate => candidate.Name == variant).Value],
          Binding { Constant: { } constant } when Constants.Evaluate(constant.Value, this._typed) is { } value => [value],
          _ => null
        };
      case LiteralPattern literal when Constants.Evaluate(literal.Value, this._typed) is { } value:
        return [value];
      default:
        return null;
    }
  }

  /// <summary>Whether an arm's body has a <c>break</c> or a <c>continue</c>, or - inside an inlined closure - a <c>return</c> or a <c>?</c>.</summary>
  private static bool Jumps(object? node, bool inClosure) {
    switch (node) {
      case null or ClosureExpr:
        return false;
      case BreakExpr or ContinueExpr:
        return true;
      case ReturnExpr or TryExpr when inClosure:
        return true;
    }

    return Children(node).Any(child => Jumps(child, inClosure));
  }

  private IrExpr? Switch(MatchExpr match, IrExpr value, RType type, int last, IrType? result) {
    var slot = result is null ? null : this.Slot(result, "result");
    var seen = new HashSet<BigInteger>();
    var sections = new List<IrSwitchSection>();
    var resolved = type.Resolved;
    var integral = resolved.Kind == RKind.Enum ? resolved.Enum!.Representation : resolved;
    for (var index = 0; index < match.Arms.Count; ++index) {
      var arm = match.Arms[index];
      var labels = (this.Labels(arm.Pattern, resolved) ?? []).Where(seen.Add).Select(label => Integer(integral, label).Value).ToList();
      var isDefault = index == last;
      if (labels.Count == 0 && !isDefault)
        continue;

      var body = this.Capture(() => this.Lower(arm.Body));
      sections.Add(new IrSwitchSection(labels, isDefault, Assigned(body, slot)));
    }

    this.Emit(new IrSwitch(Integral(value), sections));
    if (sections.All(section => section.Body.Statements.Count > 0 && Ends(section.Body.Statements[^1])))
      this._diverged = true;

    return slot is null ? null : new IrLocalRef(slot);
  }

  /// <summary>Whether a statement never lets control reach the next one.</summary>
  private static bool Ends(IrStmt statement) => statement is IrReturn or IrThrow;

  // ---------------------------------------------------------------- let ... else

  /// <summary><c>let pattern = value else { ... };</c>: the value once, the else block where the pattern does not match, then the bindings.</summary>
  private void LetElse(LetStatement let, Block otherwise) {
    var type = this._typed.TypeOf(let.Initializer!);
    var scrutinee = this.Scrutinee(let.Initializer!, let.Pattern);
    if (this._diverged)
      return;

    if (this.Test(let.Pattern, scrutinee, type) is { } matches) {
      var branch = this.Capture(() => this.Block(otherwise));
      this.Emit(new IrIf(Not(matches), new IrBlock(branch.Statements), null));
    }

    var bindings = this.Bindings(let.Pattern, scrutinee, type).ToList();
    foreach (var (pattern, item) in bindings)
      this.Declared(pattern, this.ValueOf(item, this._typed.Bindings[pattern].Type.Dereferenced));
  }

}
