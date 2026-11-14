using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
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
    private const int HashFactor = -1521134295;

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
    private List<CsMember> Structs() => [.. this._module.Types.OfType<IrStructType>().Select(this.Struct)];

    private CsTypeDeclaration Struct(IrStructType type) {
      var name = this._typeNames[type.Name];
      var fields = type.Fields.Select(field => (Type: this.TypeName(field.Type), Name: this.FieldNames(type)[field.Name], Field: field)).ToList();
      var equatable = this._equatable.Contains(type);
      var readOnly = type.IsReadOnly && this.Has(CSharpFeature.ReadOnlyStruct);
      // A readonly record struct has init accessors, which need a type of the framework; without it the struct is written out.
      if (equatable && this.Has(CSharpFeature.RecordStruct) && (!type.IsReadOnly || this._table.HasApi("IsExternalInit", this._profile))) {
        IReadOnlyList<string> modifiers = type.IsReadOnly ? ["public", "readonly"] : ["public"];
        // A component that is a struct is a field, not a property, so that its own fields can be written through it.
        var nested = fields.Where(field => field.Field.Type.IsStruct).ToList();
        return new CsTypeDeclaration(
          type.IsReadOnly ? ["public", "readonly"] : ["public"], "record struct", name, [.. fields.Select(field => new CsParameter(new CsType(field.Type), field.Name))], [],
          nested.Count == 0 ? null : [.. nested.Select(field => new CsField(modifiers, new CsType(field.Type), field.Name, new CsName(field.Name)))]) { OpenBlank = true };
      }

      var members = new List<CsMember>();
      foreach (var field in fields)
        members.Add(new CsField(field.Field.IsReadOnly ? ["public", "readonly"] : ["public"], new CsType(field.Type), field.Name, null));

      // A struct without fields has the parameterless constructor only, which C# before 10 does not let it declare.
      if (fields.Count > 0)
        members.Add(new CsConstructor(["public"], name, [.. fields.Select(field => new CsParameter(new CsType(field.Type), field.Name))],
          new CsBlock([.. fields.Select(field => new CsExpressionStatement(new CsAssignment(new CsMemberAccess(new CsName("this"), field.Name), "=", new CsName(field.Name))))])) {
          Leading = CsTrivia.Blank
        });

      if (equatable)
        members.AddRange(this.EqualityMembers(name, [.. fields.Select(field => (field.Type, field.Name))]).Select(member => member with { Leading = CsTrivia.Blank }));

      return new CsTypeDeclaration(readOnly ? ["public", "readonly"] : ["public"], "struct", name, null, equatable ? [new CsType($"{Base}IEquatable<{name}>")] : [], members) {
        OpenBlank = true
      };
    }

    /// <summary>A field of the struct being compared, read from <c>this</c> or from another value.</summary>
    private static CsMemberAccess Of(string owner, string field) => new(new CsName(owner), field);

    /// <summary>The comparer of a type, <c>EqualityComparer&lt;F&gt;.Default</c>.</summary>
    private static CsMemberAccess DefaultComparer(string type) => new(CSharpFragments.Expression(Comparer + type + ">"), "Default");

    private static CsMethod Method(IReadOnlyList<string> modifiers, string returns, string name, IReadOnlyList<CsParameter> parameters, params CsStatement[] body)
      => new(modifiers, new CsType(returns), name, [], parameters, new CsBlock(body), null);

    /// <summary>
    /// The members a <c>record struct</c> has, written out: <c>Equals(T)</c> comparing every field with
    /// <c>EqualityComparer&lt;F&gt;.Default</c>, which compares a floating-point number as
    /// <c>double.Equals</c> does; <c>Equals(object)</c>, <c>GetHashCode()</c> and the two operators.
    /// </summary>
    private IEnumerable<CsMember> EqualityMembers(string name, List<(string Type, string Name)> fields) {
      var comparisons = fields.Select(field => (CsExpression)new CsInvocation(new CsMemberAccess(DefaultComparer(field.Type), "Equals"), [Of("this", field.Name), Of("other", field.Name)]));
      var equal = fields.Count == 0 ? new CsBooleanLiteral(true) : comparisons.Aggregate((left, right) => new CsBinary(left, "&&", right) { BreakBefore = true });
      yield return Method(["public"], "bool", "Equals", [new CsParameter(new CsType(name), "other")], new CsReturn(equal));
      yield return Method(["public", "override"], "bool", "Equals", [new CsParameter(new CsType("object"), "obj")], new CsReturn(new CsBinary(
        new CsIsType(new CsName("obj"), new CsType(name)), "&&", new CsInvocation(Of("this", "Equals"), [new CsCast(new CsType(name), new CsName("obj"))]))));
      yield return Method(["public", "override"], "int", "GetHashCode", [], [.. this.HashCode(fields)]);
      IReadOnlyList<CsParameter> operands = [new CsParameter(new CsType(name), "left"), new CsParameter(new CsType(name), "right")];
      var equals = new CsInvocation(Of("left", "Equals"), [new CsName("right")]);
      yield return Method(["public", "static"], "bool", "operator ==", operands, new CsReturn(equals));
      yield return Method(["public", "static"], "bool", "operator !=", operands, new CsReturn(new CsPrefixUnary("!", equals)));
    }

    /// <summary>The body of <c>GetHashCode()</c>: <c>HashCode</c> where the framework has it, else the fields combined with a prime.</summary>
    private IEnumerable<CsStatement> HashCode(List<(string Type, string Name)> fields) {
      if (fields.Count == 0) {
        yield return new CsReturn(new CsIntegerLiteral(0));
      } else if (this._table.HasApi("HashCode.Combine", this._profile) && fields.Count <= 8) {
        yield return new CsReturn(new CsInvocation(CSharpFragments.Expression(Base + "HashCode.Combine"), [.. fields.Select(field => Of("this", field.Name))]));
      } else if (this._table.HasApi("HashCode.Combine", this._profile)) {
        yield return new CsLocalDeclaration(new CsType(Base + "HashCode"), "hash", new CsObjectCreation(new CsType(Base + "HashCode"), []));
        foreach (var field in fields)
          yield return new CsExpressionStatement(new CsInvocation(Of("hash", "Add"), [Of("this", field.Name)]));

        yield return new CsReturn(new CsInvocation(Of("hash", "ToHashCode"), []));
      } else {
        List<CsStatement> statements = [new CsLocalDeclaration(new CsType("int"), "hash", new CsIntegerLiteral(0))];
        foreach (var field in fields)
          statements.Add(new CsExpressionStatement(new CsAssignment(new CsName("hash"), "=", new CsBinary(
            new CsBinary(new CsName("hash"), "*", new CsIntegerLiteral(HashFactor)), "+",
            new CsInvocation(new CsMemberAccess(DefaultComparer(field.Type), "GetHashCode"), [Of("this", field.Name)])))));

        statements.Add(new CsReturn(new CsName("hash")));
        yield return new CsCheckedStatement(false, new CsBlock(statements));
      }
    }

  }

}
