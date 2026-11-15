using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Cli;

/// <summary>
/// <c>--bind &lt;library&gt;=&lt;module or global&gt;</c> of <c>translate --to javascript</c> (#1332, ADR 0007): the binding
/// module a native library is called through, or for <c>iife</c> the global object that holds it.
/// </summary>
public sealed partial class TranslateCommand {

  /// <summary>The code of the caveat a native function called through a binding carries in the manifest.</summary>
  public const string UnverifiedExternal = "unverified-external";

  /// <summary>The bindings of <c>--bind</c> by their libraries; the command line joins the values of the repeated option by commas.</summary>
  private static Dictionary<string, string> BindingsOf(IReadOnlyDictionary<string, string> options) {
    var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!options.TryGetValue(Bind, out var list))
      return bindings;

    foreach (var item in list.Split(','))
      bindings.TryAdd(item.Split('=', 2)[0], item.Contains('=', StringComparison.Ordinal) ? item.Split('=', 2)[1] : "");

    return bindings;
  }

  /// <summary>What is wrong with <c>--bind</c>: an item without <c>=</c>, one that binds no library or to nothing, a library bound twice, or for <c>iife</c> a target that is no global.</summary>
  private static string? BindMisuse(IReadOnlyDictionary<string, string> options) {
    if (!options.TryGetValue(Bind, out var list))
      return null;

    var module = JavaScriptProfileOf(options).Profile!.Module;
    var libraries = new HashSet<string>(StringComparer.Ordinal);
    foreach (var item in list.Split(',')) {
      var parts = item.Split('=', 2);
      if (parts.Length < 2)
        return $"'{item}' is no binding; {Bind} takes <library>=<module or global>";

      if (JavaScriptProfile.BindingProblem(parts[0], parts[1], module) is { } problem)
        return problem;

      if (!libraries.Add(parts[0]))
        return $"the library '{parts[0]}' is bound twice";
    }

    return null;
  }

  /// <summary>What is wrong with a <c>--bind</c> for a library the imported module calls nothing of, or <see langword="null"/>.</summary>
  private static string? UnusedBinding(IrModule module, IReadOnlyDictionary<string, string> options) {
    var used = module.ForeignFunctions.Select(function => function.Library).ToHashSet(StringComparer.Ordinal);
    return BindingsOf(options).Keys.FirstOrDefault(library => !used.Contains(library)) is { } unused
      ? $"{Bind} binds the library '{unused}', which the module calls nothing of"
      : null;
  }

  /// <summary>The caveat of every native function JavaScript calls through a binding: nothing proves the binding is the native function.</summary>
  private static IEnumerable<ManifestMessage> ForeignCaveats(IrModule module, IReadOnlyDictionary<string, string> options, Backend backend) {
    if (backend.Name != "javascript")
      return [];

    var bindings = BindingsOf(options);
    return module.ForeignFunctions.Where(function => bindings.ContainsKey(function.Library)).Select(function => new ManifestMessage(UnverifiedExternal,
      $"the native function '{function.Symbol}' of {function.Library} is called through the binding {bindings[function.Library]}, which nothing verifies against it"));
  }

}
