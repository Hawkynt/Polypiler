using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;

namespace Polypiler.Core.Frontends.Python.Typing;

/// <summary>
/// The type of the value of an expression, from the types of its operands, as the Python Language
/// Reference and the built-in types define them: <c>/</c> always gives a <c>float</c>, <c>//</c>,
/// <c>%</c>, <c>+</c>, <c>-</c>, <c>*</c> an <c>int</c> of two integers (a <c>bool</c> counts as one) and a
/// <c>float</c> otherwise, <c>**</c> a <c>float</c> also for an integer to a negative constant power,
/// the bitwise operators a <c>bool</c> of two <c>bool</c>s, a comparison a <c>bool</c>. An operand of
/// an unknown type makes the result unknown.
/// </summary>
internal static class ExpressionTypes {

  /// <summary>The type of an expression in a function of a module.</summary>
  public static PyType Of(PyNode expression, FunctionModel function, IReadOnlyDictionary<string, FunctionModel> functions) {
    PyType Inner(string field) => Of(expression.Node(field)!, function, functions);
    // SubsetCheck refused every other expression, every constant but a number, and every name that is no local.
    switch (expression.Kind) {
      case "Constant":
        return expression.Text("kind") switch {
          "int" => PyType.Int,
          "float" => PyType.Float,
          _ => PyType.Bool
        };
      case "Name":
        return function.Find(expression.Text("id")!)!.Type;
      case "BinOp":
        return Binary(expression.Operator()!, Inner("left"), Inner("right"), expression.Node("right")!);
      case "UnaryOp":
        var operand = Inner("operand");
        return expression.Operator() == "Not" ? PyType.Bool
          : operand == PyType.Unknown ? PyType.Unknown
          : operand == PyType.Float && expression.Operator() != "Invert" ? PyType.Float
          : PyType.Int;
      case "Compare":
        return PyType.Bool;
      case "BoolOp":
        return Joined(expression.Nodes("values").Select(value => Of(value, function, functions)));
      case "IfExp":
        return Joined([Inner("body"), Inner("orelse")]);
      default:
        // A call of a function of the module or of a built-in.
        var name = expression.Node("func")!.Text("id")!;
        var arguments = expression.Nodes("args").Select(argument => Of(argument, function, functions)).ToList();
        if (ScopeResolver.Resolve(function, functions, name) == Binding.Function)
          return functions[name].Returns;

        return name switch {
          "int" or "round" => PyType.Int,
          "float" => PyType.Float,
          "bool" => PyType.Bool,
          "abs" => arguments[0] is PyType.Unknown or PyType.Float ? arguments[0] : PyType.Int,
          _ => Binary("Pow", arguments[0], arguments[1], expression.Nodes("args")[1])
        };
    }
  }

  /// <summary>The type of a binary operation.</summary>
  /// <param name="op">The class of the operator, for example <c>Add</c>.</param>
  /// <param name="left">The type of the left operand.</param>
  /// <param name="right">The type of the right operand.</param>
  /// <param name="rightNode">The right operand, whose sign decides the type of an integer power when it is a constant.</param>
  public static PyType Binary(string op, PyType left, PyType right, PyNode rightNode) {
    if (left == PyType.Unknown || right == PyType.Unknown)
      return PyType.Unknown;

    var floating = left == PyType.Float || right == PyType.Float;
    return op switch {
      "Div" => PyType.Float,
      "Pow" => floating || Constants.Integer(rightNode) < 0 ? PyType.Float : PyType.Int,
      "LShift" or "RShift" => PyType.Int,
      "BitAnd" or "BitOr" or "BitXor" => left == PyType.Bool && right == PyType.Bool ? PyType.Bool : PyType.Int,
      _ => floating ? PyType.Float : PyType.Int
    };
  }

  /// <summary>The join of the types of alternative values, unknown where one is unknown or where no type holds them all.</summary>
  public static PyType Joined(IEnumerable<PyType> types) {
    PyType? result = PyType.Unknown;
    foreach (var type in types) {
      if (type == PyType.Unknown)
        return PyType.Unknown;

      result = PyTypes.Join(result!.Value, type);
      if (result is null)
        return PyType.Unknown;
    }

    return result!.Value;
  }

}
