using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Net;

internal sealed partial class VbNetLowerer {

  /// <summary>A statement the lowering is inside that a jump of Visual Basic leaves: a loop, a <c>Select Case</c> lowered to a switch, or one lowered to tests.</summary>
  /// <param name="Exit">The label after it.</param>
  /// <param name="Continue">The label of the next pass of a loop, or <see langword="null"/>.</param>
  /// <param name="Breakable">Whether the IR's <c>break</c> leaves it: a loop or a switch.</param>
  private sealed record Frame(ILabelSymbol? Exit, ILabelSymbol? Continue, bool Breakable);

  private readonly Dictionary<ISymbol, IrLocal> _locals = new(SymbolEqualityComparer.Default);
  private readonly List<IrLocal> _temporaries = [];
  private readonly Stack<Frame> _frames = new();
  private ILabelSymbol? _exit;
  private IMethodSymbol? _method;

  private void Begin(IMethodSymbol? method) {
    this._method = method;
    this._locals.Clear();
    this._temporaries.Clear();
    this._caught.Clear();
    this._frames.Clear();
    this._selectExits.Clear();
    this._hoisted.Clear();
    this._exit = null;
  }

  /// <summary>The local of a symbol; two symbols of one name - the variables of two catch clauses - are two locals of two names.</summary>
  private IrLocal Local(ISymbol symbol, ITypeSymbol type) {
    if (this._locals.TryGetValue(symbol, out var local))
      return local;

    var name = symbol.Name;
    for (var index = 2; this._locals.Values.Any(known => known.Name == name); ++index)
      name = symbol.Name + "_" + index.ToString(CultureInfo.InvariantCulture);
    return this._locals[symbol] = new IrLocal(name, this.Type(type, symbol));
  }

  /// <summary>The variables of the catch clauses, which their clause declares.</summary>
  private readonly HashSet<IrLocal> _caught = new(ReferenceEqualityComparer.Instance);

  /// <summary>A temporary of the procedure, declared before the statement that sets it.</summary>
  /// <remarks>In a state machine it is declared at the procedure's start and set where it stands, as a section can read what another sets.</remarks>
  private IrLocal Temporary(IrType type, List<IrStmt> statements, IrExpr value) {
    var local = new IrLocal("ply" + (this._temporaries.Count + 1).ToString(CultureInfo.InvariantCulture), type);
    this._temporaries.Add(local);
    if (this._flat) {
      this._hoisted.Add(new IrDeclare(local, Default(type)));
      statements.Add(new IrAssign(local, value));
    } else {
      statements.Add(new IrDeclare(local, value));
    }

    return local;
  }

  /// <summary>The declarations of the temporaries of a state machine.</summary>
  private readonly List<IrStmt> _hoisted = [];

  /// <summary>The statements of a block; what follows a statement control never leaves normally is dead, and left out.</summary>
  private void Block(IOperation operation, List<IrStmt> statements) {
    foreach (var inner in operation is IBlockOperation block ? [.. block.Operations] : new[] { operation }) {
      if (statements.Count > 0 && !Completes(statements[^1]))
        return;
      this.Statement(inner, statements);
    }
  }

  /// <summary>
  /// Whether control can leave a statement at its end: not after a return or a throw, nor after an
  /// <c>If</c> whose branches both end so, a try whose body and catches do, or a switch with a default
  /// whose sections all do and none breaks out.
  /// </summary>
  internal static bool Completes(IrStmt statement) => statement switch {
    IrReturn or IrThrow or IrRethrow => false,
    IrBlock block => block.Statements.Count == 0 || Completes(block.Statements[^1]),
    IrIf { Else: { } otherwise } branch => Completes(branch.Then) || Completes(otherwise),
    IrTry attempt => attempt.Finally is { } final && !Completes(final) ? false : Completes(attempt.Body) || attempt.Catches.Any(clause => Completes(clause.Body)),
    IrSwitch choice => !choice.Sections.Any(section => section.IsDefault) || choice.Sections.Any(section => Completes(section.Body) || Breaks(section.Body)),
    _ => true
  };

  /// <summary>Whether a block holds a break of the switch or loop it stands in, outside any loop or switch of its own.</summary>
  private static bool Breaks(IrStmt statement) => statement switch {
    IrBreak => true,
    IrBlock block => block.Statements.Any(Breaks),
    IrIf branch => Breaks(branch.Then) || branch.Else is { } otherwise && Breaks(otherwise),
    IrTry attempt => Breaks(attempt.Body) || attempt.Catches.Any(clause => Breaks(clause.Body)) || attempt.Finally is { } final && Breaks(final),
    _ => false
  };

  private IrBlock Body(IOperation? operation) {
    var statements = new List<IrStmt>();
    if (operation is not null)
      this.Block(operation, statements);
    return new IrBlock(statements);
  }

  private IrBlock Within(Frame frame, IOperation? operation) {
    this._frames.Push(frame);
    var body = this.Body(operation);
    this._frames.Pop();
    return body;
  }

  private void Statement(IOperation operation, List<IrStmt> statements) {
    switch (operation) {
      case IVariableDeclarationGroupOperation group:
        foreach (var declaration in group.Declarations)
          foreach (var declarator in declaration.Declarators) {
            var local = this.Local(declarator.Symbol, declarator.Symbol.Type);
            if (declarator.Symbol.IsStatic)
              this.Refuse(declarator.Syntax, "a Static local");
            if ((declarator.Initializer ?? declaration.Initializer) is { } initializer)
              statements.Add(new IrAssign(local, this.Value(initializer.Value)));
          }
        break;
      case IExpressionStatementOperation expression:
        this.Effect(expression.Operation, statements);
        break;
      case IConditionalOperation branch:
        statements.Add(new IrIf(this.Condition(branch.Condition), this.Body(branch.WhenTrue), branch.WhenFalse is null ? null : this.Body(branch.WhenFalse)));
        break;
      case IWhileLoopOperation loop:
        this.While(loop, statements);
        break;
      case IForToLoopOperation loop:
        this.ForTo(loop, statements);
        break;
      case IForEachLoopOperation loop:
        this.ForEach(loop, statements);
        break;
      case ISwitchOperation choice:
        this.Select(choice, statements);
        break;
      case IBranchOperation branch:
        this.Branch(branch, statements);
        break;
      case IReturnOperation returned:
        statements.Add(new IrReturn(returned.ReturnedValue is null ? null : this.Value(returned.ReturnedValue)));
        break;
      case ILabeledOperation label when SymbolEqualityComparer.Default.Equals(label.Label, this._exit):
        break;
      case ILabeledOperation or { Kind: OperationKind.None, Syntax: OnErrorGoToStatementSyntax or OnErrorResumeNextStatementSyntax or ResumeStatementSyntax }:
        this.JumpNeeded(operation.Syntax, $"'{operation.Syntax.ToString().Split('\n')[0].Trim()}' inside a Try, Using or With");
        break;
      case ITryOperation attempt:
        this.Try(attempt, statements);
        break;
      case IThrowOperation thrown:
        statements.Add(thrown.Exception is null ? new IrRethrow() : new IrThrow(this.Value(thrown.Exception)));
        break;
      case IReDimOperation redim:
        this.ReDim(redim, statements);
        break;
      case IEmptyOperation:
        break;
      default:
        this.Refuse(operation.Syntax, $"the statement '{operation.Syntax.ToString().Split('\n')[0].Trim()}'");
        break;
    }
  }

  /// <summary>An expression evaluated for its effect: an assignment, a call, or anything else as a statement of its own.</summary>
  private void Effect(IOperation operation, List<IrStmt> statements) {
    switch (operation) {
      case ISimpleAssignmentOperation assignment:
        this.Assign(assignment.Target, this.Value(assignment.Value), statements);
        break;
      case ICompoundAssignmentOperation compound:
        this.Compound(compound, statements);
        break;
      case IInvocationOperation call when this.WriteLine(call) is { } line:
        statements.Add(line);
        break;
      case IInvocationOperation call when this.ErrCall(call) is { } err:
        statements.Add(err);
        break;
      default:
        statements.Add(new IrExprStmt(this.Value(operation)));
        break;
    }
  }

  /// <summary>An assignment to a variable, a module variable, an element or a field.</summary>
  private void Assign(IOperation target, IrExpr value, List<IrStmt> statements) {
    switch (target) {
      case ILocalReferenceOperation local:
        statements.Add(new IrAssign(this.Local(local.Local, local.Local.Type), value));
        break;
      case IParameterReferenceOperation parameter:
        statements.Add(new IrAssign(this.Local(parameter.Parameter, parameter.Parameter.Type), value));
        break;
      case IFieldReferenceOperation { Instance: null } field when this.Global(field) is { } global:
        statements.Add(new IrGlobalAssign(global, value));
        break;
      case IFieldReferenceOperation { Instance: { } instance } field when this._fields.TryGetValue(field.Field, out var member):
        statements.Add(new IrFieldStore(this.Value(instance), member, value));
        break;
      case IArrayElementReferenceOperation { Indices.Length: 1 } element:
        statements.Add(new IrStore(this.Value(element.ArrayReference), this.Value(element.Indices[0]), value));
        break;
      case IPropertyReferenceOperation property when this.ErrValue(property) is not null:
        statements.Add(new IrGlobalAssign(this.Err, value));
        break;
      default:
        this.Refuse(target.Syntax, $"an assignment to '{target.Syntax}'");
        break;
    }
  }

  /// <summary>
  /// <c>x op= v</c>: the target's array and index are evaluated once, into temporaries where they are
  /// not plain variables, then the operation of Visual Basic's operator type of the two operands, the
  /// result converted back to the target's type as an assignment converts it.
  /// </summary>
  private void Compound(ICompoundAssignmentOperation compound, List<IrStmt> statements) {
    var target = compound.Target;
    if (target is IArrayElementReferenceOperation { Indices.Length: 1 } element) {
      var array = this.Stable(this.Value(element.ArrayReference), statements);
      var index = this.Stable(this.Value(element.Indices[0]), statements);
      var current = new IrArrayLoad(array, index);
      statements.Add(new IrStore(array, index, this.Combined(compound, current, target.Type!)));
      return;
    }

    if (target is IFieldReferenceOperation { Instance: { } instance } field && this._fields.TryGetValue(field.Field, out var member)) {
      var place = this.Value(instance);
      statements.Add(new IrFieldStore(place, member, this.Combined(compound, new IrFieldLoad(place, member), target.Type!)));
      return;
    }

    this.Assign(target, this.Combined(compound, this.Value(target), target.Type!), statements);
  }

  /// <summary>The value of a compound assignment: the target and the value in the operator's type, the result in the target's.</summary>
  private IrExpr Combined(ICompoundAssignmentOperation compound, IrExpr current, ITypeSymbol targetType) {
    var operand = this.OperatorType(compound.OperatorKind, targetType, compound.Value.Type!);
    if (operand is null) {
      this.Refuse(compound.Syntax, $"the compound assignment '{compound.Syntax}'");
      return current;
    }

    var left = this.Convert(current, targetType, operand, compound.Syntax);
    // The count of a shift stays the Integer it is; every other operand takes the operator's type.
    var right = compound.OperatorKind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift
      ? this.Value(compound.Value) : this.Convert(this.Value(compound.Value), compound.Value.Type!, operand, compound.Syntax);
    var result = this.Binary(compound.OperatorKind, left, right, operand, operand, compound.IsChecked && checks, compound.Syntax);
    return this.Convert(result, operand, targetType, compound.Syntax);
  }

  /// <summary>An expression that is a variable or a constant, or a temporary that holds it.</summary>
  private IrExpr Stable(IrExpr value, List<IrStmt> statements)
    => value is IrLocalRef or IrConst or IrGlobalRef ? value : new IrLocalRef(this.Temporary(value.Type, statements, value));

  private void While(IWhileLoopOperation loop, List<IrStmt> statements) {
    var frame = new Frame(loop.ExitLabel, loop.ContinueLabel, true);
    IrExpr? condition = loop.Condition is null ? null : this.Condition(loop.Condition);
    if (condition is not null && loop.ConditionIsUntil)
      condition = new IrUnary(IrUnaryOp.LogicalNot, condition);
    var body = this.Within(frame, loop.Body);
    statements.Add(loop.ConditionIsTop || condition is null ? new IrWhile(condition ?? new IrConst(IrType.Bool, 1), body) : new IrDoWhile(body, condition));
  }

  /// <summary>
  /// <c>For c = start To limit Step s</c>: start, limit and step evaluated once, in that order; the
  /// loop runs while the counter has not passed the limit in the step's direction, and the step is
  /// added after every pass - checked, so that a counter that steps past its type's largest value
  /// throws, as Visual Basic's does.
  /// </summary>
  private void ForTo(IForToLoopOperation loop, List<IrStmt> statements) {
    var (condition, step) = this.ForToParts(loop, statements);
    var body = this.Within(new Frame(loop.ExitLabel, loop.ContinueLabel, true), loop.Body);
    statements.Add(new IrFor(condition, body, new IrBlock(step)));
  }

  /// <summary>The start of a <c>For … To</c>, added to the statements, and the test and the step of each pass.</summary>
  private (IrExpr Condition, List<IrStmt> Step) ForToParts(IForToLoopOperation loop, List<IrStmt> statements) {
    var counterOperation = loop.LoopControlVariable is IVariableDeclaratorOperation declarator ? null : loop.LoopControlVariable;
    var type = loop.LoopControlVariable is IVariableDeclaratorOperation declared ? declared.Symbol.Type : loop.LoopControlVariable.Type!;
    var irType = this.Type(type);
    var start = this.Value(loop.InitialValue);
    var startTemp = this.Stable(start, statements);
    var limit = this.Stable(this.Value(loop.LimitValue), statements);
    var step = this.Stable(this.Value(loop.StepValue), statements);
    IrExpr Counter() => loop.LoopControlVariable is IVariableDeclaratorOperation variable ? new IrLocalRef(this.Local(variable.Symbol, variable.Symbol.Type)) : this.Value(counterOperation!);
    var assignments = new List<IrStmt>();
    if (loop.LoopControlVariable is IVariableDeclaratorOperation control)
      assignments.Add(new IrAssign(this.Local(control.Symbol, control.Symbol.Type), startTemp));
    else
      this.Assign(counterOperation!, startTemp, assignments);
    statements.AddRange(assignments);

    var zero = Default(irType);
    IrExpr Compare(IrBinaryOp op) => new IrBinary(op, Counter(), limit);
    IrExpr condition = step is IrConst constant
      ? Compare(irType.IsFloat ? (IrOperators.Float(irType, constant.Value) >= 0 ? IrBinaryOp.LessOrEqual : IrBinaryOp.GreaterOrEqual) : !irType.IsSigned || constant.Value >= 0 ? IrBinaryOp.LessOrEqual : IrBinaryOp.GreaterOrEqual)
      : !irType.IsSigned && !irType.IsFloat ? Compare(IrBinaryOp.LessOrEqual)
      : new IrConditional(new IrBinary(IrBinaryOp.GreaterOrEqual, step, zero), Compare(IrBinaryOp.LessOrEqual), Compare(IrBinaryOp.GreaterOrEqual));
    var next = this.Arithmetic(IrBinaryOp.Add, Counter(), step, irType, loop.IsChecked && checks);
    var stepBlock = new List<IrStmt>();
    if (loop.LoopControlVariable is IVariableDeclaratorOperation counter)
      stepBlock.Add(new IrAssign(this.Local(counter.Symbol, counter.Symbol.Type), next));
    else
      this.Assign(counterOperation!, next, stepBlock);
    return (condition, stepBlock);
  }

  /// <summary><c>For Each v In a</c> over an array or the code units of a string: the collection evaluated once, then every element in order.</summary>
  private void ForEach(IForEachLoopOperation loop, List<IrStmt> statements) {
    if (this.ForEachParts(loop, statements) is not var (condition, head, step))
      return;
    head.AddRange(this.Within(new Frame(loop.ExitLabel, loop.ContinueLabel, true), loop.Body).Statements);
    statements.Add(new IrFor(condition, new IrBlock(head), new IrBlock(step)));
  }

  /// <summary>The start of a <c>For Each</c>, added to the statements, and the test, the element's assignment and the step of each pass; <see langword="null"/> after a refusal.</summary>
  private (IrExpr Condition, List<IrStmt> Head, List<IrStmt> Step)? ForEachParts(IForEachLoopOperation loop, List<IrStmt> statements) {
    var collection = loop.Collection is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : loop.Collection;
    var source = this.Value(collection);
    if (!source.Type.IsArray && !source.Type.IsString) {
      this.Refuse(loop.Syntax, "For Each over anything but an array or a string");
      return null;
    }

    var held = new IrLocalRef(this.Temporary(source.Type, statements, source));
    var index = this.Temporary(IrType.I32, statements, new IrConst(IrType.I32, 0));
    IrExpr length = source.Type.IsArray ? new IrArrayLength(held) : new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [held]);
    IrExpr element = source.Type.IsArray ? new IrArrayLoad(held, new IrLocalRef(index)) : new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [held, new IrLocalRef(index)]);
    var body = new List<IrStmt>();
    if (loop.LoopControlVariable is IVariableDeclaratorOperation declarator) {
      var variable = this.Local(declarator.Symbol, declarator.Symbol.Type);
      body.Add(new IrAssign(variable, this.Convert(element, ElementOf(collection.Type!), declarator.Symbol.Type, loop.Syntax)));
    } else {
      this.Assign(loop.LoopControlVariable, this.Convert(element, ElementOf(collection.Type!), loop.LoopControlVariable.Type!, loop.Syntax), body);
    }

    return (new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), length), body, [new IrAssign(index, new IrBinary(IrBinaryOp.Add, new IrLocalRef(index), new IrConst(IrType.I32, 1)))]);
  }

  private ITypeSymbol ElementOf(ITypeSymbol collection)
    => collection is IArrayTypeSymbol array ? array.ElementType : compilation.GetSpecialType(SpecialType.System_Char);

  /// <summary>
  /// <c>Select Case</c>: the value evaluated once. Integer constants alone in every clause become a
  /// switch, whose <c>Exit Select</c> is its <c>break</c>; anything else, or a case that leaves a loop
  /// around it, tests the clauses in order: <c>=</c>, <c>a To b</c>, <c>Is op v</c>.
  /// </summary>
  private void Select(ISwitchOperation choice, List<IrStmt> statements) {
    var value = this.Value(choice.Value);
    var leavesOuter = choice.Cases.SelectMany(@case => @case.Body).SelectMany(body => body.DescendantsAndSelf()).OfType<IBranchOperation>()
      .Any(branch => branch.BranchKind == BranchKind.Break && !SymbolEqualityComparer.Default.Equals(branch.Target, choice.ExitLabel) && !SymbolEqualityComparer.Default.Equals(branch.Target, this._exit));
    var constant = value.Type.IsInteger && !leavesOuter && choice.Cases.All(@case => @case.Clauses.All(clause =>
      clause is IDefaultCaseClauseOperation || clause is ISingleValueCaseClauseOperation { Value.ConstantValue.HasValue: true }));
    if (constant) {
      var sections = choice.Cases.Select(@case => new IrSwitchSection(
        [.. @case.Clauses.OfType<ISingleValueCaseClauseOperation>().Select(clause => ((IrConst)this.Value(clause.Value)).Value).Distinct()],
        @case.Clauses.Any(clause => clause is IDefaultCaseClauseOperation),
        this.Within(new Frame(choice.ExitLabel, null, true), new BodyOf(@case.Body)))).ToList();
      // A value of an earlier case is selected by that case alone, as Select Case tests the cases in order.
      var seen = new HashSet<long>();
      sections = [.. sections.Select(section => section with { Labels = [.. section.Labels.Where(seen.Add)] })];
      statements.Add(new IrSwitch(value, sections));
      return;
    }

    var held = new IrLocalRef(this.Temporary(value.Type, statements, value));
    IrStmt? chain = null;
    foreach (var @case in choice.Cases.Reverse()) {
      var body = this.Within(new Frame(choice.ExitLabel, null, false), new BodyOf(@case.Body));
      if (@case.Clauses.Any(clause => clause is IDefaultCaseClauseOperation)) {
        chain = body;
        continue;
      }

      var test = @case.Clauses.Select(clause => this.Clause(clause, held, choice.Value.Type!)).Aggregate((left, right) => new IrBinary(IrBinaryOp.LogicalOr, left, right));
      chain = new IrIf(test, body, chain is null ? null : chain as IrBlock ?? new IrBlock([chain]));
    }

    if (chain is not null)
      statements.Add(chain);
  }

  /// <summary>The statements of a case as one operation, so that they are lowered as a block.</summary>
  private sealed class BodyOf(System.Collections.Immutable.ImmutableArray<IOperation> operations) {
    public System.Collections.Immutable.ImmutableArray<IOperation> Operations { get; } = operations;
  }

  private IrBlock Within(Frame frame, BodyOf body) {
    this._frames.Push(frame);
    var statements = new List<IrStmt>();
    foreach (var operation in body.Operations) {
      if (statements.Count > 0 && !Completes(statements[^1]))
        break;
      this.Block(operation, statements);
    }
    this._frames.Pop();
    // An Exit Select that ends a case tested in order goes where the case ends anyway; one anywhere
    // else needs a jump.
    if (!frame.Breakable && statements is [.., IrBreak last] && this._selectExits.Contains(last))
      statements.RemoveAt(statements.Count - 1);
    if (!frame.Breakable && statements.SelectMany(Statements).Any(this._selectExits.Contains))
      this.JumpNeeded(null, "Exit Select");
    return new IrBlock(statements);
  }

  /// <summary>The <c>Exit Select</c>s of the cases tested in order, until their case ends.</summary>
  private readonly HashSet<IrStmt> _selectExits = new(ReferenceEqualityComparer.Instance);

  /// <summary>A statement and every statement inside it.</summary>
  private static IEnumerable<IrStmt> Statements(IrStmt statement)
    => IrTree.Blocks(statement).SelectMany(block => block.Statements).SelectMany(Statements).Prepend(statement);

  /// <summary>A jump the structured lowering cannot write: lowered again as a state machine, or refused inside one.</summary>
  private void JumpNeeded(SyntaxNode? syntax, string construct) {
    if (this._flat)
      this.Refuse(syntax, construct);
    else
      this._wantsJumps = true;
  }

  private IrExpr Clause(ICaseClauseOperation clause, IrExpr value, ITypeSymbol type) {
    switch (clause) {
      case ISingleValueCaseClauseOperation single:
        return this.Comparison(BinaryOperatorKind.Equals, value, this.Value(single.Value), type, clause.Syntax);
      case IRangeCaseClauseOperation range:
        return new IrBinary(IrBinaryOp.LogicalAnd, this.Comparison(BinaryOperatorKind.GreaterThanOrEqual, value, this.Value(range.MinimumValue), type, clause.Syntax),
          this.Comparison(BinaryOperatorKind.LessThanOrEqual, value, this.Value(range.MaximumValue), type, clause.Syntax));
      case IRelationalCaseClauseOperation relation:
        return this.Comparison(relation.Relation, value, this.Value(relation.Value), type, clause.Syntax);
      default:
        this.Refuse(clause.Syntax, "the case clause " + clause.Syntax);
        return new IrConst(IrType.Bool, 0);
    }
  }

  /// <summary>
  /// <c>Exit</c> and <c>Continue</c>: out of the procedure a return; out of the innermost loop or
  /// switch a <c>break</c>, of the innermost loop a <c>continue</c>; to an outer one, or <c>GoTo</c>, refused.
  /// </summary>
  private void Branch(IBranchOperation branch, List<IrStmt> statements) {
    if (SymbolEqualityComparer.Default.Equals(branch.Target, this._exit)) {
      statements.Add(new IrReturn(this._method is { ReturnsVoid: false } ? new IrLocalRef(this.FunctionValue()) : null));
      return;
    }

    if (branch.BranchKind == BranchKind.Break && this._frames.Count > 0 && this._frames.Peek() is { Breakable: false } tested && SymbolEqualityComparer.Default.Equals(tested.Exit, branch.Target)) {
      var exit = new IrBreak();
      this._selectExits.Add(exit);
      statements.Add(exit);
      return;
    }

    // The IR's break leaves the innermost loop or switch: a Select Case tested in order is none.
    var innermost = this._frames.FirstOrDefault(frame => frame.Breakable);
    if (branch.BranchKind == BranchKind.Break && innermost is not null && SymbolEqualityComparer.Default.Equals(innermost.Exit, branch.Target)) {
      statements.Add(new IrBreak());
      return;
    }

    var loop = this._frames.FirstOrDefault(frame => frame.Continue is not null);
    if (branch.BranchKind == BranchKind.Continue && loop is not null && SymbolEqualityComparer.Default.Equals(loop.Continue, branch.Target)) {
      statements.Add(new IrContinue());
      return;
    }

    this.JumpNeeded(branch.Syntax, $"'{branch.Syntax}' out of a Try, Using or a statement around it");
  }

  /// <summary>The implicit local a function's name stands for, which the procedure's declarations bind first and its end returns.</summary>
  private IrLocal FunctionValue() => this._locals[this._locals.Keys.OfType<ILocalSymbol>().First(local => local.IsFunctionValue)];

  private void Try(ITryOperation attempt, List<IrStmt> statements) {
    var catches = new List<IrCatch>();
    foreach (var clause in attempt.Catches) {
      var type = ExceptionClass(clause.ExceptionType);
      if (type is null) {
        this.Refuse(clause.Syntax, $"a Catch of {clause.ExceptionType.ToDisplayString()}");
        continue;
      }

      var local = clause.ExceptionDeclarationOrExpression is IVariableDeclaratorOperation declarator ? this.Local(declarator.Symbol, declarator.Symbol.Type) : null;
      if (local is not null)
        this._caught.Add(local);
      if (clause.ExceptionDeclarationOrExpression is not null and not IVariableDeclaratorOperation)
        this.Refuse(clause.Syntax, "a Catch into a variable declared elsewhere");
      catches.Add(new IrCatch(type, local, clause.Filter is null ? null : this.Condition(clause.Filter), this.CatchBody(clause, type)));
    }

    if (attempt.ExitLabel is { } exit && attempt.Descendants().OfType<IBranchOperation>().Any(branch => SymbolEqualityComparer.Default.Equals(branch.Target, exit)))
      this.Refuse(attempt.Syntax, "Exit Try");
    statements.Add(new IrTry(this.Body(attempt.Body), catches, attempt.Finally is null ? null : this.Body(attempt.Finally)));
  }

  /// <summary><c>ReDim [Preserve] a(n)</c>: a new array of n + 1 elements; with <c>Preserve</c> the elements of the old one that fit, copied.</summary>
  private void ReDim(IReDimOperation redim, List<IrStmt> statements) {
    foreach (var clause in redim.Clauses) {
      if (clause.DimensionSizes.Length != 1) {
        this.Refuse(clause.Syntax, "ReDim of an array of several dimensions");
        continue;
      }

      var target = this.Value(clause.Operand);
      var created = this.Temporary(target.Type, statements, new IrNewArray(target.Type, this.Value(clause.DimensionSizes[0])));
      if (redim.Preserve) {
        var old = this.Stable(target, statements);
        var index = this.Temporary(IrType.I32, statements, new IrConst(IrType.I32, 0));
        var fits = new IrBinary(IrBinaryOp.LogicalAnd, new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), new IrArrayLength(old)),
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), new IrArrayLength(new IrLocalRef(created))));
        var copy = new IrFor(fits, new IrBlock([new IrStore(new IrLocalRef(created), new IrLocalRef(index), new IrArrayLoad(old, new IrLocalRef(index)))]),
          new IrBlock([new IrAssign(index, new IrBinary(IrBinaryOp.Add, new IrLocalRef(index), new IrConst(IrType.I32, 1)))]));
        statements.Add(new IrIf(new IrIsNull(old), new IrBlock([]), new IrBlock([copy])));
      }

      this.Assign(clause.Operand, new IrLocalRef(created), statements);
    }
  }

}
