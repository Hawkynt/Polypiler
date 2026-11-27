using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic;

public static partial class VisualBasicEmitter {

  private sealed partial class Writer {

    /// <summary>A loop or a switch the statements written are inside: where a <c>break</c> and a <c>continue</c> go.</summary>
    private sealed class Frame(bool isLoop, int handlers) {

      public bool IsLoop { get; } = isLoop;

      /// <summary>How many tries enclose the loop or switch: the handler a jump out of tries inside it puts in force again.</summary>
      public int Handlers { get; } = handlers;

      /// <summary>The label a <c>continue</c> of a loop or a <c>break</c> of a switch jumps to, made when the first one needs it.</summary>
      public string? Label { get; set; }

    }

    private readonly Dictionary<IrLocal, string> _locals = [];
    private readonly HashSet<string> _localNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VbVariable> _temps = [];
    private readonly Stack<Frame> _frames = new();
    private List<VbStatement> _out = [];
    private string _procedure = "";
    private bool _isFunction;

    /// <summary>Starts the names and the state of a procedure.</summary>
    private void BeginFunction(string name, IEnumerable<string> taken) {
      this._function = name;
      this._locals.Clear();
      this._localNames.Clear();
      this._localNames.UnionWith(this._moduleNames);
      this._localNames.UnionWith(taken);
      this._temps.Clear();
      this._held.Clear();
      this._frames.Clear();
      this._handlers.Clear();
      this._caught.Clear();
      this._out = [];
    }

    /// <summary>The name of a local or parameter, the first time it is asked for one that no other name of the procedure has.</summary>
    private string LocalName(IrLocal local) {
      if (this._locals.TryGetValue(local, out var known))
        return known;

      var name = VisualBasicNaming.Free(VisualBasicNaming.Valid(local.Name, "value"), this._localNames.Contains);
      this._localNames.Add(name);
      this._locals[local] = name;
      return name;
    }

    /// <summary>A new variable of the procedure for an intermediate value, declared at its top.</summary>
    private string Temporary(IrType type, string stem = "tmp") {
      var name = VisualBasicNaming.Free(stem, this._localNames.Contains);
      this._localNames.Add(name);
      this._temps.Add(this.Variable(name, type));
      return name;
    }

    /// <summary>A label of the procedure no other name has.</summary>
    private string Label(string stem) {
      var name = VisualBasicNaming.Free(stem + "1", this._localNames.Contains);
      this._localNames.Add(name);
      return name;
    }

    /// <summary>Runs an action with the statements it writes collected in a list.</summary>
    private void Into(List<VbStatement> statements, Action action) {
      var saved = this._out;
      this._out = statements;
      try {
        action();
      } finally {
        this._out = saved;
      }
    }

    /// <summary>The statements of a block, written into a list of their own.</summary>
    private List<VbStatement> Statements(IrBlock block, bool last = false) {
      var statements = new List<VbStatement>();
      this.Into(statements, () => this.Block(block, last));
      return statements;
    }

    /// <summary>An expression written with the statements it needs first, which go into a list of their own.</summary>
    private (List<VbStatement> Statements, VbExpression Value) Isolated(IrExpr expression) {
      var statements = new List<VbStatement>();
      VbExpression value = new VbInteger(0);
      this.Into(statements, () => value = this.Expression(expression));
      return (statements, value);
    }

    /// <summary>
    /// A procedure: its parameters (a scalar <c>ByVal</c>; an array and a user-defined type by
    /// reference, as Visual Basic requires - a record the body changes is copied into a local first, so
    /// that the caller's stays as the IR leaves it), its locals declared at its top, and its body.
    /// </summary>
    private VbProcedure Function(IrFunction function, bool callsInit) {
      this.BeginFunction(function.Name, []);
      this._procedure = this._functions[function.Name];
      this._isFunction = function.ReturnType != IrType.Void;
      if (function.ReturnType.IsAsync)
        this.ControlRefused("async function");

      var changed = Changed(function.Body);
      var parameters = new List<VbParameter>();
      var prologue = new List<VbStatement>();
      var copies = new List<VbVariable>();
      foreach (var parameter in function.Parameters) {
        var name = this.LocalName(parameter);
        var type = this.TypeName(parameter.Type);
        if (parameter.Type.IsArray) {
          if (changed.Contains(parameter))
            this.Refuse(InexpressibleDescriptor, $"the array parameter '{parameter.Name}' is assigned, which a Visual Basic array passed by reference would carry back to the caller");
          parameters.Add(new(false, name, type) { IsArray = true });
        } else if (parameter.Type.IsStruct && changed.Contains(parameter)) {
          var given = VisualBasicNaming.Free(name + "In", this._localNames.Contains);
          this._localNames.Add(given);
          parameters.Add(new(false, given, type));
          copies.Add(new(name, type));
          prologue.Add(new VbAssign(new VbName(name), new VbName(given)));
        } else {
          parameters.Add(new(!parameter.Type.IsStruct, name, type));
        }
      }

      foreach (var local in function.Locals)
        this.LocalName(local);

      var body = new List<VbStatement>();
      this.Into(body, () => this.Block(function.Body, true));
      var declarations = new List<VbStatement>();
      declarations.AddRange(copies.Select(copy => new VbDim([copy])));
      declarations.AddRange(function.Locals.Where(local => !function.Parameters.Contains(local)).Distinct().Select(local => new VbDim([this.Variable(this._locals[local], local.Type)])));
      declarations.AddRange(this._temps.Select(temp => new VbDim([temp])));
      if (callsInit)
        declarations.Add(new VbIf([new(new VbUnary("Not", new VbName(ReadyName)), [new VbCallStatement(new VbName(InitName), [])])], null));

      // An array is returned in a Variant: LibreOffice Basic, the oracle, has no "As T()" result, and a
      // Variant that holds a typed array assigns to a variable of that array type in every dialect.
      var returns = !this._isFunction ? null : function.ReturnType.IsArray ? this.ArrayResult(function.ReturnType) : this.TypeName(function.ReturnType);
      return new VbProcedure(function.IsPublic, this._procedure, parameters, returns, [.. declarations, .. prologue, .. body]);
    }

    /// <summary>The result type of a function that returns an array: <c>Variant</c>, after checking the array type.</summary>
    private VbTypeName ArrayResult(IrType type) {
      this.TypeName(type);
      return new("Variant");
    }

    /// <summary>The locals a body assigns or changes a field of, and the targets of its steps.</summary>
    private static HashSet<IrLocal> Changed(IrBlock body) {
      var changed = new HashSet<IrLocal>();
      foreach (var statement in AllStatements(body)) {
        if (statement is IrAssign assign)
          changed.Add(assign.Local);
        else if (statement is IrFieldStore { Target: var target } && Root(target) is { } root)
          changed.Add(root);
      }

      foreach (var step in IrTree.Nodes(body).OfType<IrStep>())
        if (step.Target is IrLocalRef { Local: var stepped })
          changed.Add(stepped);

      return changed;
    }

    /// <summary>A statement and every statement of its blocks, in pre-order.</summary>
    private static IEnumerable<IrStmt> AllStatements(IrStmt statement) {
      yield return statement;
      var blocks = statement is IrBlock block ? [block] : IrTree.Blocks(statement);
      foreach (var inner in blocks)
        foreach (var nested in inner.Statements)
          foreach (var each in AllStatements(nested))
            yield return each;
    }

    /// <summary>The local a chain of field loads starts from, or <see langword="null"/>.</summary>
    private static IrLocal? Root(IrExpr expression) => expression switch {
      IrLocalRef reference => reference.Local,
      IrFieldLoad load => Root(load.Target),
      _ => null
    };

    private void Block(IrBlock block, bool last) {
      for (var index = 0; index < block.Statements.Count; ++index)
        this.Statement(block.Statements[index], last && index == block.Statements.Count - 1);
    }

    private void Statement(IrStmt statement, bool last) {
      switch (statement) {
        case IrBlock block:
          this.Block(block, last);
          break;
        case IrDeclare { Initializer: { } initializer } declare:
          this.Assign(declare.Local, initializer);
          break;
        case IrDeclare:
          break;
        case IrAssign assign:
          this.Assign(assign.Local, assign.Value);
          break;
        case IrGlobalAssign assign:
          this._out.Add(new VbAssign(new VbName(this._globals[assign.Global.Name]), this.Expression(assign.Value)));
          break;
        case IrStore store:
          this.Store(store);
          break;
        case IrFieldStore store:
          this.FieldStore(store);
          break;
        case IrExprStmt { Expression: IrCall call }:
          this._out.Add(new VbCallStatement(new VbName(this.FunctionName(call.Function)), this.Arguments(call)));
          break;
        case IrExprStmt { Expression: IrStep step }:
          this.Step(step, false);
          break;
        case IrExprStmt { Expression: IrIntrinsic { Type.Kind: IrTypeKind.Void } intrinsic }:
          this.VoidIntrinsic(intrinsic);
          break;
        case IrExprStmt { Expression: var expression }:
          var value = this.Expression(expression);
          if (!Pure(expression))
            this._out.Add(new VbAssign(new VbName(this.Temporary(expression.Type, "discard")), value));
          break;
        case IrWriteLine line:
          this._out.Add(new VbCallStatement(new VbMember(new VbName("Debug"), "Print"), [this.Expression(line.Value)]));
          break;
        case IrIf conditional:
          this.If(conditional);
          break;
        case IrWhile loop:
          this.Loop(loop.Condition, true, loop.Body, null);
          break;
        case IrDoWhile loop:
          this.Loop(loop.Condition, false, loop.Body, null);
          break;
        case IrFor loop:
          this.Loop(loop.Condition, true, loop.Body, loop.Step);
          break;
        case IrSwitch choice:
          this.Switch(choice);
          break;
        case IrBreak:
          this.Break();
          break;
        case IrContinue:
          this.Continue();
          break;
        case IrReturn ret:
          this.Return(ret, last);
          break;
        case IrTry attempt:
          this.Try(attempt);
          break;
        case IrThrow thrown:
          this.Throw(thrown.Exception);
          break;
        case IrRethrow:
          this.Rethrow();
          break;
        default:
          this.ControlRefused(statement.GetType().Name[2..]);
          break;
      }
    }

    /// <summary>
    /// An assignment of a local. An array variable assigned from another names the same array in the
    /// IR but a copy in Visual Basic, whose array assignment copies, so that is refused.
    /// </summary>
    private void Assign(IrLocal local, IrExpr value) {
      if (local.Type.IsArray && value is IrLocalRef or IrGlobalRef or IrFieldLoad or IrConstRef or IrConditional)
        this.Refuse(InexpressibleDescriptor, $"the array '{local.Name}' would be a copy of the array assigned to it in Visual Basic, where the IR shares it");

      var written = this.Expression(value);
      this._out.Add(new VbAssign(new VbName(this.LocalName(local)), written));
    }

    /// <summary>A variable, a field of one or an element of one: what an assignment can write; <see langword="null"/> for anything else.</summary>
    private VbExpression? Place(IrExpr expression) {
      switch (expression) {
        case IrLocalRef reference:
          return new VbName(this.LocalName(reference.Local));
        case IrGlobalRef global:
          return new VbName(this._globals[global.Global.Name]);
        case IrFieldLoad load when this.Place(load.Target) is { } target:
          return new VbMember(target, this._fieldNames.GetValueOrDefault(load.Field, load.Field.Name));
        case IrArrayLoad load when this.Place(load.Array) is { } array:
          return new VbCall(array, [this.Stable(load.Index)]);
        default:
          return null;
      }
    }

    private void Store(IrStore store) {
      if (this.Place(store.Array) is not { } array) {
        this.Refuse(InexpressibleDescriptor, "a store into an array that is no variable");
        return;
      }

      var index = this.Stable(store.Index);
      var value = this.Expression(store.Value);
      this._out.Add(new VbAssign(new VbCall(array, [index]), value));
    }

    private void FieldStore(IrFieldStore store) {
      if (this.Place(store.Target) is not { } target) {
        this.Refuse(InexpressibleDescriptor, "a store into a field of a record that is no variable");
        return;
      }

      var value = this.Expression(store.Value);
      this._out.Add(new VbAssign(new VbMember(target, this._fieldNames.GetValueOrDefault(store.Field, store.Field.Name)), value));
    }

    private void If(IrIf conditional) {
      var branches = new List<VbIfBranch> { new(this.Expression(conditional.Condition), this.Statements(conditional.Then)) };
      var otherwise = conditional.Else;
      while (otherwise is { Statements: [IrIf next] }) {
        var (statements, condition) = this.Isolated(next.Condition);
        if (statements.Count > 0)
          break;

        branches.Add(new(condition, this.Statements(next.Then)));
        otherwise = next.Else;
      }

      var rest = otherwise is { Statements.Count: > 0 } ? this.Statements(otherwise) : null;
      this._out.Add(new VbIf(branches, rest));
    }

    /// <summary>
    /// A loop as a <c>Do</c> loop: the test before every pass (<paramref name="pretest"/>) or after it,
    /// a step after the pass that a <c>continue</c> also reaches. A test that needs statements of its
    /// own is written inside the loop, ending it with <c>Exit Do</c>.
    /// </summary>
    private void Loop(IrExpr condition, bool pretest, IrBlock body, IrBlock? step) {
      var frame = new Frame(true, this._handlers.Count);
      var (testStatements, test) = this.Isolated(condition);
      this._frames.Push(frame);
      var pass = this.Statements(body);
      this._frames.Pop();
      var after = step is null ? [] : this.Statements(step);
      var inline = testStatements.Count == 0 && !(test is VbBoolean { Value: true });
      var statements = new List<VbStatement>();
      VbLoopTest? pre = null;
      VbLoopTest? post = null;
      var exit = testStatements.Count > 0 ? new VbIf([new(Negate(test), [new VbExit("Do")])], null) : null;
      if (pretest) {
        if (inline)
          pre = new(false, test);
        else if (exit is not null)
          statements.AddRange([.. testStatements, exit]);
        statements.AddRange(pass);
        if (frame.Label is { } label)
          statements.Add(new VbLabel(label));
        statements.AddRange(after);
      } else {
        statements.AddRange(pass);
        if (frame.Label is { } label)
          statements.Add(new VbLabel(label));
        if (inline)
          post = new(false, test);
        else if (exit is not null)
          statements.AddRange([.. testStatements, exit]);
      }

      this._out.Add(new VbDoLoop(pre, post, statements));
    }

    /// <summary>The negation of a condition, without a double <c>Not</c>.</summary>
    private static VbExpression Negate(VbExpression condition) => condition is VbUnary { Operator: "Not" } not ? not.Operand : new VbUnary("Not", condition);

    private void Switch(IrSwitch choice) {
      var value = this.Expression(choice.Value);
      var frame = new Frame(false, this._handlers.Count);
      this._frames.Push(frame);
      var cases = new List<VbCase>();
      List<VbStatement>? otherwise = null;
      foreach (var section in choice.Sections) {
        var body = this.Statements(WithoutTrailingBreak(section.Body));
        if (section.IsDefault)
          otherwise = body;
        else
          cases.Add(new([.. section.Labels.Select(label => new VbCaseClause(this.Literal(choice.Value.Type, label)))], body));
      }

      this._frames.Pop();
      this._out.Add(new VbSelect(value, cases, otherwise));
      if (frame.Label is { } label)
        this._out.Add(new VbLabel(label));
    }

    /// <summary>A section's statements without the <c>break</c> that ends it, which Visual Basic's <c>Case</c> does not need.</summary>
    private static IrBlock WithoutTrailingBreak(IrBlock block) {
      if (block.Statements.Count == 0)
        return block;

      return block.Statements[^1] switch {
        IrBreak => new IrBlock([.. block.Statements.Take(block.Statements.Count - 1)]),
        IrBlock inner => new IrBlock([.. block.Statements.Take(block.Statements.Count - 1), WithoutTrailingBreak(inner)]),
        _ => block
      };
    }

    private void Break() {
      if (this._frames.Count == 0) {
        this.ControlRefused("break outside a loop");
        return;
      }

      var frame = this._frames.Peek();
      this.LeaveTries(frame.Handlers);
      if (frame.IsLoop) {
        this._out.Add(new VbExit("Do"));
        return;
      }

      frame.Label ??= this.Label("EndSelect");
      this._out.Add(new VbGoTo(frame.Label));
    }

    private void Continue() {
      var frame = this._frames.FirstOrDefault(candidate => candidate.IsLoop);
      if (frame is null) {
        this.ControlRefused("continue outside a loop");
        return;
      }

      this.LeaveTries(frame.Handlers);
      frame.Label ??= this.Label("Continue");
      this._out.Add(new VbGoTo(frame.Label));
    }

    private void Return(IrReturn ret, bool last) {
      if (ret.Value is { } value)
        this._out.Add(new VbAssign(new VbName(this._procedure), this.Expression(value)));
      if (!last)
        this._out.Add(new VbExit(this._isFunction ? "Function" : "Sub"));
    }

    /// <summary>
    /// A step by one: the target read, one added or subtracted in its type, the result stored. As an
    /// expression it is the value before (<see cref="IrStep.Postfix"/>) or after, held in a temporary.
    /// </summary>
    private VbExpression Step(IrStep step, bool value) {
      IrExpr target = step.Target;
      if (step.Target is IrArrayLoad load) {
        var index = this.Temporary(IrType.I32, "index");
        this._out.Add(new VbAssign(new VbName(index), this.Expression(load.Index)));
        target = load with { Index = new IrLocalRef(new IrLocal(index, IrType.I32)) };
        this._locals.TryAdd(new IrLocal(index, IrType.I32), index);
      }

      var place = this.Place(target);
      if (place is null)
        return this.Inexpressible("a step of something that is no variable");

      string? held = null;
      if (value && step.Postfix) {
        held = this.Temporary(step.Type, "before");
        this._out.Add(new VbAssign(new VbName(held), place));
      }

      this._out.Add(new VbAssign(place, this.Expression(Stepped(target, step))));
      if (value && !step.Postfix) {
        held = this.Temporary(step.Type, "after");
        this._out.Add(new VbAssign(new VbName(held), place));
      }

      return held is null ? new VbInteger(0) : new VbName(held);
    }

    /// <summary>The IR of the stepped value: the target plus or minus one in its type, wrapping or trapping as the step says.</summary>
    private static IrExpr Stepped(IrExpr target, IrStep step) {
      var type = step.Type;
      if (type.IsFloat)
        return new IrBinary(step.Increment ? IrBinaryOp.Add : IrBinaryOp.Subtract, target, new IrConst(type, IrOperators.Bits(type, 1.0)));

      var op = step.Increment ? step.Trap ? IrBinaryOp.AddChecked : IrBinaryOp.Add : step.Trap ? IrBinaryOp.SubtractChecked : IrBinaryOp.Subtract;
      if (type.IsWideInteger)
        return new IrBinary(op, target, new IrConst(type, 1));

      // A narrow integer steps in 32 bits and converts back, wrapping or trapping.
      var widened = new IrConvert(IrType.I32, target);
      return new IrConvert(type, new IrBinary(op, widened, new IrConst(IrType.I32, 1)), step.Trap ? IrConversion.Trap : IrConversion.Wrap);
    }

    /// <summary>An intrinsic without a result as a statement: the copy and the fill of an array.</summary>
    private void VoidIntrinsic(IrIntrinsic intrinsic) {
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayCopy:
          var copy = this.Operands(intrinsic.Arguments, [0, 2]);
          this._out.Add(new VbCallStatement(new VbName(this.ArrayHelper(ArrayHelpers.Copy, intrinsic.Arguments[0].Type)), copy));
          break;
        case IrIntrinsicOp.ArrayFill:
          var fill = this.Operands(intrinsic.Arguments, [0]);
          this._out.Add(new VbCallStatement(new VbName(this.ArrayHelper(ArrayHelpers.Fill, intrinsic.Arguments[0].Type)), fill));
          break;
        case IrIntrinsicOp.ArrayClear:
          var array = this.Operands(intrinsic.Arguments, [0]);
          var element = intrinsic.Arguments[0].Type.Element!;
          this._out.Add(new VbCallStatement(new VbName(this.ArrayHelper(ArrayHelpers.Fill, intrinsic.Arguments[0].Type)), [array[0], this.Literal(element, 0)]));
          break;
        default:
          this.Inexpressible(intrinsic.Op.ToString());
          break;
      }
    }

  }

}
