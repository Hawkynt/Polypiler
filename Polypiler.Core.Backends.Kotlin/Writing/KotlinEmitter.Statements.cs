using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.IR;
using static Polypiler.Core.Backends.Kotlin.Syntax.Kt;

namespace Polypiler.Core.Backends.Kotlin;

public static partial class KotlinEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// A loop or a switch the statements written are inside of. A jump names its Kotlin label unless
    /// its target is the innermost frame and a loop; a switch a <c>break</c> leaves before its end is
    /// written inside <c>label@ do { … } while (false)</c>, the one loop of Kotlin that runs once.
    /// </summary>
    private sealed class Frame(bool loop, IReadOnlyList<KtStatement>? step) {

      /// <summary>Whether the frame is a loop; otherwise a switch.</summary>
      public bool Loop { get; } = loop;

      /// <summary>The step of an IR <c>for</c>, written before every <c>continue</c> of the loop.</summary>
      public IReadOnlyList<KtStatement>? Step { get; } = step;

      /// <summary>The label, once a jump needs one.</summary>
      public string? Label { get; set; }

    }

    private readonly Dictionary<IrLocal, string> _locals = [];
    private readonly HashSet<string> _localNames = new(StringComparer.Ordinal);
    private readonly HashSet<IrLocal> _assigned = [];
    private readonly HashSet<IrLocal> _declared = [];
    private readonly List<Frame> _frames = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);

    /// <summary>Starts a function: no local is named yet, and the variables its body assigns are known.</summary>
    private void BeginFunction(string name, IrFunction? function) {
      this._function = name;
      this._locals.Clear();
      this._localNames.Clear();
      this._assigned.Clear();
      this._declared.Clear();
      this._frames.Clear();
      this._labels.Clear();
      if (function is null)
        return;

      foreach (var statement in IrClosures.Within(function.Body)) {
        if (statement is IrAssign assign)
          this._assigned.Add(assign.Local);

        foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
          if (node is IrStep { Target: IrLocalRef reference })
            this._assigned.Add(reference.Local);
          else if (node is IrBind bind)
            this._assigned.Add(bind.Local);
        }
      }
    }

    /// <summary>The Kotlin name of a parameter or local: unique in the function and free of every name of the file, so that nothing shadows.</summary>
    private string LocalName(IrLocal local) {
      if (this._locals.TryGetValue(local, out var known))
        return known;

      var name = KotlinNaming.Free(Fallback(KotlinNaming.Camel(local.Name), "value"), candidate => this._localNames.Contains(candidate) || this._fileNames.Contains(candidate));
      this._localNames.Add(name);
      this._locals[local] = name;
      return name;
    }

    /// <summary>A label of the function no other label has.</summary>
    private string NewLabel(string kind) {
      var count = this._labels.GetValueOrDefault(kind) + 1;
      this._labels[kind] = count;
      return count == 1 ? kind : kind + count;
    }

    /// <summary>The statements of a block, appended: a nested block's statements are flattened, as every local of a function has a name of its own.</summary>
    private void Statements(IrBlock block, List<KtStatement> into) {
      foreach (var statement in block.Statements)
        this.Statement(statement, into);
    }

    private KtBlock Block(IrBlock block) {
      var statements = new List<KtStatement>();
      this.Statements(block, statements);
      return new KtBlock(statements);
    }

    private void Statement(IrStmt statement, List<KtStatement> into) {
      switch (statement) {
        case IrBlock block:
          this.Statements(block, into);
          break;
        case IrDeclare declare:
          into.Add(this.Declare(declare));
          break;
        case IrAssign assign:
          into.Add(this.Assign(Name(this.LocalName(assign.Local)), assign.Local.Type, assign.Value, assign.Value is IrBinary { Left: IrLocalRef reference } && reference.Local == assign.Local));
          break;
        case IrGlobalAssign assign:
          into.Add(this.Assign(Name(this._globals[assign.Global.Name]), assign.Global.Type, assign.Value, assign.Value is IrBinary { Left: IrGlobalRef global } && global.Global == assign.Global));
          break;
        case IrStore store:
          into.Add(new KtAssignment(new KtIndex(this.Expression(store.Array), this.Index(store.Index)), "=", this.Stored(store.Array.Type.Element!, store.Value)));
          break;
        case IrExprStmt { Expression: IrStep step }:
          into.Add(this.StepStatement(step));
          break;
        case IrExprStmt expression:
          if (this.Effect(expression.Expression) is { } effect)
            into.Add(new KtExpressionStatement(effect));
          break;
        case IrWriteLine line:
          into.Add(new KtExpressionStatement(Call("println", this.Expression(line.Value))));
          break;
        case IrIf conditional:
          into.Add(this.If(conditional));
          break;
        case IrWhile loop:
          into.Add(this.Loop(null, frame => {
            var body = this.Block(loop.Body);
            return new KtWhile(frame.Label, this.Expression(loop.Condition), body);
          }));
          break;
        case IrDoWhile loop:
          into.Add(this.Loop(null, frame => {
            var body = this.Block(loop.Body);
            return new KtDoWhile(frame.Label, body, this.Expression(loop.Condition));
          }));
          break;
        case IrFor loop:
          into.Add(this.For(loop));
          break;
        case IrSwitch choice:
          into.Add(this.Switch(choice));
          break;
        case IrBreak:
          into.Add(this.Break());
          break;
        case IrContinue:
          this.Continue(into);
          break;
        case IrReturn result:
          into.Add(new KtReturn(result.Value is { } value ? this.Expression(value) : null));
          break;
        case IrThrow raise:
          into.Add(new KtThrow(this.Expression(raise.Exception)));
          break;
        default:
          this.Refuse(ControlUnsupportedDescriptor, $"the statement '{KindOf(statement)}' has no Kotlin form yet");
          break;
      }
    }

    /// <summary>
    /// A local: a <c>val</c> where it is never assigned after its first value, otherwise a <c>var</c>;
    /// always with its type, as an integer literal alone would make an <c>Int</c> of a <c>Long</c>. A
    /// number or truth value declared without a first value starts with its zero.
    /// </summary>
    private KtVariable Declare(IrDeclare declare) {
      // Two locals of one name and type are one record; a second declaration starts a variable of its own name.
      if (!this._declared.Add(declare.Local))
        this._locals.Remove(declare.Local);

      var name = this.LocalName(declare.Local);
      var type = declare.Local.Type;
      var value = declare.Initializer is IrConst constant ? this.ValueOf(type, constant.Value, typed: true)
        : declare.Initializer is { } initializer ? this.Expression(initializer)
        : KotlinTypes.Scalar(type) is not null ? this.Zero(type)
        : null;
      var mutable = this._assigned.Contains(declare.Local) || declare.Initializer is null;
      return new KtVariable(mutable, name, this.TypeOf(type), value);
    }

    /// <summary><c>target = value</c>, or <c>target op= operand</c> where the value is the target with an operator Kotlin has a compound assignment of for the type.</summary>
    private KtAssignment Assign(KtExpression target, IrType type, IrExpr value, bool compound) {
      if (compound && value is IrBinary { Op: var op } binary && (type.IsWideInteger || type.IsFloat) && !type.IsEnum
          && op switch { IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply => true, IrBinaryOp.Divide or IrBinaryOp.Remainder => type.IsFloat, _ => false })
        return new KtAssignment(target, op switch {
          IrBinaryOp.Add => "+=",
          IrBinaryOp.Subtract => "-=",
          IrBinaryOp.Multiply => "*=",
          IrBinaryOp.Divide => "/=",
          _ => "%="
        }, this.Expression(binary.Right));

      return new KtAssignment(target, "=", this.Expression(value));
    }

    /// <summary>
    /// A step as a statement: <c>x++</c> or <c>a[i]--</c>, which Kotlin's <c>inc</c> and <c>dec</c>
    /// wrap as the IR does; a trapping step <c>x = Math.incrementExact(x)</c> or its helper, of a
    /// variable or of an element whose array and index are names.
    /// </summary>
    private KtStatement StepStatement(IrStep step) {
      if (!step.Trap || step.Type.IsFloat)
        return new KtExpressionStatement(new KtPostfix(this.Place(step.Target), step.Increment ? "++" : "--"));

      var place = this.Place(step.Target);
      if (step.Target is IrArrayLoad { Array: var array, Index: var index } && !(Simple(array) && Simple(index)))
        this.Refuse(InexpressibleDescriptor, "a checked step of an array element whose array or index is computed has no Kotlin form yet");

      var one = new IrConst(step.Type, 1);
      var next = this.Expression(new IrBinary(step.Increment ? IrBinaryOp.AddChecked : IrBinaryOp.SubtractChecked, step.Target, one));
      return new KtAssignment(place, "=", step.Target is IrArrayLoad load ? this.Stored(load.Array.Type.Element!, next, load.Type) : next);
    }

    /// <summary>Whether an IR expression is a variable or a constant, which evaluates the same however often it is evaluated.</summary>
    private static bool Simple(IrExpr expression) => expression is IrLocalRef or IrConst or IrConstRef;

    /// <summary>The place a step changes: the variable, or the element of a primitive array, which holds an unsigned element as the signed type of its width.</summary>
    private KtExpression Place(IrExpr target) => target switch {
      IrLocalRef reference => Name(this.LocalName(reference.Local)),
      IrGlobalRef reference => Name(this._globals[reference.Global.Name]),
      _ => new KtIndex(this.Expression(((IrArrayLoad)target).Array), this.Index(((IrArrayLoad)target).Index))
    };

    /// <summary>An expression evaluated for its effect: the expression, or <see langword="null"/> where it has none, which Kotlin would warn about.</summary>
    private KtExpression? Effect(IrExpr expression) => expression switch {
      IrLocalRef or IrConst or IrConstRef or IrStrConst or IrGlobalRef => null,
      _ => this.Expression(expression)
    };

    /// <summary><c>if</c>, with an <c>else if</c> where the else branch is one <c>if</c>.</summary>
    private KtIf If(IrIf conditional) {
      var then = this.Block(conditional.Then);
      KtStatement? otherwise = conditional.Else switch {
        null => null,
        { Statements: [IrIf chained] } => this.If(chained),
        { } block => this.Block(block)
      };
      if (otherwise is KtBlock { Statements.Count: 0 })
        otherwise = null;

      return new KtIf(this.Expression(conditional.Condition), then, otherwise);
    }

    /// <summary>Writes a loop inside a frame of its own; the loop is built once its body knows whether a jump needs its label.</summary>
    private KtStatement Loop(IReadOnlyList<KtStatement>? step, Func<Frame, KtStatement> build) {
      var frame = new Frame(true, step);
      this._frames.Add(frame);
      try {
        return build(frame);
      } finally {
        this._frames.RemoveAt(this._frames.Count - 1);
      }
    }

    /// <summary>
    /// An IR <c>for</c>: a <c>while</c> whose body ends with the step, which every <c>continue</c> of
    /// the loop runs first, as the IR's step follows every pass.
    /// </summary>
    private KtStatement For(IrFor loop) {
      var step = new List<KtStatement>();
      this.Statements(loop.Step, step);
      return this.Loop(step, frame => {
        var statements = new List<KtStatement>();
        this.Statements(loop.Body, statements);
        if (statements.Count == 0 || !Jumps(statements[^1]))
          statements.AddRange(step);

        return new KtWhile(frame.Label, this.Expression(loop.Condition), new KtBlock(statements));
      });
    }

    /// <summary>Whether a statement never completes: a jump, a return or a throw, after which Kotlin would call a statement unreachable.</summary>
    private static bool Jumps(KtStatement statement) => statement is KtBreak or KtContinue or KtReturn or KtThrow;

    /// <summary>
    /// A switch: a <c>when</c> over its value, one branch per section with its labels as conditions,
    /// the default section as <c>else</c>. The <c>break</c> that ends a section is left out, since a
    /// branch of <c>when</c> never falls through; a <c>break</c> inside a section leaves the switch
    /// through the label of a <c>do { … } while (false)</c> around it.
    /// </summary>
    private KtStatement Switch(IrSwitch choice) {
      var type = choice.Value.Type.Integral;
      var promoted = KotlinTypes.Promoted(type);
      var subject = this.Expression(choice.Value);
      if (promoted != type)
        subject = Method(subject, KotlinTypes.To(promoted));

      var frame = new Frame(false, null);
      this._frames.Add(frame);
      var entries = new List<KtWhenEntry>();
      try {
        foreach (var section in choice.Sections.OrderBy(section => section.IsDefault)) {
          var statements = section.Body.Statements;
          if (statements.Count > 0 && statements[^1] is IrBreak)
            statements = [.. statements.Take(statements.Count - 1)];

          var body = new List<KtStatement>();
          foreach (var statement in statements)
            this.Statement(statement, body);

          var conditions = section.IsDefault ? [] : section.Labels.Select(label => this.ValueOf(promoted, label, typed: false)).ToList();
          entries.Add(new KtWhenEntry(conditions, new KtBlock(body)));
        }
      } finally {
        this._frames.RemoveAt(this._frames.Count - 1);
      }

      var when = new KtWhen(subject, entries);
      return frame.Label is { } label ? new KtDoWhile(label, new KtBlock([when]), False) : when;
    }

    /// <summary><c>break</c> of the innermost loop or switch, with its label where the target is not the innermost loop of Kotlin.</summary>
    private KtStatement Break() {
      var target = this._frames[^1];
      if (target.Loop)
        return new KtBreak(null);

      target.Label ??= this.NewLabel("switch");
      return new KtBreak(target.Label);
    }

    /// <summary>
    /// <c>continue</c> of the innermost loop, after the step of an IR <c>for</c>; with the loop's label
    /// where a switch lies between, whose wrapping loop an unlabelled jump would leave.
    /// </summary>
    private void Continue(List<KtStatement> into) {
      var index = this._frames.FindLastIndex(frame => frame.Loop);
      var target = this._frames[index];
      if (target.Step is { } step)
        into.AddRange(step);

      if (index == this._frames.Count - 1) {
        into.Add(new KtContinue(null));
        return;
      }

      target.Label ??= this.NewLabel("loop");
      into.Add(new KtContinue(target.Label));
    }

  }

}
