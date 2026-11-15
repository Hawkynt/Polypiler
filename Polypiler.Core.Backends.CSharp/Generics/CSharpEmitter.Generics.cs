using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  private sealed partial class Writer {

    /// <summary>How the instantiations of the module are written.</summary>
    private readonly CSharpGenerics _generics;

    /// <summary>The name a function or a class of the IR is spelled from: its own, or what the generics decide for an instantiation or a copy.</summary>
    private string Spelled(string name) => this._generics.Spellings.GetValueOrDefault(name, name);

    /// <summary>The C# names of names in the IR: of each spelled name, and of each name in the IR that is spelled from one.</summary>
    private Dictionary<string, string> Aliased(Dictionary<string, string> names, IEnumerable<string> named) {
      foreach (var name in named.Where(name => !names.ContainsKey(name)).ToList())
        names[name] = names[this.Spelled(name)];

      return names;
    }

    /// <summary>
    /// A generic definition written back in the place of its first instantiation: the template of the
    /// origin with its type parameters and their constraints, named as the source names it.
    /// </summary>
    private CsMethod Generic(IrFunction instance, IrGenericOrigin origin) {
      var template = origin.Template!.Function;
      var modifiers = Asynchronous(Unsafe([instance.IsPublic ? "public" : "private", "static"], template), template);
      var returns = new CsType(this.TypeName(template.ReturnType));
      var (parameters, body, expression) = this.Body(template, 0, template.Body, true);
      return new CsMethod(modifiers, returns, this._members[instance.Name], [.. origin.TypeParameters], parameters, body, expression) {
        Constraints = [.. origin.TypeParameters.Select(parameter => this.Clause(parameter, origin)).OfType<string>()]
      };
    }

    /// <summary>The constraint clause of a type parameter without <c>where</c>, in the order C# demands: the primary constraint, the interfaces, <c>new()</c>; <see langword="null"/> for none.</summary>
    private string? Clause(string parameter, IrGenericOrigin origin) {
      var constraints = origin.Constraints.Where(constraint => constraint.Parameter == parameter).ToList();
      if (constraints.Count == 0)
        return null;

      static int Rank(IrGenericConstraintKind kind) => kind switch {
        IrGenericConstraintKind.Struct or IrGenericConstraintKind.Class or IrGenericConstraintKind.Unmanaged or IrGenericConstraintKind.NotNull => 0,
        IrGenericConstraintKind.New => 2,
        _ => 1
      };
      var spelled = constraints.OrderBy(constraint => Rank(constraint.Kind)).Select(constraint => constraint.Kind switch {
        IrGenericConstraintKind.Struct => "struct",
        IrGenericConstraintKind.Class => "class",
        IrGenericConstraintKind.Unmanaged => "unmanaged",
        IrGenericConstraintKind.NotNull => "notnull",
        IrGenericConstraintKind.Equatable => $"{Base}IEquatable<{parameter}>",
        IrGenericConstraintKind.Comparable => $"{Base}IComparable<{parameter}>",
        IrGenericConstraintKind.Interface => this.TypeName(constraint.Interface!),
        _ => "new()"
      });
      return $"{parameter} : {string.Join(", ", spelled)}";
    }

    /// <summary>The type arguments of a call of an instantiation as C# writes them after the name: <c>&lt;double&gt;</c>.</summary>
    private string Arguments(IEnumerable<IrType> arguments) => $"<{string.Join(", ", arguments.Select(this.TypeName))}>";

    /// <summary>
    /// The nodes a generic definition brings: a call of an instantiation written back as its generic
    /// definition, with its type arguments; <c>default(T)</c>; the equality and the order of
    /// <c>IEquatable&lt;T&gt;</c> and <c>IComparable&lt;T&gt;</c>. <see langword="null"/> for any other node.
    /// </summary>
    private (CsExpression Node, int Binding)? GenericNode(IrExpr expression) {
      switch (expression) {
        case IrCall call when this._generics.Generalized.TryGetValue(call.Function, out var origin):
          return (new CsInvocation(this.Qualified(this._members[call.Function] + this.Arguments(origin.TypeArguments)), [.. call.Arguments.Select(this.Stored)]), Primary);
        case IrCall call when this._generics.Calls.TryGetValue(call.Function, out var instantiated):
          var name = this._members[this._generics.Generalized.First(entry => entry.Value.Name == instantiated.Origin).Key];
          return (new CsInvocation(this.Qualified(name + this.Arguments(instantiated.Arguments)), [.. call.Arguments.Select(this.Stored)]), Primary);
        case IrNull { Type.Declaration: { } declared } when this._generics.Parameters.ContainsKey(declared):
          return (new CsDefaultExpression(new CsType(declared.Name)), Primary);
        case IrGenericEquals equality:
          return this.Generic(equality.Left, equality.Right, IrGenericConstraintKind.Equatable, "Equals", "EqualityComparer", "Equals");
        case IrGenericCompare order:
          return this.Generic(order.Left, order.Right, IrGenericConstraintKind.Comparable, "CompareTo", "Comparer", "Compare");
        default:
          return null;
      }
    }

    /// <summary>
    /// The equality or the order of two values: the method of the value itself where its type is a
    /// number, a truth value or a type parameter that has the interface, else the default comparer of
    /// the type, which also takes <c>null</c>.
    /// </summary>
    private (CsExpression Node, int Binding) Generic(IrExpr left, IrExpr right, IrGenericConstraintKind constraint, string method, string comparer, string compares) {
      var type = left.Type;
      var parameter = type.Declaration is { } declared ? this._generics.Parameters.GetValueOrDefault(declared) : null;
      if (!type.IsObject || parameter?.Contains(constraint) == true) {
        var receiver = this.Operand(left, Primary);
        return (new CsInvocation(new CsMemberAccess(receiver, method), [this.Stored(right)]), Primary);
      }

      var first = this.Stored(left);
      var target = new CsMemberAccess(new CsName($"{Base}Collections.Generic.{comparer}<{this.TypeName(type)}>"), "Default");
      return (new CsInvocation(new CsMemberAccess(target, compares), [first, this.Stored(right)]), Primary);
    }

  }

}
