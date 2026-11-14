using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>
  /// The structs that need equality members: those that have them in the source, those an
  /// <see cref="IrStructEquals"/> compares, and every struct a field of one of those has, so that no
  /// comparison falls back to the reflection of <c>ValueType.Equals</c>.
  /// </summary>
  private static HashSet<IrStructType> Equatable(IrModule module) {
    var compared = module.Functions
      .SelectMany(function => Expressions(function.Body))
      .OfType<IrStructEquals>()
      .Select(equality => equality.Left.Type.StructType!);
    var pending = new Stack<IrStructType>(module.Types.OfType<IrStructType>().Where(type => type.IsEquatable).Concat(compared));
    var equatable = new HashSet<IrStructType>(ReferenceEqualityComparer.Instance);
    while (pending.TryPop(out var type))
      if (equatable.Add(type))
        foreach (var nested in type.Fields.Select(field => field.Type.StructType).OfType<IrStructType>())
          pending.Push(nested);

    return equatable;
  }

  /// <summary>Whether two expressions name the same variable, or the same field of it.</summary>
  private static bool SameVariable(IrExpr left, IrExpr right) => (left, right) switch {
    (IrLocalRef a, IrLocalRef b) => a.Local == b.Local,
    (IrFieldLoad a, IrFieldLoad b) => ReferenceEquals(a.Field, b.Field) && SameVariable(a.Target, b.Target),
    _ => false
  };

  private sealed partial class Writer {

    /// <summary>The prime the hash of a struct is combined with, field by field, where the framework has no <c>System.HashCode</c>.</summary>
    private const string HashFactor = "-1521134295";

    private const string Comparer = Base + "Collections.Generic.EqualityComparer<";

    private readonly Dictionary<string, string> _typeNames;
    private readonly HashSet<IrStructType> _equatable;
    private readonly Dictionary<IrTypeDeclaration, Dictionary<string, string>> _fieldNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The C# name of a field of a struct: its own where C# can write it, with an <c>@</c> for a
    /// keyword, and another one for a name C# refuses or that of the struct, which no member may have.
    /// </summary>
    private string FieldName(IrField field, IrType owner) => this.FieldNames(owner.Declaration!)[field.Name];

    private Dictionary<string, string> FieldNames(IrTypeDeclaration type) {
      if (!this._fieldNames.TryGetValue(type, out var names))
        this._fieldNames[type] = names = Names(type.Fields.Select(field => field.Name), [this._typeNames[type.Name].TrimStart('@')]);

      return names;
    }

    /// <summary>The structs of the module, each in the form the profile has (see the lowering table of <c>docs/reference/records.md</c>).</summary>
    private string Structs() {
      var code = new StringBuilder();
      foreach (var type in this._module.Types.OfType<IrStructType>())
        this.Struct(type, code);

      return code.ToString();
    }

    private void Struct(IrStructType type, StringBuilder code) {
      var name = this._typeNames[type.Name];
      var fields = type.Fields.Select(field => (Type: this.TypeName(field.Type), Name: this.FieldNames(type)[field.Name], Field: field)).ToList();
      var equatable = this._equatable.Contains(type);
      var readOnly = type.IsReadOnly && this.Has(CSharpFeature.ReadOnlyStruct);
      // A readonly record struct has init accessors, which need a type of the framework; without it the struct is written out.
      if (equatable && this.Has(CSharpFeature.RecordStruct) && (!type.IsReadOnly || this._table.HasApi("IsExternalInit", this._profile))) {
        code.Append("public ").Append(type.IsReadOnly ? "readonly " : "").Append("record struct ").Append(name)
          .Append('(').AppendJoin(", ", fields.Select(field => $"{field.Type} {field.Name}")).Append(')');
        // A component that is a struct is a field, not a property, so that its own fields can be written through it.
        var nested = fields.Where(field => field.Field.Type.IsStruct).ToList();
        if (nested.Count == 0) {
          code.Append(";\n\n");
          return;
        }

        code.Append(" {\n\n");
        foreach (var field in nested)
          code.Append("  public ").Append(type.IsReadOnly ? "readonly " : "").Append(field.Type).Append(' ').Append(field.Name).Append(" = ").Append(field.Name).Append(";\n");

        code.Append("\n}\n\n");
        return;
      }

      code.Append("public ").Append(readOnly ? "readonly " : "").Append("struct ").Append(name);
      if (equatable)
        code.Append(" : ").Append(Base).Append("IEquatable<").Append(name).Append('>');

      code.Append(" {\n\n");
      foreach (var field in fields)
        code.Append("  public ").Append(field.Field.IsReadOnly ? "readonly " : "").Append(field.Type).Append(' ').Append(field.Name).Append(";\n");

      // A struct without fields has the parameterless constructor only, which C# before 10 does not let it declare.
      if (fields.Count > 0) {
        code.Append("\n  public ").Append(name).Append('(').AppendJoin(", ", fields.Select(field => $"{field.Type} {field.Name}")).Append(") {\n");
        foreach (var field in fields)
          code.Append("    this.").Append(field.Name).Append(" = ").Append(field.Name).Append(";\n");

        code.Append("  }\n");
      }

      if (equatable)
        this.EqualityMembers(name, fields.Select(field => (field.Type, field.Name)).ToList(), code);

      code.Append("\n}\n\n");
    }

    /// <summary>
    /// The members a <c>record struct</c> has, written out: <c>Equals(T)</c> comparing every field with
    /// <c>EqualityComparer&lt;F&gt;.Default</c>, which compares a floating-point number as
    /// <c>double.Equals</c> does; <c>Equals(object)</c>, <c>GetHashCode()</c> and the two operators.
    /// </summary>
    private void EqualityMembers(string name, List<(string Type, string Name)> fields, StringBuilder code) {
      code.Append("\n  public bool Equals(").Append(name).Append(" other) {\n    return ")
        .Append(fields.Count == 0 ? "true" : string.Join("\n      && ", fields.Select(field => $"{Comparer}{field.Type}>.Default.Equals(this.{field.Name}, other.{field.Name})")))
        .Append(";\n  }\n");
      code.Append("\n  public override bool Equals(object obj) {\n    return obj is ").Append(name).Append(" && this.Equals((").Append(name).Append(")obj);\n  }\n");
      code.Append("\n  public override int GetHashCode() {\n");
      if (fields.Count == 0) {
        code.Append("    return 0;\n");
      } else if (this._table.HasApi("HashCode.Combine", this._profile) && fields.Count <= 8) {
        code.Append("    return ").Append(Base).Append("HashCode.Combine(").AppendJoin(", ", fields.Select(field => $"this.{field.Name}")).Append(");\n");
      } else if (this._table.HasApi("HashCode.Combine", this._profile)) {
        code.Append("    ").Append(Base).Append("HashCode hash = new ").Append(Base).Append("HashCode();\n");
        foreach (var field in fields)
          code.Append("    hash.Add(this.").Append(field.Name).Append(");\n");

        code.Append("    return hash.ToHashCode();\n");
      } else {
        code.Append("    unchecked {\n      int hash = 0;\n");
        foreach (var field in fields)
          code.Append("      hash = hash * ").Append(HashFactor).Append(" + ").Append(Comparer).Append(field.Type).Append(">.Default.GetHashCode(this.").Append(field.Name).Append(");\n");

        code.Append("      return hash;\n    }\n");
      }

      code.Append("  }\n");
      code.Append("\n  public static bool operator ==(").Append(name).Append(" left, ").Append(name).Append(" right) {\n    return left.Equals(right);\n  }\n");
      code.Append("\n  public static bool operator !=(").Append(name).Append(" left, ").Append(name).Append(" right) {\n    return !left.Equals(right);\n  }\n");
    }

  }

}
