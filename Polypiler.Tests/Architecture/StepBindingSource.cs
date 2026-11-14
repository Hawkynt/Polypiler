using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Tests.Acceptance.Support;

namespace Polypiler.Tests.Architecture;

/// <summary>
/// The step bindings of <c>Polypiler.Tests.Acceptance</c>, read from its source with the C# parser:
/// the unit tests do not reference the acceptance project, so they cannot load it. A pattern is a
/// string literal, a <c>const string</c> of the project, or a sum of them; a binding scoped to
/// features is one binding per feature.
/// </summary>
internal static class StepBindingSource {

  private static readonly Dictionary<string, StepKind> _kinds = new(StringComparer.Ordinal) {
    ["Given"] = StepKind.Given, ["When"] = StepKind.When, ["Then"] = StepKind.Then, ["StepDefinition"] = StepKind.Any
  };

  /// <summary>Every step binding of the C# files under a directory, without its build output.</summary>
  /// <exception cref="InvalidDataException">A pattern is neither a literal nor a constant of the project.</exception>
  public static IReadOnlyList<StepBinding> Read(string directory) {
    var separator = Path.DirectorySeparatorChar;
    var trees = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
      .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
      .Order(StringComparer.Ordinal)
      .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
      .ToList();
    var constants = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
    foreach (var field in trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()).Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword)))
      foreach (var variable in field.Declaration.Variables)
        constants.TryAdd(((TypeDeclarationSyntax)field.Parent!).Identifier.Text + "." + variable.Identifier.Text, variable.Initializer!.Value);

    var bindings = new List<StepBinding>();
    foreach (var type in trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())) {
      var attributes = type.AttributeLists.SelectMany(list => list.Attributes).ToList();
      if (!attributes.Any(attribute => Name(attribute) == "Binding"))
        continue;

      string?[] typeScopes = Scopes(attributes, type.Identifier.Text, constants);
      foreach (var method in type.Members.OfType<MethodDeclarationSyntax>()) {
        var own = method.AttributeLists.SelectMany(list => list.Attributes).ToList();
        string?[] scopes = own.Any(attribute => Name(attribute) == "Scope") ? Scopes(own, type.Identifier.Text, constants) : typeScopes;
        foreach (var attribute in own.Where(attribute => _kinds.ContainsKey(Name(attribute))))
          foreach (var feature in scopes)
            bindings.Add(new StepBinding(_kinds[Name(attribute)], Evaluate(attribute.ArgumentList!.Arguments[0].Expression, type.Identifier.Text, constants),
              type.Identifier.Text + "." + method.Identifier.Text, feature));
      }
    }

    return bindings;
  }

  /// <summary>The name of an attribute without its namespace and without <c>Attribute</c>.</summary>
  private static string Name(AttributeSyntax attribute) {
    var name = attribute.Name switch {
      QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
      IdentifierNameSyntax identifier => identifier.Identifier.Text,
      var other => other.ToString()
    };
    return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
  }

  /// <summary>The features of the <c>Scope</c> attributes, or one <see langword="null"/> for no scope.</summary>
  private static string?[] Scopes(IEnumerable<AttributeSyntax> attributes, string type, IReadOnlyDictionary<string, ExpressionSyntax> constants) {
    var features = attributes.Where(attribute => Name(attribute) == "Scope")
      .Select(attribute => attribute.ArgumentList!.Arguments.Single(argument => argument.NameEquals?.Name.Identifier.Text == "Feature"))
      .Select(argument => (string?)Evaluate(argument.Expression, type, constants))
      .ToArray();
    return features.Length > 0 ? features : [null];
  }

  /// <summary>The value of a constant string expression of the project.</summary>
  private static string Evaluate(ExpressionSyntax expression, string type, IReadOnlyDictionary<string, ExpressionSyntax> constants) => expression switch {
    LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
    BinaryExpressionSyntax sum when sum.IsKind(SyntaxKind.AddExpression) => Evaluate(sum.Left, type, constants) + Evaluate(sum.Right, type, constants),
    ParenthesizedExpressionSyntax inner => Evaluate(inner.Expression, type, constants),
    MemberAccessExpressionSyntax member when constants.TryGetValue(member.ToString(), out var value) => Evaluate(value, member.Expression.ToString(), constants),
    IdentifierNameSyntax name when constants.TryGetValue(type + "." + name.Identifier.Text, out var value) => Evaluate(value, type, constants),
    _ => throw new InvalidDataException($"{expression.SyntaxTree.FilePath}({expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1}): '{expression}' is not a constant string")
  };

}
