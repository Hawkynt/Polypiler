using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.VisualBasic.Parsing;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

/// <summary>
/// <c>Variant</c> parameters and results across the module (#1564, now #1587's framework): a call
/// whose every <c>Variant</c> argument the framework proved of one scalar kind calls a clone of the
/// procedure with those parameters of that type, and a clone's result that has one kind on every
/// exit is of that type. A <c>Public</c> procedure keeps its declared signature, which the host calls;
/// a <c>Private</c> one whose calls all agree is that one clone under its own name. Every clone keeps
/// <c>Variant</c> arithmetic: its typed parameters carry their kind, so <c>Integer</c> 32767 + 1
/// promotes as it does in a <c>Variant</c>. Which call goes where, and the names of the clones, are
/// the framework's; the lowering writes them.
/// </summary>
internal sealed partial class VbaLowerer {

  /// <summary>The functions or clones the calls of the procedure being lowered go to, by name.</summary>
  private readonly HashSet<string> _calls = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Whether a signature has a parameter or a result declared <c>Variant</c>.</summary>
  private static bool HasVariants(Signature signature) => signature.DeclaredVariant.Any(variant => variant) || signature.DeclaredVariantResult;

  /// <summary>The functions of a round, its refusals reported.</summary>
  private List<IrFunction> Reported((List<IrFunction> Functions, List<VbaRefusal> Refusals) round) {
    foreach (var refusal in round.Refusals)
      this.Report(refusal);
    return round.Functions;
  }

  /// <summary>
  /// One lowering of the module: every procedure the typing keeps, in the order of the module, then
  /// every clone its calls go to, each once, in the order the calls name them.
  /// </summary>
  private (List<IrFunction> Functions, List<VbaRefusal> Refusals) LowerRound(List<VbaProcedure> procedures) {
    var functions = new List<IrFunction>();
    var refusals = new List<VbaRefusal>();
    var queue = new Queue<(VbaProcedure Procedure, Signature Signature, DynamicFunctionTyping? Typing)>();
    var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var byName = procedures.ToDictionary(procedure => procedure.Name, StringComparer.OrdinalIgnoreCase);
    var writing = this._phase == Phase.Writing && this._writeTypes;
    foreach (var procedure in procedures) {
      var typing = this._phase == Phase.Writing ? this._typings.GetValueOrDefault(procedure.Name) : null;
      var failed = this._failed.Contains(procedure.Name);
      // A private procedure whose calls all go to one clone is that clone, lowered where a call names it.
      if (writing && !failed && (typing is { Signature: not null } || this._dropped.ContainsKey(procedure.Name)))
        continue;
      if (typing is not { Signature: null })
        typing = this.RootTyping(procedure.Name);
      queue.Enqueue((procedure, this._procedures[procedure.Name], typing));
      queued.Add(procedure.Name);
    }

    while (queue.TryDequeue(out var next)) {
      try {
        functions.Add(this.Procedure(next.Procedure, next.Signature, next.Typing));
      } catch (VbaRefusal refusal) {
        if (writing && next.Typing is { Signature: not null } clone) {
          this._failed.Add(clone.Name);
          this._failed.Add(clone.Function);
        } else {
          refusals.Add(refusal);
        }

        continue;
      }

      foreach (var name in this._calls.Where(queued.Add).ToList()) {
        var typing = this._typings[name];
        queue.Enqueue((byName[typing.Function], this.Clone(typing), typing));
      }
    }

    return (functions, refusals);
  }

  /// <summary>The signature of a clone: the declared one with the parameters the framework typed of their type, the result of the type it found.</summary>
  private Signature Clone(DynamicFunctionTyping typing) {
    var root = this._procedures[typing.Function];
    var parameters = root.Parameters.Select((parameter, index) => typing.Signature![index] is { } exact ? parameter with { Type = TypeOf(exact) } : parameter).ToList();
    var result = root.DeclaredVariantResult && typing.Typed.FirstOrDefault(pair => string.Equals(pair.Key.Name, root.Name, StringComparison.OrdinalIgnoreCase)).Value is { } typed ? TypeOf(typed) : root.Returns;
    return root with {
      Name = typing.Name,
      IsPublic = false,
      Parameters = parameters,
      Returns = result,
      ResultTags = KindsOf(typing.Returned.Certainty == Certainty.Dynamic ? DynamicType.Variant : typing.Returned.Type, VbaTags.None)
    };
  }

  /// <summary>
  /// The signature a call takes: while the module is written, the clone the framework sent the call
  /// to, or the procedure itself with the kinds the framework found its result to hold; otherwise, and
  /// for a call the framework did not see, the declared one.
  /// </summary>
  private Signature Planned(Signature signature, object site) {
    if (this._phase != Phase.Writing || signature.Source is null || !this._writeTypes)
      return signature;
    if (this._typing is not { } typing || !this._sitesOf.TryGetValue(this._procedure, out var sites) || !sites.TryGetValue(site, out var call)
        || !typing.Calls.TryGetValue(call, out var target)) {
      // A call no run reaches, which the framework did not follow: of a procedure the module does not
      // keep, any clone of it will do.
      if (!this._dropped.ContainsKey(signature.Name) || this._failed.Contains(signature.Name))
        return signature;
      target = this._typings.Values.First(candidate => string.Equals(candidate.Function, signature.Name, StringComparison.OrdinalIgnoreCase)).Name;
    }

    var callee = this._typings[target];
    if (callee.Signature is not null && !this._failed.Contains(target)) {
      this._calls.Add(target);
      return this.Clone(callee);
    }

    var root = callee.Signature is null ? callee : this.RootTyping(callee.Function)!;
    return signature with { ResultTags = signature.DeclaredVariantResult ? KindsOf(root.Returned.Certainty == Certainty.Dynamic ? DynamicType.Variant : root.Returned.Type, VbaTags.None) : signature.ResultTags };
  }

  /// <summary>Records the IR call a call of a procedure of the module became while the module is analysed.</summary>
  private IrCall Site(object site, IrCall call) {
    if (this._phase == Phase.Analysing)
      (this._sitesOf.TryGetValue(this._procedure, out var sites) ? sites : this._sitesOf[this._procedure] = new(ReferenceEqualityComparer.Instance))[site] = call;
    return call;
  }

}
