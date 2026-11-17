using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The code for a lazy sequence the Rust backend cannot write as an iterator.</summary>
  public const string IteratorForm = "PLY1558";

  /// <summary>The registered diagnostic of <see cref="IteratorForm"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor IteratorFormDescriptor = new(IteratorForm, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "rust.backend.iterators");

  /// <summary>The name of the field of an iterator's struct that holds the state it resumes in.</summary>
  private const string StateField = "state";

  /// <summary>An iterator function of the module: the name of its struct and the generic of each sequence parameter.</summary>
  /// <param name="Struct">The name of the struct.</param>
  /// <param name="Generics">The sequence parameters, each with the name of its generic.</param>
  /// <param name="Borrows">Whether it takes an array or a string, which the struct borrows for the lifetime <c>'a</c>.</param>
  private sealed record IteratorShape(string Struct, IReadOnlyList<(IrLocal Parameter, string Generic)> Generics, bool Borrows);

  /// <summary>Where the body of an iterator is left on its way out: a cursor of a loop over a sequence, or a finally block.</summary>
  /// <param name="Cursor">The number of the cursor, or -1 for a finally block.</param>
  /// <param name="Finally">The finally block, or <see langword="null"/> for a cursor.</param>
  private sealed record Region(int Cursor, IrBlock? Finally);

  /// <summary>A part of a state of an iterator, before its exit.</summary>
  private abstract record Piece;

  /// <summary>A statement that does not suspend, written as it is.</summary>
  private sealed record Run(IrStmt Statement) : Piece;

  /// <summary><c>if !c { jump }</c>.</summary>
  private sealed record Unless(IrExpr Condition, int Target) : Piece;

  /// <summary><c>self.cursorN = Some(sequence);</c>.</summary>
  private sealed record Open(int Cursor, IrExpr Sequence) : Piece;

  /// <summary><c>self.cursorN = None;</c>.</summary>
  private sealed record Close(int Cursor) : Piece;

  /// <summary>The next value of a cursor into a variable; at the end of the cursor a jump.</summary>
  private sealed record Pull(int Cursor, IrLocal Element, int Exit) : Piece;

  /// <summary>How a state of an iterator ends.</summary>
  private abstract record Exit;

  /// <summary>Goes on in another state.</summary>
  private sealed record Goto(int Target) : Exit;

  /// <summary>Gives a value and resumes in another state.</summary>
  private sealed record Produce(IrExpr Value, int Resume) : Exit;

  /// <summary>Ends the sequence.</summary>
  private sealed record Finish : Exit;

  /// <summary>A state of an iterator: what it does, how it ends.</summary>
  private sealed class State {

    public List<Piece> Pieces { get; } = [];

    public Exit? Exit { get; set; }

  }

  private sealed partial class Writer {

    /// <summary>The iterator functions of the module by their IR name.</summary>
    private readonly Dictionary<string, IteratorShape> _iterators = new(StringComparer.Ordinal);

    /// <summary>The structs and implementations of the iterators, in the order of their functions.</summary>
    private readonly List<RsItem> _iteratorItems = [];

    /// <summary>The fields of the iterator being written: the cursors.</summary>
    private Dictionary<int, string> _cursors = [];

    /// <summary>The number of the state that ends the iterator being written.</summary>
    private int _end;

    /// <summary>Whether the states of the iterator being written stand in a loop: a state goes on in another one.</summary>
    private bool _looping;

    /// <summary>
    /// Names the struct of every iterator function - its Rust name in upper camel case, unique among
    /// the types - and the generic of each of its sequence parameters, <c>S</c>, <c>S1</c>, ….
    /// </summary>
    private void NameIterators(IrModule module) {
      var iterators = module.Functions.Where(IrSequences.IsIterator).ToList();
      var taken = new HashSet<string>(this._types.Values, StringComparer.Ordinal);
      var names = RustNames.Unique([.. iterators.Select(function => function.Name)], name => RustNames.UpperCamel(this._functions[name].Replace("r#", "", StringComparison.Ordinal), this.Edition), taken);
      foreach (var (function, name) in iterators.Zip(names)) {
        var sequences = function.Parameters.Where(parameter => parameter.Type.IsLazySequence).ToList();
        this._iterators[function.Name] = new(name, [.. sequences.Select((parameter, index) => (parameter, index == 0 ? "S" : "S" + index.ToString(CultureInfo.InvariantCulture)))],
          function.Parameters.Any(parameter => parameter.Type.IsArray || parameter.Type.IsString));
      }
    }

    /// <summary>Records that a lazy sequence cannot be written as a Rust iterator, once per construct, and returns a stand-in.</summary>
    private RsExpression RefuseIterator(string what) {
      if (this._refused.Add(what))
        this._problems.Add(IteratorFormDescriptor.At($"'{what}' cannot be written as a Rust iterator (in {this._construct})"));

      return new RsPath("_refused");
    }

    /// <summary>The Rust type of the values of a sequence.</summary>
    private string ItemType(IrType sequence) {
      var element = sequence.Element!;
      return element.IsString ? OwnedString
        : element.IsObject ? this.ObjectType(element, false)
        : element.IsFunction ? this.FunctionType(element)
        : TypeName(element) is not null || element.IsStruct || element.IsEnum ? this.Type(element)
        : ((RsPath)this.RefuseIterator($"a sequence of {element}")).Name;
    }

    /// <summary>The bound of a sequence: <c>Iterator&lt;Item = T&gt; + Clone</c>.</summary>
    private string SequenceBound(IrType sequence) => $"Iterator<Item = {this.ItemType(sequence)}> + Clone";

    /// <summary>The type of a sequence parameter of a function that is no iterator: <c>impl Iterator&lt;Item = T&gt; + Clone</c>.</summary>
    private string SequenceParameterType(IrType sequence) => "impl " + this.SequenceBound(sequence);

    /// <summary>The struct of an iterator with the types of its generics, <c>TakeI32&lt;Fib&gt;</c>.</summary>
    private static string StructType(IteratorShape shape, string lifetime, Func<int, string> generic) {
      var arguments = (shape.Borrows ? [lifetime] : Array.Empty<string>()).Concat(shape.Generics.Select((_, index) => generic(index))).ToList();
      return arguments.Count == 0 ? shape.Struct : $"{shape.Struct}<{string.Join(", ", arguments)}>";
    }

    /// <summary>
    /// The Rust type of a sequence a field of an iterator holds: the struct of the iterator function
    /// called, or the generic of a sequence parameter.
    /// </summary>
    private string HeldSequenceType(IrExpr sequence, IteratorShape shape) {
      switch (sequence) {
        case IrCall call when this._iterators.TryGetValue(call.Function, out var callee): {
          if (call.Function == this._function.Name || this.Reaches(call.Function, this._function.Name))
            return ((RsPath)this.RefuseIterator("a recursive iterator function, whose struct would hold itself")).Name;

          var positions = this._module.Functions.First(function => function.Name == call.Function).Parameters;
          return StructType(callee, "'a", index => this.HeldSequenceType(call.Arguments[positions.ToList().IndexOf(callee.Generics[index].Parameter)], shape));
        }
        case IrLocalRef { Local: var local } when shape.Generics.FirstOrDefault(pair => pair.Parameter == local) is { Generic: { } generic }:
          return generic;
        case IrArraySequence elements:
          return $"std::iter::Copied<std::slice::Iter<'a, {this.ItemType(elements.Type)}>>";
        default:
          return ((RsPath)this.RefuseIterator("a loop inside an iterator over a sequence that is no call of an iterator function and no sequence parameter")).Name;
      }
    }

    /// <summary>Whether the struct of an iterator function holds, through the cursors of its loops, that of another one.</summary>
    private bool Reaches(string from, string to) {
      var seen = new HashSet<string>(StringComparer.Ordinal);
      var pending = new Stack<string>([from]);
      while (pending.Count > 0) {
        var name = pending.Pop();
        if (!seen.Add(name))
          continue;

        var function = this._module.Functions.First(candidate => candidate.Name == name);
        foreach (var held in HeldSequences(function.Body)) {
          foreach (var call in IrTree.Nodes(held).OfType<IrCall>().Where(call => this._iterators.ContainsKey(call.Function))) {
            if (call.Function == to)
              return true;

            pending.Push(call.Function);
          }
        }
      }

      return false;
    }

    /// <summary>The sequences the cursors of an iterator body hold: of every loop that suspends and every <c>yield from</c>.</summary>
    private static IEnumerable<IrExpr> HeldSequences(IrBlock body)
      => IrSequences.Statements(body).Select(statement => statement switch {
        IrYieldFrom delegated => delegated.Sequence,
        IrForEachSequence loop when Cuts(loop) => loop.Sequence,
        _ => null
      }).OfType<IrExpr>();

    /// <summary>
    /// A sequence where it is enumerated or passed on: the call of an iterator function as it is, a
    /// variable cloned for an enumeration of its own (a single-pass one moved, and refused where it is
    /// named twice), an array's elements <c>a.iter().copied()</c>. A sequence held - by a variable or by
    /// a cursor of an iterator - borrows its arrays and strings as long as it lives: none of them may be
    /// written in the function, and an iterator's own may only be its parameters, which its struct borrows.
    /// </summary>
    private RsExpression SequenceValue(IrExpr sequence, bool held) {
      if (held && IrSequences.IsIterator(this._function) && Borrowed(sequence).Any(local => !this._function.Parameters.Contains(local)))
        return this.RefuseIterator("a sequence an iterator keeps that borrows an array or a string of its own");
      if (held && Borrowed(sequence).Any(this.IsWrittenHere))
        return this.RefuseIterator("an array or a string a sequence borrows, written while a variable holds the sequence");

      switch (sequence) {
        case IrCall call when this._iterators.ContainsKey(call.Function):
          return this.Expression(call);
        case IrLocalRef { Local: var local } reference when sequence.Type.Restartable:
          return new RsMethodCall(new RsPath(this._locals[local]) { Type = reference.Type }, "clone", []) { Type = reference.Type };
        case IrLocalRef { Local: var local } reference:
          if (IrTree.Nodes(this._function.Body).Count(node => node is IrLocalRef other && other.Local == local) > 1)
            return this.RefuseIterator("a single-pass sequence enumerated twice");

          return new RsPath(this._locals[local]) { Type = reference.Type };
        case IrArraySequence { Array: IrLocalRef { Local: var array } } elements when IsCopied(elements.Type.Element!):
          return new RsMethodCall(new RsMethodCall(new RsPath(this._locals[array]), "iter", []), "copied", []) { Type = sequence.Type };
        case IrArraySequence:
          return this.RefuseIterator("an array as a sequence that is no variable of numbers");
        default:
          return this.RefuseIterator($"a sequence made by {sequence.GetType().Name[2..]}");
      }
    }

    /// <summary>The arrays and strings a sequence borrows: every array or string variable it names.</summary>
    private static IEnumerable<IrLocal> Borrowed(IrExpr sequence)
      => IrTree.Nodes(sequence).OfType<IrLocalRef>().Where(reference => reference.Type.IsArray || reference.Type.IsString).Select(reference => reference.Local);

    /// <summary>Whether the function being written writes an array or a string variable, or assigns it again.</summary>
    private bool IsWrittenHere(IrLocal local)
      => this._arrayFacts.MutableLocals.Contains(local)
        || this._arrayParameters.TryGetValue(local, out var position) && this._arrayFacts.MutableParameters.Contains(position)
        || Polypiler.Core.Backends.Rust.Emit.ArrayOwnership.Statements(this._function.Body).Any(statement => statement is IrAssign assign && assign.Local == local);

    /// <summary>A sequence passed to a function: an array's elements only where no other argument names the array, which the call would borrow twice.</summary>
    private RsExpression SequenceArgument(IrCall call, IrExpr argument)
      => Borrowed(argument).Any(local => call.Arguments.Any(other => !ReferenceEquals(other, argument) && Referenced(other).Contains(local)))
        ? this.RefuseIterator("an array or a string a sequence borrows, passed with the sequence")
        : this.SequenceValue(argument, false);

    /// <summary>Whether values of a type are copied where Rust reads them out of a slice: a scalar, an enum, a struct of numbers.</summary>
    private static bool IsCopied(IrType element) => TypeName(element) is not null || element.IsEnum || element.IsStruct;

    /// <summary>A variable holding a sequence: <c>let s = f(2);</c>, assigned once.</summary>
    private void SequenceDeclaration(IrDeclare declare, List<RsStatement> into) {
      if (declare.Initializer is not { } initializer || Polypiler.Core.Backends.Rust.Emit.ArrayOwnership.Statements(this._function.Body).Any(statement => statement is IrAssign assign && assign.Local == declare.Local)) {
        into.Add(new RsExpressionStatement(this.RefuseIterator("a sequence variable assigned again")));
        return;
      }

      into.Add(new RsLet(this._locals[declare.Local], false, null, this.SequenceValue(initializer, true)));
    }

    /// <summary>
    /// A loop over a sequence that does not suspend: <c>for v in s { … }</c>. The cursor is dropped
    /// however the loop ends, which runs the finally blocks pending in it.
    /// </summary>
    private void ForEachSequence(IrForEachSequence loop, List<RsStatement> into) {
      if (Borrowed(loop.Sequence).Any(local => Mentions(loop.Body, local))) {
        into.Add(new RsExpressionStatement(this.RefuseIterator("an array or a string a sequence borrows, used in the loop over it")));
        return;
      }

      var source = this.SequenceValue(loop.Sequence, false);
      var pattern = (Assigns(loop.Body.Statements, loop.Element) ? "mut " : "") + this._locals[loop.Element];
      var statements = loop.Body.Statements;
      if (statements is [.., IrBreak or IrReturn or IrThrow] && !ContinuesLoop(statements) && !BreaksLoop(statements.Take(statements.Count - 1))) {
        // A loop whose first pass always leaves it takes one value (clippy's never_loop); the cursor is dropped after it.
        var once = this.Block(statements is [.., IrBreak] ? [.. statements.Take(statements.Count - 1)] : statements);
        into.Add(new RsExpressionStatement(new RsIf(new RsLetCondition($"Some({pattern})", new RsMethodCall(this.Receiver(source), "next", [])), once, null)));
        return;
      }

      var label = this.NewLabel("l");
      var breakable = new Breakable(BreakableKind.Loop, label);
      this._breakables.Add(breakable);
      var body = this.Block(loop.Body.Statements);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      into.Add(new RsExpressionStatement(new RsFor(breakable.Used ? label : null, pattern, source, body)));
    }

    /// <summary>Whether a statement of an iterator body has to be cut into states: it suspends, ends the sequence or jumps out of itself.</summary>
    private static bool Cuts(IrStmt statement) => Cuts(statement, 0, 0);

    private static bool Cuts(IrStmt statement, int loops, int breakables) => statement switch {
      IrYield or IrYieldFrom or IrYieldBreak or IrReturn => true,
      IrBreak => breakables == 0,
      IrContinue => loops == 0,
      IrBlock block => block.Statements.Any(inner => Cuts(inner, loops, breakables)),
      IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence => IrTree.Blocks(statement).Any(block => Cuts(block, loops + 1, breakables + 1)),
      IrSwitch => IrTree.Blocks(statement).Any(block => Cuts(block, loops, breakables + 1)),
      _ => IrTree.Blocks(statement).Any(block => Cuts(block, loops, breakables))
    };

    /// <summary>The body of an iterator function cut into states, with the regions pending at every resume point.</summary>
    private sealed class Machine(Writer writer) {

      public List<State> States { get; } = [new()];

      public int Current { get; set; }

      public List<Region> Regions { get; } = [];

      /// <summary>The loops around the statement being cut: where a break and a continue go, and how many regions are pending there.</summary>
      public List<(int Break, int? Continue, int Depth)> Frames { get; } = [];

      /// <summary>The regions pending at each resume point, by its state.</summary>
      public Dictionary<int, IReadOnlyList<Region>> Pending { get; } = [];

      /// <summary>The number of cursors made.</summary>
      public int Cursors { get; set; }

      /// <summary>The variables a <c>yield from</c> takes its values into, with their element type.</summary>
      public List<IrLocal> Items { get; } = [];

      private State At => this.States[this.Current];

      private int New() {
        this.States.Add(new());
        return this.States.Count - 1;
      }

      /// <summary>Ends the state being cut unless it has ended, and goes on in another one.</summary>
      private void End(Exit exit, int next) {
        this.At.Exit ??= exit;
        this.Current = next;
      }

      private void Add(Piece piece) {
        if (this.At.Exit is null)
          this.At.Pieces.Add(piece);
      }

      /// <summary>Leaves the regions above a depth, innermost first.</summary>
      private void Leave(int depth) {
        for (var index = this.Regions.Count - 1; index >= depth; --index)
          this.Add(this.Regions[index].Finally is { } final ? new Run(final) : new Close(this.Regions[index].Cursor));
      }

      /// <summary>Cuts one statement of the body.</summary>
      public void Cut(IrStmt statement) {
        if (statement is IrBlock block) {
          foreach (var inner in block.Statements)
            this.Cut(inner);

          return;
        }

        if (!Cuts(statement)) {
          this.Add(new Run(statement));
          return;
        }

        switch (statement) {
          case IrYield produced: {
            var resume = this.New();
            if (this.At.Exit is null)
              this.Pending[resume] = [.. this.Regions];

            this.End(new Produce(produced.Value, resume), resume);
            return;
          }
          case IrYieldBreak or IrReturn:
            this.Leave(0);
            this.End(new Finish(), this.New());
            return;
          case IrBreak: {
            var frame = this.Frames[^1];
            this.Leave(frame.Depth);
            this.End(new Goto(frame.Break), this.New());
            return;
          }
          case IrContinue: {
            var frame = this.Frames.Last(candidate => candidate.Continue is not null);
            this.Leave(frame.Depth);
            this.End(new Goto(frame.Continue!.Value), this.New());
            return;
          }
          case IrIf choice: {
            var end = this.New();
            var otherwise = choice.Else is null ? end : this.New();
            this.Add(new Unless(choice.Condition, otherwise));
            this.Cut(choice.Then);
            this.End(new Goto(end), otherwise);
            if (choice.Else is { } other) {
              this.Cut(other);
              this.End(new Goto(end), end);
            }

            return;
          }
          case IrWhile loop: {
            var head = this.New();
            var end = this.New();
            this.End(new Goto(head), head);
            if (loop.Condition is not IrConst { Value: not 0 })
              this.Add(new Unless(loop.Condition, end));

            this.Body(loop.Body, end, head);
            this.End(new Goto(head), end);
            return;
          }
          case IrDoWhile loop: {
            var pass = this.New();
            var test = this.New();
            var after = this.New();
            this.End(new Goto(pass), pass);
            this.Body(loop.Body, after, test);
            this.End(new Goto(test), test);
            this.Add(new Unless(loop.Condition, after));
            this.End(new Goto(pass), after);
            return;
          }
          case IrFor loop: {
            var top = this.New();
            var step = this.New();
            var done = this.New();
            this.End(new Goto(top), top);
            if (loop.Condition is not IrConst { Value: not 0 })
              this.Add(new Unless(loop.Condition, done));

            this.Body(loop.Body, done, step);
            this.End(new Goto(step), step);
            this.Cut(loop.Step);
            this.End(new Goto(top), done);
            return;
          }
          case IrForEachSequence loop:
            this.Sequence(loop.Sequence, loop.Element, loop.Body);
            return;
          case IrYieldFrom delegated: {
            var item = new IrLocal("$item" + this.Items.Count.ToString(CultureInfo.InvariantCulture), delegated.Sequence.Type.Element!);
            this.Items.Add(item);
            this.Sequence(delegated.Sequence, item, null);
            return;
          }
          case IrTry { Catches.Count: 0, Finally: { } final } attempt when !Cuts(final):
            this.Regions.Add(new Region(-1, final));
            this.Cut(attempt.Body);
            this.Regions.RemoveAt(this.Regions.Count - 1);
            this.Add(new Run(final));
            return;
          case IrTry:
            writer.RefuseIterator("a jump out of a try that has a catch clause, or out of a finally block, inside an iterator");
            return;
          default:
            writer.RefuseIterator($"a {statement.GetType().Name[2..]} that suspends");
            return;
        }
      }

      /// <summary>The body of a loop, whose break goes to one state and whose continue to another.</summary>
      private void Body(IrBlock body, int breaks, int continues) {
        this.Frames.Add((breaks, continues, this.Regions.Count));
        this.Cut(body);
        this.Frames.RemoveAt(this.Frames.Count - 1);
      }

      /// <summary>
      /// A loop over a sequence that suspends, or the values of a sequence yielded as the body's own:
      /// a cursor the struct keeps, opened before the loop and dropped where the loop ends, which runs
      /// the finally blocks pending in it.
      /// </summary>
      private void Sequence(IrExpr sequence, IrLocal element, IrBlock? body) {
        var cursor = this.Cursors++;
        this.Add(new Open(cursor, sequence));
        this.Regions.Add(new Region(cursor, null));
        var head = this.New();
        var exit = this.New();
        this.End(new Goto(head), head);
        this.Add(new Pull(cursor, element, exit));
        if (body is null) {
          this.Pending[head] = [.. this.Regions];
          this.End(new Produce(new IrLocalRef(element), head), exit);
        } else {
          this.Frames.Add((exit, head, this.Regions.Count));
          this.Cut(body);
          this.Frames.RemoveAt(this.Frames.Count - 1);
          this.End(new Goto(head), exit);
        }

        this.Add(new Close(cursor));
        this.Regions.RemoveAt(this.Regions.Count - 1);
      }

      /// <summary>Ends the body: the last state finishes the sequence.</summary>
      public void Finished() => this.End(new Finish(), this.Current);

    }

    /// <summary>
    /// An iterator function: a struct of its state, its parameters and the variables it keeps from one
    /// state to the next, an <c>impl Iterator</c> whose <c>next</c> resumes in the state it stopped in,
    /// an <c>impl Drop</c> where a finally block may be pending when the consumer drops it, and the
    /// function, which makes the struct in its first state.
    /// </summary>
    private RsFunction Iterator(IrFunction function, string name) {
      var shape = this._iterators[function.Name];
      if (this._free.ContainsKey(function.Name))
        this.RefuseIterator("an iterator method of a class with a hierarchy");
      if (this._throwing.Contains(function.Name))
        this.RefuseIterator("an iterator function that lets an exception out");

      foreach (var parameter in function.Parameters.Where(parameter => parameter.Type.IsCollection))
        this.RefuseIterator("an iterator function that takes a collection");
      foreach (var parameter in function.Parameters.Where(parameter => parameter.Type.IsArray && (this.IsNullableArray(parameter) || this._arrayFacts.MutableParameters.Contains(function.Parameters.ToList().IndexOf(parameter)))))
        this.RefuseIterator("an iterator function that writes an array it takes, or takes null for it");
      foreach (var parameter in function.Parameters.Where(parameter => parameter.Type.IsString && this._ownedStringParameters.Contains(parameter)))
        this.RefuseIterator("an iterator function that assigns a string it takes");

      // One borrowed parameter gives its lifetime to the struct by elision; with more the function names it.
      var references = function.Parameters.Select((parameter, index) => (parameter, index)).Count(pair => this.ParameterType(function, pair.index).StartsWith('&'));
      var lifetime = references == 1 ? "'_" : "'a";

      var machine = new Machine(this);
      machine.Cut(function.Body);
      machine.Finished();
      foreach (var produced in machine.Items)
        this._locals[produced] = this.Temporary("item");

      var states = Compacted(machine.States, machine.Pending, out var pending);
      var kept = this.Kept(function, states, pending);
      var fields = new List<RsFieldDeclaration> { new(StateField, "", "u32") };
      var values = new List<RsFieldValue> { new(StateField, new RsLiteral("0")) };
      var taken = new HashSet<string>([StateField], StringComparer.Ordinal);
      var names = new Dictionary<IrLocal, string>(this._locals);
      // The object of an iterator method is a field named after its class, self.counter.
      var receiver = this._members.GetValueOrDefault(function.Name) is { Kind: MemberKind.Instance } ? function.Parameters[0] : null;
      if (receiver is not null)
        this._locals[receiver] = RustNames.Snake(this.StructName((IrClassType)receiver.Type.Declaration!), this.Edition);
      foreach (var local in function.Parameters.Concat(function.Locals).Where(kept.Contains)) {
        var field = RustNames.Unique([this._locals[local]], same => same, taken)[0];
        var parameter = function.Parameters.Contains(local);
        var type = shape.Generics.FirstOrDefault(pair => pair.Parameter == local).Generic
          ?? (parameter ? this.KeptParameterType(function, local, "'a") : this.KeptType(local));
        fields.Add(new(field, "", type));
        values.Add(new(field, local == receiver ? new RsCall(new RsPath("Rc::clone"), [new RsPath("self")])
          : parameter ? this.KeptParameterValue(function, local, names[local]) : this.Zero(local, type)));
        this._locals[local] = "self." + field;
      }

      this._cursors = [];
      var opened = states.SelectMany(state => state.Pieces).OfType<Open>().ToList();
      foreach (var open in opened) {
        var field = RustNames.Unique(["cursor" + open.Cursor.ToString(CultureInfo.InvariantCulture)], cursor => cursor, taken)[0];
        this._cursors[open.Cursor] = field;
        fields.Add(new(field, "", $"Option<{this.HeldSequenceType(open.Sequence, shape)}>"));
        values.Add(new(field, new RsPath("None")));
      }

      var expressions = states.SelectMany(state => state.Pieces.SelectMany(piece => piece switch {
        Run run => IrTree.Nodes(run.Statement),
        Unless jump => IrTree.Nodes(jump.Condition),
        Open open => IrTree.Nodes(open.Sequence),
        _ => []
      }).Concat(state.Exit is Produce produced ? IrTree.Nodes(produced.Value) : []));
      foreach (var lambda in expressions.OfType<IrLambda>())
        if (IrTree.Nodes(lambda.Body).OfType<IrLocalRef>().Any(reference => kept.Contains(reference.Local)))
          this.RefuseIterator("a closure that captures a variable an iterator keeps across a yield");

      this._owned = [.. this._owned, .. function.Parameters.Where(parameter => parameter.Type.IsObject)];
      this._end = states.Count;
      this._looping = states.Any(state => state.Exit is Goto { Target: var target } && target != this._end
        || state.Pieces.Any(piece => piece is Unless { Target: var jump } && jump != this._end || piece is Pull { Exit: var exit } && exit != this._end));
      var item = this.ItemType(function.ReturnType);
      var arms = states.Select((state, index) => new RsMatchArm([index.ToString(CultureInfo.InvariantCulture)], new RsBlockExpression(this.Arm(state)))).ToList();
      arms.Add(new RsMatchArm(["_"], this._looping ? new RsReturn(new RsPath("None")) : new RsPath("None")));
      var dispatch = new RsMatch(new RsField(new RsPath("self"), StateField), arms);
      var body = this._looping ? new RsBlock([], new RsLoop(null, new RsBlock([new RsExpressionStatement(dispatch)], null))) : new RsBlock([], dispatch);
      var next = new RsFunction("next", "", [new RsParameter("&mut self", "")], $"Option<{item}>", Callable(body));

      var lifetimes = shape.Borrows ? new[] { "'a" } : [];
      string Listed(IEnumerable<string> parts) => parts.Any() ? $"<{string.Join(", ", parts)}>" : "";
      var bounded = shape.Generics.Select(pair => $"{pair.Generic}: {this.SequenceBound(pair.Parameter.Type)}").ToList();
      var generics = Listed(lifetimes.Concat(bounded));
      var bare = Listed(lifetimes.Concat(shape.Generics.Select(pair => pair.Generic)));
      var structType = shape.Struct + bare;
      this._iteratorItems.Add(new RsStruct(shape.Struct, "pub", fields) { Generics = bare, Attributes = ["derive(Clone)"] });
      this._iteratorItems.Add(new RsImpl("Iterator", structType, [new RsTypeAlias("Item", item), next]) { Generics = generics });
      if (this.Dropped(pending) is { } dropped)
        this._iteratorItems.Add(new RsImpl("Drop", structType, [new RsFunction("drop", "", [new RsParameter("&mut self", "")], null, dropped)]) { Generics = bare });

      var parameters = function.Parameters.Select(parameter => new RsParameter(names[parameter],
        shape.Generics.FirstOrDefault(pair => pair.Parameter == parameter).Generic ?? this.KeptParameterType(function, parameter, lifetime == "'_" ? "" : lifetime, true))).ToList();
      var made = StructType(shape, lifetime, index => shape.Generics[index].Generic);
      var named = Listed((lifetime == "'a" ? lifetimes : []).Concat(bounded));
      var make = new RsFunction(name, function.IsPublic ? "pub" : "", parameters, made, new RsBlock([], new RsStructLiteral(shape.Struct, values))) { Generics = named };
      return make;
    }

    /// <summary><c>a sequence</c>, <c>an array</c> or <c>a collection</c>, as a refusal names a type of them.</summary>
    private static string Article(IrType type) => type.IsLazySequence ? "a sequence" : type.IsArray ? "an array" : "a collection";

    /// <summary>
    /// The states without those nothing reaches, a state that only one other one goes on to merged into
    /// it, numbered anew from 0 in the order they were made; the pending regions renumbered with them.
    /// </summary>
    private static List<State> Compacted(List<State> states, Dictionary<int, IReadOnlyList<Region>> pending, out Dictionary<int, IReadOnlyList<Region>> renumbered) {
      static IEnumerable<int> Targets(State state) => state.Pieces.Select(piece => piece switch {
        Unless jump => jump.Target,
        Pull pull => pull.Exit,
        _ => -1
      }).Where(target => target >= 0).Concat(state.Exit switch {
        Goto jump => [jump.Target],
        Produce produced => [produced.Resume],
        _ => []
      });

      // A jump to a state that does nothing but go on to another one goes there directly.
      int Through(int target) {
        for (var step = 0; step < states.Count && states[target] is { Pieces.Count: 0, Exit: Goto { Target: var next } }; ++step)
          target = next;

        return target;
      }

      foreach (var state in states) {
        for (var at = 0; at < state.Pieces.Count; ++at)
          state.Pieces[at] = state.Pieces[at] switch {
            Unless jump => jump with { Target = Through(jump.Target) },
            Pull pull => pull with { Exit = Through(pull.Exit) },
            var piece => piece
          };

        if (state.Exit is Produce { Resume: var resume } produced && Through(resume) is var through && through != resume) {
          pending.TryAdd(through, pending[resume]);
          state.Exit = produced with { Resume = through };
        } else if (state.Exit is Goto jump) {
          state.Exit = jump with { Target = Through(jump.Target) };
        }
      }

      while (true) {
        var merged = false;
        var reached = new HashSet<int>([0]);
        var pendingStates = new Stack<int>([0]);
        while (pendingStates.Count > 0)
          foreach (var target in Targets(states[pendingStates.Pop()]))
            if (reached.Add(target))
              pendingStates.Push(target);

        var incoming = reached.SelectMany(index => Targets(states[index])).GroupBy(target => target).ToDictionary(group => group.Key, group => group.Count());
        foreach (var index in reached.Order()) {
          if (states[index].Exit is not Goto { Target: var target } || target == index || target == 0 || incoming[target] != 1)
            continue;

          states[index].Pieces.AddRange(states[target].Pieces);
          states[index].Exit = states[target].Exit;
          states[target].Pieces.Clear();
          states[target].Exit = new Finish();
          merged = true;
          break;
        }

        if (!merged) {
          // A state that only ends the sequence is left out: a jump to it ends the sequence itself.
          var order = reached.Order().Where(index => index == 0 || states[index].Pieces.Count > 0 || states[index].Exit is not Finish).ToList();
          var numbers = order.Select((index, position) => (index, position)).ToDictionary(pair => pair.index, pair => pair.position);
          foreach (var index in reached.Where(index => !numbers.ContainsKey(index)))
            numbers[index] = order.Count;
          renumbered = pending.Where(pair => order.Contains(pair.Key)).ToDictionary(pair => numbers[pair.Key], pair => pair.Value);
          return [.. order.Select(index => Renumbered(states[index], numbers))];
        }
      }
    }

    private static State Renumbered(State state, Dictionary<int, int> numbers) {
      var copy = new State();
      copy.Pieces.AddRange(state.Pieces.Select(piece => piece switch {
        Unless jump => jump with { Target = numbers[jump.Target] },
        Pull pull => pull with { Exit = numbers[pull.Exit] },
        _ => piece
      }));
      copy.Exit = state.Exit switch {
        Goto jump => jump with { Target = numbers[jump.Target] },
        Produce produced => produced with { Resume = numbers[produced.Resume] },
        var exit => exit
      };
      return copy;
    }
  }

}
