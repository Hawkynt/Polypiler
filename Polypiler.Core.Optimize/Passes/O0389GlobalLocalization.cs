using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Interprocedural;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0389, global variable localization (on the structured IR): a scalar global that only one function
/// names becomes a variable of that function, declared where the function first writes it. One user is
/// not enough, since a global keeps its value from one call to the next and is shared by every
/// activation of the function, and a variable is neither: the function's first statement that names the
/// global, at the top level of its body, must assign it a value that does not read it - so no activation
/// reads a value an earlier one left - and the function must not be entered again while it runs: it
/// calls itself through no chain of calls, no reference to it is taken, and nothing it reaches calls
/// code the module cannot name (<see cref="ModuleOwnership.UnknownCodeReachedFrom"/>); nor is it async
/// or an iterator, whose activations interleave. The global must not be a static field of a class,
/// which code outside may reach, nor named by a lambda, by the initializer of another global or by an
/// address. The signature of the function is unchanged, so it may be public. The declaration of the
/// global, which nothing names any more and whose initializer has no effect, is dropped.
/// </summary>
public sealed class O0389GlobalLocalization : IIrPass {

  /// <summary>The proof class the opt-report names: each activation reads only what it wrote, and no two activations are live at once.</summary>
  public const string Proof = "structural+alias-facts";

  /// <inheritdoc/>
  public string Id => "O0389";

  /// <inheritdoc/>
  public string Name => "global variable localization";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) => this.Run(module, scope, applied, _ => { });

  /// <summary>Runs the pass once and reports every global it localized and why a global some function uses stays.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">Whether the pass changes a function; the uses in every function count.</param>
  /// <param name="applied">Receives every change: the function and the position of the statement that now declares the variable.</param>
  /// <param name="note">Receives the opt-report lines <c>O0389 applied: ...</c> and <c>O0389 not applied: ...</c>.</param>
  /// <returns>The module, the same object when nothing changed.</returns>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied, Action<string> note) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    ArgumentNullException.ThrowIfNull(note);
    var census = Census(module);
    var statics = module.Types.OfType<IrClassType>().SelectMany(type => type.StaticFields).Select(global => global.Name).ToHashSet(StringComparer.Ordinal);
    foreach (var global in module.Globals.ToList()) {
      if (!census.TryGetValue(global.Name, out var uses))
        continue;

      var function = uses.Users.Count == 1 && uses.Users.Single() is { } only ? module.Functions.Single(candidate => candidate.Name == only) : null;
      if (function is not null && !scope(function))
        continue;

      var (reason, index) = function is null
        ? (Shared(global, uses, statics), -1)
        : Refusal(module, global, function, uses, statics);
      if (reason is not null) {
        note($"{this.Id} not applied: global @{global.Name}: {reason}");
        continue;
      }

      module = Localized(module, global, function!, index);
      applied(function!.Name, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
      note($"{this.Id} applied: global @{global.Name} localized into @{function.Name} [proof: {Proof}]");
    }

    return module;
  }

  /// <summary>Where a global is named: the functions, the initializers of other globals, and the first place its address escapes or a lambda names it.</summary>
  private sealed record Uses(HashSet<string> Users, List<string> Initializers) {
    public string? Escape { get; set; }
  }

  private static Dictionary<string, Uses> Census(IrModule module) {
    var census = new Dictionary<string, Uses>(StringComparer.Ordinal);
    Uses Of(IrGlobal global) => census.TryGetValue(global.Name, out var uses) ? uses : census[global.Name] = new([], []);
    foreach (var function in module.Functions) {
      foreach (var reference in IrPassSupport.Nodes(function.Body).OfType<IrGlobalRef>())
        Of(reference.Global).Users.Add(function.Name);

      foreach (var assign in IrPassSupport.Statements(function.Body).OfType<IrGlobalAssign>())
        Of(assign.Global).Users.Add(function.Name);

      foreach (var lambda in IrPassSupport.Nodes(function.Body).OfType<IrLambda>()) {
        var named = IrTree.Nodes(lambda).OfType<IrGlobalRef>().Select(reference => reference.Global)
          .Concat(lambda.Body.Statements.SelectMany(IrStatements.All).OfType<IrGlobalAssign>().Select(assign => assign.Global));
        foreach (var global in named) {
          var uses = Of(global);
          uses.Users.Add(function.Name);
          uses.Escape ??= $"a lambda of @{function.Name}";
        }
      }

      foreach (var address in IrPassSupport.Nodes(function.Body).OfType<IrIntrinsic>().Where(intrinsic => intrinsic.Op == IrIntrinsicOp.AddressOf))
        foreach (var reference in address.Arguments.OfType<IrGlobalRef>())
          Of(reference.Global).Escape ??= $"AddressOf in @{function.Name}";
    }

    foreach (var global in module.Globals.Where(global => global.Initializer is not null))
      foreach (var reference in IrTree.Nodes(global.Initializer!).OfType<IrGlobalRef>())
        Of(reference.Global).Initializers.Add(global.Name);

    return census;
  }

  /// <summary>Why a global that no single function names stays: a static field, an initializer or more than one function names it.</summary>
  private static string Shared(IrGlobal global, Uses uses, HashSet<string> statics) {
    if (statics.Contains(global.Name))
      return "external";

    if (uses.Initializers.Count > 0)
      return $"read by the initializer of @{uses.Initializers[0]}";

    return $"used by {uses.Users.Count} functions";
  }

  /// <summary>Why the one function that names a global cannot hold it as a variable, or the top-level statement that first writes it.</summary>
  private static (string? Reason, int Index) Refusal(IrModule module, IrGlobal global, IrFunction function, Uses uses, HashSet<string> statics) {
    if (statics.Contains(global.Name) || uses.Initializers.Count > 0)
      return (Shared(global, uses, statics), -1);

    if (!IsScalar(global.Type))
      return ("not scalar", -1);

    if (uses.Escape is not null)
      return ($"address escapes at {uses.Escape}", -1);

    if (function.IsAsync || IrSequences.IsIterator(function) || function.Body.Statements.Any(IrSequences.Suspends))
      return ($"@{function.Name} is resumable", -1);

    var ownership = ModuleOwnership.Of(module);
    if (ownership.IsAddressTaken(function.Name))
      return ($"@{function.Name} is address-taken", -1);

    if (ownership.IsRecursive(function.Name))
      return ($"@{function.Name} is recursive", -1);

    if (ownership.UnknownCodeReachedFrom(function.Name) is { } unknown)
      return ($"@{function.Name} may be re-entered through {unknown}", -1);

    for (var index = 0; ; ++index) {
      var statement = function.Body.Statements[index];
      if (!Names(statement, global))
        continue;

      if (statement is IrGlobalAssign assign && assign.Global.Name == global.Name && !Reads(assign.Value, global))
        return (null, index);

      var writes = IrStatements.All(statement).OfType<IrGlobalAssign>().Any(inner => inner.Global.Name == global.Name);
      return (writes && statement is not IrGlobalAssign ? "no store in the entry block" : "load before the first store", -1);
    }
  }

  /// <summary>A type whose value is one word the function can hold: a number, a truth value, an enumeration, a pointer or a function value.</summary>
  private static bool IsScalar(IrType type) => type.IsNumeric || type == IrType.Bool || type.IsEnum || type.IsPointer || type.IsFunction;

  private static bool Reads(IrExpr expression, IrGlobal global) => IrTree.Nodes(expression).OfType<IrGlobalRef>().Any(reference => reference.Global.Name == global.Name);

  private static bool Names(IrStmt statement, IrGlobal global)
    => IrTree.Nodes(statement).OfType<IrGlobalRef>().Any(reference => reference.Global.Name == global.Name)
      || IrStatements.All(statement).OfType<IrGlobalAssign>().Any(assign => assign.Global.Name == global.Name);

  /// <summary>The module with the global a variable of the function, declared by the statement that first wrote it, and the global's declaration gone.</summary>
  private static IrModule Localized(IrModule module, IrGlobal global, IrFunction function, int index) {
    var taken = function.Parameters.Concat(function.Locals).Select(local => local.Name)
      .Concat(module.Functions.Select(other => other.Name)).Concat(module.Globals.Where(other => other.Name != global.Name).Select(other => other.Name))
      .Concat(module.Constants.Select(constant => constant.Name)).ToHashSet(StringComparer.Ordinal);
    var local = new IrLocal(IrPassSupport.Fresh(global.Name, taken), global.Type);
    IrExpr Expression(IrExpr expression) => IrShape.Map(expression, node => node is IrGlobalRef reference && reference.Global.Name == global.Name ? new IrLocalRef(local) : null);
    IrStmt Statement(IrStmt statement) => IrPassSupport.Map(statement, Expression, Block) switch {
      IrGlobalAssign assign when assign.Global.Name == global.Name => new IrAssign(local, assign.Value),
      var other => other
    };
    IrBlock Block(IrBlock block) => new([.. block.Statements.Select(Statement)]);

    var statements = function.Body.Statements;
    var first = (IrGlobalAssign)statements[index];
    var body = new IrBlock([.. statements.Take(index), new IrDeclare(local, first.Value), .. statements.Skip(index + 1).Select(Statement)]);
    var localized = function with { Body = body, Locals = [.. function.Locals, local] };
    return module with {
      Functions = [.. module.Functions.Select(other => other.Name == function.Name ? localized : other)],
      Globals = [.. module.Globals.Where(other => other.Name != global.Name)]
    };
  }

}
