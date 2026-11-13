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
        var first = declaration.GetFirstToken();
        if (first != declaration.Declaration.GetFirstToken())
          throw new UnsupportedException(declaration, first.Text);

        this.Declaration(declaration.Declaration, into);
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
      case ForStatementSyntax loop:
        into.Add(this.For(loop));
        break;
      case ReturnStatementSyntax result:
        into.Add(new IrReturn(result.Expression is null ? null : this.Value(result.Expression)));
        break;
      default:
        throw new UnsupportedException(statement, statement.Kind().ToString());
    }
  }

  /// <summary>
  /// A <c>for</c> becomes a block holding its initializers and a <c>while</c> whose body ends with the
  /// incrementors. That is the same loop, because the subset has no <c>continue</c>.
  /// </summary>
  private IrBlock For(ForStatementSyntax loop) {
    var outer = new List<IrStmt>();
    if (loop.Declaration is not null)
      this.Declaration(loop.Declaration, outer);

    foreach (var initializer in loop.Initializers)
      this.Effect(initializer, outer);

    var condition = loop.Condition is null ? new IrConst(IrType.Bool, 1) : this.Value(loop.Condition);
    var body = new List<IrStmt>(this.Body(loop.Statement).Statements);
    foreach (var incrementor in loop.Incrementors)
      this.Effect(incrementor, body);

    outer.Add(new IrWhile(condition, new IrBlock(body)));
    return new IrBlock(outer);
  }

  private void Declaration(VariableDeclarationSyntax declaration, List<IrStmt> into) {
    foreach (var variable in declaration.Variables) {
      var symbol = (ILocalSymbol)model.GetDeclaredSymbol(variable)!;
      var local = this.Local(symbol.Name, Map(symbol.Type, declaration.Type));
      var initializer = variable.Initializer is null ? null : this.Value(variable.Initializer.Value);
      this._variables[symbol] = local;
      into.Add(new IrDeclare(local, initializer));
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
    var stepped = Binary(increment ? IrBinaryOp.Add : IrBinaryOp.Subtract, Convert(current, word), new IrConst(word, 1), operand, trap);
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
  /// compiler applies silently, which loses nothing; a conversion that could lose something traps
  /// when it stands in a checked context and wraps around otherwise.
  /// </summary>
  private static IrExpr Convert(IrExpr value, IrType type, bool trap = false) {
    if (value.Type == type)
      return value;

    var lossy = trap && !IrOperators.Contains(type, value.Type);
    return value is IrConst constant && !lossy
      ? new IrConst(type, IrOperators.Wrap(type, constant.Value))
      : new IrConvert(type, value, lossy ? IrConversion.Trap : IrConversion.Wrap);
  }

  /// <summary>A constant of the compiler as the IR carries it.</summary>
  private static long Carried(object value) => value switch {
    ulong large => (long)large,
    char unit => unit,
    _ => System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
  };

  private sealed class UnsupportedException(SyntaxNode node, string what) : Exception {

    public SyntaxNode Node { get; } = node;

    public string What { get; } = what;

  }

}
