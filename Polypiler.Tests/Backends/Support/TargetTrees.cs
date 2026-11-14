using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Passes;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.JavaScript.Passes;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Target;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Backends.Testing;

/// <summary>A source translated in one variant: the target syntax tree after the passes and the text the printer made of it.</summary>
/// <param name="Text">The generated file.</param>
/// <param name="CSharp">The tree of a C# file, or <see langword="null"/>.</param>
/// <param name="JavaScript">The tree of a JavaScript file, or <see langword="null"/>.</param>
/// <param name="CSharpProfile">The C# profile, or <see langword="null"/>.</param>
/// <param name="JavaScriptProfile">The JavaScript profile, or <see langword="null"/>.</param>
internal sealed record TargetTree(string Text, CsCompilationUnit? CSharp, JsProgram? JavaScript, CSharpProfile? CSharpProfile, JavaScriptProfile? JavaScriptProfile);

/// <summary>
/// Translates a source of the prototype fixtures in a variant of <see cref="GoldenOutputs"/> in process,
/// as <c>translate</c> does with the default optimization level - frontend, IR optimizer, legalization,
/// check, tree, target passes, printer - and checks what came out: that it parses back to the tree it
/// was printed from, and that no helper is left that nothing names.
/// </summary>
internal static class TargetTrees {

  private static readonly string _sources = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");

  private static string? Option(GoldenVariant variant, string name) {
    var index = Array.IndexOf(variant.Options, name);
    return index < 0 ? null : index + 1 < variant.Options.Length && !variant.Options[index + 1].StartsWith("--", StringComparison.Ordinal) ? variant.Options[index + 1] : "";
  }

  /// <summary>The module of a source after the IR optimizer, or <see langword="null"/> when the frontend refuses it.</summary>
  private static IrModule? Imported(string source, bool remainderFromQuotient) {
    var path = Path.Combine(_sources, source.Replace('/', Path.DirectorySeparatorChar));
    var text = File.ReadAllText(path);
    var module = GoldenOutputs.LanguageOf(source) == "csharp"
      ? CSharpFrontend.Import(Path.GetFileName(path), text).Module
      : JavaScriptFrontend.Import(Path.GetFileName(path), text).Module;
    return module is null ? null : Optimizer.Run(module, new OptimizerOptions { Target = new RuleTarget(remainderFromQuotient) }).Module;
  }

  /// <summary>The source translated in the variant, or <see langword="null"/> where the translation refuses it.</summary>
  /// <param name="source">The source, as <see cref="GoldenOutputs.All"/> has it.</param>
  /// <param name="variant">The variant.</param>
  public static TargetTree? Translate(string source, GoldenVariant variant) {
    var mode = Option(variant, "--float") == "strict" ? FloatMode.Strict : FloatMode.Accurate;
    if (variant.Target == "csharp") {
      if (Imported(source, CSharpCapabilities.Default.RemainderFromQuotient) is not { } module)
        return null;

      var profile = CSharpProfile.Parse(Option(variant, "--profile")!);
      var lowered = CSharpEmitter.Legalize(module, () => PrecomputedImplementations.Module, profile, mode);
      if (CSharpEmitter.Check(lowered, profile).Count > 0)
        return null;

      var tree = CSharpEmitter.Build(lowered, profile, mode, Option(variant, "--namespace"), null, TargetPassOptions.Default).Tree;
      return new(CSharpPrinter.Print(tree, profile), tree, null, profile, null);
    }

    if (Imported(source, JavaScriptCapabilities.Standard.RemainderFromQuotient) is not { } script)
      return null;

    var level = JavaScriptProfile.Parse(Option(variant, "--profile"), Option(variant, "--module"), Option(variant, "--global"), Option(variant, "--typed-arrays") is not null);
    var legalized = JavaScriptEmitter.Legalize(script, () => PrecomputedImplementations.Module, level, mode);
    if (JavaScriptEmitter.Check(legalized, level).Count > 0)
      return null;

    var program = JavaScriptEmitter.Build(legalized, level, mode, null, TargetPassOptions.Default).Tree;
    return new(JavaScriptPrinter.Print(program, level), null, program, null, level);
  }

  /// <summary>
  /// What the parser of the target says of the text, at the profile's language version or ECMAScript
  /// version, or that it reads another tree than the one printed; <see langword="null"/> when it reads
  /// the tree without a problem.
  /// </summary>
  public static string? ParseBackProblem(TargetTree translated) {
    if (translated.CSharp is { } unit) {
      if (CSharpSyntaxReader.CompilationUnit(translated.Text, translated.CSharpProfile!.LanguageVersionName, out var problems) is not { } parsed)
        return string.Join("; ", problems);

      return SyntaxShape.Of(parsed) == SyntaxShape.Of(unit) ? null : "the text parses to another tree";
    }

    var profile = translated.JavaScriptProfile!;
    var program = translated.JavaScript!;
    if (JavaScriptSyntaxReader.Program(translated.Text, GeneratedJavaScript.VersionOf(profile.Level), program.IsModule, out var problem) is not { } read)
      return problem;

    return SyntaxShape.Of(read) == SyntaxShape.Of(program with { Header = [] }) ? null : "the text parses to another tree";
  }

  /// <summary>The helpers the file declares that nothing in it names.</summary>
  public static IReadOnlyList<string> UnreferencedHelpers(TargetTree translated) {
    if (translated.CSharp is { } unit) {
      var members = CSharpTargetPasses.ModuleClass(unit)?.Members ?? [];
      // A generic helper may be called with its type arguments stated: ViewSet<byte>(…) names ViewSet.
      int Count(CsNode node, string name) => CSharpSyntaxRewriter.DescendantsAndSelf(node).Count(item => item is CsName { Identifier: var identifier }
        && (identifier == name || identifier.StartsWith(name + "<", System.StringComparison.Ordinal)));
      return [.. members.OfType<CsMethod>().Where(method => method.IsHelper && Count(unit, method.Name) == members.OfType<CsMethod>().Where(other => other.Name == method.Name).Sum(other => Count(other, method.Name))).Select(method => method.Name)];
    }

    var program = translated.JavaScript!;
    int Named(JsNode node, string name) => JavaScriptSyntaxRewriter.DescendantsAndSelf(node).Count(item => item is JsIdentifier { Name: var identifier } && identifier == name);
    return [.. JavaScriptTargetPasses.ModuleScope(program).OfType<JsFunctionDeclaration>().Where(function => function.IsHelper && Named(program, function.Name) == Named(function, function.Name)).Select(function => function.Name)];
  }

}
