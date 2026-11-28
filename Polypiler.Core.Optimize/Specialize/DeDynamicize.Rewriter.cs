using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Specialize;

public static partial class DeDynamicize {

  private sealed partial class Walker {

    private Dictionary<string, string> _names = [];

    /// <summary>
    /// The function with the walk's findings applied: the typed locals and parameters, their assignments
    /// unboxed and their reads boxed, boxes cancelled, operations of exact operands expanded, payloads
    /// read where their tag is proven marked as tested, branches no value reaches folded, and calls
    /// sent to their clones.
    /// </summary>
    public IrFunction Rewrite(string name, Dictionary<string, string> names) {
      this._names = names;
      var body = (IrBlock)this.Rewritten(this.Function.Body, this._entry);
      return this.Function with {
        Name = name,
        ReturnType = this.ReturnType,
        Parameters = [.. this.Function.Parameters.Select(this.Local)],
        Locals = [.. this.Function.Locals.Select(this.Local)],
        Body = body
      };
    }

    private IrLocal Local(IrLocal local) => this.Retyped.TryGetValue(local, out var typed) ? typed.Local : local;

    /// <summary>What a walk on the places of the function found: every parameter, local and result.</summary>
    public IEnumerable<DynamicPlace> Places(string name) {
      DynamicFact Declared(IrType type) => DynamicFact.Proven(Program.Exact(type));
      for (var index = 0; index < this.Function.Parameters.Count; ++index) {
        var parameter = this.Function.Parameters[index];
        var fact = this.Retyped.TryGetValue(parameter, out var typed) ? DynamicFact.Proven(typed.Type) : parameter.Type.IsVariant ? this._domain.Unknown : Declared(parameter.Type);
        yield return new(name, parameter.Name, DynamicPlaceKind.Parameter, fact,
          fact.Type is DynamicVariant ? (this.Signature is null && this.Function.IsPublic ? "a parameter of a public function, which the host may call with any value" : "a parameter whose calls pass more than one type") : null);
      }

      foreach (var local in this.Function.Locals.Where(local => !this.Function.Parameters.Contains(local))) {
        var fact = this.Retyped.TryGetValue(local, out var typed) ? DynamicFact.Proven(typed.Type)
          : !local.Type.IsVariant ? Declared(local.Type)
          : this._escaping.Contains(local) ? this._domain.Unknown
          : this._reaching.TryGetValue(local, out var read) ? read : this._assigned.GetValueOrDefault(local, this._domain.Unreachable);
        string? reason = null;
        if (local.Type.IsVariant && !this.Retyped.ContainsKey(local))
          reason = this._escaping.Contains(local) ? "captured, bound or written where the analysis does not follow it"
            : this._reasons.GetValueOrDefault(local) ?? (fact.Type is DynamicNothing ? "never assigned nor read" : fact.Type is DynamicOptional { Missing: not DynamicMissing.None } ? "read where it may still be missing" : "holds more than one type");
        yield return new(name, local.Name, DynamicPlaceKind.Local, fact.Type is DynamicVariant ? this._domain.Unknown : fact, reason);
      }

      if (this.Function.ReturnType != IrType.Void) {
        var result = !this.ReturnType.IsVariant ? Declared(this.ReturnType) : this.Signature is null ? this._domain.Unknown : this.Returned;
        yield return new(name, name, DynamicPlaceKind.Result, result.Type is DynamicVariant ? this._domain.Unknown : result,
          this.ReturnType.IsVariant ? (this.Signature is null ? "the result of a function the host or a dynamic call may call" : "returns more than one type") : null);
      }
    }

    #region Statements

    private IrStmt Rewritten(IrStmt statement, DynamicState<IrLocal>? inherited) {
      var state = this._before.TryGetValue(statement, out var known) ? known : inherited;
      switch (statement) {
        case IrBlock block:
          var current = state;
          var statements = new List<IrStmt>();
          foreach (var inner in block.Statements) {
            statements.Add(this.Rewritten(inner, current));
            current = this._before.TryGetValue(inner, out var next) ? next : current;
          }

          return block with { Statements = statements };
        case IrAssign assign when this.Retyped.TryGetValue(assign.Local, out var typed):
          if (this.Stores(assign.Value, state, typed.Type))
            return assign with { Local = typed.Local, Value = this.Unboxed(assign.Value, state, typed.Type) };
          // A dead store: no read sees it; its value is evaluated for its effects alone.
          var effect = this.Rewritten(assign.Value, state);
          return IrShape.IsSideEffectFree(effect) && IrShape.IsTrapFree(effect) ? new IrBlock([]) { Origins = assign.Origins } : new IrExprStmt(effect) { Origins = assign.Origins };
        case IrDeclare declare when this.Retyped.TryGetValue(declare.Local, out var typed):
          return declare with {
            Local = typed.Local,
            Initializer = declare.Initializer is null || !this.Stores(declare.Initializer, state, typed.Type) ? null : this.Unboxed(declare.Initializer, state, typed.Type)
          };
        case IrReturn { Value: { } value } result when !this.ReturnType.IsVariant && this.Function.ReturnType.IsVariant:
          return result with { Value = this.Unboxed(value, state, (DynamicExact)this.Returned.Type) };
        case IrIf conditional when this._reached.TryGetValue(conditional, out var reached) && reached != (true, true) && reached != (false, false)
          && Pure(conditional.Condition):
          // A tag test whose one branch no value reaches is the other branch.
          var kept = reached.Then ? conditional.Then : conditional.Else ?? new IrBlock([]);
          return this.Rewritten(kept, state);
      }

      var expressions = IrTree.Expressions(statement);
      var condition = this._conditions.TryGetValue(statement, out var tested) ? tested : state;
      return IrTree.With(statement, [.. expressions.Select(expression => this.Rewritten(expression, statement is IrWhile or IrDoWhile or IrFor ? condition : state))],
        [.. IrTree.Blocks(statement).Select(block => (IrBlock)this.Rewritten(block, state))]);
    }

    /// <summary>
    /// Whether a condition can be left out: it has no effect and never traps - tag tests among them, whose
    /// conversion of a tag to a narrower integer cannot fail, as every tag fits an <c>i16</c>.
    /// </summary>
    private static bool Pure(IrExpr condition) => condition switch {
      IrConvert { Contract: IrConversion.Trap } conversion => conversion.Type.Bits >= 16 && Tag(conversion.Operand) && Pure(conversion.Operand),
      IrVariantTagOf { Value: IrLocalRef } => true,
      IrVariantTagOf => false,
      _ => IrShape.IsSideEffectFreeNode(condition) && IrShape.IsTrapFreeNode(condition) && IrTree.Operands(condition).All(Pure)
    };

    /// <summary>Whether an expression is the tag of a variant, converted or not.</summary>
    private static bool Tag(IrExpr expression) => expression is IrVariantTagOf || expression is IrConvert conversion && Tag(conversion.Operand);

    /// <summary>Whether an assignment of a value to a local typed as an element stores one of it: the value is proven of the element where it is assigned; one that is not reaches no read.</summary>
    private bool Stores(IrExpr value, DynamicState<IrLocal>? state, DynamicExact type)
      => state is null || this.Eval(value, state).Type == type;

    /// <summary>A value of a typed local, parameter or result from an expression of the dynamic type: the payload of its box, which the facts prove of the tag.</summary>
    private IrExpr Unboxed(IrExpr value, DynamicState<IrLocal>? state, DynamicExact type) {
      var rewritten = this.Rewritten(value, state);
      var tag = type.BoxTag!.Value;
      if (rewritten is IrVariantOf { Value: { } payload } box && box.Tag == tag)
        return payload;
      return new IrVariantValue(rewritten, tag) { IsTested = state is not null && this.Eval(value, state) is { IsStatic: true, Type: DynamicExact proven } && proven.BoxTag == tag };
    }

    #endregion

    #region Expressions

    private IrExpr Rewritten(IrExpr expression, DynamicState<IrLocal>? state) {
      if (state is not null && this._table.Classify(expression) is { } call && this.Expanded(expression, call, state) is { } expanded)
        return expanded;

      if (expression is IrCall direct && this.Calls.TryGetValue(direct, out var site) && site.Target is not null)
        return this.Cloned(direct, site, state);

      var operands = IrTree.Operands(expression);
      var rebuilt = operands.Count == 0 ? expression : IrTree.With(expression, [.. operands.Select(operand => this.Rewritten(operand, state))]);
      switch (rebuilt) {
        case IrLocalRef reference when this.Retyped.TryGetValue(reference.Local, out var typed):
          return new IrVariantOf(typed.Type.BoxTag!.Value, new IrLocalRef(typed.Local)) { Origins = reference.Origins };
        case IrVariantValue { Value: IrVariantOf { Value: { } payload } box } value when box.Tag == value.Tag:
          return payload;
        case IrVariantValue { IsTested: false } value when state is not null && expression is IrVariantValue original
          && this.Eval(original.Value, state) is { IsStatic: true, Type: DynamicExact proven } && proven.BoxTag == value.Tag:
          return value with { IsTested = true };
        case IrVariantTagOf { Value: IrVariantOf { Value: var payload } box } when payload is null || IrShape.IsSideEffectFree(payload) && IrShape.IsTrapFree(payload):
          return new IrConst(IrType.I32, (long)box.Tag);
        default:
          return rebuilt;
      }
    }

    /// <summary>An operation of the table whose operands are proven exact, expanded to typed IR; <see langword="null"/> where it keeps the helper.</summary>
    private IrExpr? Expanded(IrExpr expression, DynamicCall call, DynamicState<IrLocal> state) {
      var leftFact = this.Eval(call.Left, state);
      var rightFact = call.Right is null ? DynamicFact.Proven(DynamicType.Nothing) : this.Eval(call.Right, state);
      var (left, leftType) = this.Operand(call.Left, leftFact, state);
      var (right, rightType) = call.Right is null ? (null, DynamicType.Nothing) : this.Operand(call.Right, rightFact, state);
      if (left.Type.IsVariant && (call.Right is null || right!.Type.IsVariant))
        return null;
      if (this._table.Result(call.Operation, leftType, rightType) is not DynamicExact { BoxTag: not null } result
        || this._table.Expand(call.Operation, left, leftType, right, rightType) is not { } expanded
        || expanded.Type != result.Type
        || IrTree.Nodes(expanded).OfType<IrCall>().Any(inner => !this._program.Functions.ContainsKey(inner.Function)))
        return null;
      if (expression.Type.IsVariant)
        return new IrVariantOf(result.BoxTag!.Value, expanded) { Origins = expression.Origins };
      return expanded.Type == expression.Type ? expanded with { Origins = expression.Origins } : null;
    }

    /// <summary>An operand as the table's expansion takes it: typed where its fact is a proven exact element with a tag, the rewritten variant otherwise.</summary>
    private (IrExpr Operand, DynamicType Element) Operand(IrExpr operand, DynamicFact fact, DynamicState<IrLocal> state) {
      var rewritten = this.Rewritten(operand, state);
      if (!operand.Type.IsVariant)
        return (rewritten, fact.Type);
      if (fact is not { IsStatic: true, Type: DynamicExact { BoxTag: not null } exact })
        return (rewritten, fact.Type is DynamicExact ? DynamicType.Variant : fact.Type);
      return rewritten is IrVariantOf { Value: { } payload } box && box.Tag == exact.BoxTag
        ? (payload, exact)
        : (new IrVariantValue(rewritten, exact.BoxTag!.Value) { IsTested = true }, exact);
    }

    /// <summary>A call sent to the clone its arguments chose: the typed arguments unboxed, a typed result boxed again where a variant is expected.</summary>
    private IrExpr Cloned(IrCall call, CallSite site, DynamicState<IrLocal>? state) {
      var arguments = new List<IrExpr>();
      for (var index = 0; index < call.Arguments.Count; ++index) {
        var rewritten = this.Rewritten(call.Arguments[index], state);
        if (site.Target!.Parameters[index] is not { } exact)
          arguments.Add(rewritten);
        else
          arguments.Add(rewritten is IrVariantOf { Value: { } payload } box && box.Tag == exact.BoxTag ? payload : new IrVariantValue(rewritten, exact.BoxTag!.Value) { IsTested = true });
      }

      var key = Program.Key(site.Function, site.Target);
      var result = this._program.Walkers[key].ReturnType;
      var target = new IrCall(this._names[key], result, arguments) { Origins = call.Origins };
      return result == call.Type ? target : new IrVariantOf(((DynamicExact)this._program.Walkers[key].Returned.Type).BoxTag!.Value, target);
    }

    #endregion

  }

}
