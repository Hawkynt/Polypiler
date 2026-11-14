using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.CSharp.Passes;

/// <summary>
/// The target passes of the C# backend, in the order they run: helper inlining (O0500), then the
/// removal of what nothing references any more (O0501), so that a helper whose every call was inlined
/// is gone from the class.
/// </summary>
public static class CSharpTargetPasses {

  /// <summary>The pipeline the emitter runs between building the tree and printing it.</summary>
  public static TargetPipeline<CsCompilationUnit, CSharpProfile> Pipeline { get; } = new([new CSharpHelperInlining(), new CSharpUnusedDeclarations()]);

  /// <summary>The class of a module in a file: the one type declaration of kind <c>class</c>, at the top or in the namespace.</summary>
  /// <param name="unit">The file.</param>
  /// <returns>The class, or <see langword="null"/> when the file has none.</returns>
  public static CsTypeDeclaration? ModuleClass(CsCompilationUnit unit)
    => unit.Members.SelectMany(member => member is CsNamespace scope ? scope.Members : [member]).OfType<CsTypeDeclaration>().FirstOrDefault(type => type.Keyword == "class");

  /// <summary>The file with its module class replaced.</summary>
  /// <param name="unit">The file.</param>
  /// <param name="old">The class as it is.</param>
  /// <param name="replacement">The class to put in its place.</param>
  /// <returns>The file.</returns>
  public static CsCompilationUnit WithModuleClass(CsCompilationUnit unit, CsTypeDeclaration old, CsTypeDeclaration replacement)
    => CSharpSyntaxRewriter.Rewrite(unit, node => ReferenceEquals(node, old) ? replacement : node);

  /// <summary>How many nodes an expression has, parentheses not counted: the measure of a body the cost rule of the inlining compares.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The count.</returns>
  public static int Size(CsExpression expression) => CSharpSyntaxRewriter.DescendantsAndSelf(expression).Count(node => node is not CsParenthesized);

}

/// <summary>
/// O0500, helper inlining in C#: a call of a private helper whose body is one returned expression is
/// replaced by that expression with the arguments put in for the parameters, where the profile's
/// cost rule says the call is not cheaper (see <see cref="Budget"/>). The helpers are written to give
/// the same result in a checked and an unchecked context, so the body stands where the call stood;
/// an argument of another type than its parameter is converted as the call converts it, and the
/// result is cast to the return type unless the body is that cast already.
/// <para>
/// It is never inlined where that could change what the program does or whether it compiles: a
/// recursive, generic, void or multi-statement helper, a parameter passed by reference, an argument
/// that is no variable, literal or operation on those that cannot trap and is read more than once, an
/// argument of a type the call converts implicitly to no other, a body that names a variable the
/// calling method declares, and constant arguments that would make the body an expression the
/// compiler evaluates: all of them constant, or one of them a constant divisor.
/// </para>
/// </summary>
public sealed class CSharpHelperInlining : ITargetPass<CsCompilationUnit, CSharpProfile> {

  /// <inheritdoc/>
  public string Id => "O0500";

  /// <inheritdoc/>
  public string Name => "helper inlining";

  /// <summary>
  /// The cost rule: the largest body, in nodes, that is inlined in a profile. Every .NET runtime of the
  /// profiles calls a small static method at the cost of a few instructions where its JIT does not
  /// inline it itself, which is about what a dozen operators cost; a larger body is left a call.
  /// </summary>
  /// <param name="profile">The profile.</param>
  /// <returns>The number of nodes.</returns>
  public static int Budget(CSharpProfile profile) => 16;

  /// <summary>The integer types a constant <c>int</c> converts to implicitly where it holds the value, with their ranges.</summary>
  private static readonly Dictionary<string, (long Low, ulong High)> _ranges = new() {
    ["sbyte"] = (sbyte.MinValue, (ulong)sbyte.MaxValue), ["byte"] = (0, byte.MaxValue), ["short"] = (short.MinValue, (ulong)short.MaxValue),
    ["ushort"] = (0, ushort.MaxValue), ["uint"] = (0, uint.MaxValue), ["ulong"] = (0, ulong.MaxValue)
  };

  /// <summary>The C# types an implicit numeric conversion leads to from each type (ECMA-334, "Implicit numeric conversions").</summary>
  private static readonly Dictionary<string, string[]> _widening = new() {
    ["sbyte"] = ["short", "int", "long", "float", "double"],
    ["byte"] = ["short", "ushort", "int", "uint", "long", "ulong", "float", "double"],
    ["short"] = ["int", "long", "float", "double"],
    ["ushort"] = ["int", "uint", "long", "ulong", "float", "double"],
    ["int"] = ["long", "float", "double"],
    ["uint"] = ["long", "ulong", "float", "double"],
    ["long"] = ["float", "double"],
    ["ulong"] = ["float", "double"],
    ["float"] = ["double"]
  };

  /// <inheritdoc/>
  public CsCompilationUnit Run(CsCompilationUnit tree, CSharpProfile profile, Action<string> applied) {
    if (CSharpTargetPasses.ModuleClass(tree) is not { Members: { } members } type)
      return tree;

    // Helpers of one name are overloads, told apart by the types of their parameters.
    var helpers = members.OfType<CsMethod>().Select(method => (Method: method, Body: Inlinable(method, profile))).Where(helper => helper.Body is not null)
      .GroupBy(helper => helper.Method.Name, StringComparer.Ordinal)
      .ToDictionary(group => group.Key, group => group.Select(helper => (helper.Method, Body: helper.Body!)).ToList(), StringComparer.Ordinal);
    if (helpers.Count == 0)
      return tree;

    var overloaded = members.OfType<CsMethod>().GroupBy(method => method.Name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
    var rewritten = members.Select(member => member is CsMethod method && !helpers.ContainsKey(method.Name) ? this.Inline(method, helpers, overloaded, applied) : member).ToList();
    return rewritten.SequenceEqual(members, ReferenceEqualityComparer.Instance) ? tree : CSharpTargetPasses.WithModuleClass(tree, type, type with { Members = rewritten });
  }

  /// <summary>The returned expression of a helper that may be inlined, or <see langword="null"/>.</summary>
  private static CsExpression? Inlinable(CsMethod method, CSharpProfile profile) {
    if (!method.IsHelper || method.TypeParameters.Count > 0 || method.ReturnType == CsType.Void || method.Parameters.Any(parameter => parameter.Modifier is not null))
      return null;

    var body = method.ExpressionBody ?? (method.Body is { Statements: [CsReturn { Value: { } value }] } ? value : null);
    if (body is null || CSharpTargetPasses.Size(body) > Budget(profile))
      return null;

    var nodes = CSharpSyntaxRewriter.DescendantsAndSelf(body).ToList();
    var recursive = nodes.Any(node => node is CsName { Identifier: var name } && name == method.Name);
    var effects = nodes.Any(node => node is CsAssignment or CsOutArgument or CsPostfixUnary or CsPrefixUnary { Operator: "++" or "--" });
    return recursive || effects ? null : body;
  }

  private CsMethod Inline(CsMethod method, Dictionary<string, List<(CsMethod Method, CsExpression Body)>> helpers, HashSet<string> overloaded, Action<string> applied) {
    var locals = CSharpSyntaxRewriter.DescendantsAndSelf(method).OfType<CsLocalDeclaration>().Select(local => local.Name)
      .Concat(method.Parameters.Select(parameter => parameter.Name)).Select(name => name.TrimStart('@')).ToHashSet(StringComparer.Ordinal);
    return CSharpSyntaxRewriter.Rewrite(method, node => {
      if (node is not CsInvocation { Target: CsName { Identifier: var name } } call || !helpers.TryGetValue(name, out var candidates))
        return node;

      if (Resolved(candidates, call, overloaded.Contains(name)) is not { } helper || Substituted(helper.Method, helper.Body, call, locals) is not { } inlined)
        return node;

      applied($"'{name}' into '{method.Name}'");
      return inlined;
    });
  }

  /// <summary>
  /// The helper a call calls: the one of its name, or of overloads the one whose parameters have
  /// exactly the types of the arguments; <see langword="null"/> where that is not certain.
  /// </summary>
  private static (CsMethod Method, CsExpression Body)? Resolved(List<(CsMethod Method, CsExpression Body)> candidates, CsInvocation call, bool overloaded) {
    if (!overloaded)
      return candidates[0];

    var types = call.Arguments.Select(TypeOf).ToList();
    var exact = candidates.Where(candidate => candidate.Method.Parameters.Select(parameter => (string?)parameter.ParameterType.Name).SequenceEqual(types)).ToList();
    return exact.Count == 1 ? exact[0] : null;
  }

  /// <summary>The body of a helper with the arguments of a call in place of its parameters, or <see langword="null"/> where that is not allowed.</summary>
  private static CsExpression? Substituted(CsMethod helper, CsExpression body, CsInvocation call, HashSet<string> locals) {
    var parameters = helper.Parameters;
    if (call.Arguments.Count != parameters.Count || call.Arguments.All(IsLiteral))
      return null;

    var nodes = CSharpSyntaxRewriter.DescendantsAndSelf(body).ToList();
    if (call.Arguments.Any(IsLiteral) && nodes.Any(node => node is CsBinary { Operator: "/" or "%" }))
      return null;

    // A name of the body that is no parameter means the same at the call only if the calling method declares no variable of it.
    var names = parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
    if (nodes.OfType<CsName>().Any(node => !names.Contains(node.Identifier) && locals.Contains(node.Identifier.TrimStart('@'))))
      return null;

    var replacements = new Dictionary<string, CsExpression>(StringComparer.Ordinal);
    for (var index = 0; index < parameters.Count; ++index) {
      var argument = call.Arguments[index];
      var uses = nodes.Count(node => node is CsName { Identifier: var name } && name == parameters[index].Name);
      if (!IsTrivial(argument) && (uses > 1 || !IsPure(argument)))
        return null;

      if (Converted(argument, parameters[index].ParameterType.Name) is not { } converted)
        return null;

      replacements[parameters[index].Name] = converted;
    }

    var substituted = CSharpSyntaxRewriter.Rewrite(body, node => node is CsName { Identifier: var name } && replacements.TryGetValue(name, out var argument) ? argument : node);
    var returns = helper.ReturnType.Name;
    var result = substituted is CsCast { TargetType.Name: var cast } && cast == returns ? substituted : new CsCast(helper.ReturnType, new CsParenthesized(substituted));
    return result with { Type = call.Type };
  }

  private static bool IsLiteral(CsExpression expression) => expression is CsIntegerLiteral or CsRealLiteral or CsBooleanLiteral;

  /// <summary>Whether an argument may be read any number of times, or not at all: a variable or a literal.</summary>
  private static bool IsTrivial(CsExpression expression) => IsLiteral(expression) || expression is CsName { Identifier: var name } && !name.Contains('.') && !name.Contains("::", StringComparison.Ordinal);

  /// <summary>Whether an argument evaluates without an effect and without trapping in any context: bitwise operations and comparisons of variables and literals.</summary>
  private static bool IsPure(CsExpression expression) => expression switch {
    CsParenthesized parenthesized => IsPure(parenthesized.Inner),
    CsPrefixUnary { Operator: "!" or "~" } unary => IsPure(unary.Operand),
    CsBinary { Operator: "&" or "|" or "^" or "<<" or ">>" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" } binary => IsPure(binary.Left) && IsPure(binary.Right),
    _ => IsTrivial(expression)
  };

  /// <summary>An argument as the parameter receives it: as it is where its type is the parameter's, cast where the call converts it implicitly, else <see langword="null"/>.</summary>
  private static CsExpression? Converted(CsExpression argument, string parameter) {
    var type = TypeOf(argument);
    if (type is null)
      return null;

    if (type == parameter)
      return argument;

    // A constant int converts to another integer type that holds it (ECMA-334, "Implicit constant expression conversions"), which the cast does too.
    var implicitly = _widening.TryGetValue(type, out var wider) && wider.Contains(parameter)
      || argument is CsIntegerLiteral { Value: var value } && type == "int" && _ranges.TryGetValue(parameter, out var range) && value >= range.Low && value <= range.High;
    return implicitly ? new CsCast(new CsType(parameter), argument) : null;
  }

  /// <summary>The C# type of an argument where it is known: that of a literal by its suffix, else the one the IR type of the node says.</summary>
  private static string? TypeOf(CsExpression expression) => expression switch {
    CsParenthesized parenthesized => TypeOf(parenthesized.Inner),
    CsBooleanLiteral => "bool",
    CsRealLiteral real => real.IsSingle ? "float" : "double",
    CsIntegerLiteral integer => integer.Suffix switch {
      CsIntegerSuffix.U => integer.Value <= uint.MaxValue ? "uint" : "ulong",
      CsIntegerSuffix.L => integer.Value >= long.MinValue && integer.Value <= long.MaxValue ? "long" : "ulong",
      CsIntegerSuffix.UL => "ulong",
      _ => integer.Value >= int.MinValue && integer.Value <= int.MaxValue ? "int"
        : integer.Value >= 0 && integer.Value <= uint.MaxValue ? "uint"
        : integer.Value >= long.MinValue && integer.Value <= long.MaxValue ? "long" : "ulong"
    },
    _ => expression.Type is { Element: null, Declaration: null } type && type.Kind != Polypiler.Core.IR.IrTypeKind.Void ? CSharpEmitter.Keyword(type) : null
  };

}

/// <summary>
/// O0501, unused declaration removal in C#: a helper or a private constant of the module class that
/// nothing in the file names any more is removed, again and again until every one left is named. The
/// emitter declares only the helpers the code calls; what this pass removes was made unused by the
/// passes before it, such as a helper whose every call was inlined. The empty line in front of a
/// removed member stays in front of the next one.
/// </summary>
public sealed class CSharpUnusedDeclarations : ITargetPass<CsCompilationUnit, CSharpProfile> {

  /// <inheritdoc/>
  public string Id => "O0501";

  /// <inheritdoc/>
  public string Name => "unused declaration removal";

  /// <inheritdoc/>
  public CsCompilationUnit Run(CsCompilationUnit tree, CSharpProfile profile, Action<string> applied) {
    while (CSharpTargetPasses.ModuleClass(tree) is { Members: { } members } type) {
      var unused = members.Where(member => Removable(member) is { } name && !Named(tree, member, name)).ToList();
      if (unused.Count == 0)
        return tree;

      var kept = new List<CsMember>();
      var blank = false;
      foreach (var member in members) {
        if (unused.Contains(member)) {
          applied($"'{Removable(member)}'");
          blank |= member.Leading.BlankLine;
          continue;
        }

        kept.Add(blank && !member.Leading.BlankLine ? member with { Leading = member.Leading with { BlankLine = true } } : member);
        blank = false;
      }

      tree = CSharpTargetPasses.WithModuleClass(tree, type, type with { Members = kept });
    }

    return tree;
  }

  /// <summary>The name of a member this pass may remove: a helper, or a private field; <see langword="null"/> for any other.</summary>
  private static string? Removable(CsMember member) => member switch {
    CsMethod { IsHelper: true } helper => helper.Name,
    CsField field when field.Modifiers.Contains("private") => field.Name,
    _ => null
  };

  /// <summary>Whether a name stands anywhere in the file outside the declaration itself: more often in the file than in the declaration.</summary>
  private static bool Named(CsCompilationUnit tree, CsMember declaration, string name) => Count(tree, name) > Count(declaration, name);

  /// <summary>How often a name stands in a node, also as a generic method with its type arguments stated, <c>name&lt;T&gt;</c>.</summary>
  private static int Count(CsNode node, string name)
    => CSharpSyntaxRewriter.DescendantsAndSelf(node).Count(item => item is CsName { Identifier: var identifier }
      && (identifier == name || (identifier.StartsWith(name, StringComparison.Ordinal) && identifier.Length > name.Length && identifier[name.Length] == '<')));

}
