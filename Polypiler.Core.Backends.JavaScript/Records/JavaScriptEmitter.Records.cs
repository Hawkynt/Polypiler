using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  /// <summary>
  /// Where a function reads a struct whose place stays live afterwards: those reads, by identity,
  /// and the parameters that are copied when the function starts.
  /// </summary>
  /// <param name="Live">The reads of a variable that is used again later, or inside a loop it was declared outside of.</param>
  /// <param name="CopiedOnEntry">The struct parameters of a public function that the function writes through.</param>
  private sealed record Liveness(HashSet<IrLocalRef> Live, IReadOnlyList<IrLocal> CopiedOnEntry) {

    /// <summary>
    /// The liveness of a function's struct reads. A JavaScript object is a reference, so a struct
    /// read into another place has to be copied exactly where the place it was read from can still
    /// be used: a later read, a later write of one of its fields, or a pass of a loop that the
    /// variable outlives. Every use is put on one line in the order of evaluation; a branch that
    /// does not run counts as running, which errs towards a copy.
    /// <para>
    /// A public function is called from outside, where nothing is known: it never writes the object
    /// it is given - a parameter it writes through is copied on entry - and never hands back an
    /// object of its caller - every other read of a struct parameter is a copy.
    /// </para>
    /// </summary>
    public static Liveness Of(IrFunction function) {
      var timeline = new Timeline();
      timeline.Block(function.Body);
      var written = timeline.Written;
      var entry = function.IsPublic ? function.Parameters.Where(parameter => parameter.Type.IsStruct && written.Contains(parameter)).ToList() : [];
      var outside = function.IsPublic ? function.Parameters.Where(parameter => parameter.Type.IsStruct && !written.Contains(parameter)).ToHashSet() : [];
      return new(timeline.LiveReads(outside), entry);
    }

  }

  /// <summary>Every use of a variable in the order of evaluation, with the loops around it.</summary>
  private sealed class Timeline {

    private readonly List<(IrLocal Local, IrLocalRef? Read, int[] Loops)> _uses = [];
    private readonly Dictionary<IrLocal, int[]> _declared = [];
    private readonly Stack<int> _loops = new();
    private int _nextLoop;

    /// <summary>The variables some field store writes through.</summary>
    public HashSet<IrLocal> Written { get; } = [];

    public void Block(IrBlock block) {
      foreach (var statement in block.Statements)
        this.Statement(statement);
    }

    private void Statement(IrStmt statement) {
      switch (statement) {
        case IrBlock block:
          this.Block(block);
          break;
        case IrDeclare declare:
          this.Expression(declare.Initializer);
          this._declared[declare.Local] = [.. this._loops];
          break;
        case IrAssign assign:
          this.Expression(assign.Value);
          break;
        case IrStore store:
          this.Expression(store.Array);
          this.Expression(store.Index);
          this.Expression(store.Value);
          break;
        case IrFieldStore store:
          // The place is named first, the value computed next, and the write happens last.
          var root = this.Place(store.Target);
          this.Expression(store.Value);
          if (root is not null) {
            this.Written.Add(root);
            this._uses.Add((root, null, [.. this._loops]));
          }

          break;
        case IrExprStmt expression:
          this.Expression(expression.Expression);
          break;
        case IrIf conditional:
          this.Expression(conditional.Condition);
          this.Block(conditional.Then);
          if (conditional.Else is not null)
            this.Block(conditional.Else);

          break;
        case IrWhile loop:
          this.Loop([loop.Condition], [loop.Body]);
          break;
        case IrDoWhile loop:
          this.Loop([loop.Condition], [loop.Body]);
          break;
        case IrFor loop:
          this.Loop([loop.Condition], [loop.Body, loop.Step]);
          break;
        case IrSwitch choice:
          this.Expression(choice.Value);
          foreach (var section in choice.Sections)
            this.Block(section.Body);

          break;
        case IrReturn result:
          this.Expression(result.Value);
          break;
      }
    }

    private void Loop(IrExpr[] conditions, IrBlock[] blocks) {
      this._loops.Push(this._nextLoop++);
      foreach (var condition in conditions)
        this.Expression(condition);

      foreach (var block in blocks)
        this.Block(block);

      this._loops.Pop();
    }

    /// <summary>The uses inside a place a field is written in; returns the variable the place is part of, if any.</summary>
    private IrLocal? Place(IrExpr target) {
      switch (target) {
        case IrLocalRef reference:
          return reference.Local;
        case IrFieldLoad load:
          return this.Place(load.Target);
        default:
          this.Expression(target);
          return null;
      }
    }

    private void Expression(IrExpr? expression) {
      switch (expression) {
        case null:
          return;
        case IrLocalRef reference:
          this._uses.Add((reference.Local, reference, [.. this._loops]));
          return;
      }

      foreach (var operand in IrTree.Operands(expression))
        this.Expression(operand);
    }

    /// <summary>The reads of a struct whose variable is used again, is declared outside a loop the read is in, or is always live.</summary>
    public HashSet<IrLocalRef> LiveReads(HashSet<IrLocal> alwaysLive) {
      var live = new HashSet<IrLocalRef>(ReferenceEqualityComparer.Instance);
      for (var index = 0; index < this._uses.Count; ++index) {
        var (local, read, loops) = this._uses[index];
        if (read is null || !local.Type.IsStruct)
          continue;

        var declared = this._declared.GetValueOrDefault(local, []);
        var later = this._uses.Skip(index + 1).Any(use => use.Local == local);
        if (alwaysLive.Contains(local) || later || loops.Any(loop => !declared.Contains(loop)))
          live.Add(read);
      }

      return live;
    }

  }

  private sealed partial class Writer {

    private readonly List<(string Kind, IrStructType Type)> _structHelpers = [];
    private Liveness _liveness = new([], []);
    private bool _structs;

    /// <summary>The name of the helper of a kind for a struct, which the module declares once it is asked for.</summary>
    private string StructHelper(string kind, IrStructType type) {
      if (!this._structHelpers.Contains((kind, type)))
        this._structHelpers.Add((kind, type));

      return $"${kind}_{type.Name}";
    }

    /// <summary>
    /// A struct as a value goes to another place - a variable, an argument, a result, an element, a
    /// field: a copy where the place it is read from stays live (see <see cref="Liveness"/>), the
    /// value itself where it is fresh or its place is dead.
    /// </summary>
    private string Value(IrExpr expression) {
      if (!expression.Type.IsStruct)
        return this.Text(expression);

      if (expression is IrConditional choice) {
        var whenTrue = this.Value(choice.WhenTrue);
        var whenFalse = this.Value(choice.WhenFalse);
        return $"{this.Operand(choice.Condition)} ? {Atomic(choice.WhenTrue, whenTrue)} : {Atomic(choice.WhenFalse, whenFalse)}";
      }

      return this.NeedsCopy(expression) ? this.Copy(expression) : this.Text(expression);
    }

    /// <summary>A value text as an operand: a conditional is the one value form that is not one.</summary>
    private static string Atomic(IrExpr expression, string text) => expression is IrConditional ? $"({text})" : text;

    /// <summary>Whether a struct is read from a place that can be used again: a live variable or a part of one, or an array element.</summary>
    private bool NeedsCopy(IrExpr expression) => expression switch {
      IrLocalRef reference => this._liveness.Live.Contains(reference),
      IrArrayLoad => true,
      IrFieldLoad load => this.NeedsCopy(load.Target),
      _ => false
    };

    /// <summary>
    /// A copy of a struct read from a place: a literal of its fields where the struct has no struct
    /// field and the place is a variable or a part of one, which can be read once per field;
    /// otherwise the copy helper of the struct, which evaluates the place once and copies deeply.
    /// </summary>
    private string Copy(IrExpr place) {
      var type = place.Type.StructType!;
      var text = this.Text(place);
      if (type.Fields.Any(field => field.Type.IsStruct) || !IsVariable(place))
        return $"{this.StructHelper("copy", type)}({text})";

      return this.Literal(type.Fields.Select(field => (field.Name, $"{text}.{field.Name}")));
    }

    private static bool IsVariable(IrExpr place) => place is IrLocalRef || place is IrFieldLoad load && IsVariable(load.Target);

    /// <summary>An object literal of keys and values, a key that is the name of its value written once where the level has shorthand properties.</summary>
    private string Literal(IEnumerable<(string Key, string Value)> entries) {
      this._structs = true;
      this.Native("object literal");
      var parts = entries.Select(entry => entry.Value == entry.Key && this.Native("shorthand property") ? entry.Key : $"{entry.Key}: {entry.Value}").ToList();
      return parts.Count == 0 ? "{}" : $"{{ {string.Join(", ", parts)} }}";
    }

    /// <summary>The default value of a struct: a literal of zeros, falses and defaults.</summary>
    private string Default(IrStructType type)
      => this.Literal(type.Fields.Select(field => (field.Name, field.Type.StructType is { } nested ? this.Default(nested)
        : field.Type == IrType.Bool ? "false" : this.Zero(field.Type))));

    private (string Text, bool Atomic) Struct(IrExpr expression) {
      switch (expression) {
        case IrStructNew creation:
          var fields = creation.Type.StructType!.Fields;
          return (this.Literal(fields.Select((field, index) => (field.Name, this.Value(creation.Arguments[index])))), true);
        case IrStructDefault value:
          return (this.Default(value.Type.StructType!), true);
        case IrFieldLoad load:
          return ($"{this.Operand(load.Target)}.{load.Field.Name}", true);
        default:
          var equality = (IrStructEquals)expression;
          return ($"{this.StructHelper("equals", equality.Left.Type.StructType!)}({this.Text(equality.Left)}, {this.Text(equality.Right)})", true);
      }
    }

    /// <summary>A write of a field through a place, which is named without a copy: the write is meant to change it.</summary>
    private string FieldStore(IrFieldStore store) => $"{this.Operand(store.Target)}.{store.Field.Name} = {this.Value(store.Value)}";

    /// <summary>
    /// The helpers of the structs the module asked for, and those they call: a deep copy, the
    /// field-wise equality and a new array of distinct default values.
    /// </summary>
    private string StructHelpers() {
      var text = new StringBuilder();
      for (var index = 0; index < this._structHelpers.Count; ++index) {
        var (kind, type) = this._structHelpers[index];
        var name = $"${kind}_{type.Name}";
        text.Append('\n');
        switch (kind) {
          case "copy":
            var copied = type.Fields.Select(field => (field.Name, field.Type.StructType is { } nested ? $"{this.StructHelper("copy", nested)}(value.{field.Name})" : $"value.{field.Name}"));
            text.Append("function ").Append(name).Append("(value) {\n  return ").Append(this.Literal(copied)).Append(";\n}\n");
            break;
          case "equals":
            var compared = type.Fields.Select(field => this.FieldEquals(field)).ToList();
            text.Append("function ").Append(name).Append("(a, b) {\n  return ").Append(compared.Count == 0 ? "true" : string.Join(" && ", compared)).Append(";\n}\n");
            break;
          default:
            text.Append("function ").Append(name).Append("(length) {\n")
              .Append("  ").Append(this.Native("const") ? "const" : "var").Append(" array = [];\n")
              .Append("  for (").Append(this.Let).Append(" index = 0; index < length; ++index) array[index] = ").Append(this.Default(type)).Append(";\n")
              .Append("  return array;\n}\n");
            break;
        }
      }

      return text.ToString();
    }

    /// <summary>
    /// The comparison of one field of two structs: a NaN equals a NaN and the zeros are equal, which
    /// <c>===</c> says of the zeros and the inequality of each NaN with itself of the NaNs; a pair of
    /// halves by its helper; a struct by its own equality; anything else by <c>===</c>.
    /// </summary>
    private string FieldEquals(IrField field) {
      var (a, b) = ($"a.{field.Name}", $"b.{field.Name}");
      if (field.Type.StructType is { } nested)
        return $"{this.StructHelper("equals", nested)}({a}, {b})";

      if (field.Type.IsFloat)
        return $"({a} === {b} || ({a} !== {a} && {b} !== {b}))";

      if (field.Type.IsLong && this.Halves) {
        this._used.Add("$eq64");
        return $"$eq64({a}, {b})";
      }

      return $"{a} === {b}";
    }

  }

}
