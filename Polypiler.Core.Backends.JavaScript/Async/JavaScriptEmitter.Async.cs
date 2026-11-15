using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>Whether the level has native async functions, or drives a generator with <c>$async</c>.</summary>
    private bool NativeAsync => this.Native("async function");

    /// <summary>
    /// The body of an async function (#1330). From ECMAScript 2017 it is the body of an <c>async function</c>,
    /// which the declaration marks. At ECMAScript 2015 the function returns <c>$async(function* () { … })</c>,
    /// whose generator yields where the IR awaits. ECMAScript 5 has no <c>Promise</c> and no queue of jobs, so
    /// no form keeps the order of either runtime: the function is refused with PLY1152.
    /// </summary>
    private void AsyncBody(IrFunction function, List<JsStatement> statements) {
      this._construct = "async function";
      this.Native("Promise");
      var body = this.Statements(function.Body);
      if (this.NativeAsync) {
        statements.AddRange(body);
        return;
      }

      statements.Add(new JsReturn(this.Helper("$async", [new JsFunctionExpression(null, [], new JsBlock(body)) { IsGenerator = true }])));
    }

    /// <summary>
    /// An await - <c>await</c>, or <c>yield</c> in the generator of <c>$async</c> - and the async values of the IR:
    /// the completed value, a value, and <c>$whenAll</c> of promises, whose array becomes the typed array of
    /// its element type where the level has one.
    /// </summary>
    private (JsExpression Node, bool Atomic) AsyncWrite(IrExpr expression) {
      switch (expression) {
        case IrAwait awaited:
          var operand = this.Text(awaited.Operand);
          JsExpression node = this.NativeAsync ? new JsAwait(operand) : new JsYield(operand, false);
          if (awaited.Operand is IrAsync { Op: IrAsyncOp.WhenAll } && awaited.Type.Element is { IsNumeric: true } element && this.Native(Storage(element)))
            return (new JsNew(Id(Storage(element)), [node]), true);

          return (node, false);
        case IrAsync { Op: IrAsyncOp.Completed }:
          return this.NativeAsync ? (new JsUnary("void", new JsNumber(0)), false) : (Invoke("Promise.resolve"), true);
        case IrAsync { Op: IrAsyncOp.FromResult } result:
          var value = this.Value(result.Arguments[0]);
          return this.NativeAsync ? (value, false) : (Invoke("Promise.resolve", value), true);
        default:
          var all = (IrAsync)expression;
          return (this.Helper("$whenAll", [new JsArray([.. all.Arguments.Select(this.Text)])]), true);
      }
    }

  }

}
