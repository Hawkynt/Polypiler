using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Polypiler.Core.Backends.CSharp.Syntax;

/// <summary>
/// Reads C# source into the syntax tree of the backend, through Roslyn's parser: the helpers and the
/// forms of the capability table, which stay readable C# source, and every generated file when a test
/// parses it back. What the tree keeps of the layout - a blank line or a comment in front of a
/// statement, a block on one line, a line break before an operator, the spelling of a literal - is
/// taken from the source, so that the printer writes a helper as its source has it.
/// </summary>
public static class CSharpSyntaxReader {

  /// <summary>One member of a type, such as a method.</summary>
  /// <param name="source">The source of the member.</param>
  /// <returns>The member.</returns>
  /// <exception cref="ArgumentException">The source is no member, or one with syntax the tree does not have.</exception>
  public static CsMember Member(string source) {
    var tree = CSharpSyntaxTree.ParseText("class __Polypiler {\n" + source + "\n}");
    Refuse(tree);
    var type = (TypeDeclarationSyntax)tree.GetCompilationUnitRoot().Members.Single();
    return Read(type.Members.Single());
  }

  /// <summary>An expression.</summary>
  /// <param name="source">The source of the expression.</param>
  /// <returns>The expression.</returns>
  /// <exception cref="ArgumentException">The source is no expression, or one with syntax the tree does not have.</exception>
  public static CsExpression Expression(string source) {
    var expression = SyntaxFactory.ParseExpression(source);
    var problems = expression.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
    if (problems.Count > 0 || expression.FullSpan.Length != source.Length)
      throw new ArgumentException($"'{source}' is no C# expression: {string.Join("; ", problems)}", nameof(source));

    return Read(expression);
  }

  /// <summary>A file, parsed at a language version.</summary>
  /// <param name="source">The text of the file.</param>
  /// <param name="version">The language version, as <c>--langversion</c> names it, such as <c>7.3</c>.</param>
  /// <param name="problems">What the parser reports: syntax the language version does not have, among others.</param>
  /// <returns>The file, or <see langword="null"/> when the parser reports a problem.</returns>
  public static CsCompilationUnit? CompilationUnit(string source, string version, out IReadOnlyList<string> problems) {
    if (!LanguageVersionFacts.TryParse(version, out var language))
      throw new ArgumentException($"'{version}' is no language version of C#.", nameof(version));

    var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(language));
    problems = [.. tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString())];
    if (problems.Count > 0)
      return null;

    var root = tree.GetCompilationUnitRoot();
    return new CsCompilationUnit([], [.. root.Members.Select(Read)]);
  }

  private static void Refuse(SyntaxTree tree) {
    var problems = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
    if (problems.Count > 0)
      throw new ArgumentException($"The source is no C# member: {string.Join("; ", problems)}");
  }

  private static ArgumentException Unknown(SyntaxNode node) => new($"The syntax tree has no node for '{node.Kind()}': {node}");

  /// <summary>Whether two tokens stand on different lines.</summary>
  private static bool Apart(SyntaxToken first, SyntaxToken second)
    => first.GetLocation().GetLineSpan().EndLinePosition.Line != second.GetLocation().GetLineSpan().StartLinePosition.Line;

  /// <summary>What stands in front of a node: a blank line where its first token is preceded by an empty line, and its comments.</summary>
  private static CsTrivia Trivia(SyntaxNode node) {
    var trivia = node.GetLeadingTrivia();
    var blank = trivia.Any(item => item.IsKind(SyntaxKind.EndOfLineTrivia));
    var comments = trivia.Where(item => item.IsKind(SyntaxKind.SingleLineCommentTrivia)).Select(item => {
      var text = item.ToString()[2..];
      return text.StartsWith(' ') ? text[1..] : text;
    }).ToList();
    return !blank && comments.Count == 0 ? CsTrivia.None : new CsTrivia(blank, comments);
  }

  private static IReadOnlyList<string> Modifiers(SyntaxTokenList modifiers) => [.. modifiers.Select(modifier => modifier.Text)];

  private static CsParameter Read(ParameterSyntax parameter)
    => new(new CsType(parameter.Type!.ToString()), parameter.Identifier.Text, parameter.Modifiers.Count == 0 ? null : string.Join(' ', parameter.Modifiers.Select(modifier => modifier.Text)));

  private static IReadOnlyList<CsParameter> Read(ParameterListSyntax parameters) => [.. parameters.Parameters.Select(Read)];

  private static CsMember Read(MemberDeclarationSyntax member) {
    CsMember read = member switch {
      MethodDeclarationSyntax method => new CsMethod(
        Modifiers(method.Modifiers), new CsType(method.ReturnType.ToString()), method.Identifier.Text,
        method.TypeParameterList is null ? [] : [.. method.TypeParameterList.Parameters.Select(parameter => parameter.Identifier.Text)],
        Read(method.ParameterList), method.Body is null ? null : Block(method.Body), method.ExpressionBody is null ? null : Read(method.ExpressionBody.Expression)),
      OperatorDeclarationSyntax @operator => new CsMethod(
        Modifiers(@operator.Modifiers), new CsType(@operator.ReturnType.ToString()), "operator " + @operator.OperatorToken.Text, [],
        Read(@operator.ParameterList), @operator.Body is null ? null : Block(@operator.Body), @operator.ExpressionBody is null ? null : Read(@operator.ExpressionBody.Expression)),
      ConstructorDeclarationSyntax constructor => new CsConstructor(Modifiers(constructor.Modifiers), constructor.Identifier.Text, Read(constructor.ParameterList), Block(constructor.Body!)),
      FieldDeclarationSyntax { Declaration.Variables: [var variable] } field => new CsField(
        Modifiers(field.Modifiers), new CsType(field.Declaration.Type.ToString()), variable.Identifier.Text, variable.Initializer is null ? null : Read(variable.Initializer.Value)),
      TypeDeclarationSyntax type => new CsTypeDeclaration(
        Modifiers(type.Modifiers),
        type is RecordDeclarationSyntax record ? "record " + record.ClassOrStructKeyword.Text : type.Keyword.Text,
        type.Identifier.Text,
        type.ParameterList is null ? null : Read(type.ParameterList),
        type.BaseList is null ? [] : [.. type.BaseList.Types.Select(baseType => new CsType(baseType.Type.ToString()))],
        type.OpenBraceToken.IsKind(SyntaxKind.None) ? null : [.. type.Members.Select(Read)]),
      FileScopedNamespaceDeclarationSyntax scope => new CsNamespace(scope.Name.ToString(), true, [.. scope.Members.Select(Read)]),
      NamespaceDeclarationSyntax scope => new CsNamespace(scope.Name.ToString(), false, [.. scope.Members.Select(Read)]),
      _ => throw Unknown(member)
    };
    return read with { Leading = Trivia(member) };
  }

  private static CsBlock Block(BlockSyntax block)
    => new([.. block.Statements.Select(Read)]) { Inline = !Apart(block.OpenBraceToken, block.CloseBraceToken), Leading = Trivia(block) };

  private static CsLocalDeclaration Declaration(VariableDeclarationSyntax declaration) {
    if (declaration.Variables is not [var variable])
      throw Unknown(declaration);

    return new CsLocalDeclaration(new CsType(declaration.Type.ToString()), variable.Identifier.Text, variable.Initializer is null ? null : Read(variable.Initializer.Value));
  }

  private static CsStatement Read(StatementSyntax statement) {
    CsStatement read = statement switch {
      BlockSyntax block => Block(block),
      LocalDeclarationStatementSyntax { Modifiers.Count: 0 } declaration => Declaration(declaration.Declaration),
      ExpressionStatementSyntax expression => new CsExpressionStatement(Read(expression.Expression)),
      IfStatementSyntax conditional => new CsIf(Read(conditional.Condition), Read(conditional.Statement), conditional.Else is null ? null : Read(conditional.Else.Statement)),
      WhileStatementSyntax loop => new CsWhile(Read(loop.Condition), Read(loop.Statement)),
      DoStatementSyntax loop => new CsDo(Read(loop.Statement), Read(loop.Condition)),
      ForStatementSyntax { Initializers.Count: 0 } loop => new CsFor(
        loop.Declaration is null ? null : Declaration(loop.Declaration), loop.Condition is null ? null : Read(loop.Condition),
        [.. loop.Incrementors.Select(Read)], Read(loop.Statement)),
      SwitchStatementSyntax choice => new CsSwitch(Read(choice.Expression), [.. choice.Sections.Select(section => new CsSwitchSection(
        [.. section.Labels.Select(label => label switch {
          CaseSwitchLabelSyntax value => Read(value.Value),
          DefaultSwitchLabelSyntax => (CsExpression?)null,
          _ => throw Unknown(label)
        })],
        [.. section.Statements.Select(Read)]))]),
      BreakStatementSyntax => new CsBreak(),
      ContinueStatementSyntax => new CsContinue(),
      ReturnStatementSyntax result => new CsReturn(result.Expression is null ? null : Read(result.Expression)),
      ThrowStatementSyntax { Expression: not null } exception => new CsThrow(Read(exception.Expression)),
      CheckedStatementSyntax context => new CsCheckedStatement(context.IsKind(SyntaxKind.CheckedStatement), Block(context.Block)),
      _ => throw Unknown(statement)
    };
    return read with { Leading = Trivia(statement) };
  }

  private static CsExpression Read(ExpressionSyntax expression) => expression switch {
    LiteralExpressionSyntax literal => Literal(literal),
    IdentifierNameSyntax or PredefinedTypeSyntax or GenericNameSyntax or AliasQualifiedNameSyntax or ThisExpressionSyntax => new CsName(expression.ToString()),
    QualifiedNameSyntax qualified => new CsMemberAccess(Read(qualified.Left), qualified.Right.ToString()),
    MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } access => new CsMemberAccess(Read(access.Expression), access.Name.ToString()),
    InvocationExpressionSyntax call => new CsInvocation(Read(call.Expression), Arguments(call.ArgumentList)),
    ElementAccessExpressionSyntax { ArgumentList.Arguments: [var index] } element => new CsElementAccess(Read(element.Expression), Read(index.Expression)),
    CastExpressionSyntax cast => new CsCast(new CsType(cast.Type.ToString()), Read(cast.Expression)),
    PrefixUnaryExpressionSyntax unary => new CsPrefixUnary(unary.OperatorToken.Text, Read(unary.Operand)),
    PostfixUnaryExpressionSyntax unary => new CsPostfixUnary(Read(unary.Operand), unary.OperatorToken.Text),
    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression } test => new CsIsType(Read(test.Left), new CsType(test.Right.ToString())),
    BinaryExpressionSyntax binary => new CsBinary(Read(binary.Left), binary.OperatorToken.Text, Read(binary.Right)) { BreakBefore = Apart(binary.Left.GetLastToken(), binary.OperatorToken) },
    AssignmentExpressionSyntax assignment => new CsAssignment(Read(assignment.Left), assignment.OperatorToken.Text, Read(assignment.Right)),
    ConditionalExpressionSyntax choice => new CsConditional(Read(choice.Condition), Read(choice.WhenTrue), Read(choice.WhenFalse)) {
      BreakBeforeQuestion = Apart(choice.Condition.GetLastToken(), choice.QuestionToken),
      BreakBeforeColon = Apart(choice.WhenTrue.GetLastToken(), choice.ColonToken)
    },
    ParenthesizedExpressionSyntax parenthesized => new CsParenthesized(Read(parenthesized.Expression)),
    CheckedExpressionSyntax context => new CsCheckedExpression(context.IsKind(SyntaxKind.CheckedExpression), Read(context.Expression)),
    ObjectCreationExpressionSyntax { Initializer: null } creation => new CsObjectCreation(new CsType(creation.Type.ToString()), creation.ArgumentList is null ? [] : Arguments(creation.ArgumentList)),
    ArrayCreationExpressionSyntax { Type.RankSpecifiers: [{ Sizes: [var size] }] } creation => new CsArrayCreation(
      new CsType(creation.Type.ElementType.ToString()), size is OmittedArraySizeExpressionSyntax ? null : Read(size),
      creation.Initializer is null ? null : Initializer(creation.Initializer)),
    StackAllocArrayCreationExpressionSyntax { Initializer: null, Type: ArrayTypeSyntax { RankSpecifiers: [{ Sizes: [var size] }] } array }
      => new CsArrayCreation(new CsType(array.ElementType.ToString()), Read(size), null) { OnStack = true },
    InitializerExpressionSyntax { RawKind: (int)SyntaxKind.ArrayInitializerExpression } initializer => Initializer(initializer),
    DefaultExpressionSyntax value => new CsDefaultExpression(new CsType(value.Type.ToString())),
    DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax } => new CsName("_"),
    _ => throw Unknown(expression)
  };

  private static CsArrayInitializer Initializer(InitializerExpressionSyntax initializer)
    => new([.. initializer.Expressions.Select(Read)]) { Tabulated = Apart(initializer.OpenBraceToken, initializer.CloseBraceToken) };

  private static IReadOnlyList<CsExpression> Arguments(ArgumentListSyntax arguments)
    => [.. arguments.Arguments.Select(argument => argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ? new CsOutArgument(Read(argument.Expression)) : Read(argument.Expression))];

  private static CsExpression Literal(LiteralExpressionSyntax literal) {
    var token = literal.Token;
    switch (literal.Kind()) {
      case SyntaxKind.TrueLiteralExpression:
        return new CsBooleanLiteral(true);
      case SyntaxKind.FalseLiteralExpression:
        return new CsBooleanLiteral(false);
      case SyntaxKind.DefaultLiteralExpression:
        return new CsDefaultLiteral();
      case SyntaxKind.StringLiteralExpression:
        return new CsStringLiteral((string)token.Value!);
      case SyntaxKind.NumericLiteralExpression when token.Value is float or double or decimal:
        return new CsRealLiteral(Convert.ToDouble(token.Value, System.Globalization.CultureInfo.InvariantCulture), token.Value is float) { Raw = token.Text };
      case SyntaxKind.NumericLiteralExpression:
        var (value, suffix) = CSharp.Printing.CSharpLiterals.ParseInteger(token.Text) ?? throw Unknown(literal);
        return new CsIntegerLiteral(value, suffix) { Raw = token.Text };
      default:
        throw Unknown(literal);
    }
  }


}
