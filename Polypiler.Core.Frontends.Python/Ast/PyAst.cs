using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.Python.Ast;

/// <summary>
/// The classes of CPython's <c>ast</c> module (<c>Parser/Python.asdl</c> of 3.14) with the names of
/// their fields in order, and the helpers of the parser's actions that work on nodes
/// (<c>Parser/action_helpers.c</c>): contexts, the names of expressions in messages, invalid targets.
/// </summary>
internal static class PyAst {

  /// <summary>The fields of every class with fields, in the order of <c>_fields</c>.</summary>
  public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]> {
    ["Module"] = ["body", "type_ignores"],
    ["FunctionDef"] = ["name", "args", "body", "decorator_list", "returns", "type_comment", "type_params"],
    ["AsyncFunctionDef"] = ["name", "args", "body", "decorator_list", "returns", "type_comment", "type_params"],
    ["ClassDef"] = ["name", "bases", "keywords", "body", "decorator_list", "type_params"],
    ["Return"] = ["value"],
    ["Delete"] = ["targets"],
    ["Assign"] = ["targets", "value", "type_comment"],
    ["TypeAlias"] = ["name", "type_params", "value"],
    ["AugAssign"] = ["target", "op", "value"],
    ["AnnAssign"] = ["target", "annotation", "value", "simple"],
    ["For"] = ["target", "iter", "body", "orelse", "type_comment"],
    ["AsyncFor"] = ["target", "iter", "body", "orelse", "type_comment"],
    ["While"] = ["test", "body", "orelse"],
    ["If"] = ["test", "body", "orelse"],
    ["With"] = ["items", "body", "type_comment"],
    ["AsyncWith"] = ["items", "body", "type_comment"],
    ["Match"] = ["subject", "cases"],
    ["Raise"] = ["exc", "cause"],
    ["Try"] = ["body", "handlers", "orelse", "finalbody"],
    ["TryStar"] = ["body", "handlers", "orelse", "finalbody"],
    ["Assert"] = ["test", "msg"],
    ["Import"] = ["names"],
    ["ImportFrom"] = ["module", "names", "level"],
    ["Global"] = ["names"],
    ["Nonlocal"] = ["names"],
    ["Expr"] = ["value"],
    ["Pass"] = [],
    ["Break"] = [],
    ["Continue"] = [],
    ["BoolOp"] = ["op", "values"],
    ["NamedExpr"] = ["target", "value"],
    ["BinOp"] = ["left", "op", "right"],
    ["UnaryOp"] = ["op", "operand"],
    ["Lambda"] = ["args", "body"],
    ["IfExp"] = ["test", "body", "orelse"],
    ["Dict"] = ["keys", "values"],
    ["Set"] = ["elts"],
    ["ListComp"] = ["elt", "generators"],
    ["SetComp"] = ["elt", "generators"],
    ["DictComp"] = ["key", "value", "generators"],
    ["GeneratorExp"] = ["elt", "generators"],
    ["Await"] = ["value"],
    ["Yield"] = ["value"],
    ["YieldFrom"] = ["value"],
    ["Compare"] = ["left", "ops", "comparators"],
    ["Call"] = ["func", "args", "keywords"],
    ["FormattedValue"] = ["value", "conversion", "format_spec"],
    ["Interpolation"] = ["value", "str", "conversion", "format_spec"],
    ["JoinedStr"] = ["values"],
    ["TemplateStr"] = ["values"],
    ["Constant"] = ["value", "kind"],
    ["Attribute"] = ["value", "attr", "ctx"],
    ["Subscript"] = ["value", "slice", "ctx"],
    ["Starred"] = ["value", "ctx"],
    ["Name"] = ["id", "ctx"],
    ["List"] = ["elts", "ctx"],
    ["Tuple"] = ["elts", "ctx"],
    ["Slice"] = ["lower", "upper", "step"],
    ["comprehension"] = ["target", "iter", "ifs", "is_async"],
    ["ExceptHandler"] = ["type", "name", "body"],
    ["arguments"] = ["posonlyargs", "args", "vararg", "kwonlyargs", "kw_defaults", "kwarg", "defaults"],
    ["arg"] = ["arg", "annotation", "type_comment"],
    ["keyword"] = ["arg", "value"],
    ["alias"] = ["name", "asname"],
    ["withitem"] = ["context_expr", "optional_vars"],
    ["match_case"] = ["pattern", "guard", "body"],
    ["MatchValue"] = ["value"],
    ["MatchSingleton"] = ["value"],
    ["MatchSequence"] = ["patterns"],
    ["MatchMapping"] = ["keys", "patterns", "rest"],
    ["MatchClass"] = ["cls", "patterns", "kwd_attrs", "kwd_patterns"],
    ["MatchStar"] = ["name"],
    ["MatchAs"] = ["pattern", "name"],
    ["MatchOr"] = ["patterns"],
    ["TypeVar"] = ["name", "bound", "default_value"],
    ["ParamSpec"] = ["name", "default_value"],
    ["TypeVarTuple"] = ["name", "default_value"]
  };

  private static readonly Dictionary<string, PyNode> _singletons = new[] {
    "Load", "Store", "Del", "And", "Or", "Add", "Sub", "Mult", "MatMult", "Div", "Mod", "Pow", "LShift", "RShift", "BitOr", "BitXor", "BitAnd", "FloorDiv",
    "Invert", "Not", "UAdd", "USub", "Eq", "NotEq", "Lt", "LtE", "Gt", "GtE", "Is", "IsNot", "In", "NotIn"
  }.ToDictionary(kind => kind, kind => new PyNode(kind, null, []));

  /// <summary>The node of a context, an operator or a comparison, such as <c>Load</c> or <c>Add</c>; one object per class.</summary>
  public static PyNode Singleton(string kind) => _singletons[kind];

  /// <summary>A node of a class, its fields given in the order of the class.</summary>
  public static PyNode Make(string kind, PyRange? range, params object?[]? values) {
    // A single None passed alone arrives as no array at all.
    values ??= [null];
    var names = Fields[kind];
    var fields = new (string, object?)[names.Length];
    for (var i = 0; i < names.Length; i++)
      fields[i] = (names[i], i < values.Length ? values[i] : null);

    return new PyNode(kind, range, fields);
  }

  /// <summary>An expression in another context, its elements too for a tuple, a list or a starred one (<c>_PyPegen_set_expr_context</c>).</summary>
  public static PyNode SetContext(PyNode expression, string context) => expression.Kind switch {
    "Tuple" or "List" => expression.With("elts", expression.Nodes("elts").Select(element => (PyNode?)SetContext(element, context)).ToList()).With("ctx", Singleton(context)),
    "Starred" => expression.With("value", SetContext(expression.Node("value")!, context)).With("ctx", Singleton(context)),
    // The grammar passes only targets: a name, a subscript or an attribute here.
    _ => expression.With("ctx", Singleton(context))
  };

  /// <summary>What an expression is called in a message (<c>_PyPegen_get_expr_name</c>).</summary>
  public static string Name(PyNode expression) => expression.Kind switch {
    "Attribute" => "attribute",
    "Subscript" => "subscript",
    "Starred" => "starred",
    "Name" => "name",
    "List" => "list",
    "Tuple" => "tuple",
    "Lambda" => "lambda",
    "Call" => "function call",
    "BoolOp" or "BinOp" or "UnaryOp" => "expression",
    "GeneratorExp" => "generator expression",
    "Yield" or "YieldFrom" => "yield expression",
    "Await" => "await expression",
    "ListComp" => "list comprehension",
    "SetComp" => "set comprehension",
    "DictComp" => "dict comprehension",
    "Dict" => "dict literal",
    "Set" => "set display",
    "JoinedStr" or "FormattedValue" => "f-string expression",
    "TemplateStr" or "Interpolation" => "t-string expression",
    "Constant" => expression.Value switch {
      null => "None",
      false => "False",
      true => "True",
      PyEllipsis => "ellipsis",
      _ => "literal"
    },
    "Compare" => "comparison",
    "IfExp" => "conditional expression",
    "NamedExpr" => "named expression",
    _ => "expression"
  };

  /// <summary>Where assigning to, iterating into or deleting an expression is invalid.</summary>
  public enum Targets {

    /// <summary>The targets of an assignment.</summary>
    Star,

    /// <summary>The targets of a <c>del</c>.</summary>
    Delete,

    /// <summary>The target of a <c>for</c>.</summary>
    For

  }

  /// <summary>The part of an expression that cannot be a target (<c>_PyPegen_get_invalid_target</c>), or <see langword="null"/>.</summary>
  public static PyNode? InvalidTarget(PyNode expression, Targets targets) {
    switch (expression.Kind) {
      case "List" or "Tuple":
        foreach (var element in expression.Nodes("elts"))
          if (InvalidTarget(element, targets) is { } invalid)
            return invalid;

        return null;
      case "Starred":
        return targets == Targets.Delete ? expression : InvalidTarget(expression.Node("value")!, targets);
      case "Compare":
        if (targets == Targets.For)
          return expression.Nodes("ops")[0].Kind == "In" ? InvalidTarget(expression.Node("left")!, targets) : null;

        return expression;
      case "Name" or "Subscript" or "Attribute":
        return null;
      default:
        return expression;
    }
  }

}
