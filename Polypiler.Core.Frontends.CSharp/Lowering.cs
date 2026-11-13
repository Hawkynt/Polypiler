using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Lowers one bound syntax tree to IR. Everything is taken from the semantic model: the types the
/// compiler chose, the conversions it inserted and the constants it folded. A construct outside the
/// subset ends the method it stands in with one diagnostic; nothing is guessed.
/// </summary>
internal sealed class Lowering(string path, SemanticModel model) {

  private readonly List<Diagnostic> _diagnostics = [];
  private readonly Dictionary<ISymbol, IrLocal> _variables = new(SymbolEqualityComparer.Default);
  private readonly List<IrLocal> _locals = [];
  private INamedTypeSymbol? _class;

  public ImportResult Run(CompilationUnitSyntax root) {
    var functions = new List<IrFunction>();
    var name = "";
    try {
      var type = this.TheClass(root);
      name = type.Identifier.Text;
      this._class = model.GetDeclaredSymbol(type);
      var seen = new HashSet<string>();
      foreach (var member in type.Members) {
        try {
          if (member is not MethodDeclarationSyntax method)
            throw new UnsupportedException(member, "a class member other than a static method");

          if (!seen.Add(method.Identifier.Text))
            throw new UnsupportedException(method, "method overloading");

          functions.Add(this.Method(method));
        } catch (UnsupportedException unsupported) {
          this.Report(unsupported);
        }
      }
    } catch (UnsupportedException unsupported) {
      this.Report(unsupported);
    }

    return this._diagnostics.Count > 0
      ? new(null, this._diagnostics)
      : new(new IrModule(name, functions), this._diagnostics);
  }

  private void Report(UnsupportedException unsupported) {
    var position = unsupported.Node.GetLocation().GetLineSpan().StartLinePosition;
    this._diagnostics.Add(new Diagnostic(
      CSharpFrontend.Unsupported,
      DiagnosticSeverity.Error,
      $"'{unsupported.What}' is not supported yet",
      path,
      position.Line + 1,
      position.Character + 1));
  }

  private ClassDeclarationSyntax TheClass(CompilationUnitSyntax root) {
    var members = root.Members;
    if (members.Count == 1 && members[0] is BaseNamespaceDeclarationSyntax scope)
      members = scope.Members;

    if (members.Count != 1 || members[0] is not ClassDeclarationSyntax type)
      throw new UnsupportedException(members.Count > 1 ? members[1] : root, "a file that is not exactly one static class");

    if (!type.Modifiers.Any(SyntaxKind.StaticKeyword))
      throw new UnsupportedException(type, "a class that is not static");

    if (type.TypeParameterList is not null)
      throw new UnsupportedException(type.TypeParameterList, "a generic class");

    return type;
  }

  private IrFunction Method(MethodDeclarationSyntax method) {
    foreach (var modifier in method.Modifiers)
      if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.StaticKeyword))
        throw new UnsupportedException(method, modifier.Text);

    if (method.TypeParameterList is not null)
      throw new UnsupportedException(method.TypeParameterList, "a generic method");

    this._variables.Clear();
    this._locals.Clear();
    var symbol = model.GetDeclaredSymbol(method)!;
    var returnType = Map(symbol.ReturnType, method.ReturnType);
    var parameters = new List<IrLocal>();
    foreach (var parameter in method.ParameterList.Parameters) {
      if (parameter.Modifiers.Count > 0)
        throw new UnsupportedException(parameter, parameter.Modifiers[0].Text);

      if (parameter.Default is not null)
        throw new UnsupportedException(parameter.Default, "a default parameter value");

      var parameterSymbol = model.GetDeclaredSymbol(parameter)!;
      var variable = new IrLocal(parameterSymbol.Name, Map(parameterSymbol.Type, parameter));
      this._variables[parameterSymbol] = variable;
      parameters.Add(variable);
    }

    IrBlock body;
    if (method.Body is not null) {
      body = this.Block(method.Body);
    } else {
      var statements = new List<IrStmt>();
      var expression = method.ExpressionBody!.Expression;
      if (returnType == IrType.Void)
        this.Effect(expression, statements);
      else
        statements.Add(new IrReturn(this.Value(expression)));

      body = new IrBlock(statements);
    }

    return new IrFunction(
      symbol.Name,
      symbol.DeclaredAccessibility == Accessibility.Public,
      returnType,
      parameters,
      this._locals.ToList(),
      body);
  }

  private static IrType Map(ITypeSymbol? type, SyntaxNode node) {
    switch (type?.SpecialType) {
      case SpecialType.System_SByte:
        return IrType.I8;
      case SpecialType.System_Byte:
        return IrType.U8;
      case SpecialType.System_Int16:
        return IrType.I16;
      case SpecialType.System_UInt16 or SpecialType.System_Char:
        return IrType.U16;
      case SpecialType.System_Int32:
        return IrType.I32;
      case SpecialType.System_UInt32:
        return IrType.U32;
      case SpecialType.System_Int64:
        return IrType.I64;
      case SpecialType.System_UInt64:
        return IrType.U64;
      case SpecialType.System_Single:
        return IrType.F32;
      case SpecialType.System_Double:
        return IrType.F64;
      case SpecialType.System_Boolean:
        return IrType.Bool;
      case SpecialType.System_Void:
        return IrType.Void;
    }

    if (type is IArrayTypeSymbol { Rank: 1 } array) {
      var element = Map(array.ElementType, node);
      if (element.IsNumeric)
        return IrType.Array(element);
    }

    throw new UnsupportedException(node, type?.ToDisplayString() ?? node.Kind().ToString());
  }

  private IrBlock Block(BlockSyntax block) {
    var statements = new List<IrStmt>();
    foreach (var statement in block.Statements)
      this.Statement(statement, statements);

    return new IrBlock(statements);
  }

  private IrBlock Body(StatementSyntax statement) {
    if (statement is BlockSyntax block)
      return this.Block(block);

    var statements = new List<IrStmt>();
    this.Statement(statement, statements);
    return new IrBlock(statements);
  }

  private void Statement(StatementSyntax statement, List<IrStmt> into) {
    switch (statement) {
      case BlockSyntax block:
        into.Add(this.Block(block));
        break;
      case CheckedStatementSyntax context:
        into.Add(this.Block(context.Block));
        break;
      case LocalDeclarationStatementSyntax declaration:
        this.LocalDeclaration(declaration, into, null);
        break;
      case ExpressionStatementSyntax expression:
        this.Effect(expression.Expression, into);
        break;
      case IfStatementSyntax conditional:
        into.Add(new IrIf(
          this.Value(conditional.Condition),
          this.Body(conditional.Statement),
          conditional.Else is null ? null : this.Body(conditional.Else.Statement)));
        break;
      case WhileStatementSyntax loop:
        into.Add(new IrWhile(this.Value(loop.Condition), this.Body(loop.Statement)));
        break;
      case DoStatementSyntax loop:
        into.Add(new IrDoWhile(this.Body(loop.Statement), this.Value(loop.Condition)));
        break;
      case ForStatementSyntax loop:
        into.Add(this.For(loop));
        break;
      case ForEachStatementSyntax loop:
        into.Add(this.ForEach(loop));
        break;
      case SwitchStatementSyntax choice:
        this.Switch(choice, into);
        break;
      case BreakStatementSyntax:
        into.Add(new IrBreak());
        break;
      case ContinueStatementSyntax:
        into.Add(new IrContinue());
        break;
      case ReturnStatementSyntax result:
        into.Add(new IrReturn(result.Expression is null ? null : this.Value(result.Expression)));
        break;
      default:
        throw new UnsupportedException(statement, statement.Kind().ToString());
    }
  }

  /// <summary>A <c>for</c> becomes a block holding its initializers and the loop.</summary>
  private IrBlock For(ForStatementSyntax loop) {
    var outer = new List<IrStmt>();
    if (loop.Declaration is not null)
      this.Declaration(loop.Declaration, outer, null);

    foreach (var initializer in loop.Initializers)
      this.Effect(initializer, outer);

    var condition = loop.Condition is null ? new IrConst(IrType.Bool, 1) : this.Value(loop.Condition);
    var body = this.Body(loop.Statement);
    var step = new List<IrStmt>();
    foreach (var incrementor in loop.Incrementors)
      this.Effect(incrementor, step);

    outer.Add(Loop(condition, body, step, loop.Statement));
    return new IrBlock(outer);
  }

  /// <summary>
  /// A <c>foreach</c> over an array becomes a counted loop: the array is evaluated once, every pass
  /// reads one element into the iteration variable, converted as the source says, and the counter
  /// advances after the pass.
  /// </summary>
  private IrBlock ForEach(ForEachStatementSyntax loop) {
    var outer = new List<IrStmt>();
    // The compiler converts the collection to IEnumerable; the loop is over the array itself.
    var array = this.Once(this.Natural(loop.Expression, Map(model.GetTypeInfo(loop.Expression).Type, loop.Expression)), outer);
    var index = this.Local($"$t{this._locals.Count}", IrType.I32);
    outer.Add(new IrDeclare(index, new IrConst(IrType.I32, 0)));

    var symbol = model.GetDeclaredSymbol(loop)!;
    var item = this.Local(symbol.Name, Map(symbol.Type, loop.Type));
    this._variables[symbol] = item;
    var element = Convert(new IrArrayLoad(array, new IrLocalRef(index)), item.Type, this.IsChecked(loop));
    var body = new IrBlock([new IrDeclare(item, element), .. this.Body(loop.Statement).Statements]);
    var condition = new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), new IrArrayLength(array));
    IrStmt advance = new IrAssign(index, new IrBinary(IrBinaryOp.Add, new IrLocalRef(index), new IrConst(IrType.I32, 1)));
    outer.Add(Loop(condition, body, [advance], loop.Statement));
    return new IrBlock(outer);
  }

  /// <summary>
  /// The loop for a body and the step that follows every pass. Without a <c>continue</c> that is a
  /// <c>while</c> whose body ends with the step; a <c>continue</c> must not skip the step, so a loop
  /// that has one keeps body and step apart.
  /// </summary>
  private static IrStmt Loop(IrExpr condition, IrBlock body, List<IrStmt> step, StatementSyntax source) {
    var continues = source
      .DescendantNodesAndSelf(node => node is not (WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax))
      .Any(node => node is ContinueStatementSyntax);
    return continues
      ? new IrFor(condition, body, new IrBlock(step))
      : new IrWhile(condition, new IrBlock([.. body.Statements, .. step]));
  }

  /// <summary>
  /// A <c>switch</c> on an integer with constant labels. C# has one scope for all sections, the IR one
  /// per section, so a local declared in a section is declared in front of the switch instead and the
  /// declaration becomes an assignment.
  /// </summary>
  private void Switch(SwitchStatementSyntax choice, List<IrStmt> into) {
    var value = this.Value(choice.Expression);
    if (!value.Type.IsInteger)
      throw new UnsupportedException(choice.Expression, $"a switch on the type {value.Type}");

    var sections = new List<IrSwitchSection>();
    foreach (var section in choice.Sections) {
      var labels = new List<long>();
      var isDefault = false;
      foreach (var label in section.Labels)
        switch (label) {
          case DefaultSwitchLabelSyntax:
            isDefault = true;
            break;
          case CaseSwitchLabelSyntax constant:
            labels.Add(((IrConst)this.Value(constant.Value)).Value);
            break;
          default:
            throw new UnsupportedException(label, label.Kind().ToString());
        }

      var body = new List<IrStmt>();
      foreach (var statement in section.Statements)
        if (statement is LocalDeclarationStatementSyntax declaration)
          this.LocalDeclaration(declaration, body, into);
        else
          this.Statement(statement, body);

      sections.Add(new IrSwitchSection(labels, isDefault, new IrBlock(body)));
    }

    into.Add(new IrSwitch(value, sections));
  }

  /// <summary>
  /// A declaration statement. A <c>const</c> declares nothing: the compiler has folded every use of
  /// it, and its type only has to be one of the subset.
  /// </summary>
  private void LocalDeclaration(LocalDeclarationStatementSyntax declaration, List<IrStmt> into, List<IrStmt>? scope) {
    var first = declaration.GetFirstToken();
    if (declaration.IsConst && declaration.Modifiers.Count == 1) {
      Map(model.GetTypeInfo(declaration.Declaration.Type).Type, declaration.Declaration.Type);
      return;
    }

    if (first != declaration.Declaration.GetFirstToken())
      throw new UnsupportedException(declaration, first.Text);

    this.Declaration(declaration.Declaration, into, scope);
  }

  /// <summary>
  /// Declares locals. With a <paramref name="scope"/> the declarations go there, in front of the
  /// statement being lowered, and the initializers stay in place as assignments.
  /// </summary>
  private void Declaration(VariableDeclarationSyntax declaration, List<IrStmt> into, List<IrStmt>? scope) {
    foreach (var variable in declaration.Variables) {
      var symbol = (ILocalSymbol)model.GetDeclaredSymbol(variable)!;
      var local = this.Local(symbol.Name, Map(symbol.Type, declaration.Type));
      var initializer = variable.Initializer is null ? null : this.Value(variable.Initializer.Value);
      this._variables[symbol] = local;
      if (scope is null) {
        into.Add(new IrDeclare(local, initializer));
      } else {
        scope.Add(new IrDeclare(local, null));
        if (initializer is not null)
          into.Add(new IrAssign(local, initializer));
      }
    }
  }

  private IrLocal Local(string name, IrType type) {
    var local = new IrLocal(name, type);
    this._locals.Add(local);
    return local;
  }

  /// <summary>Lowers an expression that stands as a statement: an assignment, a step or a call.</summary>
  private void Effect(ExpressionSyntax expression, List<IrStmt> into) {
    switch (expression) {
      case AssignmentExpressionSyntax assignment:
        this.Assignment(assignment, into);
        break;
      // As a statement the compiler accepts no unary operator but the increment and the decrement.
      case PrefixUnaryExpressionSyntax step:
        this.Step(step.Operand, step.IsKind(SyntaxKind.PreIncrementExpression), into);
        break;
      case PostfixUnaryExpressionSyntax step:
        this.Step(step.Operand, step.IsKind(SyntaxKind.PostIncrementExpression), into);
        break;
      case InvocationExpressionSyntax:
        into.Add(new IrExprStmt(this.Value(expression)));
        break;
      default:
        throw new UnsupportedException(expression, expression.Kind().ToString());
    }
  }

  private void Assignment(AssignmentExpressionSyntax assignment, List<IrStmt> into) {
    if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) {
      var simple = this.Target(assignment.Left, into, false);
      into.Add(simple.Write(this.Value(assignment.Right)));
      return;
    }

    if (model.GetSymbolInfo(assignment).Symbol is not IMethodSymbol operation)
      throw new UnsupportedException(assignment, assignment.Kind().ToString());

    var op = assignment.Kind() switch {
      SyntaxKind.AddAssignmentExpression => IrBinaryOp.Add,
      SyntaxKind.SubtractAssignmentExpression => IrBinaryOp.Subtract,
      SyntaxKind.MultiplyAssignmentExpression => IrBinaryOp.Multiply,
      SyntaxKind.DivideAssignmentExpression => IrBinaryOp.Divide,
      SyntaxKind.ModuloAssignmentExpression => IrBinaryOp.Remainder,
      SyntaxKind.AndAssignmentExpression => IrBinaryOp.And,
      SyntaxKind.OrAssignmentExpression => IrBinaryOp.Or,
      SyntaxKind.ExclusiveOrAssignmentExpression => IrBinaryOp.Xor,
      SyntaxKind.LeftShiftAssignmentExpression => IrBinaryOp.ShiftLeft,
      SyntaxKind.RightShiftAssignmentExpression => IrBinaryOp.ShiftRight,
      _ => IrBinaryOp.ShiftRightLogical
    };

    var trap = this.IsChecked(assignment);
    var target = this.Target(assignment.Left, into, true);
    var current = target.Read();
    var left = Convert(current, Map(operation.Parameters[0].Type, assignment));
    var right = this.Value(assignment.Right);
    into.Add(target.Write(Convert(Binary(op, left, right, assignment, trap), current.Type, trap)));
  }

  /// <summary>
  /// A step as a statement: the variable or element plus or minus one, computed in the type C# promotes
  /// it to and converted back, which in a checked context traps instead of wrapping around.
  /// </summary>
  private void Step(ExpressionSyntax operand, bool increment, List<IrStmt> into) {
    var trap = this.IsChecked(operand);
    var target = this.Target(operand, into, true);
    var current = target.Read();
    var word = current.Type.IsArithmetic ? current.Type : IrType.I32;
    var stepped = Binary(increment ? IrBinaryOp.Add : IrBinaryOp.Subtract, Convert(current, word), One(word), operand, trap);
    into.Add(target.Write(Convert(stepped, current.Type, trap)));
  }

  /// <summary>
  /// Whether overflow traps where a node stands: inside the nearest enclosing <c>checked</c> or
  /// <c>unchecked</c>, and as the compilation says where there is neither.
  /// </summary>
  private bool IsChecked(SyntaxNode node) {
    for (var parent = node.Parent; parent is not null; parent = parent.Parent)
      switch (parent.Kind()) {
        case SyntaxKind.CheckedExpression or SyntaxKind.CheckedStatement:
          return true;
        case SyntaxKind.UncheckedExpression or SyntaxKind.UncheckedStatement:
          return false;
      }

    return model.Compilation.Options.CheckOverflow;
  }

  /// <summary>An array index or length: C# also takes 64-bit ones, the subset only those of 32 bits.</summary>
  private IrExpr Index(ExpressionSyntax expression) {
    var index = this.Value(expression);
    return index.Type.IsWord ? index : throw new UnsupportedException(expression, $"an array index or length of the type {index.Type}");
  }

  /// <summary>
  /// What an assignment writes to. When the target is also read, an array and an index that are more
  /// than a variable or a constant are evaluated once into a temporary, as C# evaluates them once.
  /// </summary>
  private (Func<IrExpr> Read, Func<IrExpr, IrStmt> Write) Target(ExpressionSyntax target, List<IrStmt> into, bool alsoRead) {
    switch (target) {
      case IdentifierNameSyntax when this.Value(target) is IrLocalRef variable:
        return (() => variable, value => new IrAssign(variable.Local, value));
      case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element:
        var array = this.Value(element.Expression);
        var index = this.Index(element.ArgumentList.Arguments[0].Expression);
        if (alsoRead) {
          array = this.Once(array, into);
          index = this.Once(index, into);
        }

        return (() => new IrArrayLoad(array, index), value => new IrStore(array, index, value));
      default:
        throw new UnsupportedException(target, target.Kind().ToString());
    }
  }

  private IrExpr Once(IrExpr expression, List<IrStmt> into) {
    if (expression is IrLocalRef or IrConst)
      return expression;

    var temporary = this.Local($"$t{this._locals.Count}", expression.Type);
    into.Add(new IrDeclare(temporary, expression));
    return new IrLocalRef(temporary);
  }

  /// <summary>Lowers an expression to a value of the type the compiler converts it to.</summary>
  private IrExpr Value(ExpressionSyntax expression) {
    var info = model.GetTypeInfo(expression);
    var natural = this.Natural(expression, Map(info.Type, expression));
    return Convert(natural, Map(info.ConvertedType, expression));
  }

  private IrExpr Natural(ExpressionSyntax expression, IrType type) {
    var constant = model.GetConstantValue(expression);
    if (constant.HasValue)
      return new IrConst(type, Carried(constant.Value!));

    switch (expression) {
      case ParenthesizedExpressionSyntax parenthesized:
        return this.Value(parenthesized.Expression);
      case CheckedExpressionSyntax context:
        return this.Value(context.Expression);
      case IdentifierNameSyntax identifier when model.GetSymbolInfo(identifier).Symbol is { } symbol && this._variables.TryGetValue(symbol, out var variable):
        return new IrLocalRef(variable);
      case PrefixUnaryExpressionSyntax unary when unary.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
        return this.StepValue(unary, unary.Operand, unary.IsKind(SyntaxKind.PreIncrementExpression), false);
      case PostfixUnaryExpressionSyntax unary when unary.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
        return this.StepValue(unary, unary.Operand, unary.IsKind(SyntaxKind.PostIncrementExpression), true);
      case PrefixUnaryExpressionSyntax unary:
        return this.Unary(unary);
      case BinaryExpressionSyntax binary:
        return this.Binary(binary);
      case ConditionalExpressionSyntax choice:
        return new IrConditional(this.Value(choice.Condition), this.Value(choice.WhenTrue), this.Value(choice.WhenFalse));
      case CastExpressionSyntax cast:
        return Convert(this.Value(cast.Expression), type, this.IsChecked(cast));
      case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element:
        return new IrArrayLoad(this.Value(element.Expression), this.Index(element.ArgumentList.Arguments[0].Expression));
      case MemberAccessExpressionSyntax { Name.Identifier.Text: "Length" } member when model.GetTypeInfo(member.Expression).Type is IArrayTypeSymbol:
        return new IrArrayLength(this.Value(member.Expression));
      case ArrayCreationExpressionSyntax { Initializer: null } creation:
        return new IrNewArray(type, this.Index(creation.Type.RankSpecifiers[0].Sizes[0]));
      case InvocationExpressionSyntax invocation:
        return this.Call(invocation, type);
      default:
        throw new UnsupportedException(expression, expression.Kind().ToString());
    }
  }

  /// <summary>A <c>++</c> or <c>--</c> whose value is used: the step of a variable or of an array element.</summary>
  private IrExpr StepValue(ExpressionSyntax step, ExpressionSyntax operand, bool increment, bool postfix) {
    IrExpr target = operand switch {
      IdentifierNameSyntax when this.Value(operand) is IrLocalRef variable => variable,
      ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element
        => new IrArrayLoad(this.Value(element.Expression), this.Index(element.ArgumentList.Arguments[0].Expression)),
      _ => throw new UnsupportedException(operand, operand.Kind().ToString())
    };
    return new IrStep(target, increment, postfix, target.Type.IsInteger && this.IsChecked(step));
  }

  private IrExpr Unary(PrefixUnaryExpressionSyntax unary) {
    IrUnaryOp op;
    switch (unary.Kind()) {
      case SyntaxKind.UnaryPlusExpression:
        return this.Value(unary.Operand);
      case SyntaxKind.UnaryMinusExpression:
        op = IrUnaryOp.Negate;
        break;
      case SyntaxKind.BitwiseNotExpression:
        op = IrUnaryOp.BitNot;
        break;
      case SyntaxKind.LogicalNotExpression:
        op = IrUnaryOp.LogicalNot;
        break;
      default:
        throw new UnsupportedException(unary, unary.Kind().ToString());
    }

    var operand = this.Value(unary.Operand);
    if (op == IrUnaryOp.Negate && operand.Type.IsInteger && this.IsChecked(unary))
      op = IrUnaryOp.NegateChecked;

    return new IrUnary(op, operand);
  }

  private IrExpr Binary(BinaryExpressionSyntax binary) {
    IrBinaryOp? op = binary.Kind() switch {
      SyntaxKind.AddExpression => IrBinaryOp.Add,
      SyntaxKind.SubtractExpression => IrBinaryOp.Subtract,
      SyntaxKind.MultiplyExpression => IrBinaryOp.Multiply,
      SyntaxKind.DivideExpression => IrBinaryOp.Divide,
      SyntaxKind.ModuloExpression => IrBinaryOp.Remainder,
      SyntaxKind.BitwiseAndExpression => IrBinaryOp.And,
      SyntaxKind.BitwiseOrExpression => IrBinaryOp.Or,
      SyntaxKind.ExclusiveOrExpression => IrBinaryOp.Xor,
      SyntaxKind.LeftShiftExpression => IrBinaryOp.ShiftLeft,
      SyntaxKind.RightShiftExpression => IrBinaryOp.ShiftRight,
      SyntaxKind.UnsignedRightShiftExpression => IrBinaryOp.ShiftRightLogical,
      SyntaxKind.EqualsExpression => IrBinaryOp.Equal,
      SyntaxKind.NotEqualsExpression => IrBinaryOp.NotEqual,
      SyntaxKind.LessThanExpression => IrBinaryOp.Less,
      SyntaxKind.LessThanOrEqualExpression => IrBinaryOp.LessOrEqual,
      SyntaxKind.GreaterThanExpression => IrBinaryOp.Greater,
      SyntaxKind.GreaterThanOrEqualExpression => IrBinaryOp.GreaterOrEqual,
      SyntaxKind.LogicalAndExpression => IrBinaryOp.LogicalAnd,
      SyntaxKind.LogicalOrExpression => IrBinaryOp.LogicalOr,
      _ => null
    };
    if (op is null)
      throw new UnsupportedException(binary, binary.Kind().ToString());

    return Binary(op.Value, this.Value(binary.Left), this.Value(binary.Right), binary, this.IsChecked(binary));
  }

  private static IrExpr Binary(IrBinaryOp op, IrExpr left, IrExpr right, SyntaxNode node, bool trap) {
    if (trap && left.Type.IsInteger)
      op = op switch {
        IrBinaryOp.Add => IrBinaryOp.AddChecked,
        IrBinaryOp.Subtract => IrBinaryOp.SubtractChecked,
        IrBinaryOp.Multiply => IrBinaryOp.MultiplyChecked,
        _ => op
      };

    if (!IrOperators.Accepts(op, left.Type, right.Type))
      throw new UnsupportedException(node, $"{node.Kind()} on {left.Type} and {right.Type}");

    return new IrBinary(op, left, right);
  }

  private IrExpr Call(InvocationExpressionSyntax invocation, IrType type) {
    var arguments = invocation.ArgumentList.Arguments;
    if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method
        || !SymbolEqualityComparer.Default.Equals(method.ContainingType, this._class)
        || arguments.Any(argument => argument.NameColon is not null))
      throw new UnsupportedException(invocation, invocation.Kind().ToString());

    return new IrCall(method.Name, type, arguments.Select(argument => this.Value(argument.Expression)).ToList());
  }

  /// <summary>
  /// Converts between numeric types. A constant is converted here when the conversion is one the
  /// compiler applies silently; a conversion between integers that could lose something traps when
  /// it stands in a checked context and wraps around otherwise.
  /// <para>
  /// A floating-point number converted to an integer traps in a checked context. Otherwise it is
  /// converted as the runtime converts it (ADR 0002, measured on .NET 10): saturating, with a NaN
  /// becoming 0, and for a target narrower than <c>int</c> saturating to <c>int</c> first and then
  /// keeping the low bits.
  /// </para>
  /// </summary>
  private static IrExpr Convert(IrExpr value, IrType type, bool trap = false) {
    var from = value.Type;
    if (from == type)
      return value;

    if (type.IsFloat)
      return value is IrConst number
        ? new IrConst(type, IrOperators.Bits(type, Rounded(number, type)))
        : new IrConvert(type, value);

    if (from.IsFloat) {
      if (trap)
        return new IrConvert(type, value, IrConversion.Trap);

      return type.Bits < 32
        ? new IrConvert(type, new IrConvert(IrType.I32, value, IrConversion.Saturate))
        : new IrConvert(type, value, IrConversion.Saturate);
    }

    var lossy = trap && !IrOperators.Contains(type, from);
    return value is IrConst constant && !lossy
      ? new IrConst(type, IrOperators.Wrap(type, constant.Value))
      : new IrConvert(type, value, lossy ? IrConversion.Trap : IrConversion.Wrap);
  }

  /// <summary>A constant as a floating-point number of a type, rounded as the runtime rounds the conversion.</summary>
  private static double Rounded(IrConst constant, IrType type) {
    var from = constant.Type;
    if (from.IsFloat)
      return IrOperators.Float(from, constant.Value);

    if (type == IrType.F32)
      return from == IrType.U64 ? (float)(ulong)constant.Value : (float)constant.Value;

    return from == IrType.U64 ? (double)(ulong)constant.Value : (double)constant.Value;
  }

  /// <summary>The constant 1 of an arithmetic type, which a step adds or subtracts.</summary>
  private static IrConst One(IrType type) => new(type, type.IsFloat ? IrOperators.Bits(type, 1) : 1);

  /// <summary>A constant of the compiler as the IR carries it.</summary>
  private static long Carried(object value) => value switch {
    ulong large => (long)large,
    char unit => unit,
    float single => IrOperators.Bits(IrType.F32, single),
    double number => IrOperators.Bits(IrType.F64, number),
    _ => System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
  };

  private sealed class UnsupportedException(SyntaxNode node, string what) : Exception {

    public SyntaxNode Node { get; } = node;

    public string What { get; } = what;

  }

}
