using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Interprocedural;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0380, interprocedural constant propagation of arguments (on the structured IR): a parameter of a
/// function the module owns (<see cref="ModuleOwnership"/>) that every call of the function passes the
/// same literal - a constant equal by its bits, so that <c>0.0</c> and <c>-0.0</c> differ, the
/// <c>null</c> of its type, or a reference to the same function - reads as that literal in the
/// function. A recursive call that passes the parameter on at its own position passes what the
/// function was given and is not counted. The parameter must never be written - by an assignment, a
/// step, a lambda or through its address - so that every read of it is the value it arrived with. The
/// signature and every call stay as they are; reads in the bodies of lambdas stay reads of the variable
/// they capture. The pass runs until nothing changes, so a literal passed on to a second function
/// reaches that one as well.
/// </summary>
public sealed class O0380ArgumentConstantPropagation : IIrPass {

  /// <summary>The proof class the opt-report names: every call is enumerated, and each passes the literal.</summary>
  public const string Proof = "structural";

  /// <inheritdoc/>
  public string Id => "O0380";

  /// <inheritdoc/>
  public string Name => "interprocedural constant propagation";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) => this.Run(module, scope, applied, _ => { });

  /// <summary>Runs the pass until nothing changes and reports every parameter it replaced and why a parameter some call passes a literal stays.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">Whether the pass changes a function; the calls of every function count.</param>
  /// <param name="applied">Receives every change: the function and position 0.</param>
  /// <param name="note">Receives the opt-report lines <c>O0380 applied: ...</c> and <c>O0380 not applied: ...</c>.</param>
  /// <returns>The module, the same object when nothing changed.</returns>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied, Action<string> note) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    ArgumentNullException.ThrowIfNull(note);
    for (var progress = true; progress;) {
      progress = false;
      // The calls of the module as it is now: a replacement in a caller makes its calls literal.
      var ownership = ModuleOwnership.Of(module);
      for (var index = 0; index < module.Functions.Count; ++index) {
        var function = module.Functions[index];
        if (!scope(function))
          continue;

        var replaced = this.Replacements(function, ownership, note);
        if (replaced.Count == 0)
          continue;

        var body = Substituted(function.Body, replaced);
        var sites = ownership.CallSitesOf(function.Name).Count;
        foreach (var (parameter, literal) in replaced)
          note($"{this.Id} applied: @{function.Name}: parameter %{parameter.Name} = {Spelled(literal)} at all {sites} call sites [proof: {Proof}]");

        applied(function.Name, "0");
        module = module with { Functions = [.. module.Functions.Select((other, at) => at == index ? function with { Body = body } : other)] };
        progress = true;
      }
    }

    return module;
  }

  /// <summary>The parameters of a function that read as a literal, with that literal; the reason for every other one some call passes a literal.</summary>
  private Dictionary<IrLocal, IrExpr> Replacements(IrFunction function, ModuleOwnership ownership, Action<string> note) {
    var replaced = new Dictionary<IrLocal, IrExpr>();
    var read = Read(function.Body);
    var fixedValue = Unwritten(function);
    var sites = ownership.CallSitesOf(function.Name);
    var owner = ownership.WhyNotOwned(function.Name);
    for (var position = 0; position < function.Parameters.Count; ++position) {
      var parameter = function.Parameters[position];
      if (!read.Contains(parameter))
        continue;

      void Refused(string reason) => note($"{this.Id} not applied: @{function.Name}: %{parameter.Name}: {reason}");
      if (sites.Count == 0) {
        if (owner is null)
          Refused("no call site");

        continue;
      }

      var at = position;
      var passed = sites.Where(site => !PassesOn(site, function, parameter, at)).Select(site => (Site: site, Argument: site.Call.Arguments[at])).ToList();
      if (!passed.Any(pair => IsLiteral(pair.Argument)))
        continue;

      if (owner is not null) {
        Refused(owner);
        continue;
      }

      if (!fixedValue(parameter)) {
        Refused("written in the function");
        continue;
      }

      var (first, firstSite) = (passed[0].Argument, passed[0].Site);
      var reason = passed.Select(pair => !IsLiteral(pair.Argument)
          ? $"argument at {Where(pair.Site)} is not a literal"
          : !pair.Argument.Equals(first) ? $"arguments differ ({Spelled(first)} at {Where(firstSite)}, {Spelled(pair.Argument)} at {Where(pair.Site)})" : null)
        .FirstOrDefault(found => found is not null);
      if (reason is not null)
        Refused(reason);
      else
        replaced[parameter] = first;
    }

    return replaced;
  }

  /// <summary>Whether a call is the function calling itself with the parameter at the parameter's own position: it passes on what it was given.</summary>
  private static bool PassesOn(IrCallSite site, IrFunction function, IrLocal parameter, int position)
    => site.Caller == function.Name && !site.InLambda && site.Call.Arguments[position] is IrLocalRef reference && reference.Local == parameter;

  /// <summary>A literal of a scalar: a constant, a null, or a reference to a function.</summary>
  private static bool IsLiteral(IrExpr argument) => argument is IrConst or IrNull or IrFunctionRef;

  private static string Where(IrCallSite site) => site.Caller is null ? "a global initializer" : "@" + site.Caller;

  /// <summary>A literal as the opt-report writes it: a number in its type, a truth value, <c>null</c> or <c>@name</c>.</summary>
  internal static string Spelled(IrExpr literal) => literal switch {
    IrConst { Type.IsFloat: true } constant => IrOperators.Float(constant.Type, constant.Value).ToString("R", CultureInfo.InvariantCulture),
    IrConst constant when constant.Type == IrType.Bool => constant.Value != 0 ? "true" : "false",
    IrConst { Type.Kind: IrTypeKind.U64 } constant => unchecked((ulong)constant.Value).ToString(CultureInfo.InvariantCulture),
    IrConst constant => constant.Value.ToString(CultureInfo.InvariantCulture),
    IrFunctionRef reference => "@" + reference.Function,
    _ => "null"
  };

  /// <summary>The variables a body reads outside the bodies of its lambdas: the reads <see cref="Substituted"/> replaces, found by the same walk.</summary>
  private static HashSet<IrLocal> Read(IrBlock body) {
    var read = new HashSet<IrLocal>();
    void Walk(IrBlock block) {
      foreach (var statement in block.Statements)
        IrPassSupport.Map(statement, expression => {
          read.UnionWith(IrShape.Nodes(expression).OfType<IrLocalRef>().Select(reference => reference.Local));
          return expression;
        }, inner => {
          Walk(inner);
          return inner;
        });
    }

    Walk(body);
    return read;
  }

  /// <summary>Whether a parameter keeps the value it arrived with: nothing assigns, steps, binds or declares it, no lambda writes it and its address is never taken.</summary>
  private static Func<IrLocal, bool> Unwritten(IrFunction function) {
    var written = IrPassSupport.Writes(function.Body).Keys.ToHashSet();
    written.UnionWith(IrPassSupport.Declarations(function.Body).Keys);
    foreach (var lambda in IrPassSupport.Nodes(function.Body).OfType<IrLambda>())
      foreach (var statement in lambda.Body.Statements)
        written.UnionWith(IrStatements.Writes(statement));

    foreach (var address in IrPassSupport.Nodes(function.Body).OfType<IrIntrinsic>().Where(intrinsic => intrinsic.Op == IrIntrinsicOp.AddressOf))
      written.UnionWith(address.Arguments.OfType<IrLocalRef>().Select(reference => reference.Local));

    return parameter => !written.Contains(parameter);
  }

  /// <summary>A block whose every read of a replaced parameter outside the bodies of lambdas is its literal.</summary>
  private static IrBlock Substituted(IrBlock block, Dictionary<IrLocal, IrExpr> replaced) {
    IrExpr Expression(IrExpr expression) => IrShape.Map(expression, node => node is IrLocalRef reference && replaced.TryGetValue(reference.Local, out var literal) ? literal : null);
    return new IrBlock([.. block.Statements.Select(statement => IrPassSupport.Map(statement, Expression, inner => Substituted(inner, replaced)))]);
  }

}
