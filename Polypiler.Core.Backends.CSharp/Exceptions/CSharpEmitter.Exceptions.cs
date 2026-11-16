using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// Exceptions in C#: <c>try</c>, <c>catch (T e)</c>, <c>finally</c>, <c>throw</c> and <c>throw;</c>
/// in every profile; an exception filter <c>when (c)</c> from C# 6, below as one catch of every
/// exception whose block tests the clauses in order - <c>T e = caught as T; if (e != null &amp;&amp; c) …
/// else throw;</c> - where <see cref="IrExceptionFilter.MayRunLate"/> allows it and refused otherwise;
/// a throw expression from C# 7, below through the helper <c>Throw&lt;T&gt;(e)</c>. The library
/// exception classes are .NET's own (<c>global::System.DivideByZeroException</c>).
/// </summary>
public static partial class CSharpEmitter {

  /// <summary>The helper that throws where a value stands, below C# 7.</summary>
  internal static CSharpHelper ThrowHelper { get; } = new("Throw", "T", "global::System.Exception e", ["throw e;"], "<T>");

  /// <summary>What a profile without exception filters cannot write: one diagnostic per filter that must run before the finally blocks.</summary>
  private static IEnumerable<Diagnostic> FilterProblems(IrModule module, CSharpProfile profile, CSharpCapabilities table) {
    if (table.Has(CSharpFeature.ExceptionFilter, profile))
      yield break;

    foreach (var function in module.Functions)
      foreach (var attempt in Tries(function.Body))
        foreach (var clause in attempt.Catches.Where(clause => !IrExceptionFilter.MayRunLate(attempt, clause)))
          yield return InexpressibleDescriptor.At(
            $"the exception filter of a catch of {clause.Class.Name} in '{function.Name}' runs before finally blocks; it cannot be expressed in profile '{profile.Name}'");
  }

  /// <summary>Every try within a statement, the outer before the inner.</summary>
  private static IEnumerable<IrTry> Tries(IrStmt statement) {
    var inner = (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements)).SelectMany(Tries);
    return statement is IrTry attempt ? inner.Prepend(attempt) : inner;
  }

  private sealed partial class Writer {

    /// <summary>The C# name of a class: .NET's own for a library class, the module's name for any other.</summary>
    private string ClassTypeName(IrClassType type)
      => type == IrExceptionClass.KeyNotFoundException ? Base + "Collections.Generic." + type.Name
        : type == IrExceptionClass.AssertionException ? this.Nested(CSharpCollectionClasses.AssertionExceptionName)
        : type.IsLibrary ? Base + type.Name : this._typeNames[type.Name];

    /// <summary>The statements of exception handling; <see langword="false"/> for any other.</summary>
    private bool ExceptionStatement(IrStmt statement, List<CsStatement> statements) {
      switch (statement) {
        case IrThrow thrown:
          statements.Add(new CsThrow(this.Stored(thrown.Exception)));
          return true;
        case IrRethrow:
          statements.Add(new CsThrow(null));
          return true;
        case IrTry attempt:
          statements.Add(this.Try(attempt));
          return true;
        default:
          return false;
      }
    }

    /// <summary>A try; where the profile lacks the filter one of its clauses has, its clauses are tested in one catch block.</summary>
    private CsTry Try(IrTry attempt) {
      var body = new CsBlock(this.Statements(attempt.Body));
      if (!this.Has(CSharpFeature.ExceptionFilter) && attempt.Catches.Any(clause => clause.Filter is not null))
        return new CsTry(body, [this.Tested(attempt)], attempt.Finally is null ? null : new CsBlock(this.Statements(attempt.Finally)));

      var catches = new List<CsCatch>();
      foreach (var clause in attempt.Catches) {
        var scope = new Scope();
        this._scopes.Add(scope);
        // A variable the clause never reads is left out: C# warns of it (CS0168), as JavaScript's catch (e) does not.
        var name = clause.Local is { } local && Reads(clause, local) ? this.Declare(local) : null;
        var filter = clause.Filter is null ? null : this.Text(clause.Filter);
        var statements = this.Statements(clause.Body);
        this._scopes.RemoveAt(this._scopes.Count - 1);
        this._scopes[^1].Within.UnionWith(scope.Within);
        // catch (Exception) is what catch catches; a clause of every exception without a variable is written as catch alone.
        var type = clause.Class == IrExceptionClass.Exception && name is null && filter is null ? null : new CsType(this.ClassTypeName(clause.Class));
        catches.Add(new CsCatch(type, name, filter, new CsBlock(statements)));
      }

      return new CsTry(body, catches, attempt.Finally is null ? null : new CsBlock(this.Statements(attempt.Finally)));
    }

    /// <summary>Whether a catch clause reads or writes its variable: in its filter or its body, also inside a lambda.</summary>
    private static bool Reads(IrCatch clause, IrLocal local)
      => (clause.Filter is { } filter ? IrTree.Nodes(filter) : []).Concat(IrTree.Nodes(clause.Body)).Any(node => node is IrLocalRef reference && reference.Local == local)
        || IrClosures.Within(clause.Body).Any(statement => statement is IrAssign assign && assign.Local == local);

    /// <summary>
    /// The clauses of a try as one catch of every exception, below C# 6: each variable of a clause is
    /// the exception <c>as</c> its class, and the clauses are tested in order - the class, then the
    /// filter - with <c>throw;</c> where none selects the exception.
    /// </summary>
    private CsCatch Tested(IrTry attempt) {
      var scope = new Scope();
      this._scopes.Add(scope);
      var caught = "caught";
      for (var index = 1; !this._names.Add(caught); ++index)
        caught = $"caught_{index}";

      scope.Own.Add(caught);
      scope.Within.Add(caught);
      var statements = new List<CsStatement>();
      var tests = new List<CsExpression?>();
      foreach (var clause in attempt.Catches) {
        var type = this.ClassTypeName(clause.Class);
        CsExpression? test = clause.Class == IrExceptionClass.Exception ? null : new CsIsType(new CsName(caught), new CsType(type));
        if (clause.Local is { } local) {
          var name = this.Declare(local);
          statements.Add(new CsLocalDeclaration(new CsType(type), name, clause.Class == IrExceptionClass.Exception ? new CsName(caught) : new CsBinary(new CsName(caught), "as", new CsName(type))));
          test = test is null ? null : new CsBinary(new CsName(name), "!=", new CsNullLiteral());
        }

        if (clause.Filter is { } filter) {
          var condition = this.Text(filter);
          test = test is null ? condition : new CsBinary(new CsParenthesized(test), "&&", new CsParenthesized(condition));
        }

        tests.Add(test);
      }

      CsStatement? chain = null;
      for (var index = attempt.Catches.Count - 1; index >= 0; --index) {
        var handler = new CsBlock(this.Statements(attempt.Catches[index].Body));
        chain = tests[index] is { } test ? new CsIf(test, handler, chain ?? new CsBlock([new CsThrow(null)])) : handler;
      }

      statements.Add(chain!);
      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(scope.Within);
      return new CsCatch(new CsType(Base + "Exception"), caught, null, new CsBlock(statements));
    }

    /// <summary>A throw where a value stands: as it is from C# 7, through the helper before.</summary>
    private (CsExpression Node, int Binding) ThrowExpression(IrThrowExpression thrown) {
      var exception = this.Stored(thrown.Exception);
      if (this.Has(CSharpFeature.ThrowExpression))
        return (new CsThrowExpression(exception) { Type = thrown.Type }, Primary);

      var helper = this.Helper(this._table.Of(CSharpFeature.ThrowExpression)!.Helper!);
      return (new CsInvocation(CSharpFragments.Name($"{helper}<{this.TypeName(thrown.Type)}>"), [exception]) { Type = thrown.Type }, Primary);
    }

  }

}
