using System.Collections.Generic;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The generators of JavaScript (#1327): a <c>function*</c> is an iterator function whose sequence is
/// single-pass - a generator object yields its values once and nothing after it was finished or left;
/// <c>yield v</c> as a statement produces a value, <c>yield*</c> every value of a generator object or an
/// array, <c>return;</c> ends it, and a <c>for…of</c> over a generator object is a loop over it.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>A yield that stands as a statement: of a value, or of every value of a generator object or an array.</summary>
  private IrStmt Yield(YieldExpression produced) {
    if (!this._returnType.IsLazySequence)
      throw this._model.Result is { Kind: JsKind.Sequence, Element: { Kind: JsKind.Conflict } conflict }
        ? this.Refuse(produced, JavaScriptFrontend.MixedKindsDescriptor, $"'{this._model.Candidate.Name}' yields both {conflict.Kinds.First} and {conflict.Kinds.Second}")
        : this.Unsupported(produced, $"a generator of {this._model.Result.Element?.Describe() ?? "nothing"}");

    var element = this._returnType.Element!;
    var argument = produced.Argument!;
    if (!produced.Delegate)
      return new IrYield(this.Value(argument, element));

    var source = types.Of(argument);
    if (source is not ({ Kind: JsKind.Sequence } or { Kind: JsKind.Array, IsView: false }))
      throw this.Unsupported(argument, $"yield* of {source.Describe()}");

    var statements = new List<IrStmt>();
    var values = this.Expression(argument);
    var sequence = values.Type.IsArray ? new IrArraySequence(this.Once(values, statements)) : values;
    if (sequence.Type.Element == element) {
      statements.Add(new IrYieldFrom(sequence));
      return new IrBlock(statements);
    }

    // The values of the inner sequence are of a narrower type than those of the generator: each converted.
    var item = new IrLocal($"$t{this._locals.Count}", sequence.Type.Element!);
    this._locals.Add(item);
    var converted = this.To(new Typed(Read(new IrLocalRef(item)), source.Element!), element, argument);
    statements.Add(new IrForEachSequence(item, sequence, new IrBlock([new IrYield(converted)])));
    return new IrBlock(statements);
  }

  /// <summary>
  /// <c>for (const v of g)</c> over a generator object: a loop over the sequence, which closes it however
  /// the loop ends. <see langword="null"/> for a loop over anything else.
  /// </summary>
  private IrStmt? SequenceForOf(ForOfStatement loop) {
    if (types.Of(loop.Right) is not { Kind: JsKind.Sequence } iterated)
      return null;

    if (loop.Left is not VariableDeclaration { Declarations: [{ Id: Identifier { Name: var name } id }] } || this._model.Bindings.GetValueOrDefault(id) is not Variable variable)
      throw this.Unsupported(loop.Left, "a for...of that does not declare one variable");

    var sequence = this.Expression(loop.Right);
    if (!sequence.Type.IsLazySequence)
      throw this.Unsupported(loop.Right, $"a for...of over {iterated.Describe()}");

    var element = sequence.Type.Element!;
    var local = new IrLocal(name, this.Storable(variable.Type, id));
    variable.Local = local;
    this._locals.Add(local);
    var item = local.Type == element ? local : new IrLocal($"$t{this._locals.Count}", element);
    if (item != local)
      this._locals.Add(item);

    var before = this._assigned;
    var body = this.Body(loop.Body).Statements;
    this._assigned = before;
    if (item != local)
      body = [new IrDeclare(local, this.To(new Typed(Read(new IrLocalRef(item)), iterated.Element!), local.Type, id)), .. body];

    return new IrForEachSequence(item, sequence, new IrBlock(body));
  }

}
