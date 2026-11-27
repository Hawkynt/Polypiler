using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Checking;

internal sealed partial class GoChecker {

  /// <summary>What the checker knows of the function whose body it checks.</summary>
  private sealed class FunctionContext(GoSignature signature, IReadOnlyList<GoObject> results, bool named) {

    public GoSignature Signature { get; } = signature;

    public IReadOnlyList<GoObject> Results { get; } = results;

    public bool NamedResults { get; } = named;

    public List<GoObject> Locals { get; } = [];

  }

  /// <summary>Where a statement is: inside a loop, a switch, the last statement of a case.</summary>
  [System.Flags]
  private enum StatementContext {
    None = 0,
    BreakOk = 1,
    ContinueOk = 2,
    FallthroughOk = 4,
    FinalSwitchCase = 8
  }

  private void FunctionBody(GoNode declaration, GoObject? @object) {
    var method = @object is null ? this._methods[declaration] : null;
    var target = @object ?? method!;
    if (declaration.Node("Body") is not { } body)
      return;

    this.Resolve(target);
    var scope = new GoScope(this._file, "function");
    if (declaration.Node("Recv") is { } receivers) {
      var field = receivers.Nodes("List")[0];
      var type = this.TypeOf(field.Node("Type")!);
      foreach (var name in field.Nodes("Names"))
        this.Declare(scope, name, GoObjectKind.Var, type, item => item.Used = true);
    }

    var (savedScope, savedDeclaration) = (this._scope, this._declaration);
    this._scope = scope;
    this._declaration = target;
    var signature = this.Signature(declaration.Node("Type")!, scope);
    declaration.Node("Type")!.TypeOfNode = signature;
    this.Body(signature, scope, declaration.Node("Type")!, body);
    (this._scope, this._declaration) = (savedScope, savedDeclaration);
  }

  /// <summary>Checks the body of a function or function literal whose parameters are declared in <paramref name="scope"/>.</summary>
  private void Body(GoSignature signature, GoScope scope, GoNode type, GoNode body) {
    var results = new List<GoObject>();
    var named = false;
    foreach (var field in type.Node("Results")?.Nodes("List") ?? []) {
      if (field.Nodes("Names").Count == 0) {
        results.Add(new GoObject(GoObjectKind.Var, "", field.Node("Type")!.TypeOfNode ?? signature.Results[results.Count].Type));
        continue;
      }

      foreach (var name in field.Nodes("Names")) {
        results.Add(name.Target!);
        named = true;
      }
    }

    var (savedScope, savedFunction, savedIota) = (this._scope, this._function, this._iota);
    this._scope = scope;
    this._function = new FunctionContext(signature, results, named);
    this._iota = null;
    var function = this._function;
    this.StatementList(body.Nodes("List"), StatementContext.None);
    if (signature.Results.Count > 0 && !IsTerminating(body, ""))
      throw Error(body, "missing return");

    this.Labels(body);
    (this._scope, this._function, this._iota) = (savedScope, savedFunction, savedIota);
    foreach (var local in function.Locals.OrderBy(local => (local.Line, local.Column)))
      if (!local.Used)
        throw Error(local.Declaration, $"declared and not used: {local.Name}");
  }

  private void StatementList(IReadOnlyList<GoNode> list, StatementContext context) {
    var fallthroughOk = context.HasFlag(StatementContext.FallthroughOk);
    var inner = context & ~StatementContext.FallthroughOk;
    var last = list.Count - 1;
    while (last >= 0 && list[last].Is("EmptyStmt"))
      --last;

    for (var index = 0; index < list.Count; ++index)
      this.Statement(list[index], fallthroughOk && index == last ? inner | StatementContext.FallthroughOk : inner);
  }

  private void OpenScope(string kind) => this._scope = new GoScope(this._scope, kind);

  private void CloseScope() => this._scope = this._scope.Parent!;

  private void Statement(GoNode node, StatementContext context) {
    var inner = context & ~(StatementContext.FallthroughOk | StatementContext.FinalSwitchCase);
    switch (node.Kind) {
      case "EmptyStmt":
        return;
      case "DeclStmt":
        this.LocalDeclaration(node.Node("Decl")!);
        return;
      case "LabeledStmt":
        this.Statement(node.Node("Stmt")!, context);
        return;
      case "ExprStmt":
        this.ExpressionStatement(node);
        return;
      case "SendStmt":
        throw Unsupported(node, "channel send");
      case "IncDecStmt": {
        var target = node.Node("X")!;
        var x = this.Expression(target);
        if (!GoTypes.IsNumeric(x.Type))
          throw Error(target, $"invalid operation: {GoNodeText.Of(target)}{node.Text("Tok")} (non-numeric type {x.Type})");

        var one = new GoNode("BasicLit", target.Offset, target.Line, target.Column).With("Kind", "INT").With("Value", "1");
        var result = this.BinaryOperands(null, target, one, node.Text("Tok") == "++" ? "+" : "-", x);
        this.AssignVariable(target, null, result, "assignment");
        return;
      }
      case "AssignStmt":
        this.AssignStatement(node);
        return;
      case "GoStmt" or "DeferStmt":
        this.SuspendedCall(node.Is("GoStmt") ? "go" : "defer", node.Node("Call")!);
        return;
      case "ReturnStmt":
        this.Return(node);
        return;
      case "BranchStmt":
        Branch(node, context);
        return;
      case "BlockStmt":
        this.OpenScope("block");
        this.StatementList(node.Nodes("List"), inner);
        this.CloseScope();
        return;
      case "IfStmt":
        this.If(node, inner);
        return;
      case "SwitchStmt":
        this.Switch(node, inner | StatementContext.BreakOk);
        return;
      case "TypeSwitchStmt":
        throw Unsupported(node, "type switch");
      case "SelectStmt":
        throw Unsupported(node, "select");
      case "ForStmt":
        this.For(node, inner | StatementContext.BreakOk | StatementContext.ContinueOk);
        return;
      default:
        this.Range(node, inner | StatementContext.BreakOk | StatementContext.ContinueOk);
        return;
    }
  }

  // ---------------------------------------------------------------- simple statements

  /// <summary>What a call is as a statement: a conversion, an expression whose value is dropped, or a statement.</summary>
  private static string CallKind(GoNode call) {
    var callee = Unparen(call.Node("Fun")!);
    if (callee.Mode == "type")
      return "conversion";

    if (callee.Mode == "builtin")
      return callee.Text("Name") is "copy" or "delete" or "panic" or "print" or "println" or "clear" or "close" or "recover" ? "statement" : "expression";

    return "statement";
  }

  private void ExpressionStatement(GoNode node) {
    var expression = node.Node("X")!;
    var x = this.Raw(expression, null);
    if (x.Mode == GoMode.Builtin)
      throw Error(expression, $"{x} must be called");

    if (x.Mode == GoMode.TypeExpr)
      throw Error(expression, $"{x} is not an expression");

    if (Unparen(expression) is { Kind: "CallExpr" } call && CallKind(call) == "statement")
      return;

    throw Error(expression, $"{x} is not used");
  }

  private void SuspendedCall(string keyword, GoNode call) {
    var x = this.Raw(call, null);
    switch (CallKind(call)) {
      case "conversion":
        throw Error(call, $"{keyword} requires function call, not conversion");
      case "expression":
        throw Error(call, $"{keyword} discards result of {x}");
    }
  }

  /// <summary>
  /// A left-hand side of an assignment: its type, or <see langword="null"/> for the blank identifier
  /// (<c>lhsVar</c>). Assigning to a variable is no use of it.
  /// </summary>
  private GoType? LeftHandSide(GoNode lhs) {
    var identifier = Unparen(lhs);
    if (identifier.Is("Ident") && identifier.Text("Name") == "_")
      return null;

    GoObject? variable = null;
    var wasUsed = false;
    if (identifier.Is("Ident") && this.Lookup(identifier) is { Kind: GoObjectKind.Var } found) {
      variable = found;
      wasUsed = found.Used;
    }

    var x = this.Expression(lhs);
    if (variable is not null)
      variable.Used = wasUsed;

    if (x.Mode is GoMode.Variable or GoMode.MapIndex)
      return x.Type;

    if (Unparen(lhs) is { Kind: "SelectorExpr" } selector && Unparen(selector.Node("X")!) is { Kind: "IndexExpr" } index && index.Node("X")!.TypeOfNode?.Underlying is GoMap)
      throw Error(lhs, $"cannot assign to struct field {GoNodeText.Of(lhs)} in map");

    throw Error(lhs, $"cannot assign to {GoNodeText.Of(lhs)} (neither addressable nor a map index expression)");
  }

  private void AssignVariable(GoNode lhs, GoNode? rhs, GoOperand? x, string context) {
    var type = this.LeftHandSide(lhs);
    x ??= this.Expression(rhs!, type);
    if (type is null && context == "assignment")
      context = "assignment to _ identifier";

    this.Assignment(x, type, context);
  }

  private void AssignStatement(GoNode node) {
    var token = node.Text("Tok")!;
    var lhs = node.Nodes("Lhs");
    var rhs = node.Nodes("Rhs");
    switch (token) {
      case ":=":
        this.ShortVariableDeclaration(node, lhs, rhs);
        return;
      case "=":
        this.AssignVariables(lhs, rhs);
        return;
      default: {
        if (lhs.Count != 1 || rhs.Count != 1)
          throw Error(node, $"assignment operation {token} requires single-valued expressions");

        var result = this.BinaryOperands(null, lhs[0], rhs[0], token[..^1], null);
        this.AssignVariable(lhs[0], null, result, "assignment operation");
        return;
      }
    }
  }

  private void AssignVariables(IReadOnlyList<GoNode> lhs, IReadOnlyList<GoNode> rhs) {
    var isCall = rhs.Count == 1 && Unparen(rhs[0]).Is("CallExpr");
    if (lhs.Count == rhs.Count && !isCall) {
      for (var index = 0; index < lhs.Count; ++index)
        this.AssignVariable(lhs[index], rhs[index], null, "assignment");

      return;
    }

    if (rhs.Count != 1)
      throw Error(rhs[0], $"assignment mismatch: {Count(lhs.Count, "variable")} but {Count(rhs.Count, "value")}");

    var values = this.MultiExpression(rhs[0], lhs.Count == 2);
    if (values.Count != lhs.Count)
      throw Error(rhs[0], AssignmentMismatch(lhs.Count, rhs[0], values.Count));

    for (var index = 0; index < lhs.Count; ++index)
      this.AssignVariable(lhs[index], null, values[index], "assignment");
  }

  private static string AssignmentMismatch(int variables, GoNode value, int values)
    => Unparen(value).Is("CallExpr")
      ? $"assignment mismatch: {Count(variables, "variable")} but {GoNodeText.Of(value)} returns {Count(values, "value")}"
      : $"assignment mismatch: {Count(variables, "variable")} but {Count(values, "value")}";

  /// <summary>The values of an expression that may give several: a call with several results, or with <paramref name="commaOk"/> a map index and its found flag.</summary>
  private List<GoOperand> MultiExpression(GoNode node, bool commaOk) {
    var x = this.Raw(node, null);
    Exclude(x);
    if (x.Type is GoTuple tuple)
      return [.. tuple.Types.Select(type => new GoOperand(GoMode.Value, type, node))];

    if (commaOk && x.Mode is GoMode.MapIndex or GoMode.CommaOk) {
      x.Mode = GoMode.Value;
      return [x, new GoOperand(GoMode.Value, GoTypes.UntypedBool, node)];
    }

    return [x];
  }

  private void ShortVariableDeclaration(GoNode node, IReadOnlyList<GoNode> lhs, IReadOnlyList<GoNode> rhs) {
    var seen = new HashSet<string>(System.StringComparer.Ordinal);
    var variables = new List<GoObject>();
    var created = new List<GoObject>();
    foreach (var target in lhs) {
      if (!target.Is("Ident"))
        throw Error(target, $"non-name {GoNodeText.Of(target)} on left side of :=");

      var name = target.Text("Name")!;
      if (name != "_" && !seen.Add(name))
        throw Error(target, $"{name} repeated on left side of :=");

      if (name != "_" && this._scope.Local(name) is { } existing) {
        if (existing.Kind != GoObjectKind.Var)
          throw Error(target, $"cannot assign to {name}");

        target.Target = existing;
        target.TypeOfNode = existing.Type;
        variables.Add(existing);
        continue;
      }

      var @object = new GoObject(GoObjectKind.Var, name, null, target.Line, target.Column) { Declaration = target };
      target.Target = @object;
      variables.Add(@object);
      if (name != "_")
        created.Add(@object);
    }

    this.InitVariables(variables, rhs, null);
    foreach (var (target, variable) in lhs.Zip(variables))
      target.TypeOfNode = variable.Type;

    if (created.Count == 0)
      throw Error(node, "no new variables on left side of :=");

    foreach (var @object in created) {
      this._scope.Insert(@object);
      this._function?.Locals.Add(@object);
    }
  }

  /// <summary>Gives a variable without a type the type of its value, an untyped one's default type, and checks the assignment (<c>initVar</c>).</summary>
  private void InitVariable(GoObject variable, GoOperand x, string context) {
    if (variable.Type is null) {
      Exclude(x);
      SingleValue(x);
      if (x.IsNil)
        throw Error(x.Expr, $"use of untyped nil in {context}");

      variable.Type = GoTypes.Default(x.Type);
    }

    this.Assignment(x, variable.Type, context);
    if (variable.Declaration is { Kind: "Ident" } identifier)
      identifier.TypeOfNode = variable.Type;
  }

  /// <summary>Initializes variables from values: one value each, or the results of one call or comma-ok expression (<c>initVars</c>).</summary>
  private void InitVariables(IReadOnlyList<GoObject> variables, IReadOnlyList<GoNode> values, GoNode? returnStatement) {
    var context = returnStatement is null ? "assignment" : "return statement";
    var isCall = values.Count == 1 && Unparen(values[0]).Is("CallExpr");
    if (variables.Count == values.Count && !isCall) {
      for (var index = 0; index < variables.Count; ++index) {
        var x = this.Expression(values[index], variables[index].Type);
        this.InitVariable(variables[index], x, context);
      }

      return;
    }

    if (values.Count != 1) {
      if (returnStatement is not null)
        throw Error(returnStatement, values.Count > variables.Count ? "too many return values" : "not enough return values");

      throw Error(values[0], $"assignment mismatch: {Count(variables.Count, "variable")} but {Count(values.Count, "value")}");
    }

    var operands = this.MultiExpression(values[0], variables.Count == 2 && returnStatement is null);
    if (operands.Count != variables.Count) {
      if (returnStatement is not null)
        throw Error(returnStatement, operands.Count > variables.Count ? "too many return values" : "not enough return values");

      throw Error(values[0], AssignmentMismatch(variables.Count, values[0], operands.Count));
    }

    for (var index = 0; index < variables.Count; ++index)
      this.InitVariable(variables[index], operands[index], context);
  }

  private void Return(GoNode node) {
    var function = this._function!;
    var results = node.Nodes("Results");
    if (results.Count == 0 && function.Results.Count > 0 && function.NamedResults) {
      foreach (var result in function.Results)
        if (result.Name != "_" && this._scope.Lookup(result.Name)?.Object is { } found && found != result)
          throw Error(node, $"result parameter {result.Name} not in scope at return");

      return;
    }

    if (results.Count > 0 && function.Results.Count == 0)
      throw Error(results[0], "too many return values");

    var targets = function.Results.Select(result => new GoObject(GoObjectKind.Var, result.Name, result.Type)).ToList();
    this.InitVariables(targets, results, node);
  }

  private static void Branch(GoNode node, StatementContext context) {
    if (node.Node("Label") is not null)
      return;

    switch (node.Text("Tok")) {
      case "break" when !context.HasFlag(StatementContext.BreakOk):
        throw Error(node, "break is not in a loop, switch, or select");
      case "continue" when !context.HasFlag(StatementContext.ContinueOk):
        throw Error(node, "continue is not in a loop");
      case "fallthrough" when !context.HasFlag(StatementContext.FallthroughOk):
        throw Error(node, context.HasFlag(StatementContext.FinalSwitchCase) ? "cannot fallthrough final case in switch" : "fallthrough statement out of place");
    }
  }

  // ---------------------------------------------------------------- declarations in a function

  private void LocalDeclaration(GoNode declaration) {
    switch (declaration.Text("Tok")) {
      case "const": {
        GoNode? type = null;
        IReadOnlyList<GoNode> values = [];
        foreach (var spec in declaration.Nodes("Specs")) {
          if (spec.Node("Type") is not null || spec.Nodes("Values").Count > 0) {
            type = spec.Node("Type");
            values = spec.Nodes("Values");
          }

          var names = spec.Nodes("Names");
          CheckConstantArity(names, values);
          var saved = this._iota;
          this._iota = GoConstant.MakeInt(int.Parse(spec.Text("iota")!, System.Globalization.CultureInfo.InvariantCulture));
          var objects = new List<GoObject>();
          for (var index = 0; index < names.Count; ++index) {
            var @object = new GoObject(GoObjectKind.Const, names[index].Text("Name")!, null, names[index].Line, names[index].Column) { Declaration = names[index] };
            names[index].Target = @object;
            this.ConstantDeclaration(@object, type, values[index]);
            objects.Add(@object);
          }

          this._iota = saved;
          foreach (var @object in objects.Where(@object => @object.Name != "_"))
            if (this._scope.Insert(@object) is not null)
              throw Error(@object.Declaration, $"{@object.Name} redeclared in this block");
        }

        return;
      }
      case "var":
        foreach (var spec in declaration.Nodes("Specs"))
          this.LocalVariables(spec);

        return;
      default:
        foreach (var spec in declaration.Nodes("Specs")) {
          var name = spec.Node("Name")!;
          if (spec.Node("TypeParams") is not null)
            throw Unsupported(spec, $"generic type {name.Text("Name")}");

          if (spec.Flag("alias")) {
            var aliased = this.TypeOf(spec.Node("Type")!);
            this.Declare(this._scope, name, GoObjectKind.TypeName, aliased);
            continue;
          }

          var named = new GoNamed(name.Text("Name")!, null);
          var @object = this.Declare(this._scope, name, GoObjectKind.TypeName, named);
          this.TypeDeclaration(@object, spec);
        }

        return;
    }
  }

  private void LocalVariables(GoNode spec) {
    var names = spec.Nodes("Names");
    var values = spec.Nodes("Values");
    var type = spec.Node("Type") is { } typeNode ? this.TypeOf(typeNode) : null;
    var objects = names.Select(name => new GoObject(GoObjectKind.Var, name.Text("Name")!, type, name.Line, name.Column) { Declaration = name }).ToList();
    foreach (var (name, @object) in names.Zip(objects)) {
      name.Target = @object;
      name.TypeOfNode = type;
    }

    if (values.Count > 0) {
      if (values.Count > 1 && values.Count != names.Count)
        throw Error(values.Count > names.Count ? values[names.Count] : names[values.Count], values.Count > names.Count ? "extra init expr" : $"assignment mismatch: {Count(names.Count, "variable")} but {Count(values.Count, "value")}");

      this.InitVariables(objects, values, null);
    }

    foreach (var @object in objects.Where(@object => @object.Name != "_")) {
      if (this._scope.Insert(@object) is not null)
        throw Error(@object.Declaration, $"{@object.Name} redeclared in this block");

      this._function?.Locals.Add(@object);
    }
  }

  // ---------------------------------------------------------------- compound statements

  private void SimpleStatement(GoNode? node) {
    if (node is not null)
      this.Statement(node, StatementContext.None);
  }

  private void Condition(GoNode condition, string statement) {
    var x = this.Expression(condition);
    if (!GoTypes.IsBoolean(x.Type))
      throw Error(condition, $"non-boolean condition in {statement} statement");
  }

  private void If(GoNode node, StatementContext context) {
    this.OpenScope("if");
    this.SimpleStatement(node.Node("Init"));
    this.Condition(node.Node("Cond")!, "if");
    this.Statement(node.Node("Body")!, context);
    if (node.Node("Else") is { } otherwise)
      this.Statement(otherwise, context);

    this.CloseScope();
  }

  private void Switch(GoNode node, StatementContext context) {
    this.OpenScope("switch");
    this.SimpleStatement(node.Node("Init"));
    GoOperand tag;
    if (node.Node("Tag") is { } tagNode) {
      tag = this.Expression(tagNode);
      this.Assignment(tag, null, "switch expression");
      if (!GoTypes.Comparable(tag.Type) && !GoTypes.HasNil(tag.Type))
        throw Error(tagNode, $"cannot switch on {tag} ({tag.Type} is not comparable)");
    } else {
      var implicitTrue = new GoNode("Ident", node.Offset, node.Line, node.Column).With("Name", "true");
      tag = new GoOperand(GoMode.Constant, GoTypes.Bool, implicitTrue, GoConstant.MakeBool(true));
    }

    var clauses = node.Node("Body")!.Nodes("List");
    if (clauses.Count(clause => clause.Nodes("List").Count == 0) > 1)
      throw Error(clauses.Where(clause => clause.Nodes("List").Count == 0).ElementAt(1), "multiple defaults in switch");

    var seen = new List<(GoConstant Value, GoType Type)>();
    for (var index = 0; index < clauses.Count; ++index) {
      var clause = clauses[index];
      foreach (var value in clause.Nodes("List")) {
        var v = this.Expression(value);
        this.ConvertUntyped(v, tag.Type);
        var copy = new GoOperand(v.Mode, v.Type, v.Expr, v.Value);
        var comparisonNode = new GoNode("BinaryExpr", value.Offset, value.Line, value.Column).With("X", value).With("Op", "==").With("Y", tag.Expr);
        this.Comparison(copy, tag, comparisonNode, "==");
        if (v.Mode != GoMode.Constant)
          continue;

        if (seen.Any(entry => GoTypes.Identical(entry.Type, v.Type) && GoConstant.Compare(entry.Value, "==", v.Value!)))
          throw Error(value, $"duplicate case {v} in expression switch");

        seen.Add((v.Value!, v.Type));
      }

      this.OpenScope("case");
      this.StatementList(clause.Nodes("Body"), context | (index + 1 < clauses.Count ? StatementContext.FallthroughOk : StatementContext.FinalSwitchCase));
      this.CloseScope();
    }

    this.CloseScope();
  }

  private void For(GoNode node, StatementContext context) {
    this.OpenScope("for");
    this.SimpleStatement(node.Node("Init"));
    if (node.Node("Cond") is { } condition)
      this.Condition(condition, "for");

    if (node.Node("Post") is { } post) {
      if (post.Is("AssignStmt") && post.Text("Tok") == ":=")
        throw Error(post, "cannot declare in post statement of for loop");

      this.SimpleStatement(post);
    }

    this.Statement(node.Node("Body")!, context);
    this.CloseScope();
  }

  private void Range(GoNode node, StatementContext context) {
    this.OpenScope("range");
    var over = node.Node("X")!;
    var x = this.Expression(over);
    GoType? key, value;
    switch (x.Type.Underlying) {
      case GoBasic { IsString: true }:
        (key, value) = (GoTypes.Int, GoTypes.Rune);
        break;
      case GoBasic { IsInteger: true }:
        if (this._minor < 22)
          throw Error(over, $"cannot range over {x}: requires go1.22 or later");

        (key, value) = (x.Type, null);
        break;
      case GoArray array:
        (key, value) = (GoTypes.Int, array.Element);
        break;
      case GoPointer { Element.Underlying: GoArray array }:
        (key, value) = (GoTypes.Int, array.Element);
        break;
      case GoSlice slice:
        (key, value) = (GoTypes.Int, slice.Element);
        break;
      case GoMap map:
        (key, value) = (map.Key, map.Element);
        break;
      case GoSignature:
        throw Unsupported(node, "range over a function");
      default:
        throw Error(over, $"cannot range over {x}");
    }

    var keyNode = node.Node("Key");
    var valueNode = node.Node("Value");
    if (valueNode is not null && value is null)
      throw Error(valueNode, $"range over {x} permits only one iteration variable");

    var rangeOverInt = GoTypes.IsInteger(x.Type);
    if (node.Text("Tok") == ":=") {
      var created = new List<GoObject>();
      foreach (var (target, type) in new[] { (keyNode, key), (valueNode, value) }) {
        if (target is null)
          continue;

        if (!target.Is("Ident"))
          throw Error(target, $"cannot declare {GoNodeText.Of(target)}");

        var name = target.Text("Name")!;
        var @object = new GoObject(GoObjectKind.Var, name, null, target.Line, target.Column) { Declaration = target };
        target.Target = @object;
        if (name != "_")
          created.Add(@object);

        this.InitVariable(@object, rangeOverInt ? x : new GoOperand(GoMode.Value, type!, target), rangeOverInt ? "range clause" : "assignment");
      }

      if (created.Count == 0)
        throw Error(node, "no new variables on left side of :=");

      foreach (var @object in created) {
        if (this._scope.Insert(@object) is not null)
          throw Error(@object.Declaration, $"{@object.Name} repeated on left side of :=");

        this._function?.Locals.Add(@object);
      }
    } else if (keyNode is not null) {
      foreach (var (target, type) in new[] { (keyNode, key), (valueNode, value) }) {
        if (target is null)
          continue;

        if (rangeOverInt) {
          this.AssignVariable(target, null, x, "range clause");
          if (!GoTypes.IsInteger(x.Type))
            throw Error(target, $"cannot use iteration variable of type {x.Type}");
        } else {
          this.AssignVariable(target, null, new GoOperand(GoMode.Value, type!, target), "assignment");
        }
      }
    } else if (rangeOverInt) {
      this.Assignment(x, null, "range clause");
    }

    this.Statement(node.Node("Body")!, context);
    this.CloseScope();
  }

  // ---------------------------------------------------------------- terminating statements

  private static bool IsTerminating(GoNode statement, string label) {
    switch (statement.Kind) {
      case "LabeledStmt":
        return IsTerminating(statement.Node("Stmt")!, statement.Node("Label")!.Text("Name")!);
      case "ExprStmt":
        return Unparen(statement.Node("X")!) is { Kind: "CallExpr" } call && Unparen(call.Node("Fun")!) is { Kind: "Ident", Mode: "builtin" } callee && callee.Text("Name") == "panic";
      case "ReturnStmt":
        return true;
      case "BranchStmt":
        return statement.Text("Tok") is "goto" or "fallthrough";
      case "BlockStmt":
        return IsTerminatingList(statement.Nodes("List"), "");
      case "IfStmt":
        return statement.Node("Else") is { } otherwise && IsTerminating(statement.Node("Body")!, "") && IsTerminating(otherwise, "");
      case "SwitchStmt": {
        var hasDefault = false;
        foreach (var clause in statement.Node("Body")!.Nodes("List")) {
          if (clause.Nodes("List").Count == 0)
            hasDefault = true;

          if (!IsTerminatingList(clause.Nodes("Body"), "") || HasBreakList(clause.Nodes("Body"), label, true))
            return false;
        }

        return hasDefault;
      }
      case "ForStmt":
        return statement.Node("Cond") is null && !HasBreak(statement.Node("Body")!, label, true);
      default:
        return false;
    }
  }

  private static bool IsTerminatingList(IReadOnlyList<GoNode> list, string label) {
    for (var index = list.Count - 1; index >= 0; --index)
      if (!list[index].Is("EmptyStmt"))
        return IsTerminating(list[index], label);

    return false;
  }

  private static bool HasBreakList(IReadOnlyList<GoNode> list, string label, bool implicitly) => list.Any(statement => HasBreak(statement, label, implicitly));

  private static bool HasBreak(GoNode statement, string label, bool implicitly) {
    switch (statement.Kind) {
      case "LabeledStmt":
        return HasBreak(statement.Node("Stmt")!, label, implicitly);
      case "BranchStmt":
        return statement.Text("Tok") == "break" && (statement.Node("Label") is not { } target ? implicitly : target.Text("Name") == label);
      case "BlockStmt":
        return HasBreakList(statement.Nodes("List"), label, implicitly);
      case "IfStmt":
        return HasBreak(statement.Node("Body")!, label, implicitly) || statement.Node("Else") is { } otherwise && HasBreak(otherwise, label, implicitly);
      case "CaseClause":
        return HasBreakList(statement.Nodes("Body"), label, implicitly);
      case "SwitchStmt" or "ForStmt" or "RangeStmt":
        return label.Length > 0 && HasBreak(statement.Node("Body")!, label, false);
      default:
        return false;
    }
  }

  // ---------------------------------------------------------------- labels

  private sealed class LabelInfo(GoNode statement) {

    public GoNode Statement { get; } = statement;

    public bool Used { get; set; }

  }

  private sealed class PendingJump(GoNode branch) {

    public GoNode Branch { get; } = branch;

    public int JumpsOver { get; set; }

  }

  /// <summary>The labels of a function body (<c>labels</c>): declared once, used, and the targets of <c>break</c>, <c>continue</c> and <c>goto</c> valid.</summary>
  private void Labels(GoNode body) {
    if (!HasLabels(body))
      return;

    var all = new Dictionary<string, LabelInfo>(System.StringComparer.Ordinal);
    CollectLabels(body, all);
    var unresolved = BlockBranches(body.Nodes("List"), [], [], all);
    foreach (var jump in unresolved) {
      var name = jump.Branch.Node("Label")!.Text("Name")!;
      throw Error(jump.Branch.Node("Label"), all.ContainsKey(name) ? $"goto {name} jumps into block" : $"label {name} not declared");
    }

    foreach (var (name, label) in all)
      if (!label.Used && name != "_")
        throw Error(label.Statement.Node("Label"), $"label {name} declared and not used");
  }

  private static bool HasLabels(GoNode node) => node.Kind switch {
    "LabeledStmt" => true,
    "BranchStmt" => node.Node("Label") is not null,
    _ => Children(node).Any(HasLabels)
  };

  /// <summary>The statements nested directly in a statement, closures left out.</summary>
  private static IEnumerable<GoNode> Children(GoNode node) => node.Kind switch {
    "BlockStmt" => node.Nodes("List"),
    "LabeledStmt" => [node.Node("Stmt")!],
    "IfStmt" => new[] { node.Node("Body"), node.Node("Else") }.OfType<GoNode>(),
    "SwitchStmt" => node.Node("Body")!.Nodes("List"),
    "CaseClause" => node.Nodes("Body"),
    "ForStmt" or "RangeStmt" => [node.Node("Body")!],
    _ => []
  };

  private static void CollectLabels(GoNode node, Dictionary<string, LabelInfo> all) {
    if (node.Is("LabeledStmt")) {
      var label = node.Node("Label")!;
      var name = label.Text("Name")!;
      if (name != "_" && !all.TryAdd(name, new LabelInfo(node)))
        throw Error(label, $"label {name} already declared");
    }

    foreach (var child in Children(node))
      CollectLabels(child, all);
  }

  /// <summary>Checks the branches of one block; the forward <c>goto</c>s it could not resolve.</summary>
  private static List<PendingJump> BlockBranches(IReadOnlyList<GoNode> statements, List<(string Name, GoNode Statement)> enclosing, List<HashSet<string>> visible, Dictionary<string, LabelInfo> all) {
    var declared = new HashSet<string>(System.StringComparer.Ordinal);
    var blocks = new List<HashSet<string>>(visible) { declared };
    var pending = new List<PendingJump>();

    // A declaration inside the inner block is no declaration a jump out of it skips.
    void Block(IReadOnlyList<GoNode> list, List<(string, GoNode)> labels) {
      foreach (var jump in BlockBranches(list, labels, blocks, all)) {
        jump.JumpsOver = 0;
        pending.Add(jump);
      }
    }

    void Visit(GoNode statement, List<(string Name, GoNode Statement)> labels) {
      switch (statement.Kind) {
        case "DeclStmt" when statement.Node("Decl")!.Text("Tok") == "var":
        case "AssignStmt" when statement.Text("Tok") == ":=":
          foreach (var jump in pending)
            jump.JumpsOver = statement.Line;

          break;
        case "LabeledStmt": {
          var name = statement.Node("Label")!.Text("Name")!;
          if (name != "_") {
            declared.Add(name);
            foreach (var jump in pending.Where(jump => jump.Branch.Node("Label")!.Text("Name") == name).ToList()) {
              if (jump.JumpsOver > 0)
                throw Error(jump.Branch.Node("Label"), $"goto {name} jumps over variable declaration at line {jump.JumpsOver}");

              all[name].Used = true;
              pending.Remove(jump);
            }
          }

          Visit(statement.Node("Stmt")!, [.. labels, (name, statement.Node("Stmt")!)]);
          break;
        }
        case "BranchStmt" when statement.Node("Label") is { } label: {
          var name = label.Text("Name")!;
          switch (statement.Text("Tok")) {
            case "break":
              if (!labels.Concat(enclosing).Any(target => target.Name == name && target.Statement.Kind is "SwitchStmt" or "ForStmt" or "RangeStmt"))
                throw Error(label, $"invalid break label {name}");

              break;
            case "continue":
              if (!labels.Concat(enclosing).Any(target => target.Name == name && target.Statement.Kind is "ForStmt" or "RangeStmt"))
                throw Error(label, $"invalid continue label {name}");

              break;
            default:
              if (!blocks.Any(block => block.Contains(name))) {
                pending.Add(new PendingJump(statement));
                return;
              }

              break;
          }

          all[name].Used = true;
          break;
        }
        case "BlockStmt":
          Block(statement.Nodes("List"), enclosing);
          break;
        case "IfStmt":
          Block(statement.Node("Body")!.Nodes("List"), enclosing);
          if (statement.Node("Else") is { } otherwise)
            Visit(otherwise, []);

          break;
        case "SwitchStmt":
          foreach (var clause in statement.Node("Body")!.Nodes("List"))
            Block(clause.Nodes("Body"), [.. enclosing, .. labels.Where(entry => entry.Statement == statement)]);

          break;
        case "ForStmt" or "RangeStmt":
          Block(statement.Node("Body")!.Nodes("List"), [.. enclosing, .. labels.Where(entry => entry.Statement == statement)]);
          break;
      }
    }

    foreach (var statement in statements)
      Visit(statement, []);

    return pending;
  }

}
