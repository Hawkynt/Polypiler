using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.VisualBasic.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

/// <summary>
/// The inference of <c>Variant</c> parameters and results across the module (#1564): a call whose
/// every <c>Variant</c> argument has one scalar kind calls a clone of the procedure with those
/// parameters of that type; a result that has one kind on every exit becomes that type. The module is
/// lowered round after round, each with the clones and the result types the round before found, until
/// a round finds the same - a fixed point of the call graph, recursion included. A <c>Public</c>
/// procedure keeps its declared signature, which the host calls; a <c>Private</c> one whose calls all
/// agree is that one clone under its own name. Every clone keeps <c>Variant</c> arithmetic: its typed
/// parameters carry their kind, so <c>Integer</c> 32767 + 1 promotes as it does in a <c>Variant</c>.
/// </summary>
internal sealed partial class VbaLowerer {

  /// <summary>The most clones of one procedure; a call of another signature calls the dynamic procedure.</summary>
  internal const int MostClones = 4;

  /// <summary>The most rounds before the inference gives up and lowers every procedure dynamic.</summary>
  private const int MostRounds = 12;

  /// <summary>What the rounds of the inference know.</summary>
  private sealed class Specialisation(Dictionary<string, Signature> roots) {

    /// <summary>The declared signature of each procedure with a <c>Variant</c> parameter or result.</summary>
    public Dictionary<string, Signature> Roots { get; } = roots;

    /// <summary>The kinds of the parameters of each clone, by procedure, in the order they were made.</summary>
    public Dictionary<string, List<string>> Clones { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clones whose lowering was refused, which their calls then avoid.</summary>
    public HashSet<string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The result type and kinds each signature's last lowering gave, by key.</summary>
    public Dictionary<string, (VbaType Returns, VbaTags Tags)> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The keys the calls of the current round used, by procedure.</summary>
    public Dictionary<string, HashSet<string>> Used { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The keys the calls of the round before used.</summary>
    public Dictionary<string, HashSet<string>> Before { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names of the clones in the current round, by key.</summary>
    public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The key of the dynamic procedure itself.</summary>
    public static string RootKey(string procedure) => procedure + "|";

    public void Begin(IReadOnlySet<string> taken) {
      this.Before = this.Used.ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
      this.Used.Clear();
      this.Names.Clear();
      var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
      foreach (var (procedure, kinds) in this.Clones) {
        var root = this.Roots[procedure];
        // A private procedure called through one clone alone is that clone.
        if (!root.IsPublic && this.Before.TryGetValue(procedure, out var used) && used.Count == 1 && !used.Contains(RootKey(procedure)) && kinds.Contains(used.Single()[(procedure.Length + 1)..])) {
          this.Names[used.Single()] = root.Name;
          continue;
        }
      }

      foreach (var (procedure, kinds) in this.Clones)
        foreach (var kind in kinds.Where(kind => !this.Names.ContainsKey(procedure + "|" + kind))) {
          var name = procedure + "_" + kind.Replace(',', '_');
          for (var suffix = 2; !names.Add(name); ++suffix)
            name = procedure + "_" + kind.Replace(',', '_') + "_" + suffix;
          this.Names[procedure + "|" + kind] = name;
        }
    }

    /// <summary>Whether the dynamic procedure is lowered: every public one, and a private one the round before called dynamically, or did not call at all.</summary>
    public bool NeedsRoot(Signature root) => root.IsPublic || !this.Before.TryGetValue(root.Name, out var used) || used.Contains(RootKey(root.Name));

  }

  private Specialisation? _specialisation;

  /// <summary>The type the inference gave the result of the procedure lowered last, which the next round's calls take.</summary>
  private VbaType? _lastReturns;

  /// <summary>The keys of the signatures the current lowering of a procedure called.</summary>
  private readonly HashSet<string> _calls = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The kinds the result had on the exits of the current lowering.</summary>
  private VbaTags _exitTags;

  /// <summary>Whether a signature has a parameter or a result declared <c>Variant</c>.</summary>
  private static bool HasVariants(Signature signature) => signature.DeclaredVariant.Any(variant => variant) || signature.DeclaredVariantResult;

  /// <summary>
  /// The procedures lowered with the inference across the module: the rounds until the clones, the
  /// result types and the calls no longer change; then the last round's functions, and its refusals.
  /// </summary>
  private List<IrFunction> LowerAll(List<VbaProcedure> procedures) {
    var roots = procedures.Select(procedure => this._procedures[procedure.Name]).Where(HasVariants).ToDictionary(signature => signature.Name, StringComparer.OrdinalIgnoreCase);
    if (roots.Count == 0)
      return this.Reported(this.LowerRound(procedures, null));

    var helpers = this._usedHelpers.ToList();
    var err = this._err;
    var specialisation = new Specialisation(roots);
    this._specialisation = specialisation;
    try {
      string? state = null;
      for (var round = 0; round < MostRounds; ++round) {
        this._usedHelpers.Clear();
        this._usedHelpers.UnionWith(helpers);
        this._err = err;
        specialisation.Begin(new HashSet<string>(this._procedures.Keys, StringComparer.OrdinalIgnoreCase));
        var lowered = this.LowerRound(procedures, specialisation);
        var now = State(specialisation);
        if (now == state)
          return this.Reported(lowered);

        state = now;
      }
    } finally {
      this._specialisation = null;
    }

    // No fixed point within the rounds: every procedure as it is declared.
    this._usedHelpers.Clear();
    this._usedHelpers.UnionWith(helpers);
    this._err = err;
    return this.Reported(this.LowerRound(procedures, null));
  }

  /// <summary>The functions of a round, its refusals reported.</summary>
  private List<IrFunction> Reported((List<IrFunction> Functions, List<VbaRefusal> Refusals) round) {
    foreach (var refusal in round.Refusals)
      this.Report(refusal);
    return round.Functions;
  }

  /// <summary>What a round decided, as text, which two rounds compare.</summary>
  private static string State(Specialisation specialisation) => string.Join(";", [
    .. specialisation.Results.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value.Returns}/{(int)pair.Value.Tags}"),
    .. specialisation.Used.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}<{string.Join(",", pair.Value.Order(StringComparer.OrdinalIgnoreCase))}"),
    .. specialisation.Names.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}:{pair.Value}"),
    .. specialisation.Failed.Order(StringComparer.OrdinalIgnoreCase).Select(key => "!" + key)
  ]);

  /// <summary>One round: every procedure that is lowered, and every clone its calls name, each once.</summary>
  private (List<IrFunction> Functions, List<VbaRefusal> Refusals) LowerRound(List<VbaProcedure> procedures, Specialisation? specialisation) {
    var functions = new List<IrFunction>();
    var refusals = new List<VbaRefusal>();
    var queue = new Queue<(VbaProcedure Procedure, Signature Signature, string Key)>();
    var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var byName = procedures.ToDictionary(procedure => procedure.Name, StringComparer.OrdinalIgnoreCase);
    foreach (var procedure in procedures) {
      var signature = this._procedures[procedure.Name];
      if (specialisation is not null && specialisation.Roots.ContainsKey(procedure.Name) && !specialisation.NeedsRoot(signature))
        continue;
      queue.Enqueue((procedure, signature, Specialisation.RootKey(procedure.Name)));
      queued.Add(Specialisation.RootKey(procedure.Name));
    }

    while (queue.TryDequeue(out var next)) {
      try {
        functions.Add(this.Procedure(next.Procedure, next.Signature));
        if (specialisation is not null && specialisation.Roots.ContainsKey(next.Procedure.Name))
          specialisation.Results[next.Key] = (next.Key.EndsWith('|') ? next.Signature.Returns! : this._lastReturns ?? next.Signature.Returns!, this._exitTags);
      } catch (VbaRefusal refusal) {
        if (next.Key.EndsWith('|') || specialisation is null)
          refusals.Add(refusal);
        else
          specialisation.Failed.Add(next.Key);
        continue;
      }

      if (specialisation is null)
        continue;
      foreach (var key in this._calls.ToList()) {
        var procedure = key[..key.IndexOf('|', StringComparison.Ordinal)];
        if (!specialisation.Used.TryGetValue(procedure, out var used))
          specialisation.Used[procedure] = used = new(StringComparer.OrdinalIgnoreCase);
        used.Add(key);
        if (queued.Add(key))
          queue.Enqueue((byName[procedure], key.EndsWith('|') ? this._procedures[procedure] : this.Clone(specialisation, procedure, key), key));
      }
    }

    // A root lowered twice - from the list and from a call - is one function.
    return ([.. functions.DistinctBy(function => function.Name, StringComparer.OrdinalIgnoreCase)], refusals);
  }

  /// <summary>The signature of a clone: the declared one with the parameters of its kinds fixed, the result the round before found.</summary>
  private Signature Clone(Specialisation specialisation, string procedure, string key) {
    var root = specialisation.Roots[procedure];
    var kinds = key[(key.IndexOf('|', StringComparison.Ordinal) + 1)..].Split(',');
    var parameters = root.Parameters.Select((parameter, index) => kinds[index].Length == 0 ? parameter : parameter with { Type = KindType(kinds[index]) }).ToList();
    var (returns, tags) = specialisation.Results.TryGetValue(key, out var known) ? known : (root.Returns, root.DeclaredVariantResult ? VbaTags.Any : VbaTags.None);
    return root with {
      Name = specialisation.Names.GetValueOrDefault(key, root.Name),
      IsPublic = false,
      Parameters = parameters,
      Returns = root.DeclaredVariantResult ? returns : root.Returns,
      ResultTags = tags
    };
  }

  private static VbaType KindType(string kind) => kind switch {
    "Boolean" => VbaType.Boolean,
    "Byte" => VbaType.Byte,
    "Integer" => VbaType.Integer,
    "Long" => VbaType.Long,
    "Single" => VbaType.Single,
    "Double" => VbaType.Double,
    "Currency" => VbaType.Currency,
    "Date" => VbaType.Date,
    _ => VbaType.String
  };

  /// <summary>
  /// The signature a call takes: of a procedure with <c>Variant</c> parameters, the clone of the kinds
  /// its arguments have where each has one scalar kind, at most <see cref="MostClones"/> per procedure
  /// and not one whose lowering was refused; otherwise, and for every other procedure, the declared one.
  /// </summary>
  private Signature Specialised(Signature signature, IReadOnlyList<VbaValue?> arguments) {
    if (this._specialisation is not { } specialisation || signature.Source is null || !specialisation.Roots.TryGetValue(signature.Name, out var root) || !ReferenceEquals(root, signature))
      return signature;

    var record = this._inference is not { Dry: true };
    var kinds = root.Parameters.Select((parameter, index) => root.DeclaredVariant[index] && index < arguments.Count && arguments[index] is { } argument
      && TypeOf(TagsOf(argument)) is { IsArray: false, Kind: not VbaKind.LongLong } kind ? kind.ToString() : "").ToList();
    var key = signature.Name + "|" + string.Join(",", kinds);
    var dynamic = specialisation.Results.TryGetValue(Specialisation.RootKey(signature.Name), out var known) ? root with { ResultTags = known.Tags } : root;
    if (kinds.All(kind => kind.Length == 0) || specialisation.Failed.Contains(key)) {
      if (record)
        this._calls.Add(Specialisation.RootKey(signature.Name));
      return dynamic;
    }

    if (!specialisation.Clones.TryGetValue(signature.Name, out var clones))
      specialisation.Clones[signature.Name] = clones = [];
    var signatureKinds = string.Join(",", kinds);
    if (!clones.Contains(signatureKinds)) {
      if (!record || clones.Count >= MostClones) {
        if (record)
          this._calls.Add(Specialisation.RootKey(signature.Name));
        return dynamic;
      }

      clones.Add(signatureKinds);
    }

    if (record)
      this._calls.Add(key);
    // A clone first named in this round has no name of the round yet; it is lowered under one the next round gives.
    if (!specialisation.Names.ContainsKey(key))
      specialisation.Names[key] = signature.Name + "_" + signatureKinds.Replace(',', '_');
    return this.Clone(specialisation, signature.Name, key);
  }

}
