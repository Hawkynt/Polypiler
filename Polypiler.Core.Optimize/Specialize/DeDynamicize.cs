using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Optimize.Specialize;

/// <summary>The budgets of the de-dynamicization (ADR 0300 decision 3, #1564's): a budget exceeded leaves a place dynamic, it never refuses a program.</summary>
public sealed record DeDynamicizeOptions {

  /// <summary>The options with every budget at its default.</summary>
  public static DeDynamicizeOptions Default { get; } = new();

  /// <summary>The most passes over the body of a loop before the places it changes are left dynamic, and the most rounds over the call graph.</summary>
  public int MostRounds { get; init; } = 12;

  /// <summary>The most clones of one function; a call of another signature calls the dynamic function.</summary>
  public int MostClones { get; init; } = 4;

}

/// <summary>What a place of the module was found to hold, for the static-typing report (ADR 0300 decision 7).</summary>
/// <param name="Function">The function the place belongs to.</param>
/// <param name="Name">The name of the local or parameter; the function's name for its result.</param>
/// <param name="Kind">Whether it is a local, a parameter or a result.</param>
/// <param name="Fact">The fact of the place.</param>
/// <param name="Reason">For a place that stayed a variant, the first join that made it one, such as <c>assigned i32 (line 9) and str (line 12)</c>; otherwise <see langword="null"/>.</param>
public sealed record DynamicPlace(string Function, string Name, DynamicPlaceKind Kind, DynamicFact Fact, string? Reason);

/// <summary>The kinds of places a report counts.</summary>
public enum DynamicPlaceKind {

  /// <summary>A local variable.</summary>
  Local,

  /// <summary>A parameter.</summary>
  Parameter,

  /// <summary>The result of a function.</summary>
  Result

}

/// <summary>
/// What the de-dynamicization found for one function or one clone of it, for a frontend that writes
/// the typed module itself with its own lowering of typed operations (ADR 0300, Visual Basic's #1591).
/// </summary>
/// <param name="Function">The function of the module analysed.</param>
/// <param name="Name">The name of the function or clone in the typed module.</param>
/// <param name="Signature">For a clone, the exact type of every parameter it types, <see langword="null"/> for one it leaves; <see langword="null"/> for the function itself.</param>
/// <param name="Kept">Whether the function itself stays in the typed module; a clone always does.</param>
/// <param name="Typed">The variant locals given an exact type, the result's among them.</param>
/// <param name="Holds">What every variant local and parameter is assigned anywhere in the function: the join of every value, the variant for one written where the analysis does not follow it.</param>
/// <param name="Heads">What every variant local holds at the head of each loop, the variant where nothing is known of it.</param>
/// <param name="Calls">For every call of a function of the module, the name of the function or clone it goes to.</param>
/// <param name="Returned">The join of every value the function returns.</param>
/// <param name="ReturnType">The type the function or clone returns.</param>
public sealed record DynamicFunctionTyping(
  string Function,
  string Name,
  IReadOnlyList<DynamicExact?>? Signature,
  bool Kept,
  IReadOnlyDictionary<IrLocal, DynamicExact> Typed,
  IReadOnlyDictionary<IrLocal, DynamicType> Holds,
  IReadOnlyDictionary<IrStmt, IReadOnlyDictionary<IrLocal, DynamicType>> Heads,
  IReadOnlyDictionary<IrCall, string> Calls,
  DynamicFact Returned,
  IrType ReturnType);

/// <summary>The module the de-dynamicization made and what it found for every place of the functions it typed.</summary>
/// <param name="Module">The module.</param>
/// <param name="Places">The places, function by function in module order.</param>
public sealed record DeDynamicizeResult(IrModule Module, IReadOnlyList<DynamicPlace> Places);

/// <summary>
/// The de-dynamicization (ADR 0300 decisions 1 and 3): a required pass that types the dynamic values
/// a frontend lowered as <c>variant</c>s, by the semantics table of the module's language.
/// <list type="bullet">
/// <item>Within a function (#1585) the facts of every variant place are carried forward through the
/// tree, joined where branches meet, iterated to a fixed point at the head of every loop - widened
/// after <see cref="DeDynamicizeOptions.MostRounds"/> passes - and refined on the tag tests of the
/// branches (#1586). A local whose every value and every read is proven of one exact type with a tag
/// gets that type: its assignments unbox, its reads box, and the boxes cancel against their unboxes and
/// tag tests; an operation whose operands are proven exact is expanded through the table.</item>
/// <item>Across the module (#1587) a call whose every variant argument is proven exact calls a clone of
/// its function with those parameters typed, at most <see cref="DeDynamicizeOptions.MostClones"/> per
/// function and found in at most <see cref="DeDynamicizeOptions.MostRounds"/> rounds over the call
/// graph. A public function keeps its signature and gets the clones; a private one whose calls all
/// agree is that clone under its own name.</item>
/// </list>
/// A local captured by a lambda, written by a binding or by a statement the pass does not model, a
/// parameter the body writes and every place of a helper of the language stay as they are. The pass
/// leaves a module without variants as the same object; every module it makes verifies.
/// </summary>
public static partial class DeDynamicize {

  /// <summary>Types the variants of a module.</summary>
  /// <param name="module">The module.</param>
  /// <param name="semantics">The semantics table of the module's language.</param>
  /// <param name="options">The budgets.</param>
  /// <returns>The module, the same object when there is nothing to type.</returns>
  public static IrModule Run(IrModule module, IDynamicSemantics semantics, DeDynamicizeOptions options) => Analyse(module, semantics, options).Module;

  /// <summary>Types the variants of a module and says what every place was found to hold.</summary>
  /// <param name="module">The module.</param>
  /// <param name="semantics">The semantics table of the module's language.</param>
  /// <param name="options">The budgets.</param>
  /// <returns>The module and its places; the module the same object, and no places, when there is nothing to type.</returns>
  public static DeDynamicizeResult Analyse(IrModule module, IDynamicSemantics semantics, DeDynamicizeOptions options) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(semantics);
    ArgumentNullException.ThrowIfNull(options);
    if (!IrVariants.Uses(module))
      return new(module, []);

    var program = new Program(module, semantics, options);
    program.Solve();
    var rewritten = program.Rewrite();
    return new(rewritten, program.Places(rewritten));
  }

  /// <summary>
  /// What the de-dynamicization finds for every function of a module and every clone it makes, without
  /// rewriting the module: for a frontend that lowers its source again with the typing given, so that
  /// every typed operation is the one its own lowering writes.
  /// </summary>
  /// <param name="module">The module, as the frontend lowered it with every dynamic value a variant.</param>
  /// <param name="semantics">The semantics table of the module's language.</param>
  /// <param name="options">The budgets.</param>
  /// <returns>The typing of each function and clone, function by function in module order and each function's clones after it; none for a module without variants.</returns>
  public static IReadOnlyList<DynamicFunctionTyping> Typing(IrModule module, IDynamicSemantics semantics, DeDynamicizeOptions options) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(semantics);
    ArgumentNullException.ThrowIfNull(options);
    if (!IrVariants.Uses(module))
      return [];

    var program = new Program(module, semantics, options);
    program.Solve();
    return program.Typing();
  }

  /// <summary>A signature a clone is made for: the IR type and tag of every parameter it types, <see langword="null"/> for one it leaves as it is.</summary>
  private sealed record Signature(IReadOnlyList<DynamicExact?> Parameters) {

    public string Key => string.Join(",", this.Parameters.Select(parameter => parameter?.ToString() ?? "-"));

    public bool Equals(Signature? other) => other is not null && this.Key == other.Key;

    public override int GetHashCode() => this.Key.GetHashCode(StringComparison.Ordinal);

  }

  /// <summary>The analysis of a whole module: the facts of each function and clone, round after round until the clones and the results no longer change.</summary>
  private sealed class Program(IrModule module, IDynamicSemantics semantics, DeDynamicizeOptions options) {

    public IrModule Module { get; } = module;

    public IDynamicSemantics Semantics { get; } = semantics;

    public DeDynamicizeOptions Options { get; } = options;

    public DynamicFacts Domain { get; } = new(semantics.Joins);

    /// <summary>The functions of the module by name.</summary>
    public Dictionary<string, IrFunction> Functions { get; } = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);

    /// <summary>The clones of each function, in the order they were found.</summary>
    public Dictionary<string, List<Signature>> Clones { get; } = new(StringComparer.Ordinal);

    /// <summary>The result of each function and clone, by its key.</summary>
    public Dictionary<string, DynamicFact> Results { get; } = new(StringComparer.Ordinal);

    /// <summary>The analysis of each function and clone of the last round, by its key.</summary>
    public Dictionary<string, Walker> Walkers { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the rounds reached a fixed point within the budget; otherwise no clone is made and every result is dynamic.</summary>
    public bool Converged { get; private set; } = true;

    /// <summary>The functions reached other than by a direct call, which keep their signatures and are never replaced by a clone.</summary>
    public HashSet<string> Indirect { get; } = [.. module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrFunctionRef>().Select(reference => reference.Function)
      .Concat(module.Types.OfType<IrClassType>().SelectMany(type => type.Methods.Select(method => method.Function)))];

    /// <summary>Whether the functions a call goes to may be specialised: a function of the module, no helper of the language, with a variant parameter that its body never writes.</summary>
    public bool Specialisable(IrFunction function) => !this.Semantics.IsHelper(function.Name) && !this.Indirect.Contains(function.Name)
      && SpecialisableParameters(function).Any(specialisable => specialisable);

    /// <summary>For each parameter, whether a clone may type it: a variant the body never writes nor captures.</summary>
    public static bool[] SpecialisableParameters(IrFunction function) {
      var written = Written(function.Body);
      return [.. function.Parameters.Select(parameter => parameter.Type.IsVariant && !written.Contains(parameter))];
    }

    public static string Key(string function, Signature? signature) => signature is null ? function + "|" : function + "|" + signature.Key;

    /// <summary>The rounds: every function and clone analysed with the results and clones the round before found.</summary>
    public void Solve() {
      string? before = null;
      for (var round = 0; round < this.Options.MostRounds; ++round) {
        this.Walkers.Clear();
        foreach (var function in this.Module.Functions.Where(function => !this.Semantics.IsHelper(function.Name))) {
          this.Analyse(function, null);
          foreach (var signature in this.Clones.GetValueOrDefault(function.Name, []).ToList())
            this.Analyse(function, signature);
        }

        var now = this.State();
        if (now == before)
          return;
        before = now;
      }

      // Out of rounds: every function as it is, its result dynamic.
      this.Converged = false;
      this.Clones.Clear();
      this.Results.Clear();
      this.Walkers.Clear();
      foreach (var function in this.Module.Functions.Where(function => !this.Semantics.IsHelper(function.Name)))
        this.Analyse(function, null);
    }

    private void Analyse(IrFunction function, Signature? signature) {
      var walker = new Walker(this, function, signature);
      walker.Run();
      var key = Key(function.Name, signature);
      this.Walkers[key] = walker;
      this.Results[key] = this.Results.TryGetValue(key, out var known) ? this.Domain.Join(known, walker.Returned) : walker.Returned;
    }

    /// <summary>The state of the rounds in words: every clone and every result.</summary>
    private string State() => string.Join(";", this.Clones.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "[" + string.Join(" ", pair.Value.Select(signature => signature.Key)) + "]"))
      + "#" + string.Join(";", this.Results.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

    /// <summary>The fact of the result of a call with arguments of the facts given, and the clone it goes to; registers a new clone where the budget allows.</summary>
    public (DynamicFact Result, Signature? Clone) Call(string name, IReadOnlyList<DynamicFact> arguments, bool register = true) {
      if (!this.Functions.TryGetValue(name, out var function) || this.Semantics.IsHelper(name))
        return (this.Domain.Unknown, null);

      if (this.Converged && this.Specialisable(function) && SignatureOf(function, arguments) is { } signature) {
        var clones = this.Clones.TryGetValue(name, out var list) ? list : register ? this.Clones[name] = [] : [];
        if (clones.Contains(signature) || (register && clones.Count < this.Options.MostClones)) {
          if (!clones.Contains(signature))
            clones.Add(signature);
          return (this.Results.GetValueOrDefault(Key(name, signature), this.Domain.Unreachable), signature);
        }
      }

      return (function.ReturnType.IsVariant ? this.Results.GetValueOrDefault(Key(name, null), this.Domain.Unreachable) : DynamicFact.Proven(Exact(function.ReturnType)), null);
    }

    /// <summary>The signature of a call whose every specialisable argument is proven of an exact type with a tag; <see langword="null"/> otherwise.</summary>
    private static Signature? SignatureOf(IrFunction function, IReadOnlyList<DynamicFact> arguments) {
      var specialisable = SpecialisableParameters(function);
      var types = new DynamicExact?[function.Parameters.Count];
      for (var index = 0; index < types.Length; ++index) {
        if (!specialisable[index])
          continue;
        if (arguments[index] is not { IsStatic: true, Type.Plain: DynamicExact { BoxTag: not null } exact })
          return null;
        types[index] = exact;
      }

      return new(types);
    }

    /// <summary>The names of the clones: <c>Name_i32_str</c>, with a number where the name is taken; a private function called through one clone alone keeps its own name.</summary>
    public Dictionary<string, string> Names() {
      var names = new Dictionary<string, string>(StringComparer.Ordinal);
      var taken = new HashSet<string>(this.Functions.Keys, StringComparer.Ordinal);
      foreach (var (function, clones) in this.Clones.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
        if (this.SoleClone(function) is { } sole) {
          names[Key(function, sole)] = function;
          continue;
        }

        foreach (var clone in clones) {
          var stem = function + "_" + string.Join("_", clone.Parameters.OfType<DynamicExact>().Select(this.Semantics.Name));
          var name = stem;
          for (var suffix = 2; !taken.Add(name); ++suffix)
            name = stem + "_" + suffix;
          names[Key(function, clone)] = name;
        }
      }

      return names;
    }

    private HashSet<string>? _roots;

    /// <summary>
    /// Whether the dynamic function itself stays in the module: a public one, one reached otherwise than
    /// by a direct call, one without clones, and one that a function or clone staying in the module calls
    /// dynamically - found to a fixed point, so the calls of a function that goes do not keep another.
    /// </summary>
    public bool KeepsRoot(string function) {
      if (this._roots is null) {
        var roots = this.Functions.Keys.Where(name => this.Functions[name].IsPublic || this.Indirect.Contains(name) || !this.Clones.ContainsKey(name)).ToHashSet(StringComparer.Ordinal);
        for (var grown = true; grown;) {
          var called = this.Walkers.Values.Where(walker => walker.Signature is not null || roots.Contains(walker.Function.Name))
            .SelectMany(walker => walker.Calls.Values.Where(call => call.Target is null && !(walker.Signature is null && walker.Function.Name == call.Function)))
            .Select(call => call.Function).ToList();
          grown = false;
          foreach (var name in called)
            grown |= roots.Add(name);
        }

        this._roots = roots;
      }

      return this._roots.Contains(function);
    }

    /// <summary>The one clone a private function whose root goes is called through, which takes the function's own name; <see langword="null"/> where it has another number of clones or keeps its root.</summary>
    public Signature? SoleClone(string function)
      => !this.KeepsRoot(function) && this.Clones.TryGetValue(function, out var clones) && clones.Count == 1 ? clones[0] : null;

    /// <summary>The module with every function and clone rewritten.</summary>
    public IrModule Rewrite() {
      var names = this.Names();
      var functions = new List<IrFunction>();
      foreach (var function in this.Module.Functions) {
        if (this.Semantics.IsHelper(function.Name)) {
          functions.Add(function);
          continue;
        }

        if (this.KeepsRoot(function.Name))
          functions.Add(this.Walkers[Key(function.Name, null)].Rewrite(function.Name, names));
        foreach (var clone in this.Clones.GetValueOrDefault(function.Name, []))
          functions.Add(this.Walkers[Key(function.Name, clone)].Rewrite(names[Key(function.Name, clone)], names) with { SpecialisedFrom = function.Name });
      }

      return this.Module with { Functions = functions };
    }

    /// <summary>The typing of every function and clone the rounds analysed.</summary>
    public IReadOnlyList<DynamicFunctionTyping> Typing() {
      var names = this.Names();
      var typing = new List<DynamicFunctionTyping>();
      foreach (var function in this.Module.Functions.Where(function => !this.Semantics.IsHelper(function.Name))) {
        typing.Add(this.Walkers[Key(function.Name, null)].Typing(function.Name, this.KeepsRoot(function.Name), names));
        foreach (var clone in this.Clones.GetValueOrDefault(function.Name, []))
          typing.Add(this.Walkers[Key(function.Name, clone)].Typing(names[Key(function.Name, clone)], true, names));
      }

      return typing;
    }

    /// <summary>What every place of the functions the pass typed holds, in a module the pass made or was given.</summary>
    public IReadOnlyList<DynamicPlace> Places(IrModule result) {
      var places = new List<DynamicPlace>();
      var names = this.Names();
      foreach (var (key, walker) in this.Walkers.OrderBy(pair => this.Module.Functions.ToList().FindIndex(function => function.Name == pair.Value.Function.Name)).ThenBy(pair => pair.Key, StringComparer.Ordinal)) {
        var name = walker.Signature is null ? walker.Function.Name : names[key];
        if (result.Functions.All(function => function.Name != name) || (walker.Signature is null && !this.KeepsRoot(walker.Function.Name)))
          continue;
        places.AddRange(walker.Places(name));
      }

      return places;
    }

    /// <summary>The exact element of a type, the dynamic one for a type no exact element has.</summary>
    public static DynamicType Exact(IrType type) => type.IsVariant || type == IrType.Void || type.IsUnion ? DynamicType.Variant : DynamicType.Exact(type);

  }

  /// <summary>The locals and parameters a block assigns to, directly or in a statement nested in it.</summary>
  private static HashSet<IrLocal> Written(IrStmt body) {
    var written = new HashSet<IrLocal>();
    void Visit(IrStmt statement) {
      switch (statement) {
        case IrAssign assign:
          written.Add(assign.Local);
          break;
        case IrDeclare declare:
          written.Add(declare.Local);
          break;
        case IrForEachCollection loop:
          if (loop.Element is { } element)
            written.Add(element);
          if (loop.Value is { } value)
            written.Add(value);
          break;
        case IrForEachSequence loop:
          written.Add(loop.Element);
          break;
        case IrUsing scope:
          written.Add(scope.Resource);
          break;
        case IrTry attempt:
          foreach (var clause in attempt.Catches.Where(clause => clause.Local is not null))
            written.Add(clause.Local!);
          break;
      }

      foreach (var expression in IrTree.Expressions(statement))
        foreach (var node in IrTree.Nodes(expression)) {
          if (node is IrBind bind)
            written.Add(bind.Local);
          else if (node is IrStep { Target: IrLocalRef target })
            written.Add(target.Local);
          else if (node is IrLambda lambda)
            written.UnionWith(lambda.Captures);
        }

      foreach (var block in IrTree.Blocks(statement))
        foreach (var inner in block.Statements)
          Visit(inner);
    }

    Visit(body);
    return written;
  }

}
