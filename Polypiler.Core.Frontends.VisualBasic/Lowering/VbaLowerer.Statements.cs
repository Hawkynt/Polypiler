using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.VisualBasic.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

internal sealed partial class VbaLowerer {

  /// <summary>A variable of the procedure: a local, a parameter or the function's result.</summary>
  private sealed record Variable(IrLocal Local, VbaType Type, bool IsParameter) {

    /// <summary>The kinds the variable may hold when it is a <c>Variant</c>, of a fixed type or dynamic; none for a typed one.</summary>
    public VbaTags Tags { get; init; }

  }

  private readonly Dictionary<string, Variable> _locals = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, VbaValue> _localConstants = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<IrLocal> _declared = [];
  private readonly Stack<string> _loops = new();
  private List<IrStmt> _pending = [];
  private IrLocal? _result;
  private string _procedure = "";
  private int _temporaries;

  private void BeginProcedure(string name) {
    this._procedure = name;
    this._locals.Clear();
    this._localConstants.Clear();
    this._declared.Clear();
    this._loops.Clear();
    this._pending = [];
    this._result = null;
    this._temporaries = 0;
  }

  /// <summary>A new local for an intermediate value, declared where the statement that needs it is.</summary>
  private IrLocal Temporary(IrType type, IrExpr value) {
    var local = new IrLocal($"ply{++this._temporaries}", type);
    this._declared.Add(local);
    this._pending.Add(new IrDeclare(local, value));
    return local;
  }

  private IrFunction LowerProcedure(VbaProcedure procedure) {
    this.BeginProcedure(procedure.Name);
    var signature = this._procedures[procedure.Name];
    var parameters = new List<IrLocal>();
    foreach (var (name, type, _, _, _) in signature.Parameters) {
      var local = new IrLocal(name, type.Ir);
      parameters.Add(local);
      if (!this._locals.TryAdd(name, new(local, type, true) { Tags = type.IsVariant ? VbaTags.Any : VbaTags.None }))
        throw this.Unresolved(procedure.Line, $"the parameter '{name}' is declared twice");
    }

    foreach (var (name, type, byVal, _, _) in signature.Parameters)
      if (!byVal && Assigns(procedure.Body, name, !type.IsArray))
        throw this.Unsupported(procedure.Line, $"the parameter '{name}' of '{procedure.Name}', passed by reference and {(type.IsArray ? "resized" : "assigned")} - the IR passes values; declare it ByVal or return the value");

    var prologue = new List<IrStmt>();
    if (signature.Returns is { } returns) {
      this._result = new IrLocal(procedure.Name, returns.Ir);
      this._locals[procedure.Name] = new(this._result, returns, false) { Tags = returns.IsVariant ? VbaTags.Any : VbaTags.None };
      this._declared.Add(this._result);
      prologue.Add(new IrDeclare(this._result, this.Default(returns, procedure.Line)));
    }

    // Every variable a procedure declares exists, with its initial value, from its first statement on.
    foreach (var dim in Dims(procedure.Body)) {
      foreach (var variable in dim.Variables) {
        var type = this.VariableType(variable, false, procedure.Body);
        if (this._locals.ContainsKey(variable.Name))
          throw this.Unresolved(variable.Line, $"the variable '{variable.Name}' is declared twice");

        var tags = VbaTags.None;
        if (type.IsVariant)
          (type, tags) = this.VariantLocal(variable.Name);
        var local = new IrLocal(variable.Name, type.Ir);
        this._locals[variable.Name] = new(local, type, false) { Tags = tags };
        this._declared.Add(local);
        this._pending = [];
        var initial = this.Initial(variable, type);
        prologue.AddRange(this._pending);
        this._pending = [];
        prologue.Add(new IrDeclare(local, initial));
      }
    }

    var body = this.TopLevel(procedure.Body);
    var statements = new List<IrStmt>([.. prologue, .. body]);
    if (this._result is { } result)
      statements.Add(new IrReturn(new IrLocalRef(result)));
    var returnType = signature.Returns?.Ir ?? IrType.Void;
    return new IrFunction(procedure.Name, procedure.IsPublic && !this._raw, returnType, parameters, [.. this._declared.Distinct()], new IrBlock(statements));
  }

  /// <summary>The initial value of a declared variable: a fixed array has its elements, of the defaults of their type.</summary>
  private IrExpr Initial(VbaVariable variable, VbaType type) {
    if (!type.IsArray || variable.Bounds.Count == 0)
      return this.Default(type, variable.Line);

    var upper = this.ConstantValue(variable.Bounds[0].Upper, variable.Line);
    var length = upper - type.LowerBound + 1;
    if (length < 1)
      throw this.Unresolved(variable.Line, $"the array '{variable.Name}' has an upper bound below its lower bound");
    return NewArray(type, new IrConst(IrType.I32, length));
  }

  /// <summary>A new array of a length: its elements are the defaults of their type, zero and False, as Visual Basic's are.</summary>
  private static IrExpr NewArray(VbaType type, IrExpr length) => new IrNewArray(type.Ir, length);

  /// <summary>Every <c>Dim</c> of a body, also those in its blocks.</summary>
  private static IEnumerable<VbaDim> Dims(IReadOnlyList<VbaStatement> body) {
    foreach (var statement in body) {
      if (statement is VbaDim dim)
        yield return dim;
      foreach (var block in Blocks(statement))
        foreach (var inner in Dims(block))
          yield return inner;
    }
  }

  private static IEnumerable<IReadOnlyList<VbaStatement>> Blocks(VbaStatement statement) => statement switch {
    VbaIf conditional => [.. conditional.Branches.Select(branch => branch.Body), .. conditional.Else is { } otherwise ? [otherwise] : Array.Empty<IReadOnlyList<VbaStatement>>()],
    VbaSelect select => [.. select.Cases.Select(section => section.Body), .. select.Else is { } otherwise ? [otherwise] : Array.Empty<IReadOnlyList<VbaStatement>>()],
    VbaFor loop => [loop.Body],
    VbaDo loop => [loop.Body],
    _ => []
  };

  /// <summary>Whether a body assigns a variable (as a whole, or a field of it when <paramref name="fields"/> is set), resizes or erases it, or counts a loop with it.</summary>
  private static bool Assigns(IReadOnlyList<VbaStatement> body, string name, bool fields) {
    bool Root(VbaExpr target) => target switch {
      VbaName named => string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase),
      VbaMember member => fields && Root(member.Target),
      VbaCall call => fields && call.Target is VbaMember && Root(call.Target),
      _ => false
    };

    foreach (var statement in body) {
      var hit = statement switch {
        VbaAssign assign => Root(assign.Target) || assign.Target is VbaMember && fields && Root(assign.Target),
        VbaFor loop => string.Equals(loop.Variable, name, StringComparison.OrdinalIgnoreCase),
        VbaReDim redim => redim.Items.Any(item => item.Target is VbaName named && string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase)),
        VbaErase erase => erase.Targets.Any(target => target is VbaName named && string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase)),
        _ => false
      };
      if (hit || Blocks(statement).Any(block => Assigns(block, name, fields)))
        return true;
    }

    return false;
  }

  #region Error handling

  /// <summary>The exception classes a handler tells apart, with the number <c>Err.Number</c> reads for each; the last catches every other.</summary>
  private static readonly (IrClassType Class, int Number)[] _errors = [
    (IrExceptionClass.DivideByZeroException, 11),
    (IrExceptionClass.OverflowException, 6),
    (IrExceptionClass.IndexOutOfRangeException, 9),
    (IrExceptionClass.InvalidCastException, 13),
    (IrExceptionClass.NullReferenceException, 91),
    (IrExceptionClass.ArgumentException, 5),
    (IrExceptionClass.InvalidOperationException, 94),
    (IrExceptionClass.Exception, 5)
  ];

  /// <summary>The exception class <c>Err.Raise</c> and <c>Error</c> throw for an error number, or <see langword="null"/> for a number the IR has no class of.</summary>
  private static IrClassType? ClassOf(long number) => number switch {
    11 => IrExceptionClass.DivideByZeroException,
    6 => IrExceptionClass.OverflowException,
    9 => IrExceptionClass.IndexOutOfRangeException,
    13 => IrExceptionClass.InvalidCastException,
    91 => IrExceptionClass.NullReferenceException,
    5 => IrExceptionClass.ArgumentException,
    94 => IrExceptionClass.InvalidOperationException,
    _ => null
  };

  /// <summary>The throw of a library exception, as a trap of the IR is.</summary>
  private static IrStmt Throw(IrClassType type) => new IrThrow(New(type));

  private static IrNew New(IrClassType type) => new(IrType.Object(type), IrExceptionClass.Constructor(type, 0), []);

  private IrStmt SetErr(int number) => new IrGlobalAssign(this.Err, new IrConst(IrType.I32, number));

  /// <summary>The catch clauses of a handler: each sets <c>Err.Number</c> and then runs what follows.</summary>
  private List<IrCatch> Catches(IReadOnlyList<IrStmt> then)
    => [.. _errors.Select(error => new IrCatch(error.Class, null, null, new IrBlock([this.SetErr(error.Number), .. then])))];

  /// <summary>
  /// The statements of a procedure's body with its error handling: <c>On Error GoTo label</c> with the
  /// handler after the label at the end of the procedure, as a try whose catch clauses set
  /// <c>Err.Number</c> and fall into the handler; a handler that ends with <c>Resume Next</c>, and
  /// <c>On Error Resume Next</c>, as a try around every statement that follows. <c>On Error GoTo 0</c>
  /// ends either; every <c>On Error</c> clears <c>Err</c>.
  /// </summary>
  private List<IrStmt> TopLevel(IReadOnlyList<VbaStatement> body) {
    var result = new List<IrStmt>();
    var resumeNext = false;
    for (var index = 0; index < body.Count; ++index) {
      var statement = body[index];
      if (statement is VbaOnError onError) {
        result.Add(this.SetErr(0));
        if (onError.Label is null) {
          resumeNext = true;
          continue;
        }

        resumeNext = false;
        if (onError.Label == "0")
          continue;

        var label = body.Skip(index + 1).Select((candidate, offset) => (candidate, offset)).FirstOrDefault(pair => pair.candidate is VbaLabel found && string.Equals(found.Name, onError.Label, StringComparison.OrdinalIgnoreCase));
        if (label.candidate is null)
          throw this.Unsupported(onError.Line, $"On Error GoTo {onError.Label} without the label {onError.Label} later at the level of the procedure");

        var guarded = body.Skip(index + 1).Take(label.offset).ToList();
        var handler = body.Skip(index + 2 + label.offset).ToList();
        if (guarded.Any(inner => inner is VbaOnError or VbaLabel))
          throw this.Unsupported(guarded.First(inner => inner is VbaOnError or VbaLabel).Line, "a second On Error or a label before the handler of On Error GoTo");

        result.AddRange(this.Handled(guarded, handler, onError.Line));
        return result;
      }

      if (this._inference is { } inference)
        inference.Weak = resumeNext;
      result.AddRange(resumeNext ? this.Resumed(statement, []) : this.Statement(statement));
    }

    return result;
  }

  /// <summary>The guarded statements and the handler of an <c>On Error GoTo</c>.</summary>
  private List<IrStmt> Handled(IReadOnlyList<VbaStatement> guarded, IReadOnlyList<VbaStatement> handler, int line) {
    var resumes = handler.Where(statement => statement is VbaResume).ToList();
    if (resumes.Count == 0) {
      if (handler.Any(statement => statement is VbaOnError or VbaLabel or VbaGoTo))
        throw this.Unsupported(line, "a handler with On Error, a label or GoTo of its own");

      var protectedBody = guarded.SelectMany(this.Statement).ToList();
      this._inference?.Havoc();
      var handlerBody = handler.SelectMany(this.Statement).ToList();
      return [new IrTry(new IrBlock(protectedBody), this.Catches([]), null), .. handlerBody];
    }

    // A handler that ends with Resume Next and resumes nowhere else runs after the statement that
    // failed and goes on with the next one: a try around every guarded statement.
    if (resumes.Count != 1 || handler[^1] is not VbaResume { Target: "Next" } || guarded.Count == 0 || guarded[^1] is not VbaExit)
      throw this.Unsupported(resumes[0].Line, "Resume other than a Resume Next that ends a handler after an Exit");

    this._inference?.Havoc();
    var handling = handler.Take(handler.Count - 1).SelectMany(this.Statement).Append(this.SetErr(0)).ToList();
    if (this._inference is { } inference)
      inference.Weak = true;
    return [.. guarded.SelectMany(statement => this.Resumed(statement, handling))];
  }

  /// <summary>
  /// A statement under <c>Resume Next</c>: an error in it sets <c>Err.Number</c>, runs the handling and
  /// goes on with the next statement. An error in the condition of an <c>If</c> goes on inside its first
  /// branch, as Visual Basic resumes with the statement after the one that failed.
  /// </summary>
  private List<IrStmt> Resumed(VbaStatement statement, IReadOnlyList<IrStmt> handling) {
    switch (statement) {
      case VbaIf conditional when conditional.Branches.Count == 1:
        var test = new IrLocal($"ply{++this._temporaries}", IrType.Bool);
        this._declared.Add(test);
        this._pending = [];
        var condition = this.Condition(conditional.Branches[0].Condition);
        var evaluate = new List<IrStmt>([.. this._pending, new IrAssign(test, condition)]);
        this._pending = [];
        var then = conditional.Branches[0].Body.SelectMany(inner => this.Resumed(inner, handling)).ToList();
        var otherwise = conditional.Else?.SelectMany(inner => this.Resumed(inner, handling)).ToList();
        return [
          new IrDeclare(test, new IrConst(IrType.Bool, 1)),
          new IrTry(new IrBlock(evaluate), this.Catches(handling), null),
          new IrIf(new IrLocalRef(test), new IrBlock(then), otherwise is null ? null : new IrBlock(otherwise))
        ];
      case VbaAssign or VbaCallStatement or VbaError or VbaReDim or VbaErase or VbaDim or VbaLocalConst or VbaExit:
        var lowered = this.Statement(statement);
        return statement is VbaDim or VbaLocalConst or VbaExit ? lowered : [new IrTry(new IrBlock(lowered), this.Catches(handling), null)];
      default:
        throw this.Unsupported(statement.Line, $"the statement {Describe(statement)} under On Error Resume Next");
    }
  }

  private static string Describe(VbaStatement statement) => statement switch {
    VbaIf => "If with ElseIf",
    VbaSelect => "Select Case",
    VbaFor => "For",
    VbaDo => "Do",
    _ => statement.GetType().Name[3..]
  };

  #endregion

  #region Statements

  /// <summary>A statement with the statements its expressions need first.</summary>
  private List<IrStmt> Statement(VbaStatement statement) {
    var saved = this._pending;
    this._pending = [];
    try {
      var lowered = this.Lower(statement);
      return [.. this._pending, .. lowered];
    } finally {
      this._pending = saved;
    }
  }

  private List<IrStmt> Block(IReadOnlyList<VbaStatement> statements) => [.. statements.SelectMany(this.Statement)];

  private IEnumerable<IrStmt> Lower(VbaStatement statement) {
    switch (statement) {
      case VbaDim:
        return [];
      case VbaLocalConst constant:
        this.Constant(constant.Name, constant.Type, constant.Value, constant.Line, this._localConstants);
        return [];
      case VbaAssign assign:
        return [this.Assign(assign.Target, assign.Value, assign.Line)];
      case VbaCallStatement call:
        return this.CallStatement(call);
      case VbaIf conditional:
        return [this.If(conditional.Branches, conditional.Else)];
      case VbaSelect select:
        return this.Select(select);
      case VbaFor loop:
        return this.For(loop);
      case VbaDo loop:
        return [this.Do(loop)];
      case VbaExit exit:
        return [this.Exit(exit)];
      case VbaReDim redim:
        return redim.Items.SelectMany(item => this.ReDim(item, redim.Preserve, redim.Line)).ToList();
      case VbaErase erase:
        return erase.Targets.Select(target => this.Erase(target, erase.Line)).ToList();
      case VbaError error:
        return [this.Raise(error.Number, error.Line)];
      case VbaOnError onError:
        throw this.Unsupported(onError.Line, "On Error inside a block");
      case VbaLabel label:
        throw this.Unsupported(label.Line, $"the label {label.Name} outside an On Error GoTo handler");
      case VbaGoTo jump:
        throw this.Unsupported(jump.Line, "GoTo");
      case VbaResume resume:
        throw this.Unsupported(resume.Line, "Resume outside a handler that ends with it");
      case VbaUnsupported unsupported:
        throw this.Unsupported(unsupported.Line, unsupported.Construct);
      default:
        throw this.Unsupported(statement.Line, statement.GetType().Name);
    }
  }

  /// <summary><c>Err.Raise n</c> and <c>Error n</c>: the throw of the exception class of the number.</summary>
  private IrStmt Raise(VbaExpr number, int line) {
    var value = this.Expression(number);
    if (value.Expr is not IrConst { Type.IsInteger: true } constant || ClassOf(constant.Value) is not { } type)
      throw this.Unsupported(line, "Err.Raise of an error number other than the constants 5, 6, 9, 11, 13, 91 and 94, which the IR's exception classes stand for");
    return Throw(type);
  }

  private IrStmt Assign(VbaExpr target, VbaExpr value, int line) {
    switch (target) {
      case VbaName name when this.Lookup(name.Name) is { } variable:
        if (variable.Global is not null && variable.Type.IsArray)
          throw this.Unsupported(line, $"the assignment of the module-level array '{name.Name}', which the IR's globals cannot replace");
        var assigned = this.Expression(value);
        // A local given a fixed type and assigned another one is lowered again as a Variant.
        if (this.Record(name.Name, assigned))
          return new IrBlock([]);
        var coerced = this.Coerce(this.Copied(assigned), variable.Type, line);
        return variable.Local is { } local ? new IrAssign(local, coerced) : new IrGlobalAssign(variable.Global!, coerced);
      case VbaName name:
        throw this.Unresolved(line, $"'{name.Name}' is no variable that can be assigned");
      case VbaMember { Target: VbaName { Name: var err }, Name: var member } when string.Equals(err, "Err", StringComparison.OrdinalIgnoreCase) && string.Equals(member, "Number", StringComparison.OrdinalIgnoreCase):
        return new IrGlobalAssign(this.Err, this.Coerce(this.Expression(value), VbaType.Long, line));
      case VbaMember member:
        var (record, recordType) = this.Place(member.Target, line);
        var (field, fieldType) = this.Field(recordType, member.Name, line);
        return new IrFieldStore(record, field, this.Coerce(this.Copied(this.Expression(value)), fieldType, line));
      case VbaCall { Target: var arrayTarget, Arguments: [var index] }:
        var (array, arrayType) = this.Place(arrayTarget, line);
        if (!arrayType.IsArray)
          throw this.Unresolved(line, $"'{arrayType}' is no array to assign an element of");
        var position = this.Index(index, arrayType, line);
        return new IrStore(array, position, this.Coerce(this.Expression(value), arrayType.Element!, line));
      default:
        throw this.Unsupported(line, "an assignment to something that is no variable, element or field");
    }
  }

  /// <summary>A variable, or a global, by name.</summary>
  private (IrLocal? Local, IrGlobal? Global, VbaType Type)? Lookup(string name) {
    if (this._locals.TryGetValue(name, out var local))
      return (local.Local, null, local.Type);
    if (this._globals.TryGetValue(name, out var global))
      return (null, global.Global, global.Type);
    return null;
  }

  /// <summary>An array, a record, or an element or a field of one: what an element or a field is stored into.</summary>
  private (IrExpr Expr, VbaType Type) Place(VbaExpr target, int line) {
    switch (target) {
      case VbaName name when this.Lookup(name.Name) is { } variable:
        return (variable.Local is { } local ? new IrLocalRef(local) : new IrGlobalRef(variable.Global!), variable.Type);
      case VbaMember member:
        var (record, recordType) = this.Place(member.Target, line);
        var (field, fieldType) = this.Field(recordType, member.Name, line);
        return (new IrFieldLoad(record, field), fieldType);
      case VbaCall { Target: var arrayTarget, Arguments: [var index] }:
        var (array, arrayType) = this.Place(arrayTarget, line);
        if (!arrayType.IsArray)
          throw this.Unresolved(line, $"'{arrayType}' is no array");
        return (new IrArrayLoad(array, this.Index(index, arrayType, line)), arrayType.Element!);
      default:
        throw this.Unsupported(line, "a store into something that is no variable");
    }
  }

  private (IrField Field, VbaType Type) Field(VbaType record, string name, int line) {
    if (record.Kind != VbaKind.Record || !this._fields.TryGetValue(Key(record.Record!, name), out var field))
      throw this.Unresolved(line, $"'{record}' has no field '{name}'");
    return field;
  }

  /// <summary>
  /// An array assigned from a variable is copied, as Visual Basic's array assignment copies; the IR's
  /// assignment would share it. A new array, the result of a call, is taken as it is.
  /// </summary>
  private VbaValue Copied(VbaValue value) {
    if (!value.Type.IsArray || value.Expr is not (IrLocalRef or IrGlobalRef or IrFieldLoad))
      return value;

    var source = this.Temporary(value.Type.Ir, value.Expr);
    var length = new IrArrayLength(new IrLocalRef(source));
    var copy = this.Temporary(value.Type.Ir, new IrNewArray(value.Type.Ir, length));
    this._pending.Add(this.CopyElements(value.Type, new IrLocalRef(source), new IrLocalRef(copy), length));
    return new(new IrLocalRef(copy), value.Type);
  }

  /// <summary>
  /// The copy of the first elements of an array into another: <c>ArrayCopy</c> of numbers, a loop of
  /// records and of <c>Variant</c>s, which the IR's copy does not take.
  /// </summary>
  private IrStmt CopyElements(VbaType array, IrExpr source, IrExpr target, IrExpr count) {
    if (array.Element!.Kind is not (VbaKind.Record or VbaKind.Variant))
      return new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [source, new IrConst(IrType.I32, 0), target, new IrConst(IrType.I32, 0), count]));

    var index = this.Temporary(IrType.I32, new IrConst(IrType.I32, 0));
    var limit = this.Temporary(IrType.I32, count);
    return new IrWhile(new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), new IrLocalRef(limit)), new IrBlock([
      new IrStore(target, new IrLocalRef(index), new IrArrayLoad(source, new IrLocalRef(index))),
      new IrAssign(index, new IrBinary(IrBinaryOp.Add, new IrLocalRef(index), new IrConst(IrType.I32, 1)))
    ]));
  }

  private IEnumerable<IrStmt> CallStatement(VbaCallStatement call) {
    if (call.Target is VbaMember { Target: VbaName { Name: var objectName }, Name: var method }) {
      if (string.Equals(objectName, "Err", StringComparison.OrdinalIgnoreCase)) {
        if (string.Equals(method, "Clear", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 0)
          return [this.SetErr(0)];
        if (string.Equals(method, "Raise", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1)
          return [this.Raise(call.Arguments[0], call.Line)];
        throw this.Unsupported(call.Line, $"Err.{method}");
      }

      if (string.Equals(objectName, "Debug", StringComparison.OrdinalIgnoreCase) && string.Equals(method, "Print", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1) {
        var printed = this.Expression(call.Arguments[0]);
        if (printed.Type.IsVariant && (printed.Tags & VbaTags.Null) != VbaTags.None)
          throw this.Unsupported(call.Line, "Debug.Print of a Variant that may be Null, which it prints as the word Null");
        return [new IrWriteLine(this.Text(printed, call.Line))];
      }
    }

    if (call.Target is VbaName name && this._procedures.TryGetValue(name.Name, out var signature)) {
      var arguments = call.Arguments.Select(argument => argument is VbaParenthesized parenthesized ? parenthesized.Inner : argument).ToList();
      return [new IrExprStmt(this.Call(signature, arguments, call.Line))];
    }

    throw call.Target is VbaName unknown
      ? this.Unresolved(call.Line, $"'{unknown.Name}' is no procedure of the module")
      : this.Unsupported(call.Line, "the call of a member");
  }

  private IrStmt If(IReadOnlyList<VbaIfBranch> branches, IReadOnlyList<VbaStatement>? otherwise) {
    var first = branches[0];
    var condition = this.Condition(first.Condition);
    var entry = this.Snapshot();
    var then = new IrBlock(this.Block(first.Body));
    var afterThen = this.Snapshot();
    this.Restore(entry);
    IrBlock? rest = null;
    if (branches.Count > 1) {
      // An ElseIf whose condition needs statements has them inside the Else, where it is evaluated.
      var inner = this.Statement(new VbaIf(branches.Skip(1).ToList(), otherwise, 0) with { });
      rest = new IrBlock(inner);
    } else if (otherwise is not null) {
      rest = new IrBlock(this.Block(otherwise));
    }

    this.Join(afterThen, this.Snapshot());
    return new IrIf(condition, then, rest);
  }

  /// <summary><c>Select Case</c>: the value once, then an <c>If</c> chain whose tests compare it with each clause in order.</summary>
  private IEnumerable<IrStmt> Select(VbaSelect select) {
    var value = this.Expression(select.Value);
    var held = this.Temporary(value.Type.Ir, value.Expr);
    var subject = value with { Expr = new IrLocalRef(held) };
    var entry = this.Snapshot();
    var ends = new List<Dictionary<string, VbaTags>?>();
    IrStmt? chain = select.Else is null ? null : new IrBlock(this.Block(select.Else));
    ends.Add(this.Snapshot());
    for (var index = select.Cases.Count - 1; index >= 0; --index) {
      var section = select.Cases[index];
      IrExpr? test = null;
      foreach (var clause in section.Clauses.Reverse()) {
        var saved = this._pending;
        this._pending = [];
        var one = this.Clause(subject, clause, select.Line);
        if (this._pending.Count > 0)
          throw this.Unsupported(select.Line, "a Case clause whose value needs statements");
        this._pending = saved;
        test = test is null ? one : new IrBinary(IrBinaryOp.LogicalOr, one, test);
      }

      this.Restore(entry);
      chain = new IrIf(test!, new IrBlock(this.Block(section.Body)), chain is null ? null : chain is IrBlock block ? block : new IrBlock([chain]));
      ends.Add(this.Snapshot());
      this.Restore(entry);
    }

    this.Join([.. ends]);
    return chain is null ? [] : [chain];
  }

  private IrExpr Clause(VbaValue subject, VbaCaseClause clause, int line) {
    if (clause.Is is { } comparison)
      return this.Compare(comparison, subject, this.Expression(clause.Value), line);
    if (clause.Upper is { } upper)
      return new IrBinary(IrBinaryOp.LogicalAnd, this.Compare(">=", subject, this.Expression(clause.Value), line), this.Compare("<=", subject, this.Expression(upper), line));
    return this.Compare("=", subject, this.Expression(clause.Value), line);
  }

  /// <summary>
  /// <c>For v = start To end Step step</c>: the end and the step evaluated once, in the counter's type;
  /// the test by the sign of the step; the step added in the counter's type, which traps on overflow
  /// as Visual Basic's does - also after the last pass.
  /// </summary>
  private IEnumerable<IrStmt> For(VbaFor loop) {
    if (this.Lookup(loop.Variable) is { Local: { } dynamic, Type.IsVariant: true })
      return this.VariantFor(loop, dynamic);
    if (this.Lookup(loop.Variable) is not { Local: { } counter, Type: var type } || !type.IsNumeric || type.Kind == VbaKind.Boolean)
      throw this.Unresolved(loop.Line, $"the counter '{loop.Variable}' is no numeric variable of the procedure");

    if (type.IsScaled)
      throw this.Unsupported(loop.Line, $"a For loop over the {type} counter '{loop.Variable}'");

    var startValue = this.Expression(loop.Start);
    var start = this.Temporary(type.Ir, this.Coerce(startValue, type, loop.Line));
    var statements = new List<IrStmt> { new IrAssign(counter, new IrLocalRef(start)) };
    var endValue = this.Expression(loop.End);
    var end = this.Temporary(type.Ir, this.Coerce(endValue, type, loop.Line));
    var stepValue = loop.Step is null ? new VbaValue(new IrConst(type.Ir, type.IsFloat ? IrOperators.Bits(type.Ir, 1.0) : 1), type) : this.Expression(loop.Step);
    // A Variant counter given a fixed type keeps it only where its last step cannot promote it.
    var variant = this._inference?.Candidates.Contains(loop.Variable) == true;
    if (variant)
      this.Record(loop.Variable, Steady(startValue, endValue, stepValue) == type.Kind ? new(startValue.Expr, type) : new(startValue.Expr, VbaType.Variant) { Tags = VbaTags.Any });
    var step = this.Temporary(type.Ir, this.Coerce(stepValue, type, loop.Line));
    var current = new VbaValue(new IrLocalRef(counter), type);
    var limit = new VbaValue(new IrLocalRef(end), type);
    var by = new VbaValue(new IrLocalRef(step), type);
    var zero = new VbaValue(new IrConst(type.Ir, 0), type);
    IrExpr condition = stepValue.Expr is IrConst constant && !type.IsFloat
      ? this.Compare(constant.Value >= 0 ? "<=" : ">=", current, limit, loop.Line)
      : new IrConditional(this.Compare(">=", by, zero, loop.Line), this.Compare("<=", current, limit, loop.Line), this.Compare(">=", current, limit, loop.Line));
    this.EnterLoop(loop);
    var head = this.Snapshot();
    this._loops.Push("For");
    var body = this.Block(loop.Body);
    this._loops.Pop();
    this.LeaveLoop(loop, head);
    var advance = variant
      ? [new IrAssign(counter, this.Arithmetic("+", type, current, by).Expr)]
      : this.Statement(new VbaAssign(new VbaName(loop.Variable, "", loop.Line), new VbaBinary(new VbaName(loop.Variable, "", loop.Line), "+", new VbaName(this.HoldName(step, type), "", loop.Line), loop.Line), loop.Line));
    statements.Add(new IrFor(condition, new IrBlock(body), new IrBlock(advance)));
    return statements;
  }

  /// <summary>Names a local for the lowering of a synthesized statement, which refers to it by name.</summary>
  private string HoldName(IrLocal local, VbaType type) {
    this._locals[local.Name] = new(local, type, false);
    return local.Name;
  }

  private IrStmt Do(VbaDo loop) {
    this.EnterLoop(loop);
    var head = this.Snapshot();
    this._loops.Push("Do");
    var body = this.Block(loop.Body);
    this._loops.Pop();
    this.LeaveLoop(loop, head);
    if (loop.Condition is null)
      return new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock(body));

    var saved = this._pending;
    this._pending = [];
    var condition = this.Condition(loop.Condition);
    var needed = this._pending;
    this._pending = saved;
    if (loop.Until)
      condition = new IrUnary(IrUnaryOp.LogicalNot, condition);
    if (needed.Count == 0)
      return loop.PreTest ? new IrWhile(condition, new IrBlock(body)) : new IrDoWhile(new IrBlock(body), condition);

    // A test that needs statements is written into the loop, which it then leaves.
    var exit = new IrIf(new IrUnary(IrUnaryOp.LogicalNot, condition), new IrBlock([new IrBreak()]), null);
    return loop.PreTest
      ? new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock([.. needed, exit, .. body]))
      : new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock([.. body, .. needed, exit]));
  }

  private IrStmt Exit(VbaExit exit) {
    switch (exit.Kind) {
      case "Do" or "For":
        this.ExitLoop();
        if (this._loops.Count == 0)
          throw this.Unresolved(exit.Line, $"Exit {exit.Kind} outside a {exit.Kind} loop");
        if (this._loops.Peek() != exit.Kind)
          throw this.Unsupported(exit.Line, $"Exit {exit.Kind} out of a {this._loops.Peek()} loop inside it");
        return new IrBreak();
      case "Function" when this._result is { } result:
        return new IrReturn(new IrLocalRef(result));
      case "Sub" when this._result is null:
        return new IrReturn(null);
      default:
        throw this.Unsupported(exit.Line, $"Exit {exit.Kind}");
    }
  }

  /// <summary><c>ReDim</c>: a new array of the bounds, the lower one a constant that is the variable's; <c>Preserve</c> copies what fits.</summary>
  private IEnumerable<IrStmt> ReDim(VbaReDimItem item, bool preserve, int line) {
    if (item.Bounds.Count != 1)
      throw this.Unsupported(line, "a multidimensional array");
    if (item.Target is not VbaName name || this.Lookup(name.Name) is not { Type.IsArray: true } variable)
      throw this.Unresolved(line, "ReDim of something that is no array variable");

    if (variable.Global is not null)
      throw this.Unsupported(line, $"ReDim of the module-level array '{name.Name}', which the IR's globals cannot replace");

    var lower = item.Bounds[0].Lower is { } given ? this.ConstantValue(given, line) : this._optionBase;
    if (lower != variable.Type.LowerBound)
      throw this.Unsupported(line, $"ReDim with the lower bound {lower} of an array declared with {variable.Type.LowerBound}");

    var upper = this.Coerce(this.Expression(item.Bounds[0].Upper), VbaType.Long, line);
    var length = this.Temporary(IrType.I32, new IrBinary(IrBinaryOp.AddChecked, new IrBinary(IrBinaryOp.SubtractChecked, upper, new IrConst(IrType.I32, lower)), new IrConst(IrType.I32, 1)));
    // An upper bound below the lower one is "Subscript out of range" (error 9): ReDim makes no empty array.
    this._pending.Add(new IrIf(new IrBinary(IrBinaryOp.Less, new IrLocalRef(length), new IrConst(IrType.I32, 1)), new IrBlock([Throw(IrExceptionClass.IndexOutOfRangeException)]), null));
    var created = NewArray(variable.Type, new IrLocalRef(length));
    IrExpr Old() => variable.Local is { } local ? new IrLocalRef(local) : new IrGlobalRef(variable.Global!);
    var statements = new List<IrStmt>();
    if (preserve) {
      var fresh = this.Temporary(variable.Type.Ir, created);
      if (variable.Type.Element!.Kind == VbaKind.Record)
        throw this.Unsupported(line, "ReDim Preserve of an array of records");
      var kept = new IrConditional(new IrBinary(IrBinaryOp.Less, new IrArrayLength(Old()), new IrLocalRef(length)), new IrArrayLength(Old()), new IrLocalRef(length));
      statements.Add(this.CopyElements(variable.Type, Old(), new IrLocalRef(fresh), kept));
      created = new IrLocalRef(fresh);
    }

    statements.Add(variable.Local is { } target ? new IrAssign(target, created) : new IrGlobalAssign(variable.Global!, created));
    return statements;
  }

  private IrStmt Erase(VbaExpr target, int line) {
    if (target is not VbaName name || this.Lookup(name.Name) is not { Type.IsArray: true } variable)
      throw this.Unresolved(line, "Erase of something that is no array variable");
    if (variable.Global is not null)
      throw this.Unsupported(line, $"Erase of the module-level array '{name.Name}', which the IR's globals cannot replace");

    var empty = new IrNewArray(variable.Type.Ir, new IrConst(IrType.I32, 0));
    return variable.Local is { } local ? new IrAssign(local, empty) : new IrGlobalAssign(variable.Global!, empty);
  }

  #endregion

}
