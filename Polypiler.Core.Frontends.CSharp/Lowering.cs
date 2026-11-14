using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Lowers one bound syntax tree, or the parts of one class in several, to IR. Everything is taken from the semantic model: the types the
/// compiler chose, the conversions it inserted and the constants it folded. A construct outside the
/// subset ends the method it stands in with one diagnostic; nothing is guessed.
/// </summary>
internal sealed partial class Lowering(string path, SemanticModel model) {

  private readonly List<Diagnostic> _diagnostics = [];
  private readonly Dictionary<ISymbol, IrLocal> _variables = new(SymbolEqualityComparer.Default);
  private readonly List<IrLocal> _locals = [];
  private readonly Dictionary<ISymbol, IrModuleConst> _constants = new(SymbolEqualityComparer.Default);
  private readonly HashSet<string> _read = [];
  private INamedTypeSymbol? _class;

  public ImportResult Run(CompilationUnitSyntax root) => this.Run([(path, model, root)]);

  /// <summary>
  /// Lowers the parts of one static class that several files declare, each <c>partial</c>: the
  /// constants of every part first, so that a method may read a constant of another file, then the
  /// methods in file order. Each part is read with the semantic model of its own tree, and a
  /// diagnostic names the file it stands in.
  /// </summary>
  public ImportResult Run(IReadOnlyList<(string Path, SemanticModel Model, CompilationUnitSyntax Root)> parts) {
    var functions = new List<IrFunction>();
    var name = "";
    var classes = new List<(string Path, SemanticModel Model, ClassDeclarationSyntax Type)>();
    var structs = new List<(string Path, SemanticModel Model, TypeDeclarationSyntax Type)>();
    foreach (var part in parts) {
      path = part.Path;
      model = part.Model;
      try {
        var found = new List<TypeDeclarationSyntax>();
        var type = this.TheClass(part.Root, found);
        structs.AddRange(found.Select(declaration => (part.Path, part.Model, declaration)));
        var symbol = model.GetDeclaredSymbol(type);
        if (this._class is not null && !SymbolEqualityComparer.Default.Equals(this._class, symbol))
          throw new UnsupportedException(type, "files that declare different classes");

        this._class = symbol;
        name = type.Identifier.Text;
        classes.Add((part.Path, part.Model, type));
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
      }
    }

    this.DeclareStructs(structs);
    foreach (var (file, semantics, type) in classes) {
      path = file;
      model = semantics;
      foreach (var field in type.Members.OfType<FieldDeclarationSyntax>()) {
        try {
          this.Field(field);
        } catch (UnsupportedException unsupported) {
          this.Report(unsupported);
        }
      }
    }

    var seen = new HashSet<string>();
    foreach (var (file, semantics, type) in classes) {
      path = file;
      model = semantics;
      foreach (var member in type.Members.Where(member => member is not FieldDeclarationSyntax && !IsStruct(member))) {
        try {
          if (member is not MethodDeclarationSyntax method)
            throw new UnsupportedException(member, "a class member other than a static method or a constant");

          if (!seen.Add(method.Identifier.Text))
            throw new UnsupportedException(method, "method overloading");

          functions.Add(this.Method(method));
        } catch (UnsupportedException unsupported) {
          this.Report(unsupported);
        }
      }
    }

    return this._diagnostics.Count > 0
      ? new(null, this._diagnostics)
      : new(new IrModule(name, functions, this._constants.Values.ToList()) { Types = [.. this._types] }, this._diagnostics);
  }

  /// <summary>
  /// A field is a constant of the module: a <c>const</c>, or a <c>static readonly</c> whose
  /// initializer is a constant or, for an array, a list of constants. Anything else would be state.
  /// </summary>
  private void Field(FieldDeclarationSyntax field) {
    var constant = field.Modifiers.Any(SyntaxKind.ConstKeyword);
    foreach (var modifier in field.Modifiers)
      if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword
          or SyntaxKind.ConstKeyword or SyntaxKind.StaticKeyword or SyntaxKind.ReadOnlyKeyword))
        throw new UnsupportedException(field, modifier.Text);

    if (!constant && !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
      throw new UnsupportedException(field, "a field that is neither const nor static readonly");

    foreach (var variable in field.Declaration.Variables) {
      var symbol = (IFieldSymbol)model.GetDeclaredSymbol(variable)!;
      var type = this.Map(symbol.Type, field.Declaration.Type);
      if ((type.Element ?? type).IsStruct)
        throw new UnsupportedException(variable, $"a constant of the type {type}");

      if (variable.Initializer is null)
        throw new UnsupportedException(variable, "a field without an initializer");

      var initializer = variable.Initializer.Value;
      var values = type.IsArray
        ? this.ArrayOf(type, initializer).Arguments
        : [this.Value(initializer)];
      var mutable = values.FirstOrDefault(value => value is not IrConst);
      if (mutable is not null)
        throw new UnsupportedException(initializer, "a field initializer that is not a constant");

      this._constants[symbol] = new IrModuleConst(symbol.Name, type, values.Select(value => ((IrConst)value).Value).ToList());
    }
  }

  /// <summary>
  /// The array an initializer builds: <c>new T[] { … }</c>, <c>new[] { … }</c>, <c>{ … }</c> or
  /// <c>[ … ]</c>, with every element converted to the element type.
  /// </summary>
  private IrIntrinsic ArrayOf(IrType type, ExpressionSyntax initializer) {
    IEnumerable<ExpressionSyntax> elements = initializer switch {
      InitializerExpressionSyntax list => list.Expressions,
      ArrayCreationExpressionSyntax { Initializer: { } list } => list.Expressions,
      ImplicitArrayCreationExpressionSyntax creation => creation.Initializer.Expressions,
      CollectionExpressionSyntax collection => collection.Elements.Select(element
        => element is ExpressionElementSyntax single ? single.Expression : throw new UnsupportedException(element, element.Kind().ToString())),
      _ => throw new UnsupportedException(initializer, "an array initializer that is not a list of elements")
    };
    return new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, elements.Select(element => Convert(this.Value(element), type.Element!)).ToList());
  }

  private void Report(UnsupportedException unsupported) {
    var position = unsupported.Node.GetLocation().GetLineSpan().StartLinePosition;
    this._diagnostics.Add(CSharpFrontend.UnsupportedDescriptor.At(
      $"'{unsupported.What}' is not supported yet",
      path,
      position.Line + 1,
      position.Character + 1));
  }

  /// <summary>
  /// The one static class of a file, with the structs the file declares beside it and inside it
  /// added to <paramref name="structs"/>.
  /// </summary>
  private ClassDeclarationSyntax TheClass(CompilationUnitSyntax root, List<TypeDeclarationSyntax> structs) {
    var members = root.Members;
    if (members.Count == 1 && members[0] is BaseNamespaceDeclarationSyntax scope)
      members = scope.Members;

    var types = members.Where(member => !IsStruct(member)).ToList();
    if (types.Count != 1 || types[0] is not ClassDeclarationSyntax type)
      throw new UnsupportedException(types.Count > 1 ? types[1] : root, "a file that is not exactly one static class");

    structs.AddRange(members.Concat(type.Members).Where(IsStruct).Cast<TypeDeclarationSyntax>());

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
    this._read.Clear();
    var symbol = model.GetDeclaredSymbol(method)!;
    var returnType = this.Map(symbol.ReturnType, method.ReturnType);
    var parameters = new List<IrLocal>();
    foreach (var parameter in method.ParameterList.Parameters) {
      if (parameter.Modifiers.Count > 0)
        throw new UnsupportedException(parameter, parameter.Modifiers[0].Text);

      if (parameter.Default is not null)
        throw new UnsupportedException(parameter.Default, "a default parameter value");

      var parameterSymbol = model.GetDeclaredSymbol(parameter)!;
      var variable = new IrLocal(parameterSymbol.Name, this.Map(parameterSymbol.Type, parameter));
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
        statements.Add(new IrReturn(this.Initialized(expression, statements).Value));

      body = new IrBlock(statements);
    }

    // In the generated module a constant is a name of the module, which a variable of the same name
    // would hide from the function that reads it.
    var hiding = parameters.Concat(this._locals).FirstOrDefault(variable => this._read.Contains(variable.Name));
    if (hiding is not null)
      throw new UnsupportedException(method, $"a variable with the name of the field '{hiding.Name}' it is used next to");

    return new IrFunction(
      symbol.Name,
      symbol.DeclaredAccessibility == Accessibility.Public,
      returnType,
      parameters,
      this._locals.ToList(),
      body);
  }

  private IrType Map(ITypeSymbol? type, SyntaxNode node) {
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

    if (this.ViewType(type, node) is { } view)
      return view;

    if (type is INamedTypeSymbol named && this._structs.TryGetValue(named, out var declared))
      return IrType.Struct(declared);

    if (type is IArrayTypeSymbol { Rank: 1 } array) {
      var element = this.Map(array.ElementType, node);
      if (element.IsNumeric || element.IsStruct)
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
        into.Add(new IrReturn(result.Expression is null ? null : this.Initialized(result.Expression, into).Value));
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
    var array = this.Once(this.Read(loop.Expression), outer);
    var index = this.Local($"$t{this._locals.Count}", IrType.I32);
    outer.Add(new IrDeclare(index, new IrConst(IrType.I32, 0)));

    var symbol = model.GetDeclaredSymbol(loop)!;
    var item = this.Local(symbol.Name, this.Map(symbol.Type, loop.Type));
    this._variables[symbol] = item;
    var element = Convert(new IrArrayLoad(array, new IrLocalRef(index)), item.Type, this.IsChecked(loop));
    var body = new IrBlock([new IrDeclare(item, element), .. this.Body(loop.Statement).Statements]);
    IrExpr length = array.Type.IsView ? new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [array]) : new IrArrayLength(array);
    var condition = new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), length);
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
  /// declaration becomes an assignment. Those declarations and the switch then share a block, which
  /// is the scope the switch had: the next switch may declare the same names.
  /// </summary>
  private void Switch(SwitchStatementSyntax choice, List<IrStmt> into) {
    var value = this.Value(choice.Expression);
    if (!value.Type.IsInteger)
      throw new UnsupportedException(choice.Expression, $"a switch on the type {value.Type}");

    var sections = new List<IrSwitchSection>();
    var shared = new List<IrStmt>();
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
          this.LocalDeclaration(declaration, body, shared);
        else
          this.Statement(statement, body);

      sections.Add(new IrSwitchSection(labels, isDefault, new IrBlock(body)));
    }

    IrStmt lowered = new IrSwitch(value, sections);
    into.Add(shared.Count == 0 ? lowered : new IrBlock([.. shared, lowered]));
  }

  /// <summary>
  /// A declaration statement. A <c>const</c> declares nothing: the compiler has folded every use of
  /// it, and its type only has to be one of the subset.
  /// </summary>
  private void LocalDeclaration(LocalDeclarationStatementSyntax declaration, List<IrStmt> into, List<IrStmt>? scope) {
    if (declaration.IsConst) {
      this.Map(model.GetTypeInfo(declaration.Declaration.Type).Type, declaration.Declaration.Type);
      return;
    }

    var first = declaration.GetFirstToken();
    if (first != declaration.Declaration.GetFirstToken())
      throw new UnsupportedException(declaration, first.Text);

    if (scope is null && this.Paired(declaration.Declaration, into))
      return;

    this.Declaration(declaration.Declaration, into, scope);
  }

  /// <summary>
  /// Declares locals. With a <paramref name="scope"/> the declarations go there, in front of the
  /// statement being lowered, and the initializers stay in place as assignments.
  /// </summary>
  private void Declaration(VariableDeclarationSyntax declaration, List<IrStmt> into, List<IrStmt>? scope) {
    foreach (var variable in declaration.Variables) {
      var symbol = (ILocalSymbol)model.GetDeclaredSymbol(variable)!;
      var local = this.Local(symbol.Name, this.Map(symbol.Type, declaration.Type));
      IrExpr? initializer = null;
      List<IrStmt> after = [];
      if (variable.Initializer?.Value is InitializerExpressionSyntax list)
        initializer = this.ArrayOf(local.Type, list);
      else if (variable.Initializer is { } given)
        (initializer, after) = this.Initialized(given.Value, into, local);

      this._variables[symbol] = local;
      if (scope is null) {
        into.Add(new IrDeclare(local, initializer));
      } else {
        scope.Add(new IrDeclare(local, null));
        if (initializer is not null)
          into.Add(new IrAssign(local, initializer));
      }

      into.AddRange(after);
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
      case AssignmentExpressionSyntax when this.Paired(expression, into):
        break;
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
      case InvocationExpressionSyntax when this.Paired(expression, into):
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
      // An object initializer is stored into a temporary first, where nothing but a variable is evaluated before it.
      var value = assignment.Left is IdentifierNameSyntax ? this.Initialized(assignment.Right, into).Value : this.Value(assignment.Right);
      into.Add(simple.Write(value));
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
    var left = Convert(current, this.Map(operation.Parameters[0].Type, assignment));
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
        var array = this.Written(element.Expression);
        var index = this.Index(element.ArgumentList.Arguments[0].Expression);
        if (alsoRead) {
          array = this.Once(array, into);
          index = this.Once(index, into);
        }

        return (() => new IrArrayLoad(array, index), value => new IrStore(array, index, value));
      case MemberAccessExpressionSyntax member when this.FieldOf(member) is { } field:
        var place = this.Place(member.Expression, into, alsoRead);
        return (() => new IrFieldLoad(place, field), value => new IrFieldStore(place, field, value));
      default:
        throw new UnsupportedException(target, target.Kind().ToString());
    }
  }

  private IrExpr Once(IrExpr expression, List<IrStmt> into) {
    if (expression is IrLocalRef or IrConst or IrConstRef)
      return expression;

    var temporary = this.Local($"$t{this._locals.Count}", expression.Type);
    into.Add(new IrDeclare(temporary, expression));
    return new IrLocalRef(temporary);
  }

  /// <summary>
  /// Lowers an expression to a value of the type the compiler converts it to. A table of the module
  /// is not a value: handing it on would give it a second name under which it could be written.
  /// </summary>
  private IrExpr Value(ExpressionSyntax expression) {
    if (this.MemberOfStruct(expression) is { } member)
      throw new UnsupportedException(expression, member);

    // A ref expression has no type of its own; it is refused by what it takes a reference to.
    if (expression is RefExpressionSyntax reference)
      throw this.Reference(reference);

    var info = model.GetTypeInfo(expression);
    if (expression is StackAllocArrayCreationExpressionSyntax stack)
      return this.StackAlloc(stack, info.ConvertedType);

    if (expression is CollectionExpressionSyntax)
      return this.ArrayOf(this.Map(info.ConvertedType, expression), expression);

    var natural = this.Natural(expression, this.Map(info.Type, expression));
    if (natural is IrConstRef { Type.IsArray: true } table)
      throw new UnsupportedException(expression, $"the static readonly array '{table.Constant.Name}' used as a value");

    var converted = this.Map(info.ConvertedType, expression);
    return converted.IsView ? ToView(natural, converted) : Convert(natural, converted);
  }

  /// <summary>
  /// An array that is only read, measured or copied from. The array itself is meant, not what the
  /// compiler converts it to for a parameter of the type <c>Array</c>; a table of the module may stand here.
  /// </summary>
  private IrExpr Read(ExpressionSyntax expression)
    => this.Natural(expression, this.Map(model.GetTypeInfo(expression).Type, expression));

  /// <summary>An array whose elements are written, which a table of the module never is.</summary>
  private IrExpr Written(ExpressionSyntax expression) {
    var array = this.Read(expression);
    return array is IrConstRef table
      ? throw new UnsupportedException(expression, $"a write to the static readonly array '{table.Constant.Name}'")
      : array;
  }

  private IrExpr Natural(ExpressionSyntax expression, IrType type) {
    // A constant null, such as default(int[]), is not a value of the subset.
    if (model.GetConstantValue(expression) is { HasValue: true, Value: { } folded })
      return new IrConst(type, Carried(folded));

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
      case ElementAccessExpressionSyntax { ArgumentList.Arguments: [{ Expression: RangeExpressionSyntax range }] } element:
        return this.Range(element, range, type);
      case ElementAccessExpressionSyntax { ArgumentList.Arguments: [{ Expression: PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.IndexExpression } fromEnd }] } element:
        return this.FromEnd(element, fromEnd);
      case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element:
        return new IrArrayLoad(this.Read(element.Expression), this.Index(element.ArgumentList.Arguments[0].Expression));
      case MemberAccessExpressionSyntax member when this.FieldOf(member) is { } field:
        return new IrFieldLoad(this.Value(member.Expression), field);
      case BaseObjectCreationExpressionSyntax creation when type.IsView:
        return this.ViewCreation(creation, type);
      case MemberAccessExpressionSyntax { Name.Identifier.Text: "Length" or "IsEmpty" } member when this.ViewType(model.GetTypeInfo(member.Expression).Type, member) is not null:
        var length = new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [this.Read(member.Expression)]);
        return member.Name.Identifier.Text == "Length" ? length : new IrBinary(IrBinaryOp.Equal, length, new IrConst(IrType.I32, 0));
      case BaseObjectCreationExpressionSyntax creation when type.IsStruct:
        return this.Creation(creation, type);
      case DefaultExpressionSyntax or LiteralExpressionSyntax when type.IsStruct:
        return this.Default(type, expression);
      case WithExpressionSyntax:
        throw new UnsupportedException(expression, "a with expression");
      case MemberAccessExpressionSyntax { Name.Identifier.Text: "Length" } member when model.GetTypeInfo(member.Expression).Type is IArrayTypeSymbol:
        return new IrArrayLength(this.Read(member.Expression));
      case IdentifierNameSyntax or MemberAccessExpressionSyntax
        when model.GetSymbolInfo(expression).Symbol is IFieldSymbol field && this._constants.TryGetValue(field, out var global):
        this._read.Add(global.Name);
        return new IrConstRef(global);
      case ArrayCreationExpressionSyntax { Initializer: null } creation:
        if (type.Element!.IsStruct)
          this.Default(type.Element, creation);

        return new IrNewArray(type, this.Index(creation.Type.RankSpecifiers[0].Sizes[0]));
      case ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax:
        return this.ArrayOf(type, expression);
      case InvocationExpressionSyntax invocation:
        return this.Call(invocation, type);
      case MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner } part
        when model.GetSymbolInfo(inner).Symbol is IMethodSymbol { Name: "SinCos" or "SinCosPi" } sinCos && inner.ArgumentList.Arguments.Count == 1:
        return this.SinCos(sinCos, inner.ArgumentList.Arguments[0].Expression, part.Name.Identifier.Text, type)
          ?? throw new UnsupportedException(expression, $"{sinCos.ContainingType.Name}.{sinCos.Name}");
      default:
        throw new UnsupportedException(expression, expression.Kind().ToString());
    }
  }

  /// <summary>A <c>++</c> or <c>--</c> whose value is used: the step of a variable or of an array element.</summary>
  private IrExpr StepValue(ExpressionSyntax step, ExpressionSyntax operand, bool increment, bool postfix) {
    IrExpr target = operand switch {
      IdentifierNameSyntax when this.Value(operand) is IrLocalRef variable => variable,
      ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element
        => new IrArrayLoad(this.Written(element.Expression), this.Index(element.ArgumentList.Arguments[0].Expression)),
      _ => throw new UnsupportedException(operand, operand.Kind().ToString())
    };
    return new IrStep(target, increment, postfix, target.Type.IsInteger && this.IsChecked(step));
  }

  /// <summary>
  /// A prefix operator on a value. The steps were taken before, and the compiler gives no other
  /// prefix operator a type of the subset, so what is neither plus, minus nor complement is the negation.
  /// </summary>
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
      default:
        op = IrUnaryOp.LogicalNot;
        break;
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

    var left = this.Value(binary.Left);
    var right = this.Value(binary.Right);
    return left.Type.IsStruct
      ? this.Equality(op.Value, left, right, binary)
      : Binary(op.Value, left, right, binary, this.IsChecked(binary));
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
    // An invocation the compiler accepted and gave a type of the subset is bound to a method.
    var method = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
    // Equals(T) of a record struct is its field-wise equality; Equals(object) is another method.
    if (method is { Name: "Equals", Parameters: [{ Type: INamedTypeSymbol other }] } && this._structs.ContainsKey(method.ContainingType)
        && SymbolEqualityComparer.Default.Equals(other, method.ContainingType) && invocation.Expression is MemberAccessExpressionSyntax receiver)
      return new IrStructEquals(this.Value(receiver.Expression), this.Value(arguments[0].Expression));

    if (this.ViewCall(invocation, method, type) is { } view)
      return view;

    if (method.MethodKind != MethodKind.Ordinary || arguments.Any(argument => argument.NameColon is not null))
      throw new UnsupportedException(invocation, invocation.Kind().ToString());

    var operands = arguments.Select(argument => argument.Expression).ToList();
    if (SymbolEqualityComparer.Default.Equals(method.ContainingType, this._class))
      return new IrCall(method.Name, type, operands.Select(this.Value).ToList());

    return this.Library(method, operands, type)
      ?? throw new UnsupportedException(invocation, $"{method.ContainingType.Name}.{method.Name}");
  }

  /// <summary>
  /// A call into the base library, as the one operation of the IR it means exactly; <see langword="null"/>
  /// for a method, or an overload of one, that has no such operation.
  /// </summary>
  private IrExpr? Library(IMethodSymbol method, List<ExpressionSyntax> operands, IrType type) {
    // The integer and bit operations of the base library, which CSharpIntegerApi lists.
    if (this.Integer(method, operands, type) is { } integer)
      return integer;

    var parameters = method.Parameters.Select(parameter => parameter.Type.SpecialType).ToList();
    if (CSharpFloatApi.Find(method.ContainingType.ToDisplayString(), method.Name, operands.Count) is { } member)
      return this.Floating(member, method, operands, type);

    switch (method.ContainingType.ToDisplayString(), method.Name, operands.Count) {
      case ("System.Array", "Copy", 5) when parameters[4] == SpecialType.System_Int32:
        var source = this.Read(operands[0]);
        var target = this.Written(operands[2]);
        if (source.Type != target.Type)
          throw new UnsupportedException(operands[2], $"Array.Copy from {source.Type} to {target.Type}");

        return new IrIntrinsic(IrIntrinsicOp.ArrayCopy, type, [source, this.Value(operands[1]), target, this.Value(operands[3]), this.Value(operands[4])]);
      case ("System.Array", "Fill", 2):
        return new IrIntrinsic(IrIntrinsicOp.ArrayFill, type, [this.Written(operands[0]), this.Value(operands[1])]);
      case ("System.Array", "Clear", 1):
        return new IrIntrinsic(IrIntrinsicOp.ArrayClear, type, [this.Written(operands[0])]);
      case ("System.Math" or "System.MathF", "Round", 2) when method.Parameters[1].Type.Name == "MidpointRounding":
        // The mode has to be one of the two the IR has, written as a constant.
        return model.GetConstantValue(operands[1]).Value switch {
          (int)MidpointRounding.ToEven => this.Exact(IrIntrinsicOp.RoundEven, type, operands.Take(1)),
          (int)MidpointRounding.AwayFromZero => this.Exact(IrIntrinsicOp.RoundAway, type, operands.Take(1)),
          _ => null
        };
    }

    IrIntrinsicOp? op = (method.ContainingType.ToDisplayString(), method.Name) switch {
      ("System.Math" or "System.MathF", "Abs") => IrIntrinsicOp.Abs,
      ("System.Math" or "System.MathF", "Min") => IrIntrinsicOp.Min,
      ("System.Math" or "System.MathF", "Max") => IrIntrinsicOp.Max,
      ("System.Math" or "System.MathF", "Sign") => IrIntrinsicOp.Sign,
      ("System.Math" or "System.MathF", "Sqrt") => IrIntrinsicOp.Sqrt,
      ("System.Math" or "System.MathF", "Floor") => IrIntrinsicOp.Floor,
      ("System.Math" or "System.MathF", "Ceiling") => IrIntrinsicOp.Ceiling,
      ("System.Math" or "System.MathF", "Truncate") => IrIntrinsicOp.Truncate,
      ("System.Math" or "System.MathF", "Round") => IrIntrinsicOp.RoundEven,
      ("System.BitConverter", "DoubleToInt64Bits" or "SingleToInt32Bits") => IrIntrinsicOp.FloatBits,
      ("System.BitConverter", "Int64BitsToDouble" or "Int32BitsToSingle") => IrIntrinsicOp.BitsFloat,
      _ => null
    };
    return op is null ? null : this.Exact(op.Value, type, operands);
  }

  /// <summary>
  /// One part of <c>Math.SinCos</c>, <c>MathF.SinCos</c> or <c>double.SinCos</c>: the sine as <c>Sin</c>
  /// or <c>Item1</c>, the cosine as <c>Cos</c> or <c>Item2</c>. The pair as a value is outside the subset.
  /// </summary>
  private IrExpr? SinCos(IMethodSymbol method, ExpressionSyntax operand, string part, IrType type) {
    if (method.Name != "SinCos" || method.ContainingType.ToDisplayString() is not ("System.Math" or "System.MathF" or "double" or "float"))
      return null;

    var op = part is "Sin" or "Item1" ? IrIntrinsicOp.Sin : IrIntrinsicOp.Cos;
    return new IrIntrinsic(op, type, [this.Value(operand)]);
  }

  /// <summary>
  /// A floating-point member of the table, when the overload the compiler chose is one its operation
  /// is defined on: an overload on integers (<c>Math.Clamp(int, int, int)</c>) is refused by name.
  /// </summary>
  private IrExpr? Floating(CSharpFloatMember member, IMethodSymbol method, List<ExpressionSyntax> operands, IrType type) {
    if (!method.Parameters.Any(parameter => parameter.Type.SpecialType is SpecialType.System_Double or SpecialType.System_Single))
      return null;

    // A mode of MidpointRounding is no value of the subset: it is read as the constant it has to be.
    var arguments = new List<IrExpr>();
    for (var index = 0; index < operands.Count; ++index) {
      if (method.Parameters[index].Type.TypeKind != TypeKind.Enum)
        arguments.Add(this.Value(operands[index]));
      else if (model.GetConstantValue(operands[index]) is { HasValue: true, Value: int mode })
        arguments.Add(new IrConst(IrType.I32, mode));
      else
        return null;
    }

    var call = new CSharpFloatCall(
      arguments,
      operands.Select(operand => model.GetConstantValue(operand) is { HasValue: true } constant ? constant.Value : null).ToList(),
      method.Parameters.Select(parameter => parameter.Type.ToDisplayString()).ToList(),
      type);
    return member.Lower(call);
  }

  /// <summary>
  /// The intrinsic for a library method, when the overload the compiler chose is one the operation is
  /// defined on: its typing rule decides, so an overload on a type outside it is refused by name.
  /// </summary>
  private IrIntrinsic? Exact(IrIntrinsicOp op, IrType type, IEnumerable<ExpressionSyntax> operands) {
    var arguments = operands.Select(this.Value).ToList();
    return IrOperators.Accepts(op, type, arguments.Select(argument => argument.Type).ToList())
      ? new IrIntrinsic(op, type, arguments)
      : null;
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
