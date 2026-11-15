using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  /// <summary>The variable that holds the generator function of a restartable sequence or of a method, which every enumeration calls anew.</summary>
  private const string Make = "$make";

  private sealed partial class Writer {

    /// <summary>Whether the body being written is that of a function declaration of the module, which may be the generator function itself.</summary>
    private bool _direct;

    /// <summary>Whether the body written last is that of a generator function, <c>function*</c>.</summary>
    private bool _generator;

    /// <summary>
    /// The body of an iterator function. From ECMAScript 2015 a single-pass one is a generator function;
    /// a restartable one returns <c>{ [Symbol.iterator]: () =&gt; $make(a, b) }</c>, whose every
    /// enumeration calls the generator function <c>$make</c> anew with the argument values of the call,
    /// and a method calls it with its <c>this</c>. Below, the body is a state machine on
    /// <c>$generator</c>, and a restartable sequence is <c>{ $iterator: function () { … } }</c>.
    /// </summary>
    private void Iterator(IrFunction function, List<string> parameters, List<JsStatement> statements) {
      var method = function.Parameters.Count > parameters.Count;
      var restartable = function.ReturnType.Restartable;
      List<JsExpression> arguments = [.. parameters.Select(parameter => (JsExpression)Id(parameter))];
      if (this.Native("function*")) {
        var body = this.Statements(function.Body);
        if (this._direct && !restartable) {
          this._generator = true;
          statements.AddRange(body);
          return;
        }

        var keyword = this.Native("const") ? "const" : "var";
        statements.Add(new JsVariableDeclaration(keyword, [new JsDeclarator(Make, new JsFunctionExpression(null, parameters, new JsBlock(body)) { IsGenerator = true })]));
        JsExpression call = method ? new JsCall(new JsMember(Id(Make), "call"), [new JsThis(), .. arguments]) : new JsCall(Id(Make), arguments);
        statements.Add(new JsReturn(restartable && this.Native("Symbol.iterator") ? new JsObject([new JsProperty("[Symbol.iterator]", new JsArrowFunction([], null, call))]) : call));
        return;
      }

      var machine = this.StateMachine(function.Body);
      if (!restartable) {
        statements.AddRange(machine);
        return;
      }

      // The machine is made anew for every enumeration, from the argument values of the call.
      statements.Add(new JsVariableDeclaration("var", [new JsDeclarator(Make, new JsFunctionExpression(null, parameters, new JsBlock(machine)))]));
      var open = new JsFunctionExpression(null, [], new JsBlock([new JsReturn(new JsCall(Id(Make), arguments))]));
      statements.Add(new JsReturn(new JsObject([new JsProperty("$iterator", open)])));
    }

    /// <summary>An array as a sequence: the array itself, which is iterable from ECMAScript 2015; below, the restartable object of <c>$arrayIterator</c>.</summary>
    private (JsExpression Node, bool Atomic) ArraySequence(IrArraySequence sequence)
      => this.Native("Symbol.iterator") ? (this.Text(sequence.Array), false) : (this.Helper("$arrayIterator", [this.Text(sequence.Array)]), true);

    /// <summary>The statements of lazy sequences, written where the level has generators; <see langword="false"/> for any other statement.</summary>
    private bool SequenceStatement(IrStmt statement, List<JsStatement> statements) {
      switch (statement) {
        case IrYield produced:
          statements.Add(new JsExpressionStatement(new JsYield(this.Value(produced.Value), false)));
          return true;
        case IrYieldBreak:
          statements.Add(new JsReturn(null));
          return true;
        case IrYieldFrom delegated:
          statements.Add(new JsExpressionStatement(new JsYield(this.Text(delegated.Sequence), true)));
          return true;
        case IrForEachSequence loop:
          this.ForEachSequence(loop, statements);
          return true;
        default:
          return false;
      }
    }

    /// <summary>
    /// A loop over a sequence. From ECMAScript 2015 a <c>for…of</c>, which closes the iterator however
    /// the loop is left. Below, a loop over <c>next()</c> in a try whose finally block closes the cursor
    /// unless the last value it gave was its end - after a <c>break</c>, a <c>return</c> or an exception.
    /// </summary>
    private void ForEachSequence(IrForEachSequence loop, List<JsStatement> statements) {
      var outer = this._taken.Keys.ToHashSet();
      if (this.Native("for-of")) {
        var sequence = this.Text(loop.Sequence);
        var name = this.Declare(loop.Element);
        var assigned = IrSequences.Statements(loop.Body).Any(statement => statement is IrAssign assign && assign.Local == loop.Element);
        statements.Add(new JsForOf(assigned ? this.Let : this.Native("const") ? "const" : "var", name, sequence, new JsBlock(this.Statements(loop.Body))));
      } else {
        var cursor = this.Declare(new IrLocal("$it", loop.Sequence.Type));
        var result = this.Declare(new IrLocal("$r", loop.Sequence.Type));
        statements.Add(new JsVariableDeclaration("var", [new JsDeclarator(cursor, this.Helper("$iterate", [this.Text(loop.Sequence)])), new JsDeclarator(result, new JsNull())]));
        var name = this.Declare(loop.Element);
        var body = new List<JsStatement> { new JsVariableDeclaration("var", [new JsDeclarator(name, new JsMember(Id(result), "value"))]) };
        body.AddRange(this.Statements(loop.Body));
        var next = Parenthesized(new JsAssignment(Id(result), "=", new JsCall(new JsMember(Id(cursor), "next"), [])));
        var walk = new JsWhile(new JsUnary("!", new JsMember(next, "done")), new JsBlock(body));
        statements.Add(new JsTry(new JsBlock([walk]), null, null, new JsBlock([Closing(cursor, result)])));
      }

      foreach (var taken in this._taken.Keys.Where(taken => !outer.Contains(taken)).ToList())
        this._taken.Remove(taken);
    }

  }

  /// <summary><c>if (r &amp;&amp; !r.done) it["return"]();</c>: closes a cursor that has not reached its end.</summary>
  private static JsIf Closing(string cursor, string result)
    => new(Infix(Id(result), "&&", new JsUnary("!", new JsMember(Id(result), "done"))),
      new JsExpressionStatement(new JsCall(new JsIndex(Id(cursor), new JsString("return")), [])), null);

}
