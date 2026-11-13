using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Backends.JavaScript;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// Checks that generated JavaScript really is at the level it claims. The file is parsed by Acornima
/// at the ECMAScript version of the profile, where a newer construct is a syntax error, and its
/// tree is searched for what the capability table does not give the profile: a global, a member of
/// a global, a method, or a piece of syntax the emitter is never to write. Nothing here is taken
/// from the emitter: the table is the only thing the two share.
/// </summary>
internal static class GeneratedJavaScript {

  /// <summary>Everything a file uses that its profile does not have; empty when it is at its level.</summary>
  /// <param name="code">The text of the generated file.</param>
  /// <param name="profile">The profile the file was written for.</param>
  /// <param name="capabilities">What each level has; the standard table when <see langword="null"/>.</param>
  public static IReadOnlyList<string> Problems(string code, JavaScriptProfile profile, JavaScriptCapabilities? capabilities = null) {
    var table = capabilities ?? JavaScriptCapabilities.Standard;
    var parser = new Parser(new ParserOptions { EcmaVersion = VersionOf(profile.Level) });
    Node program;
    try {
      program = profile.Module == JavaScriptModuleFormat.Esm ? parser.ParseModule(code) : parser.ParseScript(code);
    } catch (ParseErrorException exception) {
      return [$"not {profile.LevelName}: {exception.Message}"];
    }

    var problems = new List<string>();
    var declared = new HashSet<string>();
    Declarations(program, declared);
    if (profile.Module == JavaScriptModuleFormat.Cjs)
      declared.Add("exports");

    Uses(program, null, declared, name => {
      var entry = table.Entries.FirstOrDefault(candidate => candidate.Name == name);
      if (entry is null)
        problems.Add($"'{name}' is not in the capability table");
      else if (!entry.Emitted)
        problems.Add($"'{name}' is never to be emitted");
      else if (!table.Has(name, profile))
        problems.Add($"'{name}' is not available in {profile}");
    });
    return [.. problems.Distinct()];
  }

  /// <summary>The ECMAScript version Acornima parses a level at.</summary>
  public static EcmaVersion VersionOf(JavaScriptLevel level) => level switch {
    JavaScriptLevel.Es5 => EcmaVersion.ES5,
    JavaScriptLevel.Es2015 => EcmaVersion.ES2015,
    JavaScriptLevel.Es2017 => EcmaVersion.ES2017,
    JavaScriptLevel.Es2020 => EcmaVersion.ES2020,
    _ => EcmaVersion.ES2022
  };

  /// <summary>Every name the file declares itself: functions, their parameters and variables.</summary>
  private static void Declarations(Node node, HashSet<string> declared) {
    switch (node) {
      case IFunction function:
        if (function.Id is not null)
          declared.Add(function.Id.Name);

        foreach (var parameter in function.Params.OfType<Identifier>())
          declared.Add(parameter.Name);

        break;
      case VariableDeclarator { Id: Identifier variable }:
        declared.Add(variable.Name);
        break;
    }

    foreach (var child in node.ChildNodes)
      Declarations(child, declared);
  }

  /// <summary>
  /// Reports every use of something the file did not declare: a free identifier, the member of one
  /// that is read (<c>Math.imul</c>), the method called on anything else (<c>.fill</c>), and the
  /// syntax the table lists as never emitted.
  /// </summary>
  private static void Uses(Node node, Node? parent, HashSet<string> declared, System.Action<string> report) {
    switch (node) {
      case Identifier identifier when !declared.Contains(identifier.Name) && IsReference(identifier, parent):
        report(identifier.Name);
        break;
      case MemberExpression { Computed: false, Property: Identifier member } access:
        if (access.Object is Identifier global && !declared.Contains(global.Name))
          report($"{global.Name}.{member.Name}");
        else if (parent is CallExpression call && call.Callee == node)
          report("." + member.Name);

        break;
      case ArrowFunctionExpression:
        report("=>");
        break;
      case BinaryExpression { Operator: Operator.Exponentiation }:
        report("**");
        break;
      case NumericLiteral literal when literal.Raw.Contains('_'):
        report("numeric separators");
        break;
    }

    foreach (var child in node.ChildNodes)
      Uses(child, node, declared, report);
  }

  /// <summary>Whether an identifier stands for a variable, rather than naming a property or a member.</summary>
  private static bool IsReference(Identifier identifier, Node? parent) => parent switch {
    MemberExpression { Computed: false } access => access.Object == identifier,
    Property { Computed: false } property => property.Value == identifier,
    _ => true
  };

}
