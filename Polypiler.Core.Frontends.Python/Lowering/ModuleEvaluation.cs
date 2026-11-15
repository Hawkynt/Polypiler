using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// Runs the statements of a module the way an import of it runs them: once, in source order, before any
/// of its functions is called. A module-level name takes the value of each assignment in turn - the
/// functions see the last one - and a parameter default is the value its expression has where the
/// <c>def</c> stands, never one evaluated per call. The value of an expression is worked out by its IR,
/// lowered as a function would lower it and run by the IR interpreter: the source itself is never run.
/// What the statements can hold is constant - numbers, strings, tuples, tables of numbers built by
/// displays or comprehensions; a default of a mutable type, one object shared by every call, is refused.
/// </summary>
internal sealed class ModuleEvaluation(LoweringContext context, Typing.TypeInference typing, List<Diagnostic> diagnostics) {

  private int _made;

  /// <summary>Runs the statements of a module.</summary>
  public void Run(ModuleModel module) {
    var lowering = new FunctionLowering(context, module, null);
    foreach (var statement in module.Statements) {
      try {
        this.Statement(module, lowering, statement);
      } catch (Refusal refusal) {
        diagnostics.Add(refusal.Diagnostic);
      }
    }

    // A nested function's defaults are evaluated where its def runs, in its function: of the import, those of constants only.
    foreach (var function in module.AllFunctions.Where(function => function.Parent is not null))
      foreach (var parameter in function.Parameters.Where(parameter => parameter.Default is not null)) {
        try {
          parameter.DefaultValue = this.Default(module, lowering, parameter);
        } catch (Refusal refusal) {
          diagnostics.Add(refusal.Diagnostic);
        }
      }
  }

  private void Statement(ModuleModel module, FunctionLowering lowering, PyNode statement) {
    switch (statement.Kind) {
      case "FunctionDef":
        foreach (var parameter in module.Functions[statement.Text("name")!].Parameters.Where(parameter => parameter.Default is not null))
          parameter.DefaultValue = this.Default(module, lowering, parameter);

        return;
      case "ClassDef":
        var type = module.Classes[statement.Text("name")!];
        foreach (var parameter in type.InitParameters.Concat(type.Methods.Values.Where(method => method.Name != "__init__").SelectMany(method => method.Parameters))
          .Where(parameter => parameter.Default is not null && parameter.DefaultValue is null))
          parameter.DefaultValue = this.Default(module, lowering, parameter);

        return;
      case "AugAssign":
        var augmented = statement.Node("target")!;
        this.Assign(module, augmented, this.Evaluate(module, statement, () => lowering.ModuleAugmented(statement), augmented.Text("id")!));
        return;
    }

    var value = statement.Node("value")!;
    var targets = statement.Is("Assign") ? statement.Nodes("targets") : [statement.Node("target")!];
    foreach (var target in targets) {
      if (target.Is("Tuple")) {
        var names = target.Nodes("elts");
        var items = value.Is("Tuple") && value.Nodes("elts").Count == names.Count ? value.Nodes("elts") : null;
        if (items is null)
          throw module.File.Refuse(value, PythonFrontend.UnsupportedStatementDescriptor, $"statement 'module-level unpacking of {module.File.Quote(value)}' is not supported by the Python frontend");

        // Every value is evaluated before the first name is assigned.
        var evaluated = names.Zip(items).Select(pair => this.Evaluate(module, lowering, pair.Second, null, pair.First.Text("id")!)).ToList();
        foreach (var (name, item) in names.Zip(evaluated))
          this.Assign(module, name, item);

        continue;
      }

      this.Assign(module, target, this.Evaluate(module, lowering, value, null, target.Text("id")!));
    }
  }

  private void Assign(ModuleModel module, PyNode target, ConstantValue value) {
    var name = target.Text("id")!;
    module.Constants[name] = value;
    if (module.GlobalVariables.TryGetValue(name, out var global)) {
      if (!(value.Type.IsNumber && value.Value is IrConst or IrBigConst))
        throw module.File.Refuse(target, PythonFrontend.UnsupportedStatementDescriptor,
          $"statement 'module variable {name} of the type {value.Type}' is not supported by the Python frontend: a variable functions assign through global holds a number or a truth value");

      global.Initial = value;
      if (PyTypes.Join(global.Type, value.Type) is { } joined)
        global.Type = joined;
    }
  }

  /// <summary>The value of a parameter's default: evaluated now, once; one of a mutable type is refused.</summary>
  private ConstantValue Default(ModuleModel module, FunctionLowering lowering, Variable parameter) {
    // A display or a construction of a mutable object is refused by its form, before its elements would need a type.
    var node = parameter.Default!;
    if (node.Kind is "List" or "ListComp" or "Dict" or "Set" or "DictComp" or "SetComp"
        || (node.Is("Call") && node.Node("func") is { Kind: "Name" } callee && callee.Text("id") is "list" or "bytearray" or "dict" or "set"))
      throw module.File.Refuse(node, PythonFrontend.MutableDefaultDescriptor, $"the default of parameter '{parameter.Name}' is a mutable object shared across calls");

    var value = this.Evaluate(module, lowering, parameter.Default!, null, parameter.Name);
    if (value.Type.Kind is PyKind.List or PyKind.ByteArray or PyKind.Table or PyKind.Object or PyKind.Dict or PyKind.Set)
      throw module.File.Refuse(parameter.Default!, PythonFrontend.MutableDefaultDescriptor, $"the default of parameter '{parameter.Name}' is a mutable object shared across calls");

    return value;
  }

  /// <summary>The value of a module-level expression: a constant of the IR, or a table of the module.</summary>
  private ConstantValue Evaluate(ModuleModel module, FunctionLowering lowering, PyNode expression, PyType? expected, string name) {
    typing.ModuleExpression(module, expression);
    return this.Evaluate(module, expression, () => lowering.ModuleValue(expression, expected), name);
  }

  /// <summary>The value of a module-level expression lowered by a function: a constant of the IR, or a table of the module.</summary>
  private ConstantValue Evaluate(ModuleModel module, PyNode expression, Func<(List<IrStmt> Prefix, IrExpr Value, PyType Type, List<IrLocal> Locals)> lower, string name) {
    var (prefix, value, type, locals) = lower();
    if (prefix.Count == 0 && IsConstant(value))
      return new ConstantValue(type, value, null);

    if (type.Kind is PyKind.Object or PyKind.Iterator or PyKind.None or PyKind.Optional or PyKind.Dict or PyKind.Set)
      throw module.File.Refuse(expression, PythonFrontend.UnsupportedStatementDescriptor,
        $"statement 'module-level {module.File.Quote(expression)}' of the type {type} is not supported by the Python frontend");

    // A list is read back as an array: the table every function then reads. Only the interpreter runs it, so its ints stay exact.
    var result = type.Kind == PyKind.List ? new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(PyTypes.Ir(type.Element!)), [value]) : value;
    var function = new IrFunction("__value", true, result.Type, [], locals, Block([.. prefix, Return(result)]));
    var probe = new IrModule(module.Name, [function, .. context.Helpers.Functions, .. context.Made]) { Types = [.. Tuples.Current.Structs, .. context.Types], Globals = [.. context.Tables] };
    object? host;
    try {
      host = IrInterpreter.Call(probe, "__value", []);
    } catch (IrException exception) {
      throw module.File.Refuse(expression, PythonFrontend.UnsupportedStatementDescriptor,
        $"statement 'module-level {module.File.Quote(expression)}, which raises {Classes.PythonName(exception.Class)} when the module is imported,' is not supported by the Python frontend");
    }

    // A table read again, such as U = T, is a table of its own with the same values: no function writes one.
    if (type.Kind is PyKind.List or PyKind.Table) {
      var element = type.Element!;
      if (element.Kind is not (PyKind.Int or PyKind.Float or PyKind.Enum))
        throw module.File.Refuse(expression, PythonFrontend.UnsupportedStatementDescriptor,
          $"statement 'module-level {module.File.Quote(expression)}' of the type {type} is not supported by the Python frontend: a module-level list holds ints, floats or members of an enum");

      var values = ((Array)host!).Cast<object>().Select(item => Fit(Constant(element, item), PyTypes.Held(element))).ToList();
      var array = IrType.Array(PyTypes.Held(element));
      var table = new IrGlobal(Unique(module, name), array, Intrinsic(IrIntrinsicOp.ArrayOf, array, values));
      context.Tables.Add(table);
      return new ConstantValue(PyType.Table(element), null, table);
    }

    return new ConstantValue(type, Constant(type, host), null);
  }

  private string Unique(ModuleModel module, string name) {
    var candidate = (module.IsMain ? "" : module.Name.Replace('.', '_') + "_") + name;
    while (context.Tables.Any(table => table.Name == candidate) || module.Functions.ContainsKey(candidate))
      candidate = $"{name}_{++this._made}";

    return candidate;
  }

  /// <summary>Whether an expression is a constant of the IR: a number, a truth value, a string, or a tuple or array of them.</summary>
  private static bool IsConstant(IrExpr value) => value switch {
    IrConst or IrBigConst or IrStrConst => true,
    IrStructNew made => made.Arguments.All(IsConstant),
    IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } array => array.Arguments.All(IsConstant),
    _ => false
  };

  /// <summary>The constant expression of a value the interpreter gave.</summary>
  private static IrExpr Constant(PyType type, object? value) => type.Kind switch {
    PyKind.Bool => Bool((bool)value!),
    PyKind.Int => Big(value is BigInteger big ? big : System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
    PyKind.Float => Float(System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
    PyKind.Str => new IrStrConst((string)value!),
    PyKind.Enum => new IrConst(PyTypes.Ir(type), System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
    PyKind.Bytes or PyKind.ByteArray => Intrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [.. ((byte[])value!).Select(octet => (IrExpr)new IrConst(IrType.U8, octet))]),
    // Every other kind is refused before the interpreter runs, so what is left is a tuple.
    _ => new IrStructNew(PyTypes.Ir(type), [.. type.Items.Zip((object?[])value!).Select(pair => Fit(Constant(pair.First, pair.Second), PyTypes.Held(pair.First)))])
  };

}
