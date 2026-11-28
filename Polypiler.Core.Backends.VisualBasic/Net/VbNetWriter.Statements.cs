using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic.Net;

internal sealed partial class VbNetWriter {

  /// <summary>A loop or a switch the statements written are inside: where a <c>break</c> and a <c>continue</c> go.</summary>
  private sealed class Frame(bool isLoop, bool hasStep) {

    public bool IsLoop { get; } = isLoop;

    /// <summary>Whether a <c>continue</c> has to run a step first: it jumps to <see cref="Label"/> instead of <c>Continue Do</c>.</summary>
    public bool HasStep { get; } = hasStep;

    /// <summary>The label before the step, made when the first <c>continue</c> needs it.</summary>
    public string? Label { get; set; }

  }

  private readonly Dictionary<IrLocal, string> _locals = [];
  private readonly HashSet<string> _localNames = new(StringComparer.OrdinalIgnoreCase);
  private readonly Stack<Frame> _frames = new();
  private int _labels;

  /// <summary>Starts the names and the state of a procedure; a local is named like nothing at the level of the module.</summary>
  private void BeginFunction(string name, IEnumerable<string> taken) {
    this._function = name;
    this._locals.Clear();
    this._localNames.Clear();
    this._localNames.UnionWith(this._moduleNames);
    this._localNames.UnionWith(taken);
    this._frames.Clear();
    this._labels = 0;
  }

  /// <summary>The name of a local or parameter, unique in the procedure without regard to case, as Visual Basic forbids a local hiding another.</summary>
  private string LocalName(IrLocal local) {
    if (this._locals.TryGetValue(local, out var known))
      return known;

    var name = VbNetNaming.Free(VbNetNaming.Valid(local.Name, "value"), this._localNames.Contains);
    this._localNames.Add(name);
    this._locals[local] = name;
    return name;
  }

  /// <summary>A name of the procedure no local has, for a catch variable or a label.</summary>
  private string Fresh(string stem) {
    var name = VbNetNaming.Free(stem, this._localNames.Contains);
    this._localNames.Add(name);
    return name;
  }

  private List<VnStatement> Statements(IrBlock block) {
    var statements = new List<VnStatement>();
    foreach (var statement in block.Statements)
      this.Statement(statement, statements);
    return statements;
  }

  private List<VnStatement> Within(Frame frame, IrBlock block) {
    this._frames.Push(frame);
    var statements = this.Statements(block);
    this._frames.Pop();
    return statements;
  }

  private void Statement(IrStmt statement, List<VnStatement> statements) {
    switch (statement) {
      case IrBlock block:
        foreach (var inner in block.Statements)
          this.Statement(inner, statements);
        break;
      case IrDeclare declare:
        statements.Add(new VnDim(this.LocalName(declare.Local), this.TypeName(declare.Local.Type), declare.Initializer is null ? this.Default(declare.Local.Type) : this.Expression(declare.Initializer)));
        break;
      case IrAssign assign:
        statements.Add(new VnAssign(new VnName(this.LocalName(assign.Local)), "=", this.Expression(assign.Value)));
        break;
      case IrGlobalAssign assign:
        statements.Add(new VnAssign(new VnName(this._globals[assign.Global.Name]), "=", this.Expression(assign.Value)));
        break;
      case IrStore store:
        statements.Add(new VnAssign(new VnInvoke(this.Expression(store.Array), [this.Index(store.Index)]), "=", this.Expression(store.Value)));
        break;
      case IrFieldStore store:
        statements.Add(new VnAssign(new VnMember(this.Expression(store.Target), this.FieldName(store.Field)), "=", this.Expression(store.Value)));
        break;
      case IrExprStmt { Expression: var expression }:
        statements.Add(this.Effect(expression));
        break;
      case IrWriteLine line:
        statements.Add(new VnCallStatement(new VnInvoke(new VnName("System.Console.WriteLine"), [this.Expression(line.Value)])));
        break;
      case IrIf branch:
        statements.Add(new VnIf(this.Expression(branch.Condition), this.Statements(branch.Then), branch.Else is null ? null : this.Statements(branch.Else)));
        break;
      case IrWhile loop:
        statements.Add(new VnDo(this.Expression(loop.Condition), false, this.Within(new Frame(true, false), loop.Body)));
        break;
      case IrDoWhile loop:
        statements.Add(new VnDo(this.Expression(loop.Condition), true, this.Within(new Frame(true, false), loop.Body)));
        break;
      case IrFor loop:
        var frame = new Frame(true, loop.Step.Statements.Count > 0);
        var body = this.Within(frame, loop.Body);
        if (frame.Label is not null)
          body.Add(new VnLabel(frame.Label));
        body.AddRange(this.Statements(loop.Step));
        statements.Add(new VnDo(this.Expression(loop.Condition), false, body));
        break;
      case IrSwitch choice:
        statements.Add(this.Select(choice));
        break;
      case IrBreak:
        statements.Add(new VnExit(this._frames.Count > 0 && !this._frames.Peek().IsLoop ? "Select" : "Do"));
        break;
      case IrContinue:
        var target = this._frames.FirstOrDefault(candidate => candidate.IsLoop);
        if (target is { HasStep: true }) {
          target.Label ??= this.Fresh("ContinueAt" + (++this._labels).ToString(CultureInfo.InvariantCulture));
          statements.Add(new VnGoTo(target.Label));
        } else {
          statements.Add(new VnContinue());
        }

        break;
      case IrReturn returned:
        statements.Add(new VnReturn(returned.Value is null ? null : this.Expression(returned.Value)));
        break;
      case IrThrow thrown:
        statements.Add(new VnThrow(this.Expression(thrown.Exception)));
        break;
      case IrRethrow:
        statements.Add(new VnThrow(null));
        break;
      case IrTry attempt:
        statements.Add(this.Try(attempt));
        break;
      default:
        this.ControlRefused(statement.GetType().Name[2..]);
        break;
    }
  }

  /// <summary>
  /// An expression evaluated for its effect: a call as a statement, a step of a variable as a compound
  /// assignment, an assignment as one; anything else through <c>PlyDiscard</c>, which evaluates it.
  /// </summary>
  private VnStatement Effect(IrExpr expression) {
    switch (expression) {
      case IrCall or IrIntrinsic { Type.Kind: IrTypeKind.Void }:
        return new VnCallStatement(this.Expression(expression));
      case IrStep { Trap: false, Target: IrLocalRef or IrGlobalRef, Type: var type } step when type.IsFloat || type.IsArithmetic && !this.Checks:
        return new VnAssign(this.Expression(step.Target), step.Increment ? "+=" : "-=", Literal(type, type.IsFloat ? IrOperators.Bits(type, 1.0) : 1));
      case IrBind bind:
        return new VnAssign(new VnName(this.LocalName(bind.Local)), "=", this.Expression(bind.Value));
      case IrStep step:
        return new VnCallStatement(this.Expression(step));
      default:
        return new VnCallStatement(new VnInvoke(this.Helper(VbNetHelpers.Discard), [this.Expression(expression)]));
    }
  }

  /// <summary>A switch as <c>Select Case</c>; the default section is <c>Case Else</c>, written last, its labels dropped, as nothing else selects them.</summary>
  private VnSelect Select(IrSwitch choice) {
    var value = this.Expression(choice.Value);
    var cases = new List<VnCase>();
    List<VnStatement>? otherwise = null;
    foreach (var section in choice.Sections) {
      var body = this.Within(new Frame(false, false), section.Body);
      if (section.IsDefault)
        otherwise = body;
      else
        cases.Add(new VnCase([.. section.Labels.Select(label => Literal(choice.Value.Type, label))], body));
    }

    return new VnSelect(value, cases, otherwise);
  }

  /// <summary>A try: every catch clause with its variable, a clause without one with a name of its own.</summary>
  private VnTry Try(IrTry attempt) {
    var body = this.Statements(attempt.Body);
    var catches = new List<VnCatch>();
    foreach (var clause in attempt.Catches) {
      var name = clause.Local is { } local ? this.LocalName(local) : this.Fresh("caught");
      var type = this.ClassName(clause.Class);
      catches.Add(new VnCatch(name, type, clause.Filter is null ? null : this.Expression(clause.Filter), this.Statements(clause.Body)));
    }

    return new VnTry(body, catches, attempt.Finally is null ? null : this.Statements(attempt.Finally));
  }

  /// <summary>The .NET name of an exception class; a class of the module's own is refused.</summary>
  private string ClassName(IrClassType type) {
    if (type.IsLibrary && ExceptionName(type) is { } name)
      return name;

    this.Refuse(VisualBasicEmitter.TypeUnsupportedDescriptor, $"the class '{type.Name}' has no Visual Basic representation in profile '{profile.Name}'");
    return "System.Exception";
  }

  private string FieldName(IrField field)
    => field == IrExceptionClass.Message ? "Message"
      : field == IrExceptionClass.InnerException ? "InnerException"
      : this._fieldNames.TryGetValue(field, out var name) ? name
      : VbNetNaming.Valid(field.Name, "Field");

}
