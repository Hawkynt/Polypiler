using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  private sealed partial class Writer {

    private HashSet<string>? _signatures;

    /// <summary>The functions of the module that are the signatures of the methods of its interfaces, which no class writes.</summary>
    private HashSet<string> Signatures => this._signatures ??= [.. this._module.Types.OfType<IrInterfaceType>().SelectMany(type => type.Functions)];

    /// <summary>Whether a class of the module derives from a class: one that is not written <c>sealed</c>.</summary>
    private bool Derived(IrClassType type) => this._module.Types.OfType<IrClassType>().Any(other => other.Base == type);

    /// <summary>
    /// The modifiers of a method: its visibility - <c>public</c> for an implementation of a method of
    /// an interface - and <c>static</c>, <c>abstract</c>, <c>virtual</c>, <c>override</c> or
    /// <c>sealed override</c> as its dispatch says.
    /// </summary>
    private List<string> MethodModifiers(IrMethod method) {
      var visibility = method.Implements.Count > 0 ? "public" : Visibility(method.Visibility);
      return method.Dispatch switch {
        IrDispatch.Static => [visibility, "static"],
        IrDispatch.Abstract => [visibility, "abstract"],
        IrDispatch.Virtual => [visibility, "virtual"],
        IrDispatch.Override => method.IsSealedOverride ? [visibility, "sealed", "override"] : [visibility, "override"],
        _ => [visibility]
      };
    }

    /// <summary>The C# name of a method of an interface or of the property of its accessor: the name of the source, spelled as C# allows.</summary>
    private string InterfaceMemberName(IrMethod method) => Names([method.Name], [])[method.Name];

    /// <summary>An interface: <c>interface I : J { T M(…); T P { get; set; } }</c>, its members without bodies.</summary>
    private CsTypeDeclaration Interface(IrInterfaceType type) {
      var functions = this._module.Functions.Where(function => type.Functions.Contains(function.Name)).ToDictionary(function => function.Name);
      var members = new List<CsMember>();
      foreach (var method in type.Methods.Where(method => method.Kind == IrMethodKind.Method)) {
        var function = functions[method.Function];
        var parameters = function.Parameters.Skip(1).Select(parameter => new CsParameter(new CsType(this.TypeName(parameter.Type)), Names([parameter.Name], [])[parameter.Name])).ToList();
        members.Add(new CsMethod([], new CsType(this.TypeName(function.ReturnType)), this.InterfaceMemberName(method), [], parameters, null, null));
      }

      foreach (var property in type.Properties)
        members.Add(new CsProperty([], new CsType(this.TypeName(property.Type)), this.InterfaceMemberName(property.Getter ?? property.Setter!),
          [.. property.Getter is null ? [] : (CsAccessor[])[new CsAccessor("get", null, null)], .. property.Setter is null ? [] : (CsAccessor[])[new CsAccessor("set", null, null)]], null));

      return new CsTypeDeclaration([Visibility(type.Visibility)], "interface", this._typeNames[type.Name], null,
        [.. type.BaseInterfaces.Select(inherited => new CsType(this._typeNames[inherited.Name]))], [.. members.Select(member => member with { Leading = CsTrivia.Blank })]);
    }

    /// <summary>A type test, <c>x is T</c>; a cast, <c>(T)x</c>, which throws <c>InvalidCastException</c> where the IR traps; and <c>x as T</c>.</summary>
    private (CsExpression Node, int Binding) Conversion(IrExpr expression) {
      switch (expression) {
        case IrTypeTest test:
          return (new CsIsType(this.Operand(test.Value, Unary), new CsType(this.TypeName(test.Target))), Loose);
        case IrCast cast:
          return (new CsCast(new CsType(this.TypeName(cast.Type)), this.Operand(cast.Value, Cast)) { Type = cast.Type }, Cast);
        default:
          var converted = (IrAsCast)expression;
          return (new CsBinary(this.Operand(converted.Value, Unary), "as", new CsName(this.TypeName(converted.Type))) { Type = converted.Type }, Loose);
      }
    }

    /// <summary>
    /// <c>if (x is T t) { … }</c> from C# 7 on, for a test of a variable whose branch first declares a
    /// variable of the type with the variable cast to it; <see langword="null"/> for any other
    /// <c>if</c>, which is written as a test and a cast. The pattern's variable belongs to the block
    /// around the <c>if</c>, and is declared there.
    /// </summary>
    private CsIf? PatternIf(IrIf conditional) {
      if (!this.Has(CSharpFeature.PatternDeclaration) || conditional.Condition is not IrTypeTest { Value: IrLocalRef tested } test
          || conditional.Then.Statements is not [IrDeclare { Local: var variable, Initializer: IrCast { Value: IrLocalRef cast } }, ..]
          || cast.Local != tested.Local || variable.Type != test.Target)
        return null;

      var operand = this.Operand(test.Value, Unary);
      var condition = new CsIsType(operand, new CsType(this.TypeName(test.Target))) { Designation = this.Declare(variable) };
      var then = new CsBlock(this.Statements(new IrBlock([.. conditional.Then.Statements.Skip(1)])));
      if (conditional.Else is null)
        return new CsIf(condition, then, null);

      return new CsIf(condition, then, conditional.Else.Statements is [IrIf chained] ? this.If(chained) : new CsBlock(this.Statements(conditional.Else)));
    }

  }

}
