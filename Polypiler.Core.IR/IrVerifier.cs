using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>Checks that a module is well-typed before anything interprets or emits it.</summary>
public static partial class IrVerifier {

  /// <summary>The code of every diagnostic the verifier reports.</summary>
  public const string Code = "PLY0100";

  /// <summary>The registered diagnostic of <see cref="Code"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Descriptor = new(Code, DiagnosticSeverity.Error, ExitCode.InternalError, "{0}", "ir.verifier");

  /// <summary>Checks every function of a module.</summary>
  /// <param name="module">The module to check.</param>
  /// <returns>One diagnostic per violated rule; empty when the module is well-typed.</returns>
  public static IReadOnlyList<Diagnostic> Verify(IrModule module) {
    var diagnostics = new List<Diagnostic>();
    var functions = new Dictionary<string, IrFunction>();
    foreach (var function in module.Functions)
      if (!functions.TryAdd(function.Name, function))
        diagnostics.Add(Error(function.Name, "the module declares the function twice"));

    var types = Types(module, diagnostics);
    var constants = new HashSet<IrModuleConst>(ReferenceEqualityComparer.Instance);
    var names = new HashSet<string>(functions.Keys);
    foreach (var constant in module.Constants) {
      constants.Add(constant);
      var element = constant.Type.Element ?? constant.Type;
      void Require(bool holds, string message) {
        if (!holds)
          diagnostics.Add(Error(constant.Name, message));
      }

      Require(names.Add(constant.Name), "the module declares the name twice");
      Require(!OnHeap(constant.Type), Stored);
      Require(constant.Type.IsStorable && !element.IsStruct && !constant.Type.IsView, $"a constant of the type {constant.Type}");
      Require(constant.Type.IsArray || constant.Values.Count == 1, $"a constant of the type {constant.Type} with {constant.Values.Count} values");
      foreach (var value in constant.Values.Where(value => !FunctionCheck.InRange(new IrConst(element, value))).Take(1))
        Require(false, $"the constant {value} is not a value of {element}");
    }

    var globals = Globals(module, names, functions, constants, types, diagnostics);
    foreach (var function in module.Functions)
      new FunctionCheck(function, functions, constants, types, diagnostics) { Globals = globals }.Run();

    return diagnostics;
  }

  /// <summary>What the verifier says of a view that would outlive the stack: in a field, a constant or an array.</summary>
  internal const string Stored = "a span is stored outside the stack";

  /// <summary>Whether a type puts a view where only the heap can hold it: as the element of an array.</summary>
  internal static bool OnHeap(IrType type) => type.IsView ? false : type.Element is { } element && (element.IsView || OnHeap(element));

  private static Diagnostic Error(string member, string message)
    => Descriptor.At($"ill-typed IR in '{member}': {message}");

  private sealed partial class FunctionCheck(
    IrFunction function,
    Dictionary<string, IrFunction> functions,
    HashSet<IrModuleConst> constants,
    TypeTable types,
    List<Diagnostic> diagnostics) {

    private readonly HashSet<IrLocal> _visible = [];
    private int _loops;

    /// <summary>The globals the code checked may name: every one of the module in a function, the earlier ones in an initializer.</summary>
    public IReadOnlySet<IrGlobal> Globals { get; init; } = new HashSet<IrGlobal>();
    private int _breakable;

    public void Run() {
      this.Require(function.ReturnType.IsStorable || function.ReturnType == IrType.Void,
        $"the return type {function.ReturnType} is not a type of the IR");
      this.Declared(function.ReturnType);
      foreach (var parameter in function.Parameters) {
        this.Require(!OnHeap(parameter.Type), Stored);
        this.Require(parameter.Type.IsStorable, $"parameter '{parameter.Name}' has the type {parameter.Type}");
        this.Declared(parameter.Type);
        this.Require(this._visible.Add(parameter), $"parameter '{parameter.Name}' is declared twice");
      }

      foreach (var local in function.Locals) {
        this.Require(!OnHeap(local.Type), Stored);
        this.Require(local.Type.IsStorable, $"local '{local.Name}' has the type {local.Type}");
        this.Declared(local.Type);
      }

      this.Block(function.Body);
    }

    private void Require(bool holds, string message) {
      if (!holds)
        diagnostics.Add(Error(function.Name, message));
    }

    /// <summary>A struct type, or an array of one, is one the module declares.</summary>
    private void Declared(IrType type) {
      var named = (type.Element ?? type).StructType;
      this.Require(named is null || types.Declares(named), $"the type {named} is not among the types of the module");
    }

    private void Block(IrBlock block) {
      var declared = new List<IrLocal>();
      foreach (var statement in block.Statements)
        this.Statement(statement, declared);

      this._visible.ExceptWith(declared);
    }

    private void Statement(IrStmt statement, List<IrLocal> declared) {
      switch (statement) {
        case IrBlock block:
          this.Block(block);
          break;
        case IrDeclare declare:
          this.Require(function.Locals.Contains(declare.Local),
            $"local '{declare.Local.Name}' is not among the locals of the function");
          if (declare.Initializer is not null)
            this.Value(declare.Initializer, declare.Local.Type, $"the initializer of '{declare.Local.Name}'");

          if (this._visible.Add(declare.Local))
            declared.Add(declare.Local);
          else
            this.Require(false, $"local '{declare.Local.Name}' is declared twice");

          break;
        case IrAssign assign:
          this.InScope(assign.Local);
          this.Value(assign.Value, assign.Local.Type, $"the value assigned to '{assign.Local.Name}'");
          break;
        case IrGlobalAssign assign:
          this.Global(assign.Global);
          this.Require(!assign.Global.Type.IsArray, $"the array global '{assign.Global.Name}' is assigned");
          this.Value(assign.Value, assign.Global.Type, $"the value assigned to '{assign.Global.Name}'");
          break;
        case IrStore store:
          this.Writable(store.Array);
          this.Element(store.Array, store.Index);
          this.Value(store.Value, store.Array.Type.Element, "the value stored in an array element");
          break;
        case IrExprStmt expression:
          this.Expression(expression.Expression);
          break;
        case IrFieldStore store:
          this.Expression(store.Target);
          this.Require(IsPlace(store.Target), $"a field is written of a {store.Target.GetType().Name}, which is no place");
          this.Member(store.Target, store.Field);
          this.Require(!store.Field.IsReadOnly, $"the read-only field '{store.Field.Name}' is written");
          this.Value(store.Value, store.Field.Type, $"the value stored in the field '{store.Field.Name}'");
          break;
        case IrIf conditional:
          this.Value(conditional.Condition, IrType.Bool, "the condition of an if");
          this.Block(conditional.Then);
          if (conditional.Else is not null)
            this.Block(conditional.Else);

          break;
        case IrWhile loop:
          this.Value(loop.Condition, IrType.Bool, "the condition of a while");
          this.Loop(loop.Body);
          break;
        case IrDoWhile loop:
          this.Loop(loop.Body);
          this.Value(loop.Condition, IrType.Bool, "the condition of a do");
          break;
        case IrFor loop:
          this.Value(loop.Condition, IrType.Bool, "the condition of a for");
          this.Loop(loop.Body);
          foreach (var step in loop.Step.Statements)
            this.Require(step is IrDeclare or IrAssign or IrGlobalAssign or IrStore or IrFieldStore or IrExprStmt, $"the step of a for holds a {step.GetType().Name}");

          this.Block(loop.Step);
          break;
        case IrSwitch choice:
          this.Switch(choice);
          break;
        case IrBreak:
          this.Require(this._breakable > 0, "a break outside a loop and outside a switch");
          break;
        case IrContinue:
          this.Require(this._loops > 0, "a continue outside a loop");
          break;
        case IrReturn result:
          if (result.Value is null)
            this.Require(function.ReturnType == IrType.Void, "a return without a value in a function that returns one");
          else
            this.Value(result.Value, function.ReturnType, "the value returned");

          break;
        default:
          this.Require(false, $"unknown statement {statement.GetType().Name}");
          break;
      }
    }

    private void Loop(IrBlock body) {
      ++this._loops;
      ++this._breakable;
      this.Block(body);
      --this._breakable;
      --this._loops;
    }

    private void Switch(IrSwitch choice) {
      this.Expression(choice.Value);
      var type = choice.Value.Type;
      this.Require(type.IsInteger, $"a switch on the type {type}");
      var labels = new HashSet<long>();
      var defaults = 0;
      foreach (var section in choice.Sections) {
        this.Require(section.IsDefault || section.Labels.Count > 0, "a switch section without a label");
        foreach (var label in section.Labels) {
          this.Require(!type.IsInteger || InRange(new IrConst(type, label)), $"the label {label} is not a value of {type}");
          this.Require(labels.Add(label), $"the label {label} stands twice in a switch");
        }

        if (section.IsDefault)
          ++defaults;

        ++this._breakable;
        this.Block(section.Body);
        --this._breakable;
      }

      this.Require(defaults <= 1, "a switch with more than one default section");
    }

    private void InScope(IrLocal local)
      => this.Require(this._visible.Contains(local), $"'{local.Name}' is used outside the scope that declares it");

    private void Value(IrExpr expression, IrType? expected, string what) {
      this.Expression(expression);
      this.Require(expression.Type == expected, $"{what} has the type {expression.Type}, expected {expected}");
    }

    /// <summary>
    /// An array that is only read: unlike everywhere else, a table of the module may stand here.
    /// </summary>
    private void Readable(IrExpr array) {
      if (array is IrConstRef table)
        this.Known(table.Constant);
      else
        this.Expression(array);
    }

    private void Writable(IrExpr array) {
      this.Require(array is not IrConstRef, "the elements of a constant table are written");
      this.Require(array.Type.Kind != IrTypeKind.ReadOnlySpan, "an element of a read-only span is written");
    }

    private void Known(IrModuleConst constant)
      => this.Require(constants.Contains(constant), $"the constant '{constant.Name}' is not among the constants of the module");

    /// <summary>A global is one the code may name: of the module, and in an initializer one declared before.</summary>
    private void Global(IrGlobal global)
      => this.Require(this.Globals.Contains(global), $"the global '{global.Name}' is not among the globals it may read");

    private void Element(IrExpr array, IrExpr index) {
      this.Readable(array);
      this.Expression(index);
      this.Require(array.Type.IsSequence, $"an element is taken of the type {array.Type}");
      this.Require(index.Type.IsWord, $"an array index has the type {index.Type}");
    }

    private void Expression(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          this.Require(InRange(constant), $"the constant {constant.Value} is not a value of {constant.Type}");
          break;
        case IrLocalRef reference:
          this.InScope(reference.Local);
          break;
        case IrGlobalRef reference:
          this.Global(reference.Global);
          break;
        case IrConstRef reference:
          this.Known(reference.Constant);
          this.Require(!reference.Type.IsArray, $"the constant table '{reference.Constant.Name}' is used as a value");
          break;
        case IrUnary unary:
          this.Expression(unary.Operand);
          this.Require(IrOperators.Accepts(unary.Op, unary.Operand.Type), $"{unary.Op} is applied to {unary.Operand.Type}");
          break;
        case IrBinary binary:
          this.Expression(binary.Left);
          this.Expression(binary.Right);
          this.Require(IrOperators.Accepts(binary.Op, binary.Left.Type, binary.Right.Type),
            $"{binary.Op} is applied to {binary.Left.Type} and {binary.Right.Type}");
          break;
        case IrConvert conversion:
          this.Expression(conversion.Operand);
          this.Require(Converts(conversion),
            $"a conversion from {conversion.Operand.Type} to {conversion.Type} with the contract {conversion.Contract}");
          break;
        case IrConditional choice:
          this.Value(choice.Condition, IrType.Bool, "the condition of a conditional expression");
          this.Expression(choice.WhenTrue);
          this.Value(choice.WhenFalse, choice.WhenTrue.Type, "the second branch of a conditional expression");
          this.Require(choice.Type.IsStorable, $"a conditional expression of the type {choice.Type}");
          break;
        case IrArrayLoad load:
          this.Element(load.Array, load.Index);
          break;
        case IrArrayLength length:
          this.Readable(length.Array);
          this.Require(length.Array.Type.IsArray, $"the length is taken of the type {length.Array.Type}");
          break;
        case IrNewArray creation:
          this.Expression(creation.Length);
          this.Require(creation.Type.IsArray && creation.Type.IsStorable, $"an array is created of the type {creation.Type}");
          this.Require(creation.Length.Type.IsWord, $"an array length has the type {creation.Length.Type}");
          this.Defaulted(creation.Type.Element);
          break;
        case IrStructNew creation:
          foreach (var argument in creation.Arguments)
            this.Expression(argument);

          if (this.Struct(creation.Type) is { } built) {
            this.Require(creation.Arguments.Count == built.Fields.Count,
              $"the struct {built.Name} is built from {creation.Arguments.Count} values for {built.Fields.Count} fields");
            foreach (var (argument, field) in creation.Arguments.Zip(built.Fields))
              this.Require(argument.Type == field.Type, $"the field '{field.Name}' of {built.Name} is given the type {argument.Type}, expected {field.Type}");
          }

          break;
        case IrStructDefault value:
          this.Struct(value.Type);
          this.Defaulted(value.Type);
          break;
        case IrFieldLoad load:
          this.Expression(load.Target);
          this.Member(load.Target, load.Field);
          break;
        case IrStructEquals equality:
          this.Expression(equality.Left);
          this.Expression(equality.Right);
          this.Struct(equality.Left.Type);
          this.Require(equality.Right.Type == equality.Left.Type, $"a struct of the type {equality.Left.Type} is compared with one of the type {equality.Right.Type}");
          break;
        case IrCall call:
          this.Call(call);
          break;
        case IrIntrinsic intrinsic:
          for (var index = 0; index < intrinsic.Arguments.Count; ++index)
            if (index == 0 && (intrinsic.Op == IrIntrinsicOp.ArrayCopy || (intrinsic.Op == IrIntrinsicOp.ViewOf && intrinsic.Type.Kind == IrTypeKind.ReadOnlySpan)))
              this.Readable(intrinsic.Arguments[index]);
            else
              this.Expression(intrinsic.Arguments[index]);

          this.Require(IrOperators.Accepts(intrinsic.Op, intrinsic.Type, intrinsic.Arguments.Select(argument => argument.Type).ToList()),
            $"{intrinsic.Op} with the type {intrinsic.Type} is applied to ({string.Join(", ", intrinsic.Arguments.Select(argument => argument.Type))})");
          if (FloatOperationTable.Problem(intrinsic) is { } problem)
            this.Require(false, problem);

          break;
        case IrStep step:
          if (step.Target is IrArrayLoad stepped)
            this.Writable(stepped.Array);

          this.Expression(step.Target);
          this.Require(step.Target is IrLocalRef or IrGlobalRef or IrArrayLoad, $"a step of a {step.Target.GetType().Name}");
          this.Require(step.Type.IsNumeric, $"a step of the type {step.Type}");
          this.Require(!step.Trap || !step.Type.IsFloat, $"a trapping step of the type {step.Type}");
          break;
        default:
          this.Require(false, $"unknown expression {expression.GetType().Name}");
          break;
      }
    }

    /// <summary>The struct of a struct type the module declares, or <see langword="null"/> after reporting that it is none.</summary>
    private IrStructType? Struct(IrType type) {
      this.Require(type.IsStruct, $"a struct is expected, not the type {type}");
      this.Declared(type);
      return type.StructType is { } named && types.Declares(named) ? named : null;
    }

    /// <summary>A struct type, or the element type of an array, has a default value where one is built.</summary>
    private void Defaulted(IrType? type) {
      if (type?.StructType is { } named && types.Declares(named))
        this.Require(types.HasDefault(named), $"the struct {named.Name} has a field of an array type and so no default value");
    }

    /// <summary>A field is one of the target type's own.</summary>
    private void Member(IrExpr target, IrField field) {
      if (this.Struct(target.Type) is { } owner)
        this.Require(owner.Owns(field), $"the field '{field.Name}' is not a field of {owner.Name}");
    }

    /// <summary>Whether an expression names a place a field can be written in: a variable, an array element, or a field of a place.</summary>
    private static bool IsPlace(IrExpr target) => target switch {
      IrLocalRef => true,
      IrArrayLoad load => load.Array is not IrConstRef,
      IrFieldLoad load => IsPlace(load.Target),
      _ => false
    };

    private void Call(IrCall call) {
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      if (!functions.TryGetValue(call.Function, out var target)) {
        this.Require(false, $"the called function '{call.Function}' does not exist");
        return;
      }

      this.Require(call.Type == target.ReturnType,
        $"the call of '{call.Function}' has the type {call.Type}, the function returns {target.ReturnType}");
      this.Require(call.Arguments.Select(argument => argument.Type).SequenceEqual(target.Parameters.Select(parameter => parameter.Type)),
        $"the arguments of the call of '{call.Function}' do not match its parameters");
    }

    private static bool Converts(IrConvert conversion) {
      var from = conversion.Operand.Type;
      if (!from.IsNumeric || !conversion.Type.IsNumeric)
        return false;

      if (conversion.Type.IsFloat)
        return conversion.Contract == IrConversion.Wrap;

      return conversion.Contract == IrConversion.Trap || conversion.Contract == (from.IsFloat ? IrConversion.Saturate : IrConversion.Wrap);
    }

    internal static bool InRange(IrConst constant) => constant.Type.Kind switch {
      IrTypeKind.Bool => constant.Value is 0 or 1,
      IrTypeKind.Void or IrTypeKind.Array or IrTypeKind.Struct or IrTypeKind.Span or IrTypeKind.ReadOnlySpan => false,
      IrTypeKind.F32 => constant.Value is >= int.MinValue and <= int.MaxValue,
      IrTypeKind.F64 => true,
      _ => constant.Type.IsLong || (constant.Value >= IrOperators.Minimum(constant.Type) && constant.Value <= IrOperators.Maximum(constant.Type))
    };

  }

}
