using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic;

public static partial class VisualBasicEmitter {

  private sealed partial class Writer {

    /// <summary>The temporaries of the procedure, whose value nothing changes after it is set.</summary>
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Visual Basic name of a function of the module.</summary>
    private string FunctionName(string function) {
      if (this._functions.TryGetValue(function, out var name))
        return name;

      this.Refuse(InexpressibleDescriptor, $"the call of '{function}', which the module does not define");
      return VisualBasicNaming.Valid(function, "Function");
    }

    /// <summary>
    /// Whether evaluating an expression can neither trap nor change anything: constants, variables, the
    /// operators that cannot fail and the conversions that wrap. Such an operand may be evaluated where
    /// the IR would skip it, as Visual Basic's <c>And</c>, <c>Or</c> and <c>IIf</c> do.
    /// </summary>
    private static bool Pure(IrExpr expression) => expression switch {
      IrConst or IrStrConst or IrLocalRef or IrGlobalRef or IrConstRef => true,
      IrUnary { Op: IrUnaryOp.NegateChecked } => false,
      IrUnary { Operand: var operand, Type: var type } => !type.IsLong && Pure(operand) || type.IsFloat && Pure(operand) || type == IrType.Bool && Pure(operand),
      IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder, Left.Type.IsFloat: false } => false,
      IrBinary { Op: IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked } => false,
      // Visual Basic's floating-point arithmetic raises an error where IEEE 754 gives an infinity or a NaN.
      IrBinary { Left.Type.IsFloat: true, Op: < IrBinaryOp.Equal } => false,
      IrBinary { Left.Type.IsLong: true, Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } => false,
      IrBinary binary => Pure(binary.Left) && Pure(binary.Right),
      IrConvert { Contract: IrConversion.Wrap, Operand: var operand, Type: var type } => !operand.Type.IsFloat && !(type.IsFloat && type == IrType.F32 && operand.Type == IrType.F64) && Pure(operand),
      IrConditional conditional => Pure(conditional.Condition) && Pure(conditional.WhenTrue) && Pure(conditional.WhenFalse),
      IrFieldLoad load => Pure(load.Target),
      _ => false
    };

    /// <summary>Whether a written expression is a literal or a temporary, which statements written after it cannot change.</summary>
    private bool IsHeld(VbExpression expression) => expression is VbInteger or VbFloat or VbString or VbBoolean || expression is VbName name && this._held.Contains(name.Name);

    /// <summary>
    /// Operands written in the order the IR evaluates them. Where a later operand needs statements of
    /// its own, every earlier operand that those statements could change is held in a temporary first,
    /// so that it is still evaluated first. An array or a record is never copied that way.
    /// </summary>
    private List<VbExpression> Sequence(IReadOnlyList<IrExpr> operands) {
      var values = new List<VbExpression>();
      for (var index = 0; index < operands.Count; ++index) {
        var mark = this._out.Count;
        var value = this.Expression(operands[index]);
        if (this._out.Count > mark && index > 0) {
          var spills = new List<VbStatement>();
          for (var earlier = 0; earlier < values.Count; ++earlier) {
            if (this.IsHeld(values[earlier]) || operands[earlier].Type.IsArray || operands[earlier].Type.IsStruct)
              continue;

            var held = this.Hold(operands[earlier].Type);
            spills.Add(new VbAssign(new VbName(held), values[earlier]));
            values[earlier] = new VbName(held);
          }

          this._out.InsertRange(mark, spills);
        }

        values.Add(value);
      }

      return values;
    }

    /// <summary>A temporary whose value is set once.</summary>
    private string Hold(IrType type, string stem = "tmp") {
      var name = this.Temporary(type, stem);
      this._held.Add(name);
      return name;
    }

    /// <summary>An expression held in a temporary unless it is a variable: what a parameter passed by reference needs.</summary>
    private VbExpression Variable(IrExpr expression, VbExpression written) {
      if (expression is IrLocalRef or IrGlobalRef or IrConstRef || expression is IrFieldLoad && this.Place(expression) is not null || written is VbName)
        return written;

      var held = this.Hold(expression.Type);
      this._out.Add(new VbAssign(new VbName(held), written));
      return new VbName(held);
    }

    private VbExpression Stable(IrExpr expression) => this.Expression(expression);

    /// <summary>The arguments of a call: an array or a record, which Visual Basic passes by reference, as a variable.</summary>
    private List<VbExpression> Arguments(IrCall call) {
      var written = this.Sequence(call.Arguments);
      for (var index = 0; index < written.Count; ++index)
        if (call.Arguments[index].Type.IsArray || call.Arguments[index].Type.IsStruct)
          written[index] = this.Variable(call.Arguments[index], written[index]);

      return written;
    }

    /// <summary>Operands of a helper call; those at the given positions, arrays, as variables.</summary>
    private List<VbExpression> Operands(IReadOnlyList<IrExpr> arguments, int[] arrays) {
      var written = this.Sequence(arguments);
      foreach (var index in arrays)
        written[index] = this.Variable(arguments[index], written[index]);
      return written;
    }

    private VbExpression Expression(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          return this.Literal(constant.Type, constant.Value);
        case IrStrConst text:
          return this.Text(text.Value, false);
        case IrLocalRef reference:
          return new VbName(this.LocalName(reference.Local));
        case IrGlobalRef global:
          return new VbName(this._globals[global.Global.Name]);
        case IrConstRef constant:
          return new VbName(this._constants[constant.Constant.Name]);
        case IrUnary unary:
          return this.Unary(unary);
        case IrBinary binary:
          return this.Binary(binary);
        case IrConvert convert:
          return this.Convert(convert);
        case IrConditional conditional:
          return this.Conditional(conditional);
        case IrArrayLoad load:
          var loaded = this.Sequence([load.Array, load.Index]);
          return new VbCall(this.Variable(load.Array, loaded[0]), [loaded[1]]);
        case IrArrayLength { Array: IrConstRef table }:
          return new VbInteger(table.Constant.Type.Element!.IsString ? table.Constant.Strings.Count : table.Constant.Values.Count);
        case IrArrayLength length:
          var array = this.Variable(length.Array, this.Expression(length.Array));
          return new VbCall(new VbName(this.ArrayHelper(ArrayHelpers.Length, length.Array.Type)), [array]);
        case IrNewArray created:
          return new VbCall(new VbName(this.ArrayHelper(ArrayHelpers.New, created.Type)), [this.Expression(created.Length)]);
        case IrCall call:
          return new VbCall(new VbName(this.FunctionName(call.Function)), this.Arguments(call));
        case IrStep step:
          return this.Step(step, true);
        case IrStructNew created when created.Type.Declaration is IrStructType type && this._typeNames.ContainsKey(type):
          var fields = this.Sequence(created.Arguments);
          for (var index = 0; index < fields.Count; ++index)
            if (created.Arguments[index].Type.IsStruct)
              fields[index] = this.Variable(created.Arguments[index], fields[index]);
          return new VbCall(new VbName(this.StructHelper(MakePrefix, type)), fields);
        case IrStructDefault created when created.Type.Declaration is IrStructType type && this._typeNames.ContainsKey(type):
          return new VbCall(new VbName(this.StructHelper(ZeroPrefix, type)), []);
        case IrFieldLoad load:
          var target = this.Variable(load.Target, this.Expression(load.Target));
          return new VbMember(target, this._fieldNames.GetValueOrDefault(load.Field, load.Field.Name));
        case IrStructEquals equals when equals.Left.Type.Declaration is IrStructType type && this._typeNames.ContainsKey(type):
          var pair = this.Sequence([equals.Left, equals.Right]);
          return new VbCall(new VbName(this.StructHelper(EqualsPrefix, type)), [this.Variable(equals.Left, pair[0]), this.Variable(equals.Right, pair[1])]);
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic);
        case IrBind bind when !bind.Local.Type.IsArray && !bind.Local.Type.IsStruct:
          // An assignment as a value - an operand a pass evaluates once and reads twice - is the
          // assignment before the expression and the variable in it.
          var bound = this.Expression(bind.Value);
          this._out.Add(new VbAssign(new VbName(this.LocalName(bind.Local)), bound));
          return new VbName(this.LocalName(bind.Local));
        case IrVariantOf made:
          return this.VariantOf(made);
        case IrVariantTagOf tag:
          return Call("VarType", this.Expression(tag.Value));
        case IrVariantValue value:
          return this.VariantValue(value);
        case IrThrowExpression thrown when this.Conversion(thrown.Type) is not null:
          this.Throw(thrown.Exception);
          return thrown.Type.IsString ? new VbString("") : this.Literal(thrown.Type, 0);
        default:
          return this.Inexpressible(expression.GetType().Name[2..]);
      }
    }

    #region Literals

    /// <summary>A constant of a type, as the bits or the value Visual Basic carries it in.</summary>
    private VbExpression Literal(IrType type, long value) {
      switch (type.Kind) {
        case IrTypeKind.Bool:
          return new VbBoolean(value != 0);
        case IrTypeKind.I8 or IrTypeKind.I16:
          return new VbInteger(value);
        case IrTypeKind.U8:
          return new VbCall(new VbName("CByte"), [new VbInteger(value)]);
        case IrTypeKind.U16 or IrTypeKind.I32:
          return LongLiteral((int)value);
        case IrTypeKind.U32:
          // The bits of a value from 2^31 on are a negative Long, written as its eight hexadecimal digits.
          return (int)(uint)value < 0 ? new VbInteger((int)(uint)value) { Hex = true } : new VbInteger((int)(uint)value);
        case IrTypeKind.I64 or IrTypeKind.U64:
          this.TypeName(type);
          return value < 0 && (type.Kind == IrTypeKind.U64 || value == long.MinValue) ? new VbInteger(value) { Suffix = "^", Hex = true } : new VbInteger(value) { Suffix = "^" };
        case IrTypeKind.F32 or IrTypeKind.F64:
          var number = type.Kind == IrTypeKind.F32 ? BitConverter.Int32BitsToSingle((int)value) : BitConverter.Int64BitsToDouble(value);
          if (!double.IsFinite(number))
            return this.Inexpressible($"the constant {number.ToString(CultureInfo.InvariantCulture)}");
          return new VbFloat(number, type.Kind == IrTypeKind.F32);
        default:
          return this.Inexpressible($"a constant of type '{type}'");
      }
    }

    /// <summary>A <c>Long</c>: in decimal, the smallest value, which has no decimal literal, as <c>&amp;H80000000</c>.</summary>
    private static VbInteger LongLiteral(int value) => value == int.MinValue ? new VbInteger(value) { Hex = true } : new VbInteger(value);

    /// <summary>A literal that is an operand of <c>Long</c> arithmetic, given the type character <c>&amp;</c> so that two literals are not multiplied as <c>Integer</c>.</summary>
    private static VbExpression AsLong(VbExpression operand) => operand is VbInteger { Hex: false, Suffix: "" } integer ? integer with { Suffix = "&" } : operand;

    /// <summary>
    /// A string as a literal, every character outside printable ASCII - which the code page of the
    /// file would change - as <c>ChrW(n)</c> joined with <c>&amp;</c>.
    /// </summary>
    private VbExpression Text(string value, bool constant) {
      VbExpression? result = null;
      var run = new StringBuilder();
      void Append(VbExpression part) => result = result is null ? part : new VbBinary(result, "&", part);
      foreach (var c in value) {
        if (c is >= ' ' and <= '~') {
          run.Append(c);
          continue;
        }

        if (constant) {
          this.Refuse(InexpressibleDescriptor, "a string constant with a character outside printable ASCII");
          return new VbString("");
        }

        if (run.Length > 0) {
          Append(new VbString(run.ToString()));
          run.Clear();
        }

        Append(new VbCall(new VbName("ChrW"), [new VbInteger(c)]));
      }

      if (run.Length > 0 || result is null)
        Append(new VbString(run.ToString()));
      return result!;
    }

    #endregion

    #region Operators

    private VbExpression Unary(IrUnary unary) {
      var type = unary.Operand.Type;
      var operand = this.Expression(unary.Operand);
      switch (unary.Op) {
        case IrUnaryOp.LogicalNot or IrUnaryOp.BitNot:
          return new VbUnary("Not", operand);
        case IrUnaryOp.NegateChecked:
          return new VbCall(new VbName(type.IsLong ? "CLngLng" : "CLng"), [new VbUnary("-", operand)]);
        case IrUnaryOp.Negate when type.IsFloat:
          return Negative(operand);
        case IrUnaryOp.Negate when type.IsWord:
          return this.CallHelper(VisualBasicHelpers.Negate32, operand);
        default:
          return this.Inexpressible($"wrapping negation of '{type}'");
      }
    }

    /// <summary>The negation of a written expression, folded into a literal.</summary>
    private static VbExpression Negative(VbExpression operand) => operand switch {
      VbFloat number => number with { Value = -number.Value },
      _ => new VbUnary("-", operand)
    };

    private VbExpression Binary(IrBinary binary) {
      if (binary.Op is IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr)
        return this.ShortCircuit(binary);

      var type = binary.Left.Type;
      var operands = this.Sequence([binary.Left, binary.Right]);
      var (left, right) = (operands[0], operands[1]);
      // Of two truth values the IR compares only for equality.
      if (type == IrType.Bool)
        return new VbBinary(left, binary.Op == IrBinaryOp.Equal ? "=" : "<>", right);

      if (type.IsFloat)
        return this.FloatBinary(binary.Op, type, left, right);
      if (type.IsWord)
        return this.WordBinary(binary.Op, type, left, right, type.Kind == IrTypeKind.I32 && Small(binary.Left) && Small(binary.Right));
      if (type.IsLong)
        return this.LongBinary(binary.Op, type, left, right);

      return this.Inexpressible($"'{binary.Op}' of '{type}'");
    }

    private static string? Comparison(IrBinaryOp op) => op switch {
      IrBinaryOp.Equal => "=",
      IrBinaryOp.NotEqual => "<>",
      IrBinaryOp.Less => "<",
      IrBinaryOp.LessOrEqual => "<=",
      IrBinaryOp.Greater => ">",
      IrBinaryOp.GreaterOrEqual => ">=",
      _ => null
    };

    private VbExpression FloatBinary(IrBinaryOp op, IrType type, VbExpression left, VbExpression right) {
      if (Comparison(op) is { } comparison)
        return new VbBinary(left, comparison, right);

      var symbol = op switch {
        IrBinaryOp.Add => "+",
        IrBinaryOp.Subtract => "-",
        IrBinaryOp.Multiply => "*",
        IrBinaryOp.Divide => "/",
        _ => null
      };
      if (symbol is null)
        return this.Inexpressible($"'{op}' of '{type}'");

      // A Single is rounded to binary32 after every operation, also where an interpreter computes in Double.
      var result = new VbBinary(left, symbol, right);
      return type == IrType.F32 ? new VbCall(new VbName("CSng"), [result]) : result;
    }

    /// <summary>Whether an <c>i32</c> operand holds a value of at most 16 bits, so that a sum, difference or product of two cannot leave 32 bits.</summary>
    private static bool Small(IrExpr operand) => operand switch {
      IrConvert { Operand.Type.Kind: IrTypeKind.I8 or IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.U16 } => true,
      IrConst { Value: >= -32768 and <= 32768 } => true,
      _ => false
    };

    private VbExpression WordBinary(IrBinaryOp op, IrType type, VbExpression left, VbExpression right, bool small = false) {
      var unsigned = type.Kind == IrTypeKind.U32;
      if (Comparison(op) is { } comparison) {
        if (!unsigned || op is IrBinaryOp.Equal or IrBinaryOp.NotEqual)
          return new VbBinary(left, comparison, right);

        // The bits of two unsigned values compare as their values once the sign bit of both is flipped.
        var sign = new VbInteger(int.MinValue) { Hex = true };
        return new VbBinary(new VbBinary(left, "Xor", sign), comparison, new VbBinary(right, "Xor", sign));
      }

      VbExpression Native(string symbol) => new VbCall(new VbName("CLng"), [new VbBinary(AsLong(left), symbol, AsLong(right))]);
      return op switch {
        // Two values of 16 bits sum, subtract and multiply within 32 bits: Visual Basic's own operator cannot overflow.
        IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply when small => Native(op == IrBinaryOp.Add ? "+" : op == IrBinaryOp.Subtract ? "-" : "*"),
        IrBinaryOp.Add => this.CallHelper(VisualBasicHelpers.Add32, left, right),
        IrBinaryOp.Subtract => this.CallHelper(VisualBasicHelpers.Subtract32, left, right),
        IrBinaryOp.Multiply => this.CallHelper(VisualBasicHelpers.Multiply32, left, right),
        IrBinaryOp.AddChecked => unsigned ? this.CallHelper(VisualBasicHelpers.AddUnsigned32, left, right) : Native("+"),
        IrBinaryOp.SubtractChecked => unsigned ? this.CallHelper(VisualBasicHelpers.SubtractUnsigned32, left, right) : Native("-"),
        IrBinaryOp.MultiplyChecked => unsigned ? this.CallHelper(VisualBasicHelpers.MultiplyUnsigned32, left, right) : Native("*"),
        IrBinaryOp.Divide => this.CallHelper(unsigned ? VisualBasicHelpers.DivideUnsigned32 : VisualBasicHelpers.Divide32, left, right),
        IrBinaryOp.Remainder => unsigned ? this.CallHelper(VisualBasicHelpers.RemainderUnsigned32, left, right) : this.CallHelper(VisualBasicHelpers.Remainder32, left, right),
        IrBinaryOp.And => new VbBinary(left, "And", right),
        IrBinaryOp.Or => new VbBinary(left, "Or", right),
        IrBinaryOp.Xor => new VbBinary(left, "Xor", right),
        IrBinaryOp.ShiftLeft => this.CallHelper(VisualBasicHelpers.ShiftLeft32, left, right),
        IrBinaryOp.ShiftRight => this.CallHelper(unsigned ? VisualBasicHelpers.ShiftRightLogical32 : VisualBasicHelpers.ShiftRight32, left, right),
        IrBinaryOp.ShiftRightLogical => this.CallHelper(VisualBasicHelpers.ShiftRightLogical32, left, right),
        _ => this.Inexpressible($"'{op}' of '{type}'")
      };
    }

    private VbExpression LongBinary(IrBinaryOp op, IrType type, VbExpression left, VbExpression right) {
      this.TypeName(type);
      var unsigned = type.Kind == IrTypeKind.U64;
      if (Comparison(op) is { } comparison) {
        if (!unsigned || op is IrBinaryOp.Equal or IrBinaryOp.NotEqual)
          return new VbBinary(left, comparison, right);

        var sign = new VbInteger(long.MinValue) { Suffix = "^", Hex = true };
        return new VbBinary(new VbBinary(left, "Xor", sign), comparison, new VbBinary(right, "Xor", sign));
      }

      VbExpression Native(string symbol) => new VbCall(new VbName("CLngLng"), [new VbBinary(left, symbol, right)]);
      return (op, unsigned) switch {
        (IrBinaryOp.AddChecked, false) => Native("+"),
        (IrBinaryOp.SubtractChecked, false) => Native("-"),
        (IrBinaryOp.MultiplyChecked, false) => Native("*"),
        (IrBinaryOp.Divide, false) => Native("\\"),
        (IrBinaryOp.Remainder, false) => Native("Mod"),
        (IrBinaryOp.And, _) => new VbBinary(left, "And", right),
        (IrBinaryOp.Or, _) => new VbBinary(left, "Or", right),
        (IrBinaryOp.Xor, _) => new VbBinary(left, "Xor", right),
        _ => this.Inexpressible($"'{op}' of '{type}'")
      };
    }

    /// <summary>
    /// <c>&amp;&amp;</c> and <c>||</c>: Visual Basic's <c>And</c> and <c>Or</c> where the right operand
    /// can neither trap nor change anything; otherwise a temporary the right operand is evaluated into
    /// only when the left one does not decide.
    /// </summary>
    private VbExpression ShortCircuit(IrBinary binary) {
      var and = binary.Op == IrBinaryOp.LogicalAnd;
      var left = this.Expression(binary.Left);
      if (Pure(binary.Right)) {
        var right = this.Expression(binary.Right);
        return new VbBinary(left, and ? "And" : "Or", right);
      }

      var held = this.Temporary(IrType.Bool, "test");
      this._out.Add(new VbAssign(new VbName(held), left));
      var (statements, value) = this.Isolated(binary.Right);
      statements.Add(new VbAssign(new VbName(held), value));
      var condition = and ? (VbExpression)new VbName(held) : new VbUnary("Not", new VbName(held));
      this._out.Add(new VbIf([new(condition, statements)], null));
      return new VbName(held);
    }

    /// <summary>
    /// <c>c ? a : b</c>: <c>IIf</c> through the conversion of the type where both branches can neither
    /// trap nor change anything (<c>IIf</c> evaluates both and returns a <c>Variant</c>); otherwise an
    /// <c>If</c> that assigns a temporary.
    /// </summary>
    private VbExpression Conditional(IrConditional conditional) {
      var type = conditional.Type;
      if (Pure(conditional.WhenTrue) && Pure(conditional.WhenFalse) && Conversion(type) is { } convert) {
        var operands = this.Sequence([conditional.Condition, conditional.WhenTrue, conditional.WhenFalse]);
        return new VbCall(new VbName(convert), [new VbCall(new VbName("IIf"), operands)]);
      }

      var condition = this.Expression(conditional.Condition);
      var held = this.Temporary(type, "choice");
      var (whenTrue, yes) = this.Isolated(conditional.WhenTrue);
      whenTrue.Add(new VbAssign(new VbName(held), yes));
      var (whenFalse, no) = this.Isolated(conditional.WhenFalse);
      whenFalse.Add(new VbAssign(new VbName(held), no));
      this._out.Add(new VbIf([new(condition, whenTrue)], whenFalse));
      return new VbName(held);
    }

    /// <summary>
    /// A value of the dynamic type as a <c>Variant</c>: <c>Empty</c>, <c>Null</c>, or the payload through
    /// the conversion function of its type, which gives the <c>Variant</c> that subtype also where a
    /// literal would have another (<c>5</c> is an <c>Integer</c>, <c>CLng(5)</c> a <c>Long</c>); a date
    /// through <c>CDate</c>, a currency's ten thousandths through <c>CCur</c> of the exact quotient.
    /// </summary>
    private VbExpression VariantOf(IrVariantOf made) {
      switch (made.Tag) {
        case IrVariantTag.Empty:
          return new VbName("Empty");
        case IrVariantTag.Null:
          return new VbName("Null");
        case IrVariantTag.String:
          return this.Expression(made.Value!);
        case IrVariantTag.Date:
          return Call("CDate", this.Expression(made.Value!));
        case IrVariantTag.Currency:
          // The ten thousandths, a LongLong, divided exactly as a Decimal.
          return Call("CCur", new VbBinary(Call("CDec", this.Expression(made.Value!)), "/", new VbInteger(10000)));
        default:
          var value = this.Expression(made.Value!);
          return Call(this.Conversion(made.Value!.Type)!, value);
      }
    }

    /// <summary>
    /// The payload of a <c>Variant</c> of a tag: its conversion function, which reads a <c>Variant</c>
    /// of that subtype exactly; where the frontend has not tested the tag, a test that raises error 13
    /// (type mismatch) for another one first.
    /// </summary>
    private VbExpression VariantValue(IrVariantValue value) {
      var variant = this.Expression(value.Value);
      if (!value.IsTested) {
        variant = this.Variable(value.Value, variant);
        var raise = new VbCallStatement(new VbMember(new VbName("Err"), "Raise"), [new VbInteger(13)]);
        this._out.Add(new VbIf([new(new VbBinary(Call("VarType", variant), "<>", new VbInteger((int)value.Tag)), [raise])], null));
      }

      if (value.Tag == IrVariantTag.Currency)
        return Call("CLngLng", new VbBinary(Call("CDec", variant), "*", new VbInteger(10000)));
      return Call(this.Conversion(value.Type)!, variant);
    }

    /// <summary>The conversion function that gives a <c>Variant</c> back its type, or <see langword="null"/> for an array or a record.</summary>
    private string? Conversion(IrType type) => type.Kind switch {
      IrTypeKind.Bool => "CBool",
      IrTypeKind.U8 => "CByte",
      IrTypeKind.I8 or IrTypeKind.I16 => "CInt",
      IrTypeKind.U16 or IrTypeKind.I32 or IrTypeKind.U32 => "CLng",
      IrTypeKind.I64 or IrTypeKind.U64 => "CLngLng",
      IrTypeKind.F32 => "CSng",
      IrTypeKind.F64 => "CDbl",
      IrTypeKind.String => "CStr",
      _ => null
    };

    #endregion

    #region Conversions

    private static VbCall Call(string function, params VbExpression[] arguments) => new(new VbName(function), arguments);

    /// <summary>
    /// A conversion between numeric types, from the representation of the operand's type to that of the
    /// result's: a value that fits is converted, one that does not wraps through a mask or a helper, or
    /// traps through the conversion function, which raises error 6 where the value does not fit.
    /// </summary>
    private VbExpression Convert(IrConvert convert) {
      var from = convert.Operand.Type;
      var to = convert.Type;
      var value = this.Expression(convert.Operand);
      if (from.Kind == to.Kind)
        return value;
      if (to.IsFloat)
        return this.ToFloat(from, to, value);
      if (from.IsFloat)
        return this.FromFloat(to, value, convert.Contract);

      var fits = IrOperators.Minimum(to) <= IrOperators.Minimum(from) && IrOperators.Maximum(from) <= IrOperators.Maximum(to);
      if (fits)
        return this.Widen(from, to, value);

      return convert.Contract == IrConversion.Wrap ? this.Wrap(from, to, value) : this.Check(from, to, value);
    }

    private static bool InLong(IrType type) => type.Kind is IrTypeKind.U16 or IrTypeKind.I32 or IrTypeKind.U32;

    private VbExpression Widen(IrType from, IrType to, VbExpression value) {
      switch (to.Kind) {
        case IrTypeKind.I16:
          return Call("CInt", value);
        case IrTypeKind.U16 or IrTypeKind.I32 or IrTypeKind.U32:
          return InLong(from) ? value : Call("CLng", value);
        default:
          this.TypeName(to);
          return from.Kind == IrTypeKind.U32 ? Call("CLngLng", this.CallHelper(VisualBasicHelpers.Unsigned32, value)) : Call("CLngLng", value);
      }
    }

    private VbExpression Wrap(IrType from, IrType to, VbExpression value) {
      var wide = from.IsLong;
      VbExpression Low(long mask) => Call("CLng", new VbBinary(value, "And", new VbInteger(mask) { Suffix = "^" }));
      switch (to.Kind) {
        case IrTypeKind.I32 or IrTypeKind.U32:
          if (wide)
            return this.CallHelper(VisualBasicHelpers.Wrap32, Call("CDbl", new VbBinary(value, "And", new VbInteger(0xFFFFFFFFL) { Suffix = "^" })));
          return InLong(from) ? value : Call("CLng", value);
        case IrTypeKind.I16:
          return this.CallHelper(VisualBasicHelpers.Wrap16, wide ? Low(0xFFFF) : value);
        case IrTypeKind.U16:
          return wide ? Low(0xFFFF) : new VbBinary(InLong(from) ? value : Call("CLng", value), "And", new VbInteger(0xFFFF) { Suffix = "&" });
        case IrTypeKind.U8:
          return Call("CByte", wide ? Low(0xFF) : new VbBinary(value, "And", new VbInteger(255)));
        case IrTypeKind.I8:
          return this.CallHelper(VisualBasicHelpers.Wrap8, wide ? Low(0xFF) : InLong(from) ? value : Call("CLng", value));
        default:
          return this.Widen(from, to, value);
      }
    }

    private VbExpression Check(IrType from, IrType to, VbExpression value) {
      if (from.Kind == IrTypeKind.U64 || to.Kind == IrTypeKind.U64)
        return this.Inexpressible($"the checked conversion of '{from}' to '{to}'");

      // The bits of a u32 that read as a negative Long are a value beyond every signed range here.
      var operand = from.Kind == IrTypeKind.U32 && to.Kind != IrTypeKind.U8 ? this.CallHelper(VisualBasicHelpers.CheckSign32, value) : value;
      switch (to.Kind) {
        case IrTypeKind.I32:
          return from.Kind == IrTypeKind.U32 ? operand : Call("CLng", operand);
        case IrTypeKind.U32:
          if (from.IsLong)
            return this.CallHelper(VisualBasicHelpers.ToUnsigned32, Call("CDbl", value));
          return this.CallHelper(VisualBasicHelpers.CheckSign32, InLong(from) ? value : Call("CLng", value));
        case IrTypeKind.I16:
          return Call("CInt", operand);
        case IrTypeKind.U16:
          return this.CallHelper(VisualBasicHelpers.CheckUnsigned16, InLong(from) ? operand : Call("CLng", operand));
        case IrTypeKind.U8:
          return Call("CByte", value);
        default:
          // i8: every wider target fits the operand, so no other type is checked here.
          return this.CallHelper(VisualBasicHelpers.Check8, InLong(from) ? operand : Call("CLng", operand));
      }
    }

    private VbExpression ToFloat(IrType from, IrType to, VbExpression value) {
      if (from.Kind == IrTypeKind.U64)
        return this.Inexpressible($"the conversion of '{from}' to '{to}'");

      var exact = from.Kind == IrTypeKind.U32 ? this.CallHelper(VisualBasicHelpers.Unsigned32, value) : value;
      return to.Kind == IrTypeKind.F32 ? Call("CSng", exact) : Call("CDbl", exact);
    }

    private VbExpression FromFloat(IrType to, VbExpression value, IrConversion contract) {
      if (to.Kind == IrTypeKind.U64 || contract == IrConversion.Saturate && to.IsLong)
        return this.Inexpressible($"the conversion of a floating-point number to '{to}'");

      if (contract == IrConversion.Saturate) {
        var bounded = this.CallHelper(VisualBasicHelpers.Saturate, value,
          new VbFloat((double)IrOperators.Minimum(to), false), new VbFloat((double)IrOperators.Maximum(to), false));
        return to.Kind switch {
          IrTypeKind.U32 => this.CallHelper(VisualBasicHelpers.Wrap32, bounded),
          IrTypeKind.U8 => Call("CByte", bounded),
          IrTypeKind.I8 or IrTypeKind.I16 => Call("CInt", bounded),
          _ => Call("CLng", bounded)
        };
      }

      var truncated = Call("Fix", value);
      return to.Kind switch {
        IrTypeKind.I32 => Call("CLng", truncated),
        IrTypeKind.I16 => Call("CInt", truncated),
        IrTypeKind.U8 => Call("CByte", truncated),
        IrTypeKind.I8 => this.CallHelper(VisualBasicHelpers.Check8, Call("CLng", truncated)),
        IrTypeKind.U16 => this.CallHelper(VisualBasicHelpers.CheckUnsigned16, Call("CLng", truncated)),
        IrTypeKind.U32 => this.CallHelper(VisualBasicHelpers.ToUnsigned32, value),
        _ => Call("CLngLng", truncated)
      };
    }

    #endregion

    #region Intrinsics

    private VbExpression Intrinsic(IrIntrinsic intrinsic) {
      var op = intrinsic.Op;
      if (op == IrIntrinsicOp.ArrayOf)
        return this.ArrayOf(intrinsic);

      var arguments = this.Sequence(intrinsic.Arguments);
      var first = intrinsic.Arguments.Count > 0 ? intrinsic.Arguments[0].Type : IrType.Void;
      if (first.IsFloat) {
        VbExpression Rounded(VbExpression result) => first == IrType.F32 ? Call("CSng", result) : result;
        switch (op) {
          case IrIntrinsicOp.Sqrt:
            return Rounded(Call("Sqr", arguments[0]));
          case IrIntrinsicOp.Abs:
            return Call("Abs", arguments[0]);
          case IrIntrinsicOp.Floor:
            return Call("Int", arguments[0]);
          case IrIntrinsicOp.Truncate:
            return Call("Fix", arguments[0]);
          case IrIntrinsicOp.Ceiling:
            return new VbUnary("-", Call("Int", new VbUnary("-", arguments[0])));
          case IrIntrinsicOp.RoundEven:
            return Call("Round", arguments[0]);
          case IrIntrinsicOp.RoundAway:
            return Rounded(this.CallHelper(VisualBasicHelpers.RoundAway, arguments[0]));
          case IrIntrinsicOp.RoundHalfUp:
            return Rounded(this.CallHelper(VisualBasicHelpers.RoundHalfUp, arguments[0]));
          case IrIntrinsicOp.Min:
            return Rounded(this.CallHelper(VisualBasicHelpers.Minimum, arguments[0], arguments[1]));
          case IrIntrinsicOp.Max:
            return Rounded(this.CallHelper(VisualBasicHelpers.Maximum, arguments[0], arguments[1]));
          case IrIntrinsicOp.Sign:
            return Call("CLng", Call("Sgn", arguments[0]));
        }
      }

      switch (op) {
        case IrIntrinsicOp.StrConcat:
          return arguments.Aggregate((left, right) => new VbBinary(left, "&", right));
        case IrIntrinsicOp.StrLength:
          return Call("Len", arguments[0]);
        case IrIntrinsicOp.StrEquals:
          return new VbBinary(arguments[0], "=", arguments[1]);
        case IrIntrinsicOp.StrCompareOrdinal:
          return this.CallHelper(VisualBasicHelpers.CompareOrdinal, arguments[0], arguments[1]);
        case IrIntrinsicOp.StrCodeUnitAt:
          return this.CallHelper(VisualBasicHelpers.CodeUnitAt, arguments[0], arguments[1]);
        case IrIntrinsicOp.StrFromCodeUnit:
          return Call("ChrW", arguments[0]);
        case IrIntrinsicOp.StrSubstring when arguments.Count == 3:
          return this.CallHelper(VisualBasicHelpers.Substring, arguments[0], arguments[1], arguments[2]);
        case IrIntrinsicOp.StrSubstring:
          return this.CallHelper(VisualBasicHelpers.Substring, arguments[0], arguments[1], new VbBinary(Call("Len", arguments[0]), "-", arguments[1]));
        case IrIntrinsicOp.StrIndexOf:
          var needle = intrinsic.Arguments[1].Type.IsString ? arguments[1] : Call("ChrW", arguments[1]);
          return this.CallHelper(VisualBasicHelpers.IndexOf, arguments[0], needle, arguments.Count == 3 ? arguments[2] : new VbInteger(0));
        case IrIntrinsicOp.StrRepeat when !intrinsic.Arguments[0].Type.IsString:
          return this.CallHelper(VisualBasicHelpers.Repeat, Call("ChrW", arguments[0]), arguments[1]);
        case IrIntrinsicOp.FormatVisualBasic when NumberTextOperations.SeparatorOf(intrinsic) is { } separator:
          return this.CallHelper(first == IrType.F32 ? VisualBasicHelpers.FormatSingle : VisualBasicHelpers.FormatDouble, arguments[0], new VbString(separator.ToString()));
        case IrIntrinsicOp.ParseDotNet when intrinsic.Type.IsFloat:
          var parsed = this.CallHelper(VisualBasicHelpers.ParseDouble, arguments[0]);
          return intrinsic.Type == IrType.F32 ? Call("CSng", parsed) : parsed;
        case IrIntrinsicOp.FormatRadix when intrinsic.Arguments is [_, IrConst { Value: var radix }, IrConst { Value: 1 }, IrConst { Value: var upper }, IrConst { Value: var radixMode }]:
          return this.FormatRadix(intrinsic.Arguments[0].Type, arguments[0], radix, upper != 0, (RadixMode)radixMode) ?? this.Inexpressible(op.ToString());
        default:
          return this.Inexpressible(op.ToString());
      }
    }

    /// <summary>
    /// The digits of an integer: in decimal with a minus sign, <c>CStr</c>; in hexadecimal as the bits of
    /// its width, <c>Hex</c> of the type that has them; <see langword="null"/> for any other form.
    /// </summary>
    private VbExpression? FormatRadix(IrType type, VbExpression value, long radix, bool upper, RadixMode radixMode) {
      if (radix == 10 && radixMode == RadixMode.SignMagnitude && type.Kind != IrTypeKind.U64)
        return Call("CStr", type.Kind == IrTypeKind.U32 ? this.CallHelper(VisualBasicHelpers.Unsigned32, value) : value);
      if (radix == 16 && (radixMode == RadixMode.TwoComplement || !type.IsSigned)) {
        // Hex writes the bits of an Integer in four digits and of a Long in eight; i8 and u16 have none of their own.
        var bits = type.Kind switch {
          IrTypeKind.I8 => Call("CByte", new VbBinary(value, "And", new VbInteger(255))),
          _ => value
        };
        var digits = Call("Hex", bits);
        return upper ? digits : Call("LCase", digits);
      }

      return null;
    }

    /// <summary>A new array of the arguments: a temporary made with its length and filled element by element.</summary>
    private VbExpression ArrayOf(IrIntrinsic intrinsic) {
      var elements = this.Sequence(intrinsic.Arguments);
      var held = this.Hold(intrinsic.Type, "elements");
      this._out.Add(new VbAssign(new VbName(held), new VbCall(new VbName(this.ArrayHelper(ArrayHelpers.New, intrinsic.Type)), [new VbInteger(elements.Count)])));
      for (var index = 0; index < elements.Count; ++index)
        this._out.Add(new VbAssign(new VbCall(new VbName(held), [new VbInteger(index)]), elements[index]));
      return new VbName(held);
    }

    #endregion

  }

}
