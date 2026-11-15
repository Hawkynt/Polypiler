using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// Resource scopes in C#: a <c>using</c> statement in every profile, stacked where a scope holds only
/// the next; a using declaration <c>using var r = e;</c> where the source wrote one, the profile has
/// C# 8 (<see cref="CSharpFeature.UsingDeclaration"/>) and the scope is the last statement of its
/// block, so that the rest of the block is its body. The interface of cleanup is .NET's own,
/// <c>global::System.IDisposable</c>, which the module does not declare.
/// </summary>
public static partial class CSharpEmitter {

  private sealed partial class Writer {

    /// <summary>Names every interface of cleanup of a module after .NET's <c>System.IDisposable</c>.</summary>
    private void NameDisposable(IrModule module) {
      foreach (var contract in module.Types.OfType<IrInterfaceType>().Where(contract => contract.IsDisposable))
        this._typeNames[contract.Name] = Base + "IDisposable";
    }

    /// <summary>The statements of a block in the scope being written; a scope of a using declaration that ends the block writes its body after it.</summary>
    private void Sequence(IReadOnlyList<IrStmt> block, List<CsStatement> statements) {
      for (var index = 0; index < block.Count; ++index) {
        if (index == block.Count - 1 && block[index] is IrUsing { IsDeclaration: true } scope && this.Has(CSharpFeature.UsingDeclaration)) {
          statements.Add(this.Resource(scope) with { IsUsing = true });
          this.Sequence(scope.Body.Statements, statements);
          return;
        }

        this.Statement(block[index], statements);
      }
    }

    /// <summary>The declaration of the resource of a scope: <c>var</c> where the profile infers the type a declaration would.</summary>
    private CsLocalDeclaration Resource(IrUsing scope) {
      // The initializer is read before the variable is declared, as the IR reads it.
      var initializer = this.Stored(scope.Initializer);
      var type = this.Infers(new IrDeclare(scope.Resource, scope.Initializer)) ? CsType.Var : new CsType(this.TypeName(scope.Resource.Type));
      return new CsLocalDeclaration(type, this.Declare(scope.Resource), initializer);
    }

    /// <summary>A using statement; one whose body is only another using statement stacks it.</summary>
    private CsUsing Using(IrUsing scope) {
      var own = new Scope();
      this._scopes.Add(own);
      var resource = this.Resource(scope);
      CsStatement body = scope.Body.Statements is [IrUsing { IsDeclaration: false } inner] ? this.Using(inner) : new CsBlock(this.Statements(scope.Body));
      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(own.Within);
      return new CsUsing(resource, body);
    }

  }

}
