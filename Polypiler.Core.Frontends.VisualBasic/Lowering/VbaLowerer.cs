using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.VisualBasic.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

/// <summary>Raised inside the lowering of a construct the subset does not hold or a name that names nothing; reported with its line.</summary>
internal sealed class VbaRefusal(DiagnosticDescriptor descriptor, int line, string argument) : Exception(argument) {

  public DiagnosticDescriptor Descriptor { get; } = descriptor;

  public int Line { get; } = line;

}

/// <summary>Lowers a parsed standard module into the IR: the declarations first, then every procedure.</summary>
internal sealed partial class VbaLowerer(string file, List<Diagnostic> diagnostics) {

  /// <summary>A procedure's signature, which calls are checked against.</summary>
  private sealed record Signature(string Name, bool IsPublic, IReadOnlyList<(string Name, VbaType Type, bool ByVal, bool Optional, VbaExpr? Default)> Parameters, VbaType? Returns, VbaProcedure? Source);

  private readonly Dictionary<string, VbaType> _types = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, (IrField Field, VbaType Type)> _fields = new(StringComparer.Ordinal);
  private readonly Dictionary<string, VbaValue> _constants = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, (IrGlobal Global, VbaType Type)> _globals = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, Signature> _procedures = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<IrTypeDeclaration> _declarations = [];
  private readonly HashSet<string> _enumTypes = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _usedHelpers = new(StringComparer.Ordinal);
  private int _optionBase;
  private IrGlobal? _err;

  /// <summary>Whether the procedures lowered are the frontend's own helpers, whose floating-point arithmetic is not checked and which may name the IR's operations.</summary>
  private bool _raw;

  private VbaRefusal Unsupported(int line, string construct) => new(VisualBasicFrontend.Unsupported, line, construct);

  private VbaRefusal Unresolved(int line, string message) => new(VisualBasicFrontend.Unresolved, line, message);

  private void Report(VbaRefusal refusal) => diagnostics.Add(VisualBasicFrontend.Report(refusal.Descriptor, file, refusal.Line, refusal.Message));

  /// <summary>The global that holds <c>Err.Number</c>, made when a procedure first uses it.</summary>
  private IrGlobal Err => this._err ??= new IrGlobal("ErrNumber", IrType.I32, new IrConst(IrType.I32, 0));

  /// <summary>The declarations of the module being lowered.</summary>
  private IReadOnlyList<VbaDeclaration> _module = [];

  public IrModule? Lower(VbaModule parsed) {
    this._module = parsed.Declarations;
    var name = parsed.Name ?? Path.GetFileNameWithoutExtension(file);
    var procedures = new List<VbaProcedure>();
    foreach (var declaration in parsed.Declarations) {
      try {
        this.Declare(declaration, procedures);
      } catch (VbaRefusal refusal) {
        this.Report(refusal);
      }
    }

    if (diagnostics.Count > 0)
      return null;

    var functions = new List<IrFunction>();
    foreach (var procedure in procedures) {
      try {
        functions.Add(this.Procedure(procedure));
      } catch (VbaRefusal refusal) {
        this.Report(refusal);
      }
    }

    if (diagnostics.Count > 0)
      return null;

    functions.AddRange(this.Helpers());
    if (diagnostics.Count > 0)
      return null;

    var globals = this._globals.Values.Select(global => global.Global).ToList();
    if (this._err is { } err)
      globals.Add(err);
    return new IrModule(name, functions) { Types = this._declarations, Globals = globals };
  }

  #region Declarations

  private void Declare(VbaDeclaration declaration, List<VbaProcedure> procedures) {
    switch (declaration) {
      case VbaOption option:
        this.Option(option);
        break;
      case VbaTypeDeclaration type:
        var record = new IrStructType(type.Name);
        this._types[type.Name] = new(VbaKind.Record, record);
        var fields = new List<IrField>();
        foreach (var variable in type.Fields) {
          var fieldType = this.VariableType(variable, true);
          var field = new IrField(variable.Name, fieldType.Ir, false);
          fields.Add(field);
          this._fields[Key(record, variable.Name)] = (field, fieldType);
        }

        record.Define(fields);
        this._declarations.Add(record);
        break;
      case VbaEnumDeclaration enumeration:
        // An Enum's members are Long constants; a variable of the Enum is a Long ([MS-VBAL] 5.2.3.4).
        this._enumTypes.Add(enumeration.Name);
        long next = 0;
        foreach (var member in enumeration.Members) {
          var value = member.Value is null ? next : this.ConstantValue(member.Value, member.Line);
          if (value is < int.MinValue or > int.MaxValue)
            throw this.Unsupported(member.Line, $"the Enum value {value}, which is no Long");
          this._constants[member.Name] = new(new IrConst(IrType.I32, value), VbaType.Long);
          next = value + 1;
        }

        break;
      case VbaConstDeclaration constant:
        this.Constant(constant.Name, constant.Type, constant.Value, constant.Line, this._constants);
        break;
      case VbaVariableDeclaration variables:
        foreach (var variable in variables.Variables) {
          var type = this.VariableType(variable, false, this._module.OfType<VbaProcedure>().SelectMany(procedure => procedure.Body));
          var global = new IrGlobal("g" + variable.Name, type.Ir, this.Default(type, variable.Line));
          this._globals[variable.Name] = (global, type);
        }

        break;
      case VbaProcedure procedure:
        if (this._procedures.ContainsKey(procedure.Name))
          throw this.Unresolved(procedure.Line, $"the procedure '{procedure.Name}' is declared twice");
        this._procedures[procedure.Name] = this.SignatureOf(procedure);
        procedures.Add(procedure);
        break;
      case VbaUnsupportedDeclaration unsupported:
        throw this.Unsupported(unsupported.Line, unsupported.Construct);
    }
  }

  private void Option(VbaOption option) {
    var words = option.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    switch (words.FirstOrDefault()?.ToLowerInvariant()) {
      case "explicit":
        break;
      case "base" when words.Length == 2 && words[1] is "0" or "1":
        this._optionBase = words[1] == "1" ? 1 : 0;
        break;
      case "compare" when words.Length == 2 && string.Equals(words[1], "Binary", StringComparison.OrdinalIgnoreCase):
        break;
      case "private" when words.Length == 2 && string.Equals(words[1], "Module", StringComparison.OrdinalIgnoreCase):
        break;
      default:
        throw this.Unsupported(option.Line, "Option " + option.Text);
    }
  }

  private static string Key(IrStructType record, string field) => record.Name + "." + field.ToUpperInvariant();

  /// <summary>The value of a constant expression of integers, as an enum member's value needs it.</summary>
  private long ConstantValue(VbaExpr expression, int line) {
    var value = this.ConstantExpression(expression, line);
    return value.Expr is IrConst { Type.IsInteger: true } constant ? constant.Value : throw this.Unsupported(line, "a value that is no integer constant where Visual Basic needs one");
  }

  /// <summary>A constant: its value lowered, of the declared type or the literal's, and recorded under its name.</summary>
  private void Constant(string name, VbaTypeRef? declared, VbaExpr value, int line, Dictionary<string, VbaValue> into) {
    var lowered = this.ConstantExpression(value, line);
    if (declared is not null)
      lowered = new(this.Coerce(lowered, this.TypeOf(declared, false), line), this.TypeOf(declared, false));
    into[name] = lowered;
  }

  /// <summary>An expression of literals and constants, lowered outside any procedure.</summary>
  private VbaValue ConstantExpression(VbaExpr expression, int line) {
    if (!IsConstant(expression))
      throw this.Unsupported(line, "a constant whose value is no constant expression");

    var saved = this._pending;
    this._pending = [];
    try {
      var value = this.Expression(expression);
      if (this._pending.Count > 0)
        throw this.Unsupported(line, "a constant whose value needs statements");
      return value;
    } finally {
      this._pending = saved;
    }
  }

  private static bool IsConstant(VbaExpr expression) => expression switch {
    VbaInteger or VbaFloat or VbaString or VbaBoolean or VbaName => true,
    VbaUnary unary => IsConstant(unary.Operand),
    VbaBinary binary => IsConstant(binary.Left) && IsConstant(binary.Right),
    VbaParenthesized parenthesized => IsConstant(parenthesized.Inner),
    _ => false
  };

  /// <summary>The type a name of a type denotes.</summary>
  private VbaType TypeOf(VbaTypeRef? type, bool field) {
    if (type is null)
      throw this.Unsupported(0, "Variant");

    switch (type.Name.ToLowerInvariant()) {
      case "boolean":
        return VbaType.Boolean;
      case "byte":
        return VbaType.Byte;
      case "integer":
        return VbaType.Integer;
      case "long":
        return VbaType.Long;
      case "longlong":
        return VbaType.LongLong;
      case "single":
        return VbaType.Single;
      case "double":
        return VbaType.Double;
      case "string":
        return VbaType.String;
    }

    if (this._enumTypes.Contains(type.Name))
      return VbaType.Long;
    if (this._types.TryGetValue(type.Name, out var record))
      return record;

    var name = type.Name.ToLowerInvariant() switch {
      "variant" => "Variant",
      "currency" => "Currency",
      "date" => "Date",
      "object" => "Object",
      "decimal" => "Decimal",
      "longptr" => "LongPtr",
      _ when type.Name.EndsWith(" * n", StringComparison.Ordinal) => "String * n",
      _ => null
    };
    if (name is not null)
      throw this.Unsupported(type.Line, name);

    throw this.Unresolved(type.Line, $"the type '{type.Name}' is not declared");
  }

  /// <summary>The type of a declared variable: an array of its element type with its lower bound, or a scalar. A field of a record holds no array.</summary>
  private VbaType VariableType(VbaVariable variable, bool field, IEnumerable<VbaStatement>? resized = null) {
    if (variable.Type is null)
      throw this.Unsupported(variable.Line, $"Variant (the variable '{variable.Name}' has no type)");

    var element = this.TypeOf(variable.Type, field);
    if (field && element.Kind == VbaKind.String)
      throw this.Unsupported(variable.Line, $"the String field '{variable.Name}' of a Type, which the IR's records do not hold");
    if (!variable.IsArray)
      return element;
    if (field)
      throw this.Unsupported(variable.Line, $"the array field '{variable.Name}' of a Type");
    if (variable.Bounds.Count > 1)
      throw this.Unsupported(variable.Line, "a multidimensional array");
    if (element.IsArray)
      throw this.Unsupported(variable.Line, "an array of arrays");
    if (element.Kind == VbaKind.String)
      throw this.Unsupported(variable.Line, $"the array of Strings '{variable.Name}', which the IR cannot create without its elements");

    // A dynamic array takes the lower bound its first ReDim gives it.
    var first = variable.IsDynamicArray ? ReDims(resized ?? [], variable.Name).FirstOrDefault() : null;
    var lower = variable.Bounds.Count == 1 && variable.Bounds[0].Lower is { } given ? (int)this.ConstantValue(given, variable.Line)
      : first is { Bounds: [{ Lower: { } redimmed }] } ? (int)this.ConstantValue(redimmed, variable.Line)
      : this._optionBase;
    return VbaType.ArrayOf(element, lower);
  }

  /// <summary>Every item of a <c>ReDim</c> of a name in statements and their blocks, in order.</summary>
  private static IEnumerable<VbaReDimItem> ReDims(IEnumerable<VbaStatement> statements, string name) {
    foreach (var statement in statements) {
      if (statement is VbaReDim redim)
        foreach (var item in redim.Items.Where(item => item.Target is VbaName named && string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase)))
          yield return item;
      foreach (var block in Blocks(statement))
        foreach (var item in ReDims(block, name))
          yield return item;
    }
  }

  private Signature SignatureOf(VbaProcedure procedure) {
    var parameters = new List<(string, VbaType, bool, bool, VbaExpr?)>();
    foreach (var parameter in procedure.Parameters) {
      if (parameter.Type is null)
        throw this.Unsupported(parameter.Line, $"Variant (the parameter '{parameter.Name}' has no type)");

      var type = this.TypeOf(parameter.Type, false);
      if (parameter.IsArray)
        type = VbaType.ArrayOf(type, this._optionBase);
      if (parameter.Optional && parameter.Default is null && !this._raw)
        throw this.Unsupported(parameter.Line, $"the Optional parameter '{parameter.Name}' without a default, which IsMissing tests");
      parameters.Add((parameter.Name, type, parameter.ByVal, parameter.Optional, parameter.Default));
    }

    VbaType? returns = null;
    if (procedure.IsFunction) {
      if (procedure.Returns is null)
        throw this.Unsupported(procedure.Line, $"Variant (the function '{procedure.Name}' has no result type)");
      returns = this.TypeOf(procedure.Returns, false);
      if (procedure.ReturnsArray)
        returns = VbaType.ArrayOf(returns, this._optionBase);
    }

    return new(procedure.Name, procedure.IsPublic, parameters, returns, procedure);
  }

  /// <summary>The value a variable of a type starts with: zero, <c>False</c>, an empty string, an empty array, a record of defaults.</summary>
  private IrExpr Default(VbaType type, int line) => type.Kind switch {
    VbaKind.String => new IrStrConst(""),
    VbaKind.Record => new IrStructNew(type.Ir, [.. type.Record!.Fields.Select(field => this.Default(this._fields[Key(type.Record, field.Name)].Type, line))]),
    VbaKind.Array => new IrNewArray(type.Ir, new IrConst(IrType.I32, 0)),
    _ => new IrConst(type.Ir, 0)
  };

  #endregion

}
