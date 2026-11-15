using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  /// <summary>The variable of a state machine that holds its resume point.</summary>
  private const string State = "$s";

  /// <summary>The parameter of the step of a state machine: the cursor <c>$generator</c> made, with <c>enter</c>, <c>leave</c> and <c>end</c>.</summary>
  private const string Cursor = "$g";

  /// <summary>The label of the loop of a state machine, which a jump from inside a loop of its own continues.</summary>
  private const string Dispatch = "$machine";

  /// <summary>
  /// The state machine of an iterator function being written: its resume points, each a case of the
  /// switch of its step, the loops and switches a jump leaves, the finally blocks pending, and the
  /// jumps of the statements written as they are that leave them.
  /// </summary>
  private sealed class Machine {

    /// <summary>The statements of every resume point, by its number; 0 is the start, 1 the end.</summary>
    public List<List<JsStatement>> Cases { get; } = [[], []];

    /// <summary>The resume point being written.</summary>
    public List<JsStatement> Current { get; set; } = null!;

    /// <summary>The jumps out of statements written as they are, by the jump: written as the jump of the machine.</summary>
    public Dictionary<IrStmt, List<JsStatement>> Escapes { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The loops and switches around the statement being written: where a break and a continue go, and how many finally blocks are pending there.</summary>
    public List<(int Break, int? Continue, int Depth)> Frames { get; } = [];

    /// <summary>How many finally blocks of the body are pending at the statement being written.</summary>
    public int Depth { get; set; }

    /// <summary>The resume points that leave finally blocks before they go on, by where they go and how many they leave.</summary>
    public Dictionary<(int Target, int Leaving), int> Trampolines { get; } = [];

    /// <summary>The variables the machine keeps from one resume to the next, in the order they are declared.</summary>
    public List<string> Kept { get; } = [];

  }

  private sealed partial class Writer {

    /// <summary>The state machine being written, or <see langword="null"/>.</summary>
    private Machine? _machine;

    /// <summary>
    /// The body of an iterator function below ECMAScript 2015: the variables it keeps, then a cursor of
    /// <c>$generator</c> whose step switches over the resume point. A statement that suspends is cut
    /// into resume points - a yield returns its value and goes on at the next one; a loop, a switch,
    /// a conditional and a finally block become jumps between them - and every other statement is
    /// written as it is, its jumps out of it becoming jumps of the machine. A try's finally block is a
    /// function the cursor keeps while the body is inside the try, which leaving the try, closing the
    /// cursor and an exception run, innermost first.
    /// </summary>
    private List<JsStatement> StateMachine(IrBlock body) {
      var machine = this._machine = new Machine();
      machine.Current = machine.Cases[0];
      machine.Cases[1].Add(new JsReturn(new JsCall(new JsMember(Id(Cursor), "end"), [])));
      ++this._functionDepth;
      this.Flatten(body);
      if (!Terminated(machine.Current))
        machine.Current.Add(new JsReturn(new JsCall(new JsMember(Id(Cursor), "end"), [])));

      --this._functionDepth;
      this._machine = null;
      var cases = machine.Cases.Select((statements, index) => new JsSwitchCase(Number(index.ToString(CultureInfo.InvariantCulture)), statements)).ToList();
      var dispatch = new JsLabeled(Dispatch, new JsFor(null, null, null, new JsBlock([new JsSwitch(Id(State), cases)])));
      var step = new JsFunctionExpression(null, [Cursor], new JsBlock([dispatch]));
      return [
        new JsVariableDeclaration("var", [new JsDeclarator(State, Number("0")), .. machine.Kept.Select(name => new JsDeclarator(name, null))]),
        new JsReturn(this.Helper("$generator", [step]))
      ];
    }

    /// <summary>Whether a resume point being written already ends: with a return, a throw or a jump.</summary>
    private static bool Terminated(List<JsStatement> statements) => statements is [.., JsReturn or JsThrow or JsLabeledContinue];

    /// <summary>A new resume point.</summary>
    private static int Point(Machine machine) {
      machine.Cases.Add([]);
      return machine.Cases.Count - 1;
    }

    /// <summary>
    /// The statements of a jump to a resume point from where the given number of finally blocks is
    /// pending: through a resume point that leaves the ones more than pend there.
    /// </summary>
    private static List<JsStatement> Goto(Machine machine, int target, int depth) {
      var leaving = machine.Depth - depth;
      if (leaving > 0) {
        if (!machine.Trampolines.TryGetValue((target, leaving), out var through)) {
          through = Point(machine);
          machine.Trampolines[(target, leaving)] = through;
          var leaves = Enumerable.Range(0, leaving).Select(_ => (JsStatement)new JsExpressionStatement(new JsCall(new JsMember(Id(Cursor), "leave"), [])));
          machine.Cases[through].AddRange([.. leaves, .. Jump(target)]);
        }

        target = through;
      }

      return Jump(target);
    }

    /// <summary><c>$s = target; continue $machine;</c>.</summary>
    private static List<JsStatement> Jump(int target)
      => [new JsExpressionStatement(new JsAssignment(Id(State), "=", Number(target.ToString(CultureInfo.InvariantCulture)))), new JsLabeledContinue(Dispatch)];

    /// <summary>Goes on at a resume point: the one being written jumps there unless it already ends.</summary>
    private static void Begin(Machine machine, int point) {
      if (!Terminated(machine.Current))
        machine.Current.AddRange(Goto(machine, point, machine.Depth));

      machine.Current = machine.Cases[point];
    }

    /// <summary>A jump of a statement written as it is that goes out of it, as its jump of the machine.</summary>
    private bool Escaping(IrStmt statement, List<JsStatement> statements) {
      if (this._machine is not { } machine || !machine.Escapes.TryGetValue(statement, out var jump))
        return false;

      statements.AddRange(jump);
      return true;
    }

    /// <summary>A variable the machine keeps from one resume to the next.</summary>
    private string Keep(IrLocal local) {
      var name = this.Declare(local);
      this._machine!.Kept.Add(name);
      return name;
    }

    /// <summary>The jump of a break, a continue, a return or a yield break that stands where the machine is cut.</summary>
    private List<JsStatement> JumpOf(IrStmt jump) {
      var machine = this._machine!;
      switch (jump) {
        case IrBreak:
          var broken = machine.Frames[^1];
          return Goto(machine, broken.Break, broken.Depth);
        case IrContinue:
          var looped = machine.Frames.Last(frame => frame.Continue is not null);
          return Goto(machine, looped.Continue!.Value, looped.Depth);
        default:
          // The end runs every pending finally block; written as it is, the finally blocks around the jump run first.
          return Jump(1);
      }
    }

    /// <summary>
    /// Records every jump of a statement written as it is that leaves it: a break or a continue whose
    /// loop or switch is outside it, and every return.
    /// </summary>
    private void Escapes(IrStmt statement, int loops, int breakables) {
      var machine = this._machine!;
      switch (statement) {
        case IrBreak when breakables == 0:
        case IrContinue when loops == 0:
        case IrReturn or IrYieldBreak:
          machine.Escapes[statement] = this.JumpOf(statement);
          return;
        case IrBlock block:
          foreach (var inner in block.Statements)
            this.Escapes(inner, loops, breakables);

          return;
        case IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence:
          foreach (var block in IrTree.Blocks(statement))
            this.Escapes(block, loops + 1, breakables + 1);

          return;
        case IrSwitch:
          foreach (var block in IrTree.Blocks(statement))
            this.Escapes(block, loops, breakables + 1);

          return;
        default:
          foreach (var block in IrTree.Blocks(statement))
            this.Escapes(block, loops, breakables);

          return;
      }
    }

    /// <summary>Writes a statement of the body into the machine: cut where it suspends, as it is elsewhere.</summary>
    private void Flatten(IrStmt statement) {
      var machine = this._machine!;
      if (!IrSequences.Suspends(statement)) {
        switch (statement) {
          case IrDeclare declare:
            var kept = this.Keep(declare.Local);
            if (declare.Initializer is not null)
              machine.Current.Add(new JsExpressionStatement(new JsAssignment(Id(kept), "=", this.Value(declare.Initializer))));

            return;
          case IrBreak or IrContinue or IrReturn or IrYieldBreak:
            machine.Current.AddRange(this.JumpOf(statement));
            return;
          case IrBlock block:
            foreach (var inner in block.Statements)
              this.Flatten(inner);

            return;
          default:
            this.Escapes(statement, 0, 0);
            this.Statement(statement, machine.Current);
            return;
        }
      }

      switch (statement) {
        case IrBlock block:
          foreach (var inner in block.Statements)
            this.Flatten(inner);

          break;
        case IrYield produced:
          var value = this.Value(produced.Value);
          var resume = Point(machine);
          machine.Current.AddRange([new JsExpressionStatement(new JsAssignment(Id(State), "=", Number(resume.ToString(CultureInfo.InvariantCulture)))), Produced(value)]);
          machine.Current = machine.Cases[resume];
          break;
        case IrIf conditional:
          this.FlattenIf(conditional);
          break;
        case IrWhile loop:
          var head = Point(machine);
          var end = Point(machine);
          Begin(machine, head);
          if (loop.Condition is not IrConst { Value: not 0 })
            machine.Current.Add(this.Unless(loop.Condition, end));

          this.Loop(loop.Body, end, head);
          Begin(machine, head);
          machine.Current = machine.Cases[end];
          break;
        case IrDoWhile loop:
          var pass = Point(machine);
          var test = Point(machine);
          var after = Point(machine);
          Begin(machine, pass);
          this.Loop(loop.Body, after, test);
          Begin(machine, test);
          machine.Current.Add(new JsIf(this.Text(loop.Condition), new JsBlock(Goto(machine, pass, machine.Depth)), null));
          Begin(machine, after);
          break;
        case IrFor loop:
          var top = Point(machine);
          var step = Point(machine);
          var done = Point(machine);
          Begin(machine, top);
          if (loop.Condition is not IrConst { Value: not 0 })
            machine.Current.Add(this.Unless(loop.Condition, done));

          this.Loop(loop.Body, done, step);
          Begin(machine, step);
          this.Flatten(loop.Step);
          Begin(machine, top);
          machine.Current = machine.Cases[done];
          break;
        case IrSwitch choice:
          this.FlattenSwitch(choice);
          break;
        case IrTry attempt:
          // A try that suspends has a finally block and no catch clause: the verifier lets no yield stand in a try that has one.
          ++this._functionDepth;
          var final = new JsFunctionExpression(null, [], new JsBlock(this.Statements(attempt.Finally!)));
          --this._functionDepth;
          machine.Current.Add(new JsExpressionStatement(new JsCall(new JsMember(Id(Cursor), "enter"), [final])));
          ++machine.Depth;
          this.Flatten(attempt.Body);
          --machine.Depth;
          if (!Terminated(machine.Current))
            machine.Current.Add(new JsExpressionStatement(new JsCall(new JsMember(Id(Cursor), "leave"), [])));

          break;
        case IrUsing scope:
          this.Flatten(scope.Lowered());
          break;
        case IrForEachSequence loop:
          this.FlattenSequence(loop.Sequence, loop.Element, loop.Body);
          break;
        case IrYieldFrom delegated:
          this.FlattenSequence(delegated.Sequence, null, null);
          break;
        default:
          // A loop over a collection that suspends: its enumeration is not cut into resume points yet.
          this.Native("yield inside a loop over a collection");
          this.Statement(statement, machine.Current);
          break;
      }
    }

    /// <summary><c>return { value: v, done: false };</c>.</summary>
    private static JsReturn Produced(JsExpression value) => new(new JsObject([new JsProperty("value", value), new JsProperty("done", new JsBoolean(false))]));

    /// <summary><c>if (!(condition)) { jump }</c>.</summary>
    private JsIf Unless(IrExpr condition, int target)
      => new(new JsUnary("!", this.Operand(condition)), new JsBlock(Goto(this._machine!, target, this._machine!.Depth)), null);

    /// <summary>The body of a loop, whose break goes to one resume point and whose continue to another.</summary>
    private void Loop(IrBlock body, int breaks, int? continues) {
      var machine = this._machine!;
      machine.Frames.Add((breaks, continues, machine.Depth));
      this.Flatten(body);
      machine.Frames.RemoveAt(machine.Frames.Count - 1);
    }

    private void FlattenIf(IrIf conditional) {
      var machine = this._machine!;
      var otherwise = Point(machine);
      var end = Point(machine);
      machine.Current.Add(this.Unless(conditional.Condition, otherwise));
      this.Flatten(conditional.Then);
      if (!Terminated(machine.Current))
        machine.Current.AddRange(Goto(machine, end, machine.Depth));

      machine.Current = machine.Cases[otherwise];
      if (conditional.Else is not null)
        this.Flatten(conditional.Else);

      Begin(machine, end);
    }

    /// <summary>A switch that suspends: a switch of its own jumps to the resume point of the section chosen; a break ends the switch.</summary>
    private void FlattenSwitch(IrSwitch choice) {
      var machine = this._machine!;
      var type = choice.Value.Type;
      var halves = type.IsLong && this.Halves;
      var discriminant = halves ? this.Helper("$key64", choice.Value) : this.Text(choice.Value);
      var end = Point(machine);
      var points = choice.Sections.Select(_ => Point(machine)).ToList();
      var cases = new List<JsSwitchCase>();
      for (var index = 0; index < choice.Sections.Count; ++index) {
        var section = choice.Sections[index];
        var labels = section.Labels
          .Select(label => halves ? (JsExpression?)new JsString($"{(ulong)label >> 32}:{(ulong)label & 0xFFFFFFFF}") : this.Constant(type, label).Node)
          .ToList();
        if (section.IsDefault)
          labels.Add(null);

        foreach (var label in labels)
          cases.Add(new JsSwitchCase(label, label == labels[^1] ? Goto(machine, points[index], machine.Depth) : []));
      }

      machine.Current.Add(new JsSwitch(discriminant, cases));
      if (!choice.Sections.Any(section => section.IsDefault))
        machine.Current.AddRange(Goto(machine, end, machine.Depth));

      for (var index = 0; index < choice.Sections.Count; ++index) {
        machine.Current = machine.Cases[points[index]];
        this.Loop(choice.Sections[index].Body, end, null);
        if (!Terminated(machine.Current))
          machine.Current.AddRange(Goto(machine, end, machine.Depth));
      }

      machine.Current = machine.Cases[end];
    }

    /// <summary>
    /// A loop over a sequence that suspends, or the values of a sequence yielded as the body's own: a
    /// cursor the machine keeps, which a pending finally block closes unless it has reached its end.
    /// The end of the loop leaves that block, so a break and the end close the cursor alike.
    /// </summary>
    private void FlattenSequence(IrExpr sequence, IrLocal? element, IrBlock? body) {
      var machine = this._machine!;
      var cursor = this.Keep(new IrLocal("$it", sequence.Type));
      var result = this.Keep(new IrLocal("$r", sequence.Type));
      machine.Current.Add(new JsExpressionStatement(new JsAssignment(Id(cursor), "=", this.Helper("$iterate", [this.Text(sequence)]))));
      machine.Current.Add(new JsExpressionStatement(new JsAssignment(Id(result), "=", new JsNull())));
      var close = new JsFunctionExpression(null, [], new JsBlock([Closing(cursor, result)]));
      machine.Current.Add(new JsExpressionStatement(new JsCall(new JsMember(Id(Cursor), "enter"), [close])));
      ++machine.Depth;
      var head = Point(machine);
      var exit = Point(machine);
      Begin(machine, head);
      var next = Parenthesized(new JsAssignment(Id(result), "=", new JsCall(new JsMember(Id(cursor), "next"), [])));
      machine.Current.Add(new JsIf(new JsMember(next, "done"), new JsBlock(Goto(machine, exit, machine.Depth)), null));
      var current = new JsMember(Id(result), "value");
      if (element is null) {
        machine.Current.AddRange([new JsExpressionStatement(new JsAssignment(Id(State), "=", Number(head.ToString(CultureInfo.InvariantCulture)))), Produced(current)]);
      } else {
        machine.Current.Add(new JsExpressionStatement(new JsAssignment(Id(this.Keep(element)), "=", current)));
        this.Loop(body!, exit, head);
        Begin(machine, head);
      }

      machine.Current = machine.Cases[exit];
      machine.Current.Add(new JsExpressionStatement(new JsCall(new JsMember(Id(Cursor), "leave"), [])));
      --machine.Depth;
    }

  }

}
