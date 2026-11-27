using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Go.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Go;

public static partial class GoEmitter {

  private sealed partial class Writer {

    private readonly Dictionary<IrLocal, string> _locals = [];
    private readonly HashSet<string> _localNames = new(StringComparer.Ordinal);
    private readonly HashSet<IrLocal> _read = [];
    private readonly List<HashSet<IrLocal>> _scopes = [];
    private IrType _result = IrType.Void;

    /// <summary>Starts a function, or the initializers of the package for <see langword="null"/>: no locals yet.</summary>
    private void BeginFunction(IrFunction? function) {
      this._function = function?.Name ?? "the package";
      this._locals.Clear();
      this._localNames.Clear();
      this._read.Clear();
      this._scopes.Clear();
      this._prelude = [];
      this._result = function?.ReturnType ?? IrType.Void;
      this._referenced.Clear();
      this._referenced.UnionWith(this._types.Values);
      if (function is null) {
        this._referenced.UnionWith(this._packageNames);
        return;
      }

      foreach (var node in IrTree.Nodes(function.Body))
        switch (node) {
          case IrCall call when this._functions.TryGetValue(call.Function, out var called):
            this._referenced.Add(called);
            break;
          case IrConstRef reference:
            this._referenced.Add(this._constants[reference.Constant.Name]);
            break;
          case IrGlobalRef global:
            this._referenced.Add(this._globals[global.Global]);
            break;
        }

      foreach (var statement in IrTreeStatements(function.Body))
        if (statement is IrGlobalAssign assignment)
          this._referenced.Add(this._globals[assignment.Global]);

      foreach (var node in IrTree.Nodes(function.Body))
        if (node is IrLocalRef reference)
          this._read.Add(reference.Local);
    }

    /// <summary>The Go name of a parameter or local, the same for the whole function, no name of the package and no other local's.</summary>
    private string LocalName(IrLocal local) {
      if (this._locals.TryGetValue(local, out var name))
        return name;

      name = GoNaming.Free(Unhelper(GoNaming.Local(local.Name)), this.Taken);
      this._localNames.Add(name);
      this._locals[local] = name;
      return name;
    }

    /// <summary>
    /// The names of the package a function names - the functions it calls, the constants, globals and
    /// types it reads - which none of its locals may take; any other name of the package a local may
    /// shadow, as Go allows.
    /// </summary>
    private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);

    /// <summary>Whether a local may not take a name.</summary>
    private bool Taken(string candidate)
      => this._localNames.Contains(candidate) || this._referenced.Contains(candidate) || GoNaming.Keywords.Contains(candidate) || GoNaming.Reserved.Contains(candidate) || candidate == package;

    /// <summary>A name for a value the writer keeps itself.</summary>
    private string Temporary() {
      var name = GoNaming.Free("t", this.Taken);
      this._localNames.Add(name);
      return name;
    }

    /// <summary>
    /// A function with its parameters grouped by type; a trailing <c>return</c> of a function without
    /// result is left out, and one with a result that Go does not see end gets <c>panic("unreachable")</c>.
    /// </summary>
    private GoFuncDecl Function(IrFunction function) {
      this.BeginFunction(function);
      if (function.IsAsync)
        this.Refuse(FunctionUnsupportedDescriptor, "function feature 'async' cannot be emitted as Go");

      var parameters = new List<GoField>();
      foreach (var parameter in function.Parameters) {
        var name = this.LocalName(parameter);
        var type = this.TypeOf(parameter.Type);
        if (parameters.Count > 0 && GoSyntaxRewriter.Shape(parameters[^1].FieldType) == GoSyntaxRewriter.Shape(type))
          parameters[^1] = parameters[^1] with { Names = [.. parameters[^1].Names, name] };
        else
          parameters.Add(new GoField([name], type));
      }

      foreach (var local in function.Locals)
        this.LocalName(local);

      IReadOnlyList<GoField> results = function.ReturnType == IrType.Void ? [] : [new GoField([], this.TypeOf(function.ReturnType))];
      this._scopes.Add([.. function.Parameters]);
      var body = new List<GoStmt>();
      // A local the body reads without declaring it is declared at the top, with its zero value.
      var declared = IrTreeDeclarations(function.Body);
      foreach (var local in function.Locals.Where(local => !declared.Contains(local) && this._read.Contains(local) && !function.Parameters.Contains(local))) {
        body.Add(new GoVarStmt(new GoValueSpec([this.LocalName(local)], this.TypeOf(local.Type), [])));
        this._scopes[^1].Add(local);
      }

      body.AddRange(this.Statements(function.Body.Statements));
      this._scopes.Clear();
      if (function.ReturnType == IrType.Void && body.Count > 0 && body[^1] is GoReturn { Results.Count: 0 })
        body.RemoveAt(body.Count - 1);

      if (function.ReturnType != IrType.Void && !Terminates(body))
        body.Add(new GoExprStmt(Call(Name("panic"), new GoString("unreachable"))));

      var goName = this._functions.TryGetValue(function.Name, out var known) ? known : GoNaming.Function(function.Name, false);
      return new GoFuncDecl(goName, new GoFuncType(parameters, results), new GoBlock(body));
    }

    /// <summary>Every statement of a body, the body first.</summary>
    private static IEnumerable<IrStmt> IrTreeStatements(IrStmt statement) {
      yield return statement;
      var nested = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).Cast<IrStmt>();
      foreach (var inner in nested)
        foreach (var descendant in IrTreeStatements(inner))
          yield return descendant;
    }

    /// <summary>The locals a body declares.</summary>
    private static HashSet<IrLocal> IrTreeDeclarations(IrBlock body) {
      var declared = new HashSet<IrLocal>();
      void Walk(IrStmt statement) {
        if (statement is IrDeclare declaration)
          declared.Add(declaration.Local);

        if (statement is IrBlock block) {
          foreach (var inner in block.Statements)
            Walk(inner);

          return;
        }

        foreach (var nested in IrTree.Blocks(statement))
          Walk(nested);
      }

      Walk(body);
      return declared;
    }

    /// <summary>The statements of a block in a scope of their own.</summary>
    private List<GoStmt> Statements(IReadOnlyList<IrStmt> statements) {
      this._scopes.Add([]);
      var output = new List<GoStmt>();
      for (var index = 0; index < statements.Count; ++index) {
        if (index + 1 < statements.Count && this.Counted(statements, index, output)) {
          ++index;
          continue;
        }

        this.Statement(statements[index], output);
      }

      this._scopes.RemoveAt(this._scopes.Count - 1);
      return output;
    }

    /// <summary>Writes one statement with the prelude its expressions need in front of it.</summary>
    private void Statement(IrStmt statement, List<GoStmt> output) {
      var saved = this._prelude;
      this._prelude = [];
      try {
        var written = this.Produce(statement);
        output.AddRange(this._prelude);
        output.AddRange(written);
      } finally {
        this._prelude = saved;
      }
    }

    private bool IsDeclared(IrLocal local) => this._scopes.Any(scope => scope.Contains(local));

    private IEnumerable<GoStmt> Produce(IrStmt statement) {
      switch (statement) {
        case IrBlock block: {
          // A block that declares nothing is no scope Go needs: its statements stand in the enclosing one.
          var inner = this.Statements(block.Statements);
          return Declares(inner) ? [new GoBlockStmt(new GoBlock(inner))] : inner;
        }
        case IrDeclare declaration:
          return this.Declare(declaration);
        case IrAssign assignment:
          return this.Assign(assignment.Local, assignment.Value);
        case IrGlobalAssign assignment:
          return [new GoAssign([Name(this._globals[assignment.Global])], "=", [this.Expression(assignment.Value)])];
        case IrStore store:
          return this.Store(store);
        case IrFieldStore store: {
          var target = this.Expression(store.Target);
          var value = this.Expression(store.Value);
          return [new GoAssign([new GoSelector(target, this.FieldName(store.Field))], "=", [value])];
        }
        case IrExprStmt expression:
          return this.Effect(expression.Expression);
        case IrWriteLine line:
          return [new GoExprStmt(Call(Member("fmt", "Println"), this.Expression(line.Value)))];
        case IrIf conditional:
          return this.If(conditional);
        case IrWhile loop:
          return this.While(loop.Condition, loop.Body, null);
        case IrFor loop:
          return this.While(loop.Condition, loop.Body, loop.Step);
        case IrDoWhile loop:
          return this.DoWhile(loop);
        case IrSwitch choice:
          return [this.Switch(choice)];
        case IrBreak:
          return [new GoBranch("break")];
        case IrContinue:
          return [new GoBranch("continue")];
        case IrReturn { Value: null }:
          return [new GoReturn([])];
        case IrReturn { Value: IrConditional choice } when this._prelude.Count == 0:
          return this.ReturnChoice(choice);
        case IrReturn result:
          return [new GoReturn([this.Expression(result.Value)])];
        case IrThrow thrown:
          return [this.Panic(thrown.Exception)];
        default:
          this.Refuse(ControlUnsupportedDescriptor, $"control-flow construct '{KindOf(statement)}' cannot be emitted as Go");
          return [];
      }
    }

    /// <summary>A declaration: <c>var x T</c> for none or a constant, <c>x := value</c> otherwise; nothing but the effects of the value for a local nothing reads.</summary>
    private IEnumerable<GoStmt> Declare(IrDeclare declaration) {
      var local = declaration.Local;
      if (!this._read.Contains(local))
        return declaration.Initializer is { } dropped ? this.Effect(dropped) : [];

      // A local declared again in a scope that already has it is assigned: Go allows one declaration of a name per scope.
      if (this.IsDeclared(local) && this._scopes[^1].Contains(local))
        return declaration.Initializer is { } again ? this.Assign(local, again) : [new GoAssign([Name(this.LocalName(local))], "=", [this.Zero(local.Type)])];

      this._scopes[^1].Add(local);
      var name = this.LocalName(local);
      if (declaration.Initializer is not { } initializer)
        return [new GoVarStmt(new GoValueSpec([name], this.TypeOf(local.Type), []))];

      if (initializer is IrConditional choice) {
        var choiceStatements = this.AssignChoice(Name(name), choice);
        return [new GoVarStmt(new GoValueSpec([name], this.TypeOf(local.Type), [])), .. choiceStatements];
      }

      var value = this.Expression(initializer);
      if (IsUntyped(value) || value is GoIdent { Name: "true" or "false" } || initializer is IrConstRef or IrStrConst)
        return [new GoVarStmt(new GoValueSpec([name], this.TypeOf(local.Type), IsZero(initializer) ? [] : [value]))];

      return [new GoAssign([Name(name)], ":=", [value])];
    }

    private static bool IsZero(IrExpr expression) => expression is IrConst { Value: 0 } constant && !(constant.Type.IsFloat && constant.Value != 0);

    /// <summary>The zero value of a type.</summary>
    private GoExpr Zero(IrType type) => type switch {
      { Kind: IrTypeKind.Bool } => Name("false"),
      { IsNumeric: true } => new GoInt(0),
      { IsString: true } => new GoString(""),
      { IsStruct: true } => new GoComposite(this.TypeOf(type), []),
      _ => Name("nil")
    };

    /// <summary>An assignment: <c>x++</c>, <c>x += y</c> where the value is an operator on the variable itself, <c>x = value</c> otherwise.</summary>
    private IEnumerable<GoStmt> Assign(IrLocal local, IrExpr value) {
      if (!this._read.Contains(local))
        return this.Effect(value);

      var target = Name(this.LocalName(local));
      if (value is IrConditional choice)
        return this.AssignChoice(target, choice);

      if (value is IrBinary { Left: IrLocalRef reference } binary && reference.Local == local && Compound(binary) is { } op) {
        if (op is "+" or "-" && binary.Right is IrConst { Value: 1 } && !binary.Type.IsFloat)
          return [new GoIncDec(target, op == "+")];

        var right = this.Expression(binary.Right);
        if (binary.Op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical) {
          var mask = binary.Type.IsLong ? 63 : 31;
          right = binary.Right is IrConst constant ? new GoInt(constant.Value & mask) : new GoBinary(right, "&", new GoInt(mask));
        }

        return [new GoAssign([target], op + "=", [right])];
      }

      return [new GoAssign([target], "=", [this.Expression(value)])];
    }

    /// <summary>The operator of an assignment <c>x op= y</c> for a binary node, where Go's operator is the IR's.</summary>
    private static string? Compound(IrBinary binary) {
      var type = binary.Type;
      if (!type.IsArithmetic)
        return null;

      return binary.Op switch {
        IrBinaryOp.Add => "+",
        IrBinaryOp.Subtract => "-",
        IrBinaryOp.Multiply when !type.IsFloat || binary.Right is not IrBinary => "*",
        IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor when type.IsInteger => OperatorOf(binary.Op),
        IrBinaryOp.ShiftLeft when type.IsInteger => "<<",
        IrBinaryOp.ShiftRight when type.IsInteger => ">>",
        IrBinaryOp.Divide or IrBinaryOp.Remainder when !type.IsSigned && type.IsInteger && binary.Right is not IrConst { Value: 0 } => OperatorOf(binary.Op),
        IrBinaryOp.Divide or IrBinaryOp.Remainder when type.IsSigned && binary.Right is IrConst { Value: not 0 and not -1 } => OperatorOf(binary.Op),
        IrBinaryOp.ShiftRightLogical when type.IsInteger && !type.IsSigned => ">>",
        IrBinaryOp.Divide when type.IsFloat => "/",
        _ => null
      } is { } op && !(type.IsFloat && binary.Right is IrBinary { Op: IrBinaryOp.Multiply }) ? op : null;
    }

    /// <summary>The conditional assigned to a place: the place assigned in the arm that runs.</summary>
    private IEnumerable<GoStmt> AssignChoice(GoExpr target, IrConditional choice) {
      var condition = this.Expression(choice.Condition);
      var (whenTrue, truePrelude) = this.Capture(choice.WhenTrue);
      var (whenFalse, falsePrelude) = this.Capture(choice.WhenFalse);
      return [new GoIf(null, condition,
        new GoBlock([.. truePrelude, new GoAssign([target], "=", [whenTrue])]),
        new GoBlockStmt(new GoBlock([.. falsePrelude, new GoAssign([target], "=", [whenFalse])])))];
    }

    /// <summary><c>return c ? a : b</c> as <c>if c { return a }</c> and <c>return b</c>.</summary>
    private IEnumerable<GoStmt> ReturnChoice(IrConditional choice) {
      var condition = this.Expression(choice.Condition);
      var (whenTrue, truePrelude) = this.Capture(choice.WhenTrue);
      var (whenFalse, falsePrelude) = this.Capture(choice.WhenFalse);
      return [new GoIf(null, condition, new GoBlock([.. truePrelude, new GoReturn([whenTrue])]), null), .. falsePrelude, new GoReturn([whenFalse])];
    }

    /// <summary>A store into an array element: <c>a[i] = v</c>, <c>a[i]++</c> or <c>a[i] += y</c> where the value is an operator on the same element.</summary>
    private IEnumerable<GoStmt> Store(IrStore store) {
      if (!store.Array.Type.IsArray) {
        this.Inexpressible($"Store into {store.Array.Type}");
        return [];
      }

      if (store.Value is IrBinary { Left: IrArrayLoad element } binary && element.Array is IrLocalRef or IrConstRef or IrGlobalRef
        && element.Index is IrLocalRef or IrConst && element.Array == store.Array && element.Index == store.Index && Compound(binary) is { } op
        && binary.Op is not IrBinaryOp.ShiftLeft and not IrBinaryOp.ShiftRight and not IrBinaryOp.ShiftRightLogical) {
        var place = (GoIndex)this.Load(element);
        if (op is "+" or "-" && binary.Right is IrConst { Value: 1 } && !binary.Type.IsFloat)
          return [new GoIncDec(place, op == "+")];

        return [new GoAssign([place], op + "=", [this.Expression(binary.Right)])];
      }

      var operands = this.Operands(store.Array, Unclamped(store.Index), store.Value);
      return [new GoAssign([new GoIndex(operands[0], operands[1])], "=", [operands[2]])];
    }

    /// <summary>
    /// An expression evaluated for its effect: a call or a step as a statement of its own, any other
    /// expression that may trap as an assignment to <c>_</c>, one that can neither trap nor change
    /// anything not at all (Go refuses an expression statement that is no call).
    /// </summary>
    private IEnumerable<GoStmt> Effect(IrExpr expression) {
      switch (expression) {
        case IrStep step when step.Target is IrLocalRef or IrGlobalRef or IrArrayLoad { Array.Type.IsArray: true }: {
          var target = step.Target is IrArrayLoad element ? this.Load(element) : this.Expression(step.Target);
          return [this.Stepped(step, target)];
        }
        case IrCall or IrThrowExpression:
          return [new GoExprStmt(this.Expression(expression))];
        case IrConst or IrStrConst or IrConstRef or IrLocalRef or IrGlobalRef:
          return [];
      }

      var value = this.Expression(expression);
      if (value is GoCall { Callee: GoIdent { Name: var name } } && this._byName.ContainsKey(name))
        return [new GoExprStmt(value)];

      return IsUntyped(value) || value is GoIdent ? [] : [new GoAssign([Name("_")], "=", [value])];
    }

    /// <summary>
    /// An <c>if</c>, with <c>else if</c> for an else branch that is one more <c>if</c> whose condition
    /// needs no statements; where the then branch cannot fall through, the else branch follows the
    /// <c>if</c> outdented, as Go is written, unless it declares a name the enclosing block could have.
    /// </summary>
    private IEnumerable<GoStmt> If(IrIf conditional) {
      var condition = this.Expression(conditional.Condition);
      var then = this.Statements(conditional.Then.Statements);
      if (conditional.Else is not { } otherwise)
        return [new GoIf(null, condition, new GoBlock(then), null)];

      if (Terminates(then)) {
        var outdented = this.Statements(otherwise.Statements);
        if (!Declares(outdented))
          return [new GoIf(null, condition, new GoBlock(then), null), .. outdented];
      }

      if (otherwise.Statements is [IrIf chained]) {
        var (_, prelude) = this.Capture(chained.Condition);
        if (prelude.Count == 0) {
          this._scopes.Add([]);
          var inner = this.If(chained).ToList();
          this._scopes.RemoveAt(this._scopes.Count - 1);
          if (inner is [GoIf elseIf])
            return [new GoIf(null, condition, new GoBlock(then), elseIf)];
        }
      }

      var elseStatements = this.Statements(otherwise.Statements);
      return [new GoIf(null, condition, new GoBlock(then), elseStatements.Count == 0 ? null : new GoBlockStmt(new GoBlock(elseStatements)))];
    }

    /// <summary>Whether statements declare a name in their own scope.</summary>
    private static bool Declares(IEnumerable<GoStmt> statements) => statements.Any(statement => statement is GoVarStmt or GoAssign { Op: ":=" });

    /// <summary>
    /// A loop that tests before every pass: <c>for c { … }</c>, <c>for { … }</c> for a condition that is
    /// always true; with a step <c>for ; c; step { … }</c>, or the step at the end of the body where no
    /// <c>continue</c> skips it. A condition that needs statements, or a step that is no simple statement
    /// while a <c>continue</c> would skip it, makes the loop <c>for first := true; ; first = false</c>
    /// with the step and the test at its top.
    /// </summary>
    private IEnumerable<GoStmt> While(IrExpr conditionIr, IrBlock bodyIr, IrBlock? stepIr) {
      var (condition, prelude) = this.Capture(conditionIr);
      var always = conditionIr is IrConst { Value: not 0 };
      var body = this.Statements(bodyIr.Statements);
      var step = stepIr is null ? [] : this.Statements(stepIr.Statements);
      var continues = Continues(bodyIr);
      if (prelude.Count == 0) {
        GoExpr? test = always ? null : condition;
        if (step.Count == 0)
          return [new GoFor(null, test, null, new GoBlock(body))];

        if (step is [GoIncDec or GoAssign { Op: not ":=" } or GoExprStmt] && test is not null)
          return [new GoFor(null, test, step[0], new GoBlock(body))];

        if (!continues)
          return [new GoFor(null, test, null, new GoBlock([.. body, .. step]))];
      }

      if (!continues && step.Count == 0)
        return [new GoFor(null, null, null, new GoBlock([.. prelude, .. this.Exit(condition, always), .. body]))];

      var first = Name(this.Temporary());
      var top = new List<GoStmt>();
      if (step.Count > 0)
        top.Add(new GoIf(null, new GoUnary("!", first), new GoBlock(step), null));

      top.AddRange(prelude);
      top.AddRange(this.Exit(condition, always));
      return [new GoFor(new GoAssign([first], ":=", [Name("true")]), null, new GoAssign([first], "=", [Name("false")]), new GoBlock([.. top, .. body]))];
    }

    /// <summary><c>if !c { break }</c>, or nothing for a condition that is always true.</summary>
    private IEnumerable<GoStmt> Exit(GoExpr condition, bool always)
      => always ? [] : [new GoIf(null, Not(condition), new GoBlock([new GoBranch("break")]), null)];

    /// <summary>
    /// A loop that runs its body before the first test: <c>for { body; if !c { break } }</c>, or, where a
    /// <c>continue</c> of the body has to reach the test, <c>for first := true; ; first = false</c> with
    /// the test at the top of every pass but the first.
    /// </summary>
    private IEnumerable<GoStmt> DoWhile(IrDoWhile loop) {
      var body = this.Statements(loop.Body.Statements);
      var (condition, prelude) = this.Capture(loop.Condition);
      var always = loop.Condition is IrConst { Value: not 0 };
      if (!Continues(loop.Body))
        return [new GoFor(null, null, null, new GoBlock([.. body, .. prelude, .. this.Exit(condition, always)]))];

      var first = Name(this.Temporary());
      var test = new GoIf(null, new GoUnary("!", first), new GoBlock([.. prelude, .. this.Exit(condition, always)]), null);
      return [new GoFor(new GoAssign([first], ":=", [Name("true")]), null, new GoAssign([first], "=", [Name("false")]), new GoBlock([test, .. body]))];
    }

    /// <summary>Whether a <c>continue</c> of the body, outside its inner loops, continues the loop.</summary>
    private static bool Continues(IrBlock body) {
      bool Walk(IrStmt statement) => statement switch {
        IrContinue => true,
        IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence => false,
        IrBlock block => block.Statements.Any(Walk),
        _ => IrTree.Blocks(statement).Any(Walk)
      };

      return body.Statements.Any(Walk);
    }

    /// <summary>
    /// A counted loop <c>for i := a; c; step { … }</c>: the declaration before an <c>IrFor</c> becomes its
    /// init clause when the condition needs no statements and the step is one simple statement.
    /// </summary>
    private bool Counted(IReadOnlyList<IrStmt> statements, int index, List<GoStmt> output) {
      var (local, initializer, declare) = statements[index] switch {
        IrDeclare { Initializer: { } value } declaration => (declaration.Local, value, true),
        IrAssign assignment => (assignment.Local, assignment.Value, false),
        _ => (null!, null!, false)
      };
      if (local is null || !this._read.Contains(local) || initializer is IrConditional)
        return false;

      // A declared counter moves into the clause only when nothing after the loop reads it.
      if (declare && statements.Skip(index + 2).SelectMany(IrTree.Nodes).Any(node => node is IrLocalRef reference && reference.Local == local))
        return false;

      if (!declare && !this.IsDeclared(local))
        return false;

      IrExpr condition;
      IReadOnlyList<IrStmt> body;
      IrStmt step;
      switch (statements[index + 1]) {
        case IrFor { Step.Statements: [var only] } loop:
          (condition, body, step) = (loop.Condition, loop.Body.Statements, only);
          break;
        case IrWhile { Body.Statements: [.., var trailing] } loop when Steps(trailing, local) && !Continues(loop.Body):
          (condition, body, step) = (loop.Condition, loop.Body.Statements.Take(loop.Body.Statements.Count - 1).ToList(), trailing);
          break;
        default:
          return false;
      }

      if (IrTree.Nodes(condition).Any(node => node is IrConditional or IrStep or IrBinary { Op: IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr }))
        return false;

      var saved = this._prelude;
      this._prelude = [];
      try {
        var value = this.Expression(initializer);
        var (_, conditionPrelude) = this.Capture(condition);
        if (this._prelude.Count > 0 || conditionPrelude.Count > 0)
          return false;

        this._scopes.Add(declare ? [local] : []);
        var name = Name(this.LocalName(local));
        var post = this.Statements([step]);
        if (post is not [GoIncDec or GoAssign { Op: not ":=" }] || this._prelude.Count > 0) {
          this._scopes.RemoveAt(this._scopes.Count - 1);
          return false;
        }

        var init = declare ? new GoAssign([name], ":=", [this.Typed(value, local.Type)]) : new GoAssign([name], "=", [value]);
        var test = this.Expression(condition);
        var written = this.Statements(body);
        this._scopes.RemoveAt(this._scopes.Count - 1);
        var always = condition is IrConst { Value: not 0 };
        output.Add(new GoFor(init, always ? null : test, post[0], new GoBlock(written)));
        return true;
      } finally {
        this._prelude = saved;
      }
    }

    /// <summary>Whether a statement steps a local: an assignment to it or a step of it.</summary>
    private static bool Steps(IrStmt statement, IrLocal local) => statement switch {
      IrAssign assignment => assignment.Local == local,
      IrExprStmt { Expression: IrStep { Target: IrLocalRef reference } } => reference.Local == local,
      _ => false
    };

    /// <summary>A switch on an integer: one clause per section, no <c>fallthrough</c>, the <c>break</c> that ends a section left out.</summary>
    private GoSwitch Switch(IrSwitch choice) {
      var value = this.Expression(choice.Value);
      var clauses = new List<GoCaseClause>();
      foreach (var section in choice.Sections) {
        var statements = section.Body.Statements;
        if (statements.Count > 0 && statements[^1] is IrBreak)
          statements = [.. statements.Take(statements.Count - 1)];

        var body = this.Statements(statements);
        IReadOnlyList<GoExpr>? labels = section.IsDefault ? null : [.. section.Labels.Select(label => this.Literal(choice.Value.Type, label))];
        clauses.Add(new GoCaseClause(labels, body));
      }

      return new GoSwitch(null, value, clauses);
    }

    /// <summary>
    /// Whether statements end in a terminating statement (The Go Programming Language Specification,
    /// "Terminating statements"), after which Go needs no <c>return</c>.
    /// </summary>
    private static bool Terminates(IReadOnlyList<GoStmt> statements) => statements.Count > 0 && Terminates(statements[^1]);

    private static bool Terminates(GoStmt statement) => statement switch {
      GoReturn => true,
      GoExprStmt { Expression: GoCall { Callee: GoIdent { Name: "panic" } } } => true,
      GoBlockStmt block => Terminates(block.Body.Statements),
      GoIf { Else: { } otherwise } conditional => Terminates(conditional.Then.Statements) && Terminates(otherwise),
      GoFor { Condition: null } loop => !Breaks(loop.Body.Statements, null),
      GoSwitch choice => choice.Clauses.Any(clause => clause.Values is null)
        && choice.Clauses.All(clause => Terminates(clause.Body) && !Breaks(clause.Body, null)),
      GoLabeled labeled => Terminates(labeled.Statement),
      _ => false
    };

    /// <summary>Whether statements hold a <c>break</c> of the statement they are in: one without a label outside inner loops and switches.</summary>
    private static bool Breaks(IEnumerable<GoStmt> statements, string? label) => statements.Any(statement => statement switch {
      GoBranch { Keyword: "break", Label: null } => true,
      GoBranch { Keyword: "break" } branch => branch.Label == label,
      GoBlockStmt block => Breaks(block.Body.Statements, label),
      GoIf conditional => Breaks(conditional.Then.Statements, label) || (conditional.Else is { } otherwise && Breaks([otherwise], label)),
      GoLabeled labeled => Breaks([labeled.Statement], label),
      _ => false
    });

  }

}
