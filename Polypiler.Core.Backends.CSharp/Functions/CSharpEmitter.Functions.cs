using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>Every statement within a statement, those in the bodies of its lambdas included, the statement first.</summary>
  private static IEnumerable<IrStmt> AllWithin(IrStmt statement) {
    yield return statement;
    var inner = statement is IrBlock block ? block.Statements : Parts(statement)!.Value.Statements;
    foreach (var nested in inner.SelectMany(AllWithin))
      yield return nested;

    foreach (var lambda in IrTree.Expressions(statement).SelectMany(Outermost))
      foreach (var nested in AllWithin(lambda.Body))
        yield return nested;
  }

  /// <summary>The lambdas of an expression that no other lambda of it holds.</summary>
  private static IEnumerable<IrLambda> Outermost(IrExpr expression)
    => expression is IrLambda lambda ? [lambda] : IrTree.Operands(expression).SelectMany(Outermost);

  /// <summary>The parameters and locals of the lambdas in a body, at any depth.</summary>
  private static IEnumerable<IrLocal> LambdaVariables(IrBlock body)
    => IrTree.Nodes(body).OfType<IrLambda>().SelectMany(lambda => lambda.Parameters.Concat(lambda.Locals));

  /// <summary>Whether a body writes a variable: assigns it or steps it, also inside a lambda.</summary>
  private static bool Writes(IrBlock body, IrLocal variable)
    => AllWithin(body).Any(statement => statement is IrAssign assign && assign.Local == variable)
      || IrTree.Nodes(body).Any(node => node is IrStep { Target: IrLocalRef reference } && reference.Local == variable);

  /// <summary>The name a type contributes to the name of a delegate type the class declares: <c>Int32</c>, <c>DoubleArray</c>, the name of a class.</summary>
  private static string Readable(IrType type, System.Func<IrType, string> declared) => type.Kind switch {
    IrTypeKind.Void => "Void",
    IrTypeKind.Bool => "Boolean",
    IrTypeKind.I8 => "SByte",
    IrTypeKind.U8 => "Byte",
    IrTypeKind.I16 => "Int16",
    IrTypeKind.U16 => "UInt16",
    IrTypeKind.I32 => "Int32",
    IrTypeKind.U32 => "UInt32",
    IrTypeKind.I64 => "Int64",
    IrTypeKind.U64 => "UInt64",
    IrTypeKind.F32 => "Single",
    IrTypeKind.F64 => "Double",
    IrTypeKind.String => "String",
    IrTypeKind.Array => Readable(type.Element!, declared) + "Array",
    IrTypeKind.Span => Readable(type.Element!, declared) + "Span",
    IrTypeKind.ReadOnlySpan => Readable(type.Element!, declared) + "ReadOnlySpan",
    _ => declared(type)
  };

  private sealed partial class Writer {

    /// <summary>The delegate types the class declares, by their signature, with their names.</summary>
    private readonly Dictionary<IrFunctionType, string> _delegates = [];

    /// <summary>The locals of the function being written that are written as local functions.</summary>
    private HashSet<IrLocal> _localFunctions = [];

    /// <summary>The signatures a public function or a member of a class names, whose delegate types are public.</summary>
    private HashSet<IrFunctionType>? _published;

    /// <summary>
    /// A function type as the profile writes it: <c>Action&lt;T&gt;</c>, <c>Action</c>, <c>Func</c> and
    /// <c>Action</c> of as many parameters as the framework has them for, else a delegate type the class
    /// declares, named from its signature (<c>Int32ToInt32</c>, <c>ToVoid</c>).
    /// </summary>
    private string FunctionTypeName(IrType type) {
      var signature = type.Signature!;
      var count = signature.Parameters.Count;
      var arguments = signature.Parameters.Select(this.TypeName).ToList();
      if (signature.Result == IrType.Void) {
        if (count == 0 && this._table.HasApi(CSharpFunctionRows.Action, this._profile))
          return Base + "Action";

        if ((count == 1 && this._table.HasApi(CSharpFunctionRows.ActionOfOne, this._profile))
            || (count <= 4 && this._table.HasApi(CSharpFunctionRows.ActionOfFour, this._profile))
            || (count <= 16 && this._table.HasApi(CSharpFunctionRows.ActionOfSixteen, this._profile)))
          return $"{Base}Action<{string.Join(", ", arguments)}>";
      } else if ((count <= 4 && this._table.HasApi(CSharpFunctionRows.FuncOfFour, this._profile)) || (count <= 16 && this._table.HasApi(CSharpFunctionRows.FuncOfSixteen, this._profile))) {
        return $"{Base}Func<{string.Join(", ", [.. arguments, this.TypeName(signature.Result)])}>";
      }

      var name = this.DelegateName(signature) + this.TypeArguments(signature);
      return this._inClass ? $"{this._class}.{name}" : name;
    }

    /// <summary>
    /// The type parameters of generic definitions a signature names, in the order it names them: a
    /// delegate type the class declares for it is generic over them, as the definition it stands in is.
    /// </summary>
    private List<IrTypeDeclaration> TypeParameters(IrFunctionType signature) {
      var found = new List<IrTypeDeclaration>();
      void Visit(IrType type) {
        if (type.Element is { } element)
          Visit(element);
        else if (type.Signature is { } nested)
          foreach (var part in nested.Parameters.Append(nested.Result))
            Visit(part);
        else if (type.Declaration is { } declared && this._generics.Parameters.ContainsKey(declared) && !found.Contains(declared))
          found.Add(declared);
      }

      foreach (var part in signature.Parameters.Append(signature.Result))
        Visit(part);

      return found;
    }

    /// <summary>The list of type parameters after the name of a generic delegate type, <c>&lt;T&gt;</c>; empty for one that is not generic.</summary>
    private string TypeArguments(IrFunctionType signature)
      => this.TypeParameters(signature) is { Count: > 0 } parameters ? $"<{string.Join(", ", parameters.Select(parameter => parameter.Name))}>" : "";

    /// <summary>The name of the delegate type the class declares for a signature, given the first time the signature is asked for.</summary>
    private string DelegateName(IrFunctionType signature) {
      if (this._delegates.TryGetValue(signature, out var known))
        return known;

      var name = string.Concat(signature.Parameters.Select(this.ReadableName)) + "To" + this.ReadableName(signature.Result);
      var taken = this._taken.Concat(this._typeNames.Values).Concat(this._delegates.Values).Select(word => word.TrimStart('@')).ToHashSet();
      var chosen = name;
      for (var number = 2; taken.Contains(chosen); ++number)
        chosen = name + number;

      return this._delegates[signature] = chosen;
    }

    private string ReadableName(IrType type)
      => Readable(type, named => named.Signature is { } signature ? "Of" + this.DelegateName(signature) : this.TypeName(named).TrimStart('@').Replace(".", "", System.StringComparison.Ordinal));

    /// <summary>
    /// The delegate types the class declares, in the order they were first named: public where a public
    /// function or a member of a class names the signature, private elsewhere.
    /// </summary>
    private List<CsMember> Delegates() {
      var published = this._published ??= this.Published();
      var members = new List<CsMember>();
      var inClass = this._inClass;
      this._inClass = false;
      for (var index = 0; index < this._delegates.Count; ++index) {
        var (signature, name) = this._delegates.ElementAt(index);
        var parameters = signature.Parameters.Select((parameter, position) => new CsParameter(new CsType(this.TypeName(parameter)), $"a{position + 1}")).ToList();
        members.Add(new CsDelegateDeclaration([published.Contains(signature) ? "public" : "private"], new CsType(this.TypeName(signature.Result)), name + this.TypeArguments(signature), parameters));
      }

      this._inClass = inClass;
      return members;
    }

    /// <summary>Every signature a public function or a member of a class names in its parameters or its result, also inside another signature or an array.</summary>
    private HashSet<IrFunctionType> Published() {
      var found = new HashSet<IrFunctionType>();
      void Visit(IrType type) {
        if (type.Element is { } element)
          Visit(element);

        if (type.Signature is not { } signature || !found.Add(signature))
          return;

        foreach (var part in signature.Parameters.Append(signature.Result))
          Visit(part);
      }

      foreach (var function in this._module.Functions.Where(function => function.IsPublic || this.IsMember(function)))
        foreach (var type in function.Parameters.Select(parameter => parameter.Type).Append(function.ReturnType))
          Visit(type);

      foreach (var field in this._module.Types.OfType<IrClassType>().SelectMany(type => type.Fields))
        Visit(field.Type);

      return found;
    }

    /// <summary>
    /// The locals of a body written as local functions where the language has them: each holds one
    /// lambda for good - declared with it, or declared without a value and assigned it once in the
    /// same block - and what the lambda captures is a parameter, a local declared with a value or
    /// another such local, which C# requires to be assigned wherever the function can be called.
    /// </summary>
    private HashSet<IrLocal> LocalFunctions(IrBlock body) {
      if (!this.Has(CSharpFeature.LocalFunction))
        return [];

      var statements = AllWithin(body).ToList();
      var definitions = statements.SelectMany(statement => statement switch {
        IrDeclare { Initializer: not null } declare => [declare.Local],
        IrAssign assign => [assign.Local],
        _ => (IEnumerable<IrLocal>)[]
      }).GroupBy(local => local).ToDictionary(group => group.Key, group => group.Count());
      var initialized = statements.OfType<IrDeclare>().Where(declare => declare.Initializer is not null).Select(declare => declare.Local).ToHashSet();
      var parameters = IrTree.Nodes(body).OfType<IrLambda>().SelectMany(lambda => lambda.Parameters).ToHashSet();
      var candidates = new Dictionary<IrLocal, IrLambda>();
      foreach (var block in statements.OfType<IrBlock>().Prepend(body))
        foreach (var statement in block.Statements) {
          if (statement is not IrDeclare { Local.Type.IsFunction: true } declare || definitions.GetValueOrDefault(declare.Local) != 1)
            continue;

          var lambda = declare.Initializer as IrLambda
            ?? (declare.Initializer is null ? block.Statements.OfType<IrAssign>().FirstOrDefault(assign => assign.Local == declare.Local)?.Value as IrLambda : null);
          if (lambda is not null)
            candidates[declare.Local] = lambda;
        }

      bool Admitted(IrLocal capture) => parameters.Contains(capture) || initialized.Contains(capture) || candidates.ContainsKey(capture) || !statements.OfType<IrDeclare>().Any(declare => declare.Local == capture);
      return [.. candidates.Where(candidate => candidate.Value.Captures.All(Admitted)).Select(candidate => candidate.Key)];
    }

    /// <summary>
    /// The statements of function values: a local function in place of its variable's declaration or
    /// assignment, a variable of a function type declared without a value as <c>null</c> - a recursive
    /// lambda reads it before C# would call it assigned - and a loop with variables of every pass.
    /// <see langword="false"/> for any other statement.
    /// </summary>
    private bool FunctionStatement(IrStmt statement, List<CsStatement> statements) {
      switch (statement) {
        case IrDeclare declare when this._localFunctions.Contains(declare.Local):
          var name = this.Declare(declare.Local);
          if (declare.Initializer is IrLambda lambda)
            statements.Add(this.LocalFunction(name, declare.Local, lambda));

          return true;
        case IrAssign { Value: IrLambda assigned } assign when this._localFunctions.Contains(assign.Local):
          statements.Add(this.LocalFunction(this.Local(assign.Local), assign.Local, assigned));
          return true;
        case IrDeclare { Initializer: null, Local.Type.IsFunction: true } declare:
          statements.Add(new CsLocalDeclaration(new CsType(this.TypeName(declare.Local.Type)), this.Declare(declare.Local), new CsNullLiteral { Type = declare.Local.Type }));
          return true;
        case IrFor { PerIteration.Count: > 0 } loop:
          statements.Add(this.PerIteration(loop, statements));
          return true;
        default:
          return false;
      }
    }

    /// <summary>A local function: <c>static</c> where the language has that and it captures nothing but itself; an expression body for one return.</summary>
    private CsLocalFunction LocalFunction(string name, IrLocal variable, IrLambda lambda) {
      var isStatic = lambda.Captures.All(capture => capture == variable) && this.Has(CSharpFeature.StaticLocalFunction);
      var (parameters, body, expression) = this.Inside(lambda, true, true);
      return new CsLocalFunction(isStatic ? ["static"] : [], new CsType(this.TypeName(lambda.ReturnType)), name, parameters, body, expression);
    }

    /// <summary>
    /// The parameters and the body of a lambda in a block of its own: an expression body for one return,
    /// and for nothing returned one effect, where <paramref name="arrow"/> admits it.
    /// </summary>
    private (List<CsParameter> Parameters, CsBlock? Body, CsExpression? Expression) Inside(IrLambda lambda, bool typed, bool arrow) {
      var scope = new Scope();
      this._scopes.Add(scope);
      var parameters = lambda.Parameters.Select(parameter => new CsParameter(new CsType(typed ? this.TypeName(parameter.Type) : ""), this.Declare(parameter))).ToList();
      CsBlock? body = null;
      CsExpression? expression = null;
      if (arrow && lambda.Body.Statements is [IrReturn { Value: { } value }])
        expression = this.Stored(value);
      else if (arrow && lambda.ReturnType == IrType.Void && lambda.Body.Statements is [IrAssign or IrGlobalAssign or IrStore or IrFieldStore or IrExprStmt])
        expression = this.Effect(lambda.Body.Statements[0]);
      else
        body = new CsBlock(this.Statements(lambda.Body));

      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(scope.Within);
      return (parameters, body, expression);
    }

    /// <summary>
    /// A lambda: an anonymous method with typed parameters and a block in C# 2, else a lambda whose
    /// parameters the delegate it converts to types, <c>static</c> where it captures nothing and the
    /// language has static lambdas.
    /// </summary>
    private (CsExpression Node, int Binding) Lambda(IrLambda lambda) {
      var anonymous = !this.Has(CSharpFeature.LambdaExpression);
      var (parameters, body, expression) = this.Inside(lambda, anonymous, !anonymous);
      return (new CsLambda(parameters, body, expression) {
        IsAnonymousMethod = anonymous,
        IsStatic = lambda.Captures.Count == 0 && this.Has(CSharpFeature.StaticAnonymousFunction),
        Type = lambda.Type
      }, Loose);
    }

    /// <summary>
    /// A function value where nothing gives it a delegate type - called at once, a branch of a
    /// conditional, compared with null: a lambda, a method group and a local function are cast to theirs.
    /// </summary>
    private CsExpression Typed(IrExpr value, int level) {
      if (value is not (IrLambda or IrFunctionRef or IrBoundMethod) && !(value is IrLocalRef reference && this._localFunctions.Contains(reference.Local)))
        return this.Operand(value, level);

      var cast = new CsCast(new CsType(this.TypeName(value.Type)), this.Operand(value, Cast)) { Type = value.Type };
      return level < Unary ? new CsParenthesized(cast) { Type = value.Type } : cast;
    }

    /// <summary>The nodes of function values: a lambda, a function or a bound method as a value, a call of a function value, and the conditionals and comparisons of them.</summary>
    private (CsExpression Node, int Binding)? FunctionNode(IrExpr expression) {
      switch (expression) {
        case IrLambda lambda:
          return this.Lambda(lambda);
        case IrFunctionRef reference:
          return (this.Qualified(this._members[reference.Function]) with { Type = reference.Type }, Primary);
        case IrBoundMethod bound:
          var owner = this.Owners.GetValueOrDefault(bound.Method.Function);
          var target = bound.Receiver is null ? new CsName(this._typeNames[owner!.Name]) : this.Operand(bound.Receiver, Primary);
          return (new CsMemberAccess(target, owner is null ? this.InterfaceMemberName(bound.Method) : this.MethodName(bound.Method, owner)) { Type = bound.Type }, Primary);
        case IrInvoke { Callee: IrFunctionRef reference } invocation:
          return (new CsInvocation(this.Qualified(this._members[reference.Function]), [.. invocation.Arguments.Select(this.Stored)]) { Type = invocation.Type }, Primary);
        case IrInvoke invocation:
          // A local function is called as it stands; any other function value that no type is given is cast to its own.
          var callee = invocation.Callee is IrLocalRef local && this._localFunctions.Contains(local.Local) ? this.Operand(local, Primary) : this.Typed(invocation.Callee, Primary);
          return (new CsInvocation(callee, [.. invocation.Arguments.Select(this.Stored)]) { Type = invocation.Type }, Primary);
        case IrConditional { Type.IsFunction: true } choice:
          var condition = this.Operand(choice.Condition, Unary);
          var whenTrue = this.Typed(choice.WhenTrue, Unary);
          return (new CsConditional(condition, whenTrue, this.Typed(choice.WhenFalse, Unary)), Loose);
        case IrReferenceEquals { Left.Type.IsFunction: true } identity:
          var left = this.Typed(identity.Left, Unary);
          return (new CsBinary(left, "==", this.Typed(identity.Right, Unary)), Loose);
        default:
          return null;
      }
    }

    /// <summary>
    /// A loop whose variables get a variable of their own every pass. Each pass copies them into
    /// variables of the body, which the closures of the pass capture, and copies back what the body
    /// wrote before the step - in a <c>finally</c>, which a <c>continue</c>, a <c>break</c> and a
    /// <c>return</c> run too. The step is the third part of a <c>for</c>, after the copy; the
    /// temporaries it declares are declared in front of the loop.
    /// </summary>
    private CsFor PerIteration(IrFor loop, List<CsStatement> statements) {
      var incrementors = new List<CsExpression>();
      foreach (var step in loop.Step.Statements)
        if (step is IrDeclare declare) {
          var temporary = this.Declare(declare.Local);
          statements.Add(new CsLocalDeclaration(new CsType(this.TypeName(declare.Local.Type)), temporary, null));
          if (declare.Initializer is not null)
            incrementors.Add(new CsAssignment(new CsName(temporary) { Type = declare.Local.Type }, "=", this.Stored(declare.Initializer)));
        } else {
          incrementors.Add(this.Effect(step));
        }

      var condition = this.Text(loop.Condition);
      var outer = loop.PerIteration.ToDictionary(variable => variable, this.Variable);
      var scope = new Scope();
      this._scopes.Add(scope);
      var copies = new List<CsStatement>();
      var back = new List<CsStatement>();
      foreach (var variable in loop.PerIteration) {
        var inner = this.Declare(variable);
        copies.Add(new CsLocalDeclaration(new CsType(this.TypeName(variable.Type)), inner, outer[variable]));
        if (Writes(loop.Body, variable))
          back.Add(new CsExpressionStatement(new CsAssignment(outer[variable], "=", new CsName(inner) { Type = variable.Type })));
      }

      var body = this.Body(loop.Body, null);
      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(scope.Within);
      List<CsStatement> pass = back.Count == 0 ? [.. copies, .. body] : [.. copies, new CsTryFinally(new CsBlock(body), new CsBlock(back))];
      return new CsFor(null, condition, incrementors, new CsBlock(pass));
    }

  }

}
