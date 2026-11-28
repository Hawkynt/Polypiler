using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.VisualBasic.Parsing;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

/// <summary>
/// <c>Variant</c>: every local declared without a type or <c>As Variant</c>, every <c>Variant</c>
/// parameter and result is typed by the shared framework (ADR 0300, #1591). The module is first
/// lowered with every one of them dynamic, its operations the helpers of
/// <see cref="VariantHelperSource"/>, which the framework's de-dynamicization analyses with the table
/// of <see cref="Semantics"/>: what each local holds at every loop's head, which locals have one type,
/// which calls go to a clone of their procedure for the types of their arguments. The module is then
/// lowered again with that typing, so every typed operation is the one this lowering writes for its
/// type. The lowering keeps no fixed point of its own: it follows the kinds of the values statement by
/// statement, and takes those at a loop's head from the framework.
/// </summary>
internal sealed partial class VbaLowerer {

  /// <summary>
  /// The kinds the <c>Variant</c> locals of the procedure being lowered hold as its statements are
  /// lowered: flow-sensitive within a pass, with the kinds at the head of every loop and the type of
  /// every local the framework found given.
  /// </summary>
  private sealed class Inference(HashSet<string> candidates) {

    /// <summary>The <c>Variant</c> locals.</summary>
    public HashSet<string> Candidates { get; } = candidates;

    /// <summary>The kinds seen assigned in the current lowering.</summary>
    public Dictionary<string, VbaTags> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The locals lowered to a fixed type: an array its syntax states, or the type the framework found.</summary>
    public Dictionary<string, VbaType> Fixed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each local may hold anywhere in the procedure, as the framework found it.</summary>
    public Dictionary<string, VbaTags> Anywhere { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The kinds each local holds at the statement being lowered: flow-sensitive, as the statements run.</summary>
    public Dictionary<string, VbaTags> Current { get; private set; } = candidates.ToDictionary(name => name, _ => VbaTags.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>The kinds each local holds at the head of each loop, as the framework found them; none while the module is analysed.</summary>
    public Dictionary<VbaStatement, Dictionary<string, VbaTags>> Heads { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The locals read where they may still be <c>Empty</c>.</summary>
    public HashSet<string> ReadEmpty { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The states at the <c>Exit</c> statements of the loops being lowered, innermost last.</summary>
    public Stack<Dictionary<string, VbaTags>> Exits { get; } = new();

    /// <summary>Whether an assignment may be skipped, under <c>On Error Resume Next</c>: it adds to what the local holds.</summary>
    public bool Weak { get; set; }

    public void See(string name, VbaTags tags) {
      this.Seen[name] = this.Seen.GetValueOrDefault(name) | tags;
      this.Current[name] = this.Weak ? this.Current[name] | tags : tags;
    }

    public Dictionary<string, VbaTags> Snapshot() => new(this.Current, StringComparer.OrdinalIgnoreCase);

    public void Restore(Dictionary<string, VbaTags> state) => this.Current = new(state, StringComparer.OrdinalIgnoreCase);

    /// <summary>The union of states, where the branches of an <c>If</c> or a loop's paths meet.</summary>
    public static Dictionary<string, VbaTags> Join(IEnumerable<Dictionary<string, VbaTags>> states) {
      var joined = new Dictionary<string, VbaTags>(StringComparer.OrdinalIgnoreCase);
      foreach (var state in states)
        foreach (var (name, tags) in state)
          joined[name] = joined.GetValueOrDefault(name) | tags;
      return joined;
    }

    /// <summary>Whatever a local may hold anywhere: where a handler starts, after any statement of the code it guards.</summary>
    public void Havoc() {
      foreach (var name in this.Candidates)
        this.Current[name] |= this.Anywhere.GetValueOrDefault(name) | this.Seen.GetValueOrDefault(name) | VbaTags.Empty;
    }

    /// <summary>The kinds a read of a dynamic local gives where it is read.</summary>
    public VbaTags Read(string name) {
      var tags = this.Current[name];
      if ((tags & VbaTags.Empty) != VbaTags.None)
        this.ReadEmpty.Add(name);
      return tags;
    }

  }

  /// <summary>How far the typing of the module is: analysed with every <c>Variant</c> dynamic, or lowered with what the framework found.</summary>
  private enum Phase {

    /// <summary>The module has no <c>Variant</c>, or it is lowered dynamic with nothing found yet.</summary>
    None,

    /// <summary>Lowered with every <c>Variant</c> dynamic for the framework: a refusal that depends on the kinds a value may hold is no refusal yet.</summary>
    Analysing,

    /// <summary>Lowered with the framework's typing: the kinds at the loops' heads, the typed locals and the clones.</summary>
    Writing

  }

  private Phase _phase;

  /// <summary>Whether the typing written is the framework's whole typing, or only the kinds at the loops' heads, every <c>Variant</c> staying dynamic (<see cref="VisualBasicInference.Dynamic"/>).</summary>
  private bool _writeTypes = true;

  /// <summary>The arrays each procedure's <c>Variant</c> locals hold, by the syntax that makes them (<c>Array</c>, <c>ReDim</c>): the IR's dynamic type carries no array.</summary>
  private readonly Dictionary<string, Dictionary<string, VbaType>> _arrays = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The kinds each procedure's <c>Variant</c> locals were seen assigned when it was analysed.</summary>
  private readonly Dictionary<string, Dictionary<string, VbaTags>> _analysedSeen = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The IR loop each loop of a procedure became when it was analysed.</summary>
  private readonly Dictionary<string, Dictionary<VbaStatement, IrStmt>> _loopsOf = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The IR call each call of a procedure became when it was analysed, by the node of the call.</summary>
  private readonly Dictionary<string, Dictionary<object, IrCall>> _sitesOf = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The framework's typing of every function and clone of the module, by the name it has in the output.</summary>
  private readonly Dictionary<string, DynamicFunctionTyping> _typings = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The procedures every call of which goes to a clone, which the module does not keep unless a clone is refused, with their typing.</summary>
  private readonly Dictionary<string, DynamicFunctionTyping> _dropped = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The clones whose lowering was refused, whose calls go to the procedure itself.</summary>
  private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The typing of the function or clone being lowered.</summary>
  private DynamicFunctionTyping? _typing;

  private Inference? _inference;

  /// <summary>The kinds an element of the framework allows, as the lowering follows them: a value of which nothing is known holds what a parameter may, and a <c>Currency</c> or a <c>LongLong</c> where the procedure was seen to assign one.</summary>
  private static VbaTags KindsOf(DynamicType type, VbaTags seen)
    => type is DynamicVariant ? VbaTags.Any | (seen & (VbaTags.Currency | VbaTags.LongLong)) : Semantics.Tags(type);

  /// <summary>The type of an exact element of the framework.</summary>
  private static VbaType TypeOf(DynamicExact exact) => TypeOf(Semantics.Tags(exact))!;

  /// <summary>
  /// A procedure, lowered with the typing of the phase: the kinds of its <c>Variant</c> locals
  /// followed as its statements run, those at each loop's head and the type of each local the
  /// framework found, and its arrays.
  /// </summary>
  private IrFunction Procedure(VbaProcedure procedure, Signature signature, DynamicFunctionTyping? typing) {
    var candidates = this._raw ? [] : Candidates(procedure, signature);
    this._typing = typing;
    var inference = candidates.Count == 0 ? null : new Inference(candidates);
    this._inference = inference;
    try {
      if (inference is not null)
        this.Prepare(procedure, inference, typing);
      return this.LowerProcedure(procedure, signature);
    } finally {
      // What was seen up to a refusal still tells the arrays apart.
      if (this._phase == Phase.Analysing && inference is not null)
        this._analysedSeen[procedure.Name] = new(inference.Seen, StringComparer.OrdinalIgnoreCase);
      this._inference = null;
      this._typing = null;
    }
  }

  /// <summary>Gives the inference of a procedure its arrays and, when the module is written, the framework's typing.</summary>
  private void Prepare(VbaProcedure procedure, Inference inference, DynamicFunctionTyping? typing) {
    foreach (var (name, type) in this._arrays.GetValueOrDefault(procedure.Name, []))
      inference.Fixed[name] = type;
    if (this._phase != Phase.Writing || typing is null)
      return;

    var seen = this._analysedSeen.GetValueOrDefault(procedure.Name, []);
    VbaTags Kinds(DynamicType type, string name) => KindsOf(type, seen.GetValueOrDefault(name));
    if (this._writeTypes)
      foreach (var (local, exact) in typing.Typed.Where(pair => inference.Candidates.Contains(pair.Key.Name)))
        inference.Fixed.TryAdd(local.Name, TypeOf(exact));
    foreach (var (local, holds) in typing.Holds.Where(pair => inference.Candidates.Contains(pair.Key.Name)))
      inference.Anywhere[local.Name] = Kinds(holds, local.Name);
    foreach (var (loop, statement) in this._loopsOf.GetValueOrDefault(procedure.Name, []))
      if (typing.Heads.TryGetValue(statement, out var head))
        inference.Heads[loop] = head.Where(pair => inference.Candidates.Contains(pair.Key.Name)).ToDictionary(pair => pair.Key.Name, pair => Kinds(pair.Value, pair.Key.Name), StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>
  /// The procedures of the module, typed by the framework: lowered with every <c>Variant</c> dynamic -
  /// a second time with the arrays the first found where it found any - analysed, and lowered with
  /// the typing found. A module without <c>Variant</c>s is lowered once.
  /// </summary>
  private List<IrFunction> LowerAll(List<VbaProcedure> procedures) {
    if (!procedures.Any(procedure => HasVariants(this._procedures[procedure.Name]) || Candidates(procedure, this._procedures[procedure.Name]).Count > 0))
      return this.Reported(this.LowerRound(procedures));

    var helpers = this._usedHelpers.ToList();
    var err = this._err;
    void Reset() {
      this._usedHelpers.Clear();
      this._usedHelpers.UnionWith(helpers);
      this._err = err;
    }

    this._phase = Phase.Analysing;
    var (functions, _) = this.LowerRound(procedures);
    // The arrays the first lowering found; one read where it may still be Empty is none, and the
    // lowering with the rest is the one the framework analyses.
    if (this.FindArrays(procedures))
      do {
        this._readEmpty.Clear();
        Reset();
        (functions, _) = this.LowerRound(procedures);
      } while (this.DropEmptyArrays());

    var analysed = new IrModule("Analysed", functions) { Types = this._declarations, Globals = [.. this._globals.Values.Select(global => global.Global), .. this._err is { } global ? [global] : Array.Empty<IrGlobal>()] };
    foreach (var typing in DeDynamicize.Typing(analysed, Semantics.Instance, DeDynamicizeOptions.Default))
      if (typing.Kept || typing.Signature is not null)
        this._typings[typing.Name] = typing;
      else
        this._dropped[typing.Name] = typing;
    this._phase = Phase.Writing;
    // A clone the lowering refuses is none: its calls go to the procedure itself, and the module is
    // lowered once more - at most once per clone.
    for (var failed = -1; ;) {
      Reset();
      failed = this._failed.Count;
      var written = this.LowerRound(procedures);
      if (this._failed.Count == failed)
        return this.Reported(written);
    }
  }

  /// <summary>The typing of a procedure itself, kept or not.</summary>
  private DynamicFunctionTyping? RootTyping(string procedure)
    => this._typings.TryGetValue(procedure, out var typing) && typing.Signature is null ? typing : this._dropped.GetValueOrDefault(procedure);

  /// <summary>Drops the arrays read where they may still be <c>Empty</c>; whether there were any.</summary>
  private bool DropEmptyArrays() {
    var dropped = false;
    foreach (var (procedure, arrays) in this._arrays)
      foreach (var name in arrays.Keys.Where(name => this._readEmpty.Contains((procedure, name))).ToList())
        dropped |= arrays.Remove(name);
    return dropped;
  }

  /// <summary>The arrays the analysis found: a local that is only ever assigned arrays of <c>Variant</c>s from one lower bound, never read before; whether there are any.</summary>
  private bool FindArrays(List<VbaProcedure> procedures) {
    foreach (var procedure in procedures)
      foreach (var (name, tags) in this._analysedSeen.GetValueOrDefault(procedure.Name, []))
        if (TypeOf(tags) is { IsArray: true } array && !this._readEmpty.Contains((procedure.Name, name)))
          (this._arrays.TryGetValue(procedure.Name, out var arrays) ? arrays : this._arrays[procedure.Name] = new(StringComparer.OrdinalIgnoreCase))[name] = array;
    return this._arrays.Count > 0;
  }

  /// <summary>The locals of each procedure read where they may still be <c>Empty</c> when it was analysed.</summary>
  private readonly HashSet<(string Procedure, string Name)> _readEmpty = new(new ReadEmptyComparer());

  private sealed class ReadEmptyComparer : IEqualityComparer<(string Procedure, string Name)> {

    public bool Equals((string Procedure, string Name) x, (string Procedure, string Name) y)
      => StringComparer.OrdinalIgnoreCase.Equals(x.Procedure, y.Procedure) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

    public int GetHashCode((string Procedure, string Name) obj)
      => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Procedure), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));

  }

  /// <summary>The locals a procedure declares as <c>Variant</c>, explicitly or by giving no type, and its result where it is declared so.</summary>
  private static HashSet<string> Candidates(VbaProcedure procedure, Signature signature) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (signature.DeclaredVariantResult)
      names.Add(procedure.Name);
    foreach (var variable in Dims(procedure.Body).SelectMany(dim => dim.Variables))
      if (!variable.IsArray && (variable.Type is null || string.Equals(variable.Type.Name, "Variant", StringComparison.OrdinalIgnoreCase)))
        names.Add(variable.Name);
    return names;
  }

  /// <summary>The type of a local declared <c>Variant</c>: the fixed one it was given, or the dynamic type.</summary>
  private (VbaType Type, VbaTags Tags) VariantLocal(string name) {
    // Every Variant local of a procedure is a candidate of its inference.
    var inference = this._inference!;
    if (inference.Fixed.TryGetValue(name, out var type))
      return (type, TagOf(type.Kind));
    return (VbaType.Variant, VbaTags.Empty);
  }

  /// <summary>
  /// The kinds a dynamic <c>Variant</c> local holds where it is read, or <see langword="null"/> for any
  /// other variable; a read of one given a fixed type is recorded where it may still be <c>Empty</c>.
  /// </summary>
  private VbaTags? Flowing(string name, VbaType type) {
    if (this._inference is not { } inference || !inference.Candidates.Contains(name))
      return null;
    var tags = inference.Read(name);
    if (this._phase == Phase.Analysing && (tags & VbaTags.Empty) != VbaTags.None)
      this._readEmpty.Add((this._procedure, name));
    return type.IsVariant ? tags : null;
  }

  /// <summary>A refusal of what a <c>Variant</c> may hold: while the module is analysed it is none, for the typing found may avoid it.</summary>
  private void RefuseVariant(int line, string what) {
    if (this._phase != Phase.Analysing)
      throw this.Unsupported(line, what);
  }

  #region Flow

  private Dictionary<string, VbaTags>? Snapshot() => this._inference?.Snapshot();

  private void Restore(Dictionary<string, VbaTags>? state) {
    if (state is not null)
      this._inference!.Restore(state);
  }

  /// <summary>Continues after branches with the union of the states they end in.</summary>
  private void Join(params Dictionary<string, VbaTags>?[] states) {
    if (this._inference is { } inference)
      inference.Restore(Inference.Join(states.OfType<Dictionary<string, VbaTags>>()));
  }

  /// <summary>Enters a loop: the state at its head joins the state before it with what the framework found there.</summary>
  private void EnterLoop(VbaStatement loop) {
    if (this._inference is not { } inference)
      return;
    if (inference.Heads.TryGetValue(loop, out var head))
      inference.Restore(Inference.Join([inference.Current, head]));
    inference.Exits.Push(new(StringComparer.OrdinalIgnoreCase));
  }

  /// <summary>Leaves a loop whose body has been lowered: the code after it starts from the head, the end of its body and every <c>Exit</c>.</summary>
  private void LeaveLoop(VbaStatement loop, Dictionary<string, VbaTags>? head) {
    if (this._inference is not { } inference || head is null)
      return;
    var exits = inference.Exits.Pop();
    inference.Restore(Inference.Join([head, inference.Current, exits]));
  }

  /// <summary>The IR loop a loop became, which the framework's facts at its head are found by.</summary>
  private T Lowered<T>(VbaStatement loop, T lowered) where T : IrStmt {
    if (this._phase == Phase.Analysing)
      (this._loopsOf.TryGetValue(this._procedure, out var loops) ? loops : this._loopsOf[this._procedure] = new(ReferenceEqualityComparer.Instance))[loop] = lowered;
    return lowered;
  }

  /// <summary>An <c>Exit For</c> or <c>Exit Do</c>: the state goes to the code after the loop.</summary>
  private void ExitLoop() {
    if (this._inference is { Exits.Count: > 0 } inference) {
      var exits = inference.Exits.Peek();
      foreach (var (name, tags) in inference.Current)
        exits[name] = exits.GetValueOrDefault(name) | tags;
    }
  }

  #endregion

  /// <summary>
  /// Records what a <c>Variant</c> local is assigned; whether it is one of a fixed type assigned a value
  /// that holds none of its kind - a store the framework found no read sees, kept only for the effects
  /// of its value.
  /// </summary>
  private bool Record(string name, VbaValue value) {
    if (this._inference is not { } inference || !inference.Candidates.Contains(name))
      return false;

    inference.See(name, TagsOf(value));
    return inference.Fixed.TryGetValue(name, out var type) && !type.IsArray && (TagsOf(value) & TagOf(type.Kind)) == VbaTags.None;
  }

  #region Tags

  /// <summary>The kind of a scalar type as a tag; none for the others.</summary>
  private static VbaTags TagOf(VbaKind kind) => kind switch {
    VbaKind.Boolean => VbaTags.Boolean,
    VbaKind.Byte => VbaTags.Byte,
    VbaKind.Integer => VbaTags.Integer,
    VbaKind.Long => VbaTags.Long,
    VbaKind.LongLong => VbaTags.LongLong,
    VbaKind.Single => VbaTags.Single,
    VbaKind.Double => VbaTags.Double,
    VbaKind.String => VbaTags.String,
    VbaKind.Currency => VbaTags.Currency,
    VbaKind.Date => VbaTags.Date,
    _ => VbaTags.None
  };

  /// <summary>The kinds a value may hold: those of a value of a <c>Variant</c>, the one of a typed value, an array of <c>Variant</c>s from 0 or 1 as one.</summary>
  private static VbaTags TagsOf(VbaValue value) => value.IsVariant ? value.Tags
    : value.Type is { IsArray: true, Element.IsVariant: true, LowerBound: 0 or 1 } array ? (array.LowerBound == 0 ? VbaTags.ArrayFrom0 : VbaTags.ArrayFrom1)
    : TagOf(value.Type.Kind);

  /// <summary>The scalar type of a single kind, or <see langword="null"/> for several, none, <c>Empty</c> or <c>Null</c>.</summary>
  private static VbaType? TypeOf(VbaTags tags) => tags switch {
    VbaTags.Boolean => VbaType.Boolean,
    VbaTags.Byte => VbaType.Byte,
    VbaTags.Integer => VbaType.Integer,
    VbaTags.Long => VbaType.Long,
    VbaTags.LongLong => VbaType.LongLong,
    VbaTags.Single => VbaType.Single,
    VbaTags.Double => VbaType.Double,
    VbaTags.String => VbaType.String,
    VbaTags.Currency => VbaType.Currency,
    VbaTags.Date => VbaType.Date,
    VbaTags.ArrayFrom0 => VbaType.ArrayOf(VbaType.Variant, 0),
    VbaTags.ArrayFrom1 => VbaType.ArrayOf(VbaType.Variant, 1),
    _ => null
  };

  private static bool IsSingle(VbaTags tags) => tags != VbaTags.None && (tags & (tags - 1)) == VbaTags.None;

  /// <summary>The single kinds of a set.</summary>
  private static IEnumerable<VbaTags> Each(VbaTags tags) => Enum.GetValues<VbaTags>().Where(tag => IsSingle(tag) && (tags & tag) != VbaTags.None);

  /// <summary>The kinds a result of arithmetic that starts in a type may end in: Visual Basic promotes a <c>Variant</c> that overflows to the next wider type instead of raising error 6.</summary>
  private static VbaTags Chain(VbaKind start) => start switch {
    VbaKind.Byte => VbaTags.Byte | VbaTags.Integer | VbaTags.Long,
    VbaKind.Boolean or VbaKind.Integer => VbaTags.Integer | VbaTags.Long,
    VbaKind.Long => VbaTags.Long | VbaTags.Double,
    VbaKind.Single => VbaTags.Single | VbaTags.Double,
    VbaKind.Currency => VbaTags.Currency,
    VbaKind.Date => VbaTags.Date | VbaTags.Double,
    _ => VbaTags.Double
  };

  /// <summary>The kind <c>Empty</c> is in arithmetic: an <c>Integer</c> 0.</summary>
  private static VbaKind Numeric(VbaTags tag) => tag == VbaTags.Empty ? VbaKind.Integer : TypeOf(tag)!.Kind;

  /// <summary>The kinds <c>+</c>, <c>-</c> or <c>*</c> of values of two sets of kinds may give.</summary>
  private static VbaTags ArithmeticTags(string op, VbaTags left, VbaTags right) {
    var result = ((left | right) & VbaTags.Null) != VbaTags.None ? VbaTags.Null : VbaTags.None;
    foreach (var a in Each(left & ~VbaTags.Null))
      foreach (var b in Each(right & ~VbaTags.Null)) {
        if (op == "+" && a == VbaTags.String && b == VbaTags.String)
          result |= VbaTags.String;
        else if (op == "+" && (a == VbaTags.Empty) != (b == VbaTags.Empty))
          result |= a == VbaTags.Empty ? b : a;
        else if (a == VbaTags.String || b == VbaTags.String)
          // A string is read as a Double; with a Date, + and - give a Date, as the helpers do.
          result |= (a | b) == (VbaTags.String | VbaTags.Date) && op != "*" ? Chain(VbaKind.Date) : VbaTags.Double;
        else
          result |= Chain(ArithmeticType(op, new(Numeric(a)), new(Numeric(b))).Kind);
      }

    return result;
  }

  /// <summary>The kinds <c>\</c> or <c>Mod</c> of values of two sets of kinds may give: the operands as integers, a floating-point one as a <c>Long</c>.</summary>
  private static VbaTags IntegerDivisionTags(VbaTags left, VbaTags right) {
    static VbaKind Whole(VbaTags tag) => tag is VbaTags.Single or VbaTags.Double or VbaTags.String or VbaTags.Currency or VbaTags.Date ? VbaKind.Long : Numeric(tag);
    var result = ((left | right) & VbaTags.Null) != VbaTags.None ? VbaTags.Null : VbaTags.None;
    foreach (var a in Each(left & ~VbaTags.Null))
      foreach (var b in Each(right & ~VbaTags.Null))
        result |= Chain(Promote(new(Whole(a)), new(Whole(b))).Kind);
    return result;
  }

  /// <summary>The kinds a negation of a value of a set of kinds may give: of a <c>Byte</c>, a <c>Boolean</c> or <c>Empty</c> an <c>Integer</c>.</summary>
  private static VbaTags NegationTags(VbaTags operand) {
    var result = operand & VbaTags.Null;
    foreach (var tag in Each(operand & ~VbaTags.Null))
      result |= Chain(tag is VbaTags.Byte or VbaTags.Boolean or VbaTags.Empty ? VbaKind.Integer : tag == VbaTags.String ? VbaKind.Double : TypeOf(tag)!.Kind);
    return result;
  }

  /// <summary>The number of a type in the order the helpers widen in: <c>Byte</c> 1, <c>Integer</c> 2, <c>Long</c> 3, <c>Single</c> 5, <c>Double</c> 6.</summary>
  private static int RankCode(VbaKind kind) => kind switch {
    VbaKind.Byte => 1,
    VbaKind.Boolean or VbaKind.Integer => 2,
    VbaKind.Long => 3,
    VbaKind.Single => 5,
    _ => 6
  };

  private static IrConst I16(long value) => new(IrType.I16, value);

  /// <summary>A set of kinds as Visual Basic names them, for a diagnostic.</summary>
  private static string Names(VbaTags tags) => string.Join(" or ", Each(tags).Select(tag => tag.ToString()));

  #endregion

  #region Boxing

  /// <summary>A value as a <c>Variant</c>: a typed one boxed with the tag of its type, the result of a promoting operation with the tag its value and its start give.</summary>
  private IrExpr Box(VbaValue value, int line) {
    if (value.Type.IsVariant)
      return value.Expr;
    if (value.Promoted is { } from)
      return this.HelperCall("PlyVarFromWide", value.Expr, I16(RankCode(from.Kind)));

    // While the module is analysed, an array stands for no kind, and a LongLong is the IR's i64: the typing found decides.
    if (this._phase == Phase.Analysing && value.Type.Kind is VbaKind.Array or VbaKind.LongLong)
      return value.Type.Kind == VbaKind.LongLong ? new IrVariantOf(IrVariantTag.I64, value.Expr) : new IrVariantOf(IrVariantTag.Null, null);

    var tag = value.Type.Kind switch {
      VbaKind.Boolean => IrVariantTag.Bool,
      VbaKind.Byte => IrVariantTag.U8,
      VbaKind.Integer => IrVariantTag.I16,
      VbaKind.Long => IrVariantTag.I32,
      VbaKind.Single => IrVariantTag.F32,
      VbaKind.Double => IrVariantTag.F64,
      VbaKind.String => IrVariantTag.String,
      VbaKind.Currency => IrVariantTag.Currency,
      VbaKind.Date => IrVariantTag.Date,
      VbaKind.Array => throw this.Unsupported(line, $"a Variant that holds an array ({value.Type}) and may hold another kind, which the IR's dynamic type does not carry"),
      _ => throw this.Unsupported(line, $"a {value.Type} in a Variant")
    };
    return new IrVariantOf(tag, value.Expr);
  }

  private VbaValue Boxed(VbaValue value, int line) => new(this.Box(value, line), VbaType.Variant) { Tags = TagsOf(value) };

  /// <summary>A <c>Variant</c> converted to a type as an assignment converts it: <c>Empty</c> to 0, <c>""</c> or <c>False</c>, <c>Null</c> error 94.</summary>
  private IrExpr Unbox(VbaValue value, VbaType target, int line) {
    var tags = value.Tags;
    var currency = (tags & VbaTags.Currency) != VbaTags.None;
    if (target.Kind == VbaKind.String)
      return this.HelperCall(currency ? "PlyVarCStrC" : "PlyVarCStr", value.Expr);

    if (!target.IsNumeric)
      throw this.Unresolved(line, $"Type mismatch: 'Variant' where '{target}' is expected");

    return target.Kind switch {
      VbaKind.Boolean => this.HelperCall("PlyVarCBool", value.Expr),
      VbaKind.Byte when !currency => this.HelperCall("PlyVarCByte", value.Expr),
      VbaKind.Currency => this.HelperCall("PlyVarCur", value.Expr),
      VbaKind.Date => this.HelperCall("PlyVarDate", value.Expr),
      _ => this.Coerce(new(this.HelperCall(currency ? "PlyVarDblC" : "PlyVarDbl", value.Expr), VbaType.Double), target, line)
    };
  }


  /// <summary>The value of a global, which a <c>Variant</c> may hold anything in.</summary>
  private static VbaValue Returned(IrExpr call, VbaType type) => new(call, type) { Tags = type.IsVariant ? VbaTags.Any : VbaTags.None };

  /// <summary>The value of a call of a procedure of the module: a result declared <c>Variant</c> holds the kinds the inference found, also where it has a fixed type.</summary>
  private static VbaValue Returned(IrExpr call, Signature signature)
    => new(call, signature.Returns!) { Tags = signature.DeclaredVariantResult ? (signature.Returns!.IsVariant ? signature.ResultTags : TagOf(signature.Returns.Kind)) : VbaTags.None };

  #endregion

  #region Operators

  /// <summary>A binary operator with an operand of a <c>Variant</c>: arithmetic that promotes, <c>&amp;</c>, <c>+</c> of strings, a comparison.</summary>
  private VbaValue VariantBinary(string op, VbaValue left, VbaValue right, int line) {
    var a = TagsOf(left);
    var b = TagsOf(right);
    var typed = !left.Type.IsVariant && !right.Type.IsVariant;
    if (op is "=" or "<>" or "<" or "<=" or ">" or ">=")
      return this.VariantComparison(op, left, right, line);
    if (op == "&")
      return this.VariantConcatenation(left, right, line);
    if (op == "+" && typed && a == VbaTags.String && b == VbaTags.String)
      return new(new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [left.Expr, right.Expr]), VbaType.String) { Tags = VbaTags.String };

    if (((a | b) & VbaTags.LongLong) != VbaTags.None)
      this.RefuseVariant(line, "the arithmetic of a Variant that holds a LongLong");
    var currency = ((a | b) & VbaTags.Currency) != VbaTags.None;
    if (currency && !typed && op is "^" or "\\" or "Mod")
      this.RefuseVariant(line, $"'{op}' of a Variant that may hold a Currency");

    switch (op) {
      case "+" or "-" or "*":
        if (typed && IsSingle(a) && IsSingle(b) && ((a | b) & ~(VbaTags.Integral | VbaTags.Double)) == VbaTags.None)
          return this.Exact(op, left, right);
        if (typed && IsSingle(a) && IsSingle(b)) {
          var scaled = this.Arithmetic(op, ArithmeticType(op, left.Type, right.Type), left, right);
          return scaled with { Tags = TagOf(scaled.Type.Kind) };
        }

        var code = op == "+" ? 0 : op == "-" ? 1 : 2;
        return new(this.HelperCall(currency ? "PlyVarArithC" : "PlyVarArith", I16(code), this.Box(left, line), this.Box(right, line)), VbaType.Variant) { Tags = ArithmeticTags(op, a, b) };
      case "/":
        if (typed) {
          var quotient = this.Divide(left, right);
          return quotient with { Tags = TagOf(quotient.Type.Kind) };
        }

        var floats = VbaTags.Double | (((a | b) & VbaTags.Single) != VbaTags.None ? VbaTags.Single : VbaTags.None);
        return new(this.HelperCall(currency ? "PlyVarDivC" : "PlyVarDiv", this.Box(left, line), this.Box(right, line)), VbaType.Variant) { Tags = floats | ((a | b) & VbaTags.Null) };
      case "^":
        if (typed)
          return new(this.Pow(this.ToFloat(left, IrType.F64), this.ToFloat(right, IrType.F64)), VbaType.Double) { Tags = VbaTags.Double };
        return new(this.HelperCall("PlyVarPow", this.Box(left, line), this.Box(right, line)), VbaType.Variant) { Tags = VbaTags.Double | ((a | b) & VbaTags.Null) };
      default:
        // \ and Mod: Like and Is are refused before an operand is lowered.
        return new(this.HelperCall("PlyVarIntDiv", I16(op == "Mod" ? 1 : 0), this.Box(left, line), this.Box(right, line)), VbaType.Variant) { Tags = IntegerDivisionTags(a, b) };
    }
  }

  /// <summary>
  /// <c>+</c>, <c>-</c> or <c>*</c> of two values of one kind each, computed as a <c>Double</c>: exact
  /// for the integers, and for a result beyond <c>Long</c> the rounding Visual Basic's promotion to
  /// <c>Double</c> gives. The kind it starts in and the value give its tag.
  /// </summary>
  private VbaValue Exact(string op, VbaValue left, VbaValue right) {
    var start = Promote(left.Type, right.Type);
    var irOp = op switch { "+" => IrBinaryOp.Add, "-" => IrBinaryOp.Subtract, _ => IrBinaryOp.Multiply };
    IrExpr result = new IrBinary(irOp, this.ToFloat(left, IrType.F64), this.ToFloat(right, IrType.F64));
    return start.Kind == VbaKind.Double
      ? new(this.Checked(result), VbaType.Double) { Tags = VbaTags.Double }
      : new(result, VbaType.Double) { Tags = Chain(start.Kind), Promoted = start };
  }

  /// <summary>A comparison with an operand of a <c>Variant</c>: <c>Null</c> when either may be <c>Null</c>, otherwise a <c>Boolean</c>.</summary>
  private VbaValue VariantComparison(string op, VbaValue left, VbaValue right, int line) {
    if (!left.Type.IsVariant && !right.Type.IsVariant)
      return new(this.Compare(op, left, right, line), VbaType.Boolean) { Tags = VbaTags.Boolean };
    if (((TagsOf(left) | TagsOf(right)) & VbaTags.Null) == VbaTags.None)
      return new(this.Compare(op, left, right, line), VbaType.Boolean) { Tags = VbaTags.Boolean };

    var code = this.VariantOrder(left, right, line);
    var number = Array.IndexOf<string>(["=", "<>", "<", "<=", ">", ">="], op);
    return new(this.HelperCall("PlyVarCmpOp", code, I16(number)), VbaType.Variant) { Tags = VbaTags.Boolean | VbaTags.Null };
  }

  /// <summary>
  /// How a <c>Variant</c> compares with another value, as <c>PlyVarCmp</c> gives it: -1, 0 or 1, or 2
  /// when either is <c>Null</c>. Of two <c>Variant</c>s a number is below a string; a number compared
  /// with a value declared <c>String</c>, or a string with one declared a number, is compared as a
  /// number, the string read by the locale (<c>PlyVarCmpT</c>).
  /// </summary>
  private IrExpr VariantOrder(VbaValue left, VbaValue right, int line) {
    static bool Mixed(VbaValue typed, VbaTags other) => !typed.IsVariant
      && ((typed.Type.Kind == VbaKind.String && (other & VbaTags.Numeric) != VbaTags.None) || (typed.Type.IsNumeric && (other & VbaTags.String) != VbaTags.None));

    var helper = ((TagsOf(left) | TagsOf(right)) & VbaTags.Currency) != VbaTags.None ? "PlyVarCmpC"
      : Mixed(left, TagsOf(right)) || Mixed(right, TagsOf(left)) ? "PlyVarCmpT" : "PlyVarCmp";
    return this.HelperCall(helper, this.Box(left, line), this.Box(right, line));
  }

  /// <summary>A comparison of a <c>Variant</c> as a test: one with <c>Null</c> is not true.</summary>
  private IrExpr VariantTest(string op, VbaValue left, VbaValue right, int line) {
    var code = this.VariantOrder(left, right, line);
    if (((TagsOf(left) | TagsOf(right)) & VbaTags.Null) != VbaTags.None)
      return this.HelperCall("PlyVarIsTrue", this.HelperCall("PlyVarCmpOp", code, I16(Array.IndexOf<string>(["=", "<>", "<", "<=", ">", ">="], op))));

    var irOp = op switch {
      "=" => IrBinaryOp.Equal,
      "<>" => IrBinaryOp.NotEqual,
      "<" => IrBinaryOp.Less,
      "<=" => IrBinaryOp.LessOrEqual,
      ">" => IrBinaryOp.Greater,
      _ => IrBinaryOp.GreaterOrEqual
    };
    return new IrBinary(irOp, code, I32(0));
  }

  /// <summary><c>&amp;</c> with an operand of a <c>Variant</c>: <c>Null &amp; Null</c> is <c>Null</c>, otherwise <c>Null</c> and <c>Empty</c> are <c>""</c>.</summary>
  private VbaValue VariantConcatenation(VbaValue left, VbaValue right, int line) {
    var a = TagsOf(left);
    var b = TagsOf(right);
    if (((a | b) & VbaTags.Currency) != VbaTags.None && (a & b & VbaTags.Null) != VbaTags.None)
      this.RefuseVariant(line, "'&' of two Variants that may be Null and may hold a Currency");
    if ((a & b & VbaTags.Null) != VbaTags.None) {
      return new(this.HelperCall("PlyVarConcat", this.Box(left, line), this.Box(right, line)), VbaType.Variant) { Tags = VbaTags.String | VbaTags.Null };
    }

    IrExpr Part(VbaValue value) {
      if (!value.Type.IsVariant)
        return this.Text(value, line);
      return this.HelperCall((value.Tags & VbaTags.Currency) != VbaTags.None ? "PlyVarTextC" : "PlyVarText", value.Expr);
    }

    return new(new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [Part(left), Part(right)]), VbaType.String) { Tags = VbaTags.String };
  }

  /// <summary>Unary <c>-</c> or <c>Not</c> of a value of a <c>Variant</c>.</summary>
  private VbaValue VariantUnary(string op, VbaValue operand, int line) {
    var tags = TagsOf(operand);
    if (op == "Not") {
      if ((tags & VbaTags.LongLong) != VbaTags.None)
        this.RefuseVariant(line, "Not of a Variant that may hold a LongLong");
      if ((tags & VbaTags.Currency) != VbaTags.None)
        this.RefuseVariant(line, "Not of a Variant that may hold a Currency");
      if ((tags & ~(VbaTags.Boolean | VbaTags.Null)) == VbaTags.None)
        return new(this.HelperCall("PlyVarNot", this.Box(operand, line)), VbaType.Variant) { Tags = tags };
      return new(this.HelperCall("PlyVarBitNot", this.Box(operand, line)), VbaType.Variant) { Tags = Each(tags).Aggregate(VbaTags.None, (kinds, tag) => kinds | BitwiseKind(tag)) };
    }

    if ((tags & VbaTags.LongLong) != VbaTags.None)
      this.RefuseVariant(line, "the negation of a Variant that may hold a LongLong");
    if (!operand.Type.IsVariant && IsSingle(tags) && (tags & ~(VbaTags.Integral | VbaTags.Double)) == VbaTags.None) {
      var negated = new IrUnary(IrUnaryOp.Negate, this.ToFloat(operand, IrType.F64));
      return operand.Type.Kind == VbaKind.Double
        ? new(negated, VbaType.Double) { Tags = VbaTags.Double }
        : new(negated, VbaType.Double) { Tags = NegationTags(tags), Promoted = operand.Type.Kind is VbaKind.Long ? VbaType.Long : VbaType.Integer };
    }

    if (!operand.Type.IsVariant && operand.Promoted is null && operand.Type.IsScaled) {
      var negated = this.Unary(new VbaUnary("-", new VbaEmpty(line), line), operand with { Tags = VbaTags.None });
      return negated with { Tags = TagOf(negated.Type.Kind) };
    }

    var helper = (tags & VbaTags.Currency) != VbaTags.None ? "PlyVarNegC" : "PlyVarNeg";
    return new(this.HelperCall(helper, this.Box(operand, line)), VbaType.Variant) { Tags = NegationTags(tags) };
  }

  /// <summary>
  /// <c>And</c>, <c>Or</c>, <c>Xor</c>, <c>Eqv</c>, <c>Imp</c> of a <c>Variant</c> ([MS-VBAL] §5.6.9.8):
  /// three-valued on <c>Boolean</c>s and <c>Null</c>, otherwise bitwise in the effective type the kinds
  /// give - <c>Byte</c>, <c>Boolean</c>, <c>Integer</c> or <c>Long</c> - with the <c>Null</c> rules of
  /// the operators: <c>0 And Null</c> is 0, <c>x Or Null</c> is x where x is not 0.
  /// </summary>
  private VbaValue VariantLogical(string op, VbaValue left, VbaValue right, int line) {
    var tags = TagsOf(left) | TagsOf(right);
    if ((tags & VbaTags.LongLong) != VbaTags.None)
      this.RefuseVariant(line, $"{op} of a Variant that may hold a LongLong");
    if ((tags & VbaTags.Currency) != VbaTags.None)
      this.RefuseVariant(line, $"{op} of a Variant that may hold a Currency");

    if ((tags & ~(VbaTags.Boolean | VbaTags.Null)) == VbaTags.None && op is "And" or "Or" or "Xor") {
      var code = op == "And" ? 0 : op == "Or" ? 1 : 2;
      return new(this.HelperCall("PlyVarLogic", I16(code), this.Box(left, line), this.Box(right, line)), VbaType.Variant) { Tags = VbaTags.Boolean | (tags & VbaTags.Null) };
    }

    var number = Array.IndexOf<string>(["And", "Or", "Xor", "Eqv", "Imp"], op);
    return new(this.HelperCall("PlyVarBits", I16(number), this.Box(left, line), this.Box(right, line)), VbaType.Variant) { Tags = BitwiseTags(TagsOf(left), TagsOf(right)) };
  }

  /// <summary>The kinds a bitwise operator of values of two sets of kinds may give: the effective types of [MS-VBAL] §5.6.9.8, and <c>Null</c>.</summary>
  private static VbaTags BitwiseTags(VbaTags left, VbaTags right) {
    var result = ((left | right) & VbaTags.Null) != VbaTags.None ? VbaTags.Null : VbaTags.None;
    foreach (var a in Each(left))
      foreach (var b in Each(right))
        result |= a == VbaTags.Null ? BitwiseKind(b) : b == VbaTags.Null ? BitwiseKind(a)
          : a == b && a is VbaTags.Byte or VbaTags.Boolean ? a
          : BitwiseKind(a) == VbaTags.Long || BitwiseKind(b) == VbaTags.Long ? VbaTags.Long : VbaTags.Integer;
    return result;
  }

  /// <summary>The effective type of one kind in a bitwise operator: a <c>Byte</c> and a <c>Boolean</c> their own, an <c>Integer</c> and <c>Empty</c> an <c>Integer</c>, every other a <c>Long</c>, <c>Null</c> nothing.</summary>
  private static VbaTags BitwiseKind(VbaTags tag) => tag switch {
    VbaTags.Null => VbaTags.Null,
    VbaTags.Byte or VbaTags.Boolean => tag,
    VbaTags.Integer or VbaTags.Empty => VbaTags.Integer,
    _ => VbaTags.Long
  };


  /// <summary>A <c>Variant</c> as a condition: <c>Null</c> and <c>Empty</c> are false, a number is true when it is not zero, a string by <c>CBool</c>.</summary>
  private IrExpr VariantCondition(VbaValue value, int line) {
    if ((value.Tags & VbaTags.Currency) != VbaTags.None)
      this.RefuseVariant(line, "a condition of a Variant that may hold a Currency");
    return this.HelperCall("PlyVarIsTrue", value.Expr);
  }

  #endregion

  #region Built-in functions

  /// <summary>
  /// The built-in functions that look at a <c>Variant</c>: <c>IsEmpty</c>, <c>IsNull</c>,
  /// <c>VarType</c>, <c>TypeName</c>, <c>IsNumeric</c> and <c>CVar</c>; a typed value answers by its
  /// type, a dynamic one by its tag. <see langword="null"/> for every other name.
  /// </summary>
  private VbaValue? VariantBuiltin(string key, Func<int, VbaValue> argument, int line) {
    if (key is not ("isempty" or "isnull" or "vartype" or "typename" or "isnumeric" or "cvar"))
      return null;

    var value = argument(0);
    // A typed value is never Empty or Null, and its type is its own.
    IrExpr Answer(IrExpr constant) {
      if (!Pure(value.Expr))
        this.Temporary(value.Expr.Type, value.Expr);
      return constant;
    }

    var dynamic = value.Type.IsVariant || value.Promoted is not null;
    switch (key) {
      case "isempty" or "isnull":
        if (!value.Type.IsVariant)
          return new(Answer(new IrConst(IrType.Bool, 0)), VbaType.Boolean);
        return new(new IrBinary(IrBinaryOp.Equal, new IrVariantTagOf(value.Expr), I32(key == "isempty" ? 0 : 1)), VbaType.Boolean);
      case "vartype":
        if (!dynamic)
          return new(Answer(I16((int)VarTypeOf(value.Type, line))), VbaType.Integer);
        return new(new IrConvert(IrType.I16, new IrVariantTagOf(this.Box(value, line)), IrConversion.Trap), VbaType.Integer);
      case "typename":
        if (!dynamic)
          return new(Answer(new IrStrConst(VarTypeOf(value.Type, line) == IrVariantTag.Bool ? "Boolean" : value.Type.ToString())), VbaType.String);
        return new(this.HelperCall("PlyVarTypeName", this.Box(value, line)), VbaType.String);
      case "isnumeric":
        if (value.Type.Kind == VbaKind.String)
          return new(this.HelperCall("PlyIsNumText", value.Expr), VbaType.Boolean);
        if (!value.Type.IsVariant)
          return new(Answer(new IrConst(IrType.Bool, value.Type.IsNumeric && value.Type.Kind != VbaKind.Date ? 1 : 0)), VbaType.Boolean);
        return new(this.HelperCall("PlyVarIsNumeric", value.Expr), VbaType.Boolean);
      default:
        return this.Boxed(value, line);
    }
  }

  /// <summary>The tag a value of a typed scalar has in a <c>Variant</c>.</summary>
  private IrVariantTag VarTypeOf(VbaType type, int line) => type.Kind switch {
    VbaKind.Boolean => IrVariantTag.Bool,
    VbaKind.Byte => IrVariantTag.U8,
    VbaKind.Integer => IrVariantTag.I16,
    VbaKind.Long => IrVariantTag.I32,
    VbaKind.LongLong => IrVariantTag.I64,
    VbaKind.Single => IrVariantTag.F32,
    VbaKind.Double => IrVariantTag.F64,
    VbaKind.String => IrVariantTag.String,
    VbaKind.Currency => IrVariantTag.Currency,
    VbaKind.Date => IrVariantTag.Date,
    _ => throw this.Unsupported(line, $"VarType of a {type}")
  };

  /// <summary>An operation of the IR on a <c>Variant</c> as a helper names it: <c>PlyIrVarType</c> and the payloads <c>PlyIrVarBool</c>, <c>PlyIrVarU8</c>, <c>PlyIrVarI16</c>, <c>PlyIrVarI32</c>, <c>PlyIrVarF32</c>, <c>PlyIrVarF64</c>, <c>PlyIrVarStr</c>, each read after a test of the tag.</summary>
  private static VbaValue? VariantPrimitive(string key, IrExpr value) {
    VbaValue Payload(IrVariantTag tag, VbaType type) => new(new IrVariantValue(value, tag) { IsTested = true }, type);
    return key switch {
      "plyirvartype" => new(new IrConvert(IrType.I16, new IrVariantTagOf(value), IrConversion.Trap), VbaType.Integer),
      "plyirvarbool" => Payload(IrVariantTag.Bool, VbaType.Boolean),
      "plyirvaru8" => Payload(IrVariantTag.U8, VbaType.Byte),
      "plyirvari16" => Payload(IrVariantTag.I16, VbaType.Integer),
      "plyirvari32" => Payload(IrVariantTag.I32, VbaType.Long),
      "plyirvarf32" => Payload(IrVariantTag.F32, VbaType.Single),
      "plyirvarf64" => Payload(IrVariantTag.F64, VbaType.Double),
      "plyirvarstr" => Payload(IrVariantTag.String, VbaType.String),
      "plyirvarcur" => Payload(IrVariantTag.Currency, VbaType.LongLong),
      "plyirvardate" => Payload(IrVariantTag.Date, VbaType.Double),
      "plyirmkcur" => new(new IrVariantOf(IrVariantTag.Currency, value), VbaType.Variant) { Tags = VbaTags.Currency },
      "plyirmkdate" => new(new IrVariantOf(IrVariantTag.Date, value), VbaType.Variant) { Tags = VbaTags.Date },
      _ => null
    };
  }

  #endregion

  #region For

  /// <summary>
  /// The kind a <c>For</c> loop over a <c>Variant</c> counter keeps the counter in, or <see langword="null"/>
  /// when its last step may promote it: the start's kind when the end and the step are integer
  /// constants no wider and the end plus the step still fit, or <c>Double</c>, which does not promote.
  /// </summary>
  private static VbaKind? Steady(VbaValue start, VbaValue end, VbaValue step) {
    if (start.IsVariant || end.Type.IsVariant || step.Type.IsVariant || !start.Type.IsNumeric || !end.Type.IsNumeric || !step.Type.IsNumeric || start.Type.Kind == VbaKind.Boolean)
      return null;
    if (start.Type.Kind == VbaKind.Double)
      return VbaKind.Double;
    if (start.Type.Kind is not (VbaKind.Byte or VbaKind.Integer or VbaKind.Long) || end.Expr is not IrConst { Type.IsInteger: true } last || step.Expr is not IrConst { Type.IsInteger: true } by
        || end.Type.Rank > start.Type.Rank || step.Type.Rank > start.Type.Rank)
      return null;

    var next = IrOperators.Number(last.Type, last.Value) + IrOperators.Number(by.Type, by.Value);
    return next >= IrOperators.Minimum(start.Type.Ir) && next <= IrOperators.Maximum(start.Type.Ir) ? start.Type.Kind : null;
  }

  /// <summary>
  /// <c>For</c> over a dynamic counter: the bounds as <c>Variant</c>s, the test by the sign of the
  /// step, and the step added by <c>Variant</c> arithmetic, which promotes the counter instead of
  /// overflowing.
  /// </summary>
  private IEnumerable<IrStmt> VariantFor(VbaFor loop, IrLocal counter) {
    var start = this.Expression(loop.Start);
    var startHeld = this.Temporary(IrType.Variant, this.Box(start, loop.Line));
    var end = this.Expression(loop.End);
    var step = loop.Step is null ? new VbaValue(I16(1), VbaType.Integer) : this.Expression(loop.Step);
    var bounds = TagsOf(start) | TagsOf(end) | TagsOf(step);
    if ((bounds & (VbaTags.String | VbaTags.Null | VbaTags.Empty)) != VbaTags.None)
      this.RefuseVariant(loop.Line, $"a For loop whose bounds may be a {Names(bounds & (VbaTags.String | VbaTags.Null | VbaTags.Empty))}");

    var candidate = this._inference?.Candidates.Contains(loop.Variable) == true;
    if (candidate)
      this.Record(loop.Variable, start);
    var limit = this.Temporary(IrType.Variant, this.Box(end, loop.Line));
    var by = this.Temporary(IrType.Variant, this.Box(step, loop.Line));
    var statements = new List<IrStmt> { new IrAssign(counter, new IrLocalRef(startHeld)) };
    this.EnterLoop(loop);
    var head = this.Snapshot();
    var counterTags = candidate ? this._inference!.Read(loop.Variable) : VbaTags.Any;
    if ((counterTags & (VbaTags.String | VbaTags.Null | VbaTags.Empty)) != VbaTags.None)
      this.RefuseVariant(loop.Line, $"a For loop whose Variant counter '{loop.Variable}' may hold a {Names(counterTags & (VbaTags.String | VbaTags.Null | VbaTags.Empty))}");

    var condition = this.HelperCall("PlyVarForTest", new IrLocalRef(counter), new IrLocalRef(limit), new IrLocalRef(by));
    this._loops.Push("For");
    var body = this.Block(loop.Body);
    this._loops.Pop();
    if (candidate) {
      // The step is added to what the body left in the counter, a String too, which the locale reads.
      var stepped = this._inference!.Read(loop.Variable);
      if ((stepped & VbaTags.String) != VbaTags.None)
        this.RefuseVariant(loop.Line, $"a For loop whose Variant counter '{loop.Variable}' may hold a String");
      this.Record(loop.Variable, new(I16(0), VbaType.Variant) { Tags = Steady(start, end, step) is { } kind ? TagOf(kind) : ArithmeticTags("+", stepped, TagsOf(step)) });
    }

    this.LeaveLoop(loop, head);
    var advance = new IrAssign(counter, this.HelperCall("PlyVarArith", I16(0), new IrLocalRef(counter), new IrLocalRef(by)));
    statements.Add(this.Lowered(loop, new IrFor(condition, new IrBlock(body), new IrBlock([advance]))));
    return statements;
  }

  #endregion

}
