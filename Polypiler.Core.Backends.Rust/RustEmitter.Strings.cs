using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Helpers;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The Rust type of a string the code owns: its UTF-16 code units.</summary>
  public const string OwnedString = "Vec<u16>";

  /// <summary>The Rust type of a string a parameter borrows.</summary>
  public const string BorrowedString = "&[u16]";

  private sealed partial class Writer {

    /// <summary>The string parameters of the function being written that it assigns, which it owns as a copy of its own.</summary>
    private HashSet<IrLocal> _ownedStringParameters = [];

    /// <summary>Whether a string expression is a parameter the function borrows.</summary>
    private bool IsBorrowedString(IrExpr expression)
      => expression is IrLocalRef { Local: var local } && this._function.Parameters.Contains(local) && !this._ownedStringParameters.Contains(local) && !this._owned.Contains(local);

    /// <summary>A helper of the string representation the crate carries, with the helpers it calls; its name.</summary>
    private string StringHelper(string name) {
      foreach (var helper in Strings.With(name))
        this.Spell(helper.Name, () => helper);

      return name;
    }

    /// <summary>A constant string: the code units of a Rust string literal, which holds no unpaired surrogate.</summary>
    private RsExpression Utf16(string value) {
      if (HasLoneSurrogate(value))
        return this.Refuse("a string with an unpaired surrogate");

      return new RsCall(new RsPath(this.StringHelper("ply_utf16")), [new RsString(value)]) { Type = IrType.String };
    }

    private static bool HasLoneSurrogate(string value) {
      for (var index = 0; index < value.Length; ++index) {
        if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
          ++index;
        else if (char.IsSurrogate(value[index]))
          return true;
      }

      return false;
    }

    /// <summary>The code units of a constant of the module, or of an element of a table of them: a <c>&amp;str</c> the crate keeps.</summary>
    private RsExpression ConstantText(IrConstRef constant) => new RsCall(new RsPath(this.StringHelper("ply_utf16")), [new RsPath(this._constants[constant.Constant.Name])]) { Type = IrType.String };

    /// <summary>
    /// A string as a value the receiving place owns, a <c>Vec&lt;u16&gt;</c>: a constant's code units,
    /// a borrowed one copied, an owned variable cloned or, where it is returned, moved; an element of
    /// an array cloned; the result of an operation as it is.
    /// </summary>
    private RsExpression StringValue(IrExpr text, bool move = false) => text switch {
      IrStrConst constant => this.Utf16(constant.Value),
      IrConstRef constant => this.ConstantText(constant),
      IrArrayLoad { Array: IrConstRef table } load => this.TableText(table, load.Index),
      IrArrayLoad { Array: IrGlobalRef { Global: var locked } } when IsLocked(locked) => this.Expression(text),
      IrLocalRef reference when this.IsBorrowedString(reference) => new RsMethodCall(new RsPath(this._locals[reference.Local]), "to_vec", []) { Type = IrType.String },
      IrLocalRef reference => move ? new RsPath(this._locals[reference.Local]) { Type = IrType.String } : new RsMethodCall(new RsPath(this._locals[reference.Local]), "clone", []) { Type = IrType.String },
      IrArrayLoad load => new RsMethodCall(this.Expression(load), "clone", []) { Type = IrType.String },
      IrConditional choice => this.Choice(choice, branch => this.StringValue(branch, move)),
      _ => this.Expression(text)
    };

    /// <summary>A string where a <c>&amp;[u16]</c> is taken: a borrowed one as it is, anything else borrowed for the statement.</summary>
    private RsExpression StringArgument(IrExpr text) => text switch {
      IrLocalRef reference when this.IsBorrowedString(reference) => new RsPath(this._locals[reference.Local]) { Type = IrType.String },
      IrIntrinsic { Op: IrIntrinsicOp.StrFromCodeUnit } unit => new RsReference(false, new RsArray([this.Expression(unit.Arguments[0])])) { Type = IrType.String },
      IrArrayLoad { Array: IrGlobalRef { Global: var locked } } when IsLocked(locked) => new RsReference(false, this.Operand(this.StringValue(text), 15)) { Type = IrType.String },
      IrLocalRef or IrArrayLoad => new RsReference(false, this.StringPlace(text)) { Type = IrType.String },
      _ => new RsReference(false, this.Operand(this.StringValue(text), 15)) { Type = IrType.String }
    };

    /// <summary>A string as a slice of exactly the type <c>&amp;[u16]</c>, as the elements of an array of slices must be.</summary>
    private RsExpression StringSlice(IrExpr text) => text switch {
      IrLocalRef reference when this.IsBorrowedString(reference) => new RsPath(this._locals[reference.Local]) { Type = IrType.String },
      IrArrayLoad { Array: IrGlobalRef { Global: var locked } } when IsLocked(locked) => new RsReference(false, new RsIndex(this.Operand(this.StringValue(text), 15), new RsRange(null, null, false))) { Type = IrType.String },
      IrLocalRef or IrArrayLoad => new RsReference(false, new RsIndex(this.StringPlace(text), new RsRange(null, null, false))) { Type = IrType.String },
      _ => new RsReference(false, new RsIndex(this.Operand(this.StringValue(text), 15), new RsRange(null, null, false))) { Type = IrType.String }
    };

    /// <summary>A string as it stands for a comparison, a length or an index: a variable or an element in place, anything else as its value.</summary>
    private RsExpression StringPlace(IrExpr text) => text switch {
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = IrType.String },
      IrArrayLoad { Array: not (IrConstRef or IrGlobalRef) } load => this.Element(this.ArrayTarget(load.Array), this.Index(load.Index), IrType.String),
      _ => this.Operand(this.StringValue(text), 15)
    };

    /// <summary>An element of a table of strings: the code units of the <c>&amp;str</c> the table holds.</summary>
    private RsExpression TableText(IrConstRef table, IrExpr index)
      => new RsCall(new RsPath(this.StringHelper("ply_utf16")), [this.Element(new RsPath(this._constants[table.Constant.Name]), this.Index(index), null)]) { Type = IrType.String };

    /// <summary>A code unit or a string where a slice of code units is taken: a unit as the array of itself.</summary>
    private RsExpression Needle(IrExpr needle)
      => needle.Type.IsString ? this.StringArgument(needle) : new RsReference(false, new RsArray([this.Expression(needle)])) { Type = IrType.String };

    /// <summary>
    /// A string operation of the IR: the slice methods where Rust has them with the IR's meaning
    /// (<c>len</c>, indexing, <c>starts_with</c>, <c>ends_with</c>, <c>repeat</c>, <c>concat</c>,
    /// <c>join</c>, the order of slices), the helpers of <see cref="Strings"/> for the rest.
    /// </summary>
    private RsExpression StringOperation(IrIntrinsic operation) {
      var arguments = operation.Arguments;
      RsExpression I32(RsExpression value) => this.Cast(value, IrType.I32);
      RsExpression Call(string helper, params IReadOnlyList<RsExpression> values) => new RsCall(new RsPath(this.StringHelper(helper)), values) { Type = operation.Type };
      RsExpression Length(IrExpr text) => new RsMethodCall(this.StringPlace(text), "len", []);
      switch (operation.Op) {
        case IrIntrinsicOp.StrLength:
          return I32(Length(arguments[0]));
        case IrIntrinsicOp.StrCodeUnitAt:
          return this.Element(this.StringPlace(arguments[0]), this.Index(arguments[1]), IrType.U16);
        case IrIntrinsicOp.StrConcat:
          return new RsMethodCall(new RsArray([.. arguments.Select(this.StringSlice)]), "concat", []) { Type = IrType.String };
        case IrIntrinsicOp.StrFromCodeUnit:
          return new RsMacro("vec", [new RsArray([this.Expression(arguments[0])])]) { Bracketed = true, Type = IrType.String };
        case IrIntrinsicOp.StrFromCodeUnits when arguments.Count == 1:
          return new RsMethodCall(this.ArrayTarget(arguments[0]), "to_vec", []) { Type = IrType.String };
        case IrIntrinsicOp.StrFromCodeUnits: {
          var array = this.ArrayTarget(arguments[0]);
          return new RsMethodCall(new RsIndex(array, this.Spelled(new RsCall(new RsPath(this.RangeHelper()), [this.Expression(arguments[1]), this.Expression(arguments[2]), new RsMethodCall(array, "len", [])]))), "to_vec", []) { Type = IrType.String };
        }
        case IrIntrinsicOp.StrToCodeUnits:
          return new RsMethodCall(this.StringPlace(arguments[0]), "to_vec", []) { Type = operation.Type };
        case IrIntrinsicOp.StrRepeat: {
          var count = this.Checked(new RsCall(new RsPath("usize::try_from"), [Suffixed(this.Expression(arguments[1]))]), IrTrap.IntegerOverflow);
          return arguments[0].Type.IsString
            ? new RsMethodCall(this.StringPlace(arguments[0]), "repeat", [count]) { Type = IrType.String }
            : new RsMacro("vec", [new RsArray([this.Expression(arguments[0])]) { Count = count }]) { Bracketed = true, Type = IrType.String };
        }
        case IrIntrinsicOp.StrSubstring: {
          var text = this.StringPlace(arguments[0]);
          var length = arguments.Count > 2 ? this.Expression(arguments[2]) : this.Method(I32(Length(arguments[0])), "wrapping_sub", IrType.I32, this.Expression(arguments[1]));
          var range = this.Spelled(new RsCall(new RsPath(this.RangeHelper()), [this.Expression(arguments[1]), length, new RsMethodCall(text, "len", [])]));
          return new RsMethodCall(new RsIndex(text, range), "to_vec", []) { Type = IrType.String };
        }
        case IrIntrinsicOp.StrEquals:
          return this.Binary(this.StringPlace(arguments[0]), "==", this.StringPlace(arguments[1]), IrType.Bool);
        case IrIntrinsicOp.StrCompareOrdinal:
          return this.Cast(new RsMethodCall(this.StringSlice(arguments[0]), "cmp", [this.StringSlice(arguments[1])]), IrType.I32);
        case IrIntrinsicOp.StrIndexOf:
          return Call("ply_index_of", this.StringArgument(arguments[0]), this.Needle(arguments[1]), arguments.Count > 2 ? this.Expression(arguments[2]) : new RsLiteral("0"));
        case IrIntrinsicOp.StrLastIndexOf:
          return Call("ply_last_index_of", this.StringArgument(arguments[0]), this.Needle(arguments[1]), arguments.Count > 2 ? this.Expression(arguments[2]) : I32(Length(arguments[0])));
        case IrIntrinsicOp.StrStartsWith:
          return new RsMethodCall(this.StringPlace(arguments[0]), "starts_with", [this.Needle(arguments[1])]) { Type = IrType.Bool };
        case IrIntrinsicOp.StrEndsWith:
          return new RsMethodCall(this.StringPlace(arguments[0]), "ends_with", [this.Needle(arguments[1])]) { Type = IrType.Bool };
        case IrIntrinsicOp.StrContains:
          return this.Binary(Call("ply_index_of", this.StringArgument(arguments[0]), this.Needle(arguments[1]), new RsLiteral("0")), ">=", new RsLiteral("0"), IrType.Bool);
        case IrIntrinsicOp.StrReplaceAll:
          return Call("ply_replace", this.StringArgument(arguments[0]), this.Needle(arguments[1]), this.Needle(arguments[2]));
        case IrIntrinsicOp.StrReplaceFirst:
          return Call("ply_replace_first", this.StringArgument(arguments[0]), this.Needle(arguments[1]), this.Needle(arguments[2]));
        case IrIntrinsicOp.StrSplit:
          return Call("ply_split", this.StringArgument(arguments[0]), this.Needle(arguments[1]), arguments.Count > 2 ? this.Expression(arguments[2]) : new RsLiteral("-1"));
        case IrIntrinsicOp.StrJoin:
          // The separator exactly a slice: join takes its type from it.
          return new RsMethodCall(this.ArrayTarget(arguments[1]), "join", [this.StringSlice(arguments[0])]) { Type = IrType.String };
        case IrIntrinsicOp.StrTrim or IrIntrinsicOp.StrTrimStart or IrIntrinsicOp.StrTrimEnd: {
          var set = StringOperations.SetOf(operation) ?? WhitespaceSet.DotNet;
          var start = operation.Op != IrIntrinsicOp.StrTrimEnd;
          var end = operation.Op != IrIntrinsicOp.StrTrimStart;
          return Call("ply_trim", this.StringArgument(arguments[0]), new RsLiteral(start ? "true" : "false"), new RsLiteral(end ? "true" : "false"),
            new RsReference(false, new RsPath(this.WhitespaceTable(set))));
        }
        case IrIntrinsicOp.StrPadStart or IrIntrinsicOp.StrPadEnd:
          return Call("ply_pad", this.StringArgument(arguments[0]), this.Expression(arguments[1]), this.Needle(arguments[2]), new RsLiteral(operation.Op == IrIntrinsicOp.StrPadStart ? "true" : "false"));
        default:
          return this.Refuse(operation.Op.ToString());
      }
    }

    /// <summary>The tables of white space the crate carries, by name.</summary>
    private readonly SortedDictionary<string, RsConst> _whitespace = new(StringComparer.Ordinal);

    /// <summary>The table of the code units of a white-space set, a <c>static</c> of the crate; its name.</summary>
    private string WhitespaceTable(WhitespaceSet set) {
      var (name, units) = Strings.Whitespace(set);
      if (!this._whitespace.ContainsKey(name))
        this._whitespace[name] = new RsConst(name, "", $"[u16; {units.Count}]",
          new RsArray([.. units.Select(unit => (RsExpression)new RsLiteral(((int)unit).ToString(CultureInfo.InvariantCulture)))])) { IsStatic = true };

      return name;
    }

    /// <summary>
    /// <c>text = text + more</c> as an append to the vector the variable owns: <c>push</c> for a code
    /// unit, <c>extend_from_slice</c> for a string; <see langword="null"/> where the concatenation does
    /// not start with the variable, or a later part names it.
    /// </summary>
    private IReadOnlyList<RsStatement>? Appended(IrLocal local, IrExpr value) {
      if (value is not IrIntrinsic { Op: IrIntrinsicOp.StrConcat, Arguments: [IrLocalRef first, ..] arguments } || first.Local != local || this.IsBorrowedString(first))
        return null;

      var rest = arguments.Skip(1).ToList();
      if (rest.Any(part => IrTree.Nodes(part).Any(node => node is IrLocalRef reference && reference.Local == local)))
        return null;

      var target = new RsPath(this._locals[local]) { Type = IrType.String };
      return [.. rest.Select(part => (RsStatement)new RsExpressionStatement(part is IrIntrinsic { Op: IrIntrinsicOp.StrFromCodeUnit } unit
        ? new RsMethodCall(target, "push", [this.Expression(unit.Arguments[0])])
        : new RsMethodCall(target, "extend_from_slice", [this.StringArgument(part)])))];
    }

  }

}
