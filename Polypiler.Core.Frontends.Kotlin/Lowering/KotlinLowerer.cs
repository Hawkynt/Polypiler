using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Kotlin.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Kotlin.Lowering;

/// <summary>A construct outside the imported subset, refused with a diagnostic at its place.</summary>
internal sealed class KotlinRefusal(DiagnosticDescriptor descriptor, KPosition at, string message) : Exception(message) {

  public DiagnosticDescriptor Descriptor { get; } = descriptor;

  public KPosition At { get; } = at;

}

/// <summary>
/// Lowers a parsed Kotlin file to the IR: the functions and properties of the file in two passes -
/// the signatures first, so that a function may call one declared after it, then the bodies. Every
/// construct is lowered with Kotlin's meaning: an expression has the Kotlin type of its operands
/// (<c>Byte + Byte</c> is an <c>Int</c>, <c>Int + Long</c> a <c>Long</c>), and where the IR's
/// operation means something else the lowering says what Kotlin does - the signed quotient and
/// remainder go through a function of the module that gives the smallest value divided by -1 (and 0
/// as its remainder) where the IR's division would trap.
/// </summary>
internal sealed partial class KotlinLowerer(string file, string moduleName) {

  private readonly List<Diagnostic> _diagnostics = [];
  private readonly Dictionary<string, List<Signature>> _functions = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Property> _properties = new(StringComparer.Ordinal);
  private readonly List<IrFunction> _lowered = [];
  private readonly List<IrModuleConst> _constants = [];
  private readonly List<IrGlobal> _globals = [];
  private readonly Dictionary<string, IrFunction> _helpers = new(StringComparer.Ordinal);
  private readonly HashSet<string> _irNames = new(StringComparer.Ordinal);

  /// <summary>What the lowering refused, in source order; empty when the file is in the subset.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics => this._diagnostics;

  /// <summary>A function of the file: its name in the IR, its parameters, their defaults and its result.</summary>
  private sealed record Signature(string Name, string IrName, IReadOnlyList<IrType> Parameters, IReadOnlyList<KExpression?> Defaults, IrType Result, bool IsPublic, KFunction Declaration);

  /// <summary>A property of the file: a constant of the module or a global.</summary>
  private sealed record Property(IrType Type, IrModuleConst? Constant, IrGlobal? Global, bool Mutable);

  private void Report(DiagnosticDescriptor descriptor, KPosition at, string message)
    => this._diagnostics.Add(Diagnostic.Create(descriptor, new SourceRange(file, at.Line, at.Column, at.Line, at.Column), message));

  private static KotlinRefusal Refuse(DiagnosticDescriptor descriptor, KNode node, string message) => new(descriptor, node.At, message);

  /// <summary>Lowers the file.</summary>
  /// <param name="kotlin">The parsed file.</param>
  /// <returns>The module; meaningful only where <see cref="Diagnostics"/> is empty.</returns>
  public IrModule Lower(KFile kotlin) {
    foreach (var declaration in kotlin.Declarations)
      this.Guard(declaration, () => this.Declare(declaration));

    this.FindWritten(kotlin);
    foreach (var declaration in kotlin.Declarations.OfType<KProperty>())
      this.Guard(declaration, () => this.LowerProperty(declaration));

    foreach (var signature in this._functions.Values.SelectMany(overloads => overloads))
      this.Guard(signature.Declaration, () => this._lowered.Add(this.LowerFunction(signature)));

    return new IrModule(moduleName, [.. this._lowered, .. this._helpers.Values], this._constants) { Globals = this._globals };
  }

  /// <summary>Runs a step of the lowering, a refusal reported where it stands.</summary>
  private void Guard(KNode node, Action step) {
    try {
      step();
    } catch (KotlinRefusal refusal) {
      this.Report(refusal.Descriptor, refusal.At, refusal.Message);
    }
  }

  /// <summary>A name of the IR no function, constant or global of the module has yet.</summary>
  private string TakeName(string wanted) {
    var name = wanted;
    for (var suffix = 2; !this._irNames.Add(name); ++suffix)
      name = $"{wanted}_{suffix}";

    return name;
  }

  private void Declare(KDeclaration declaration) {
    switch (declaration) {
      case KUnsupportedDeclaration unsupported:
        throw Refuse(KotlinFrontend.Type, unsupported, $"the {unsupported.Kind} '{unsupported.Name}' is outside the imported subset");
      case KFunction function: {
        var parameters = function.Parameters.Select(parameter => this.TypeOf(parameter.Type!)).ToList();
        var result = function.Result is { } declared ? this.TypeOf(declared) : function.Expression is null ? IrType.Void
          : throw Refuse(KotlinFrontend.Function, function, $"the function '{function.Name}' has an expression body without a declared result type, which the frontend does not infer");
        if (!this._functions.TryGetValue(function.Name, out var overloads))
          this._functions[function.Name] = overloads = [];

        if (overloads.Any(other => other.Parameters.SequenceEqual(parameters)))
          throw Refuse(KotlinFrontend.Function, function, $"the function '{function.Name}' is declared twice with the same parameters");

        var irName = this.TakeName(overloads.Count == 0 ? function.Name : function.Name + "_" + string.Join("_", parameters.Select(type => type.ToString())));
        overloads.Add(new Signature(function.Name, irName, parameters, [.. function.Parameters.Select(parameter => parameter.Default)], result,
          !function.Modifiers.Contains("private"), function));
        break;
      }
      case KProperty property:
        this.TakeName(property.Name);
        break;
    }
  }

  /// <summary>
  /// A property of the file: <c>const val</c> and a <c>val</c> of a constant number, truth value or
  /// string, or of an array of constants nothing writes, is a constant of the module; anything else is
  /// a global with its initializer, initialised in source order.
  /// </summary>
  private void LowerProperty(KProperty property) {
    var type = property.Type is { } declared ? this.TypeOf(declared) : null;
    if (property.Initializer is null)
      throw Refuse(KotlinFrontend.Function, property, $"the property '{property.Name}' has no initializer");

    this.BeginFunction(null);
    var value = this.Expression(property.Initializer, type);
    type ??= value.Type;
    value = this.Coerce(value, type, property.Initializer);
    if (!property.Mutable && this.ConstantOf(property.Name, type, value) is { } constant) {
      this._constants.Add(constant);
      this._properties[property.Name] = new Property(type, constant, null, false);
      return;
    }

    if (this._pending.Count > 0)
      throw Refuse(KotlinFrontend.Function, property, $"the initializer of the property '{property.Name}' needs statements");

    var global = new IrGlobal(property.Name, type, value);
    this._globals.Add(global);
    this._properties[property.Name] = new Property(type, null, global, property.Mutable);
  }

  /// <summary>The constant of the module a value is, or <see langword="null"/> for a value that is computed.</summary>
  private IrModuleConst? ConstantOf(string name, IrType type, IrExpr value) => value switch {
    IrConst constant when type.IsNumeric || type == IrType.Bool => new IrModuleConst(name, type, [constant.Value]),
    IrStrConst text => IrModuleConst.OfStrings(name, IrType.String, [text.Value]),
    IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } array when array.Arguments.All(element => element is IrConst) && type.Element is { IsNumeric: true } or { Kind: IrTypeKind.Bool }
      && !this._written.Contains(name)
      => new IrModuleConst(name, type, [.. array.Arguments.Cast<IrConst>().Select(element => element.Value)]),
    _ => null
  };

  /// <summary>The names of the properties of the file that some function stores into an element of, which makes them no table.</summary>
  private readonly HashSet<string> _written = new(StringComparer.Ordinal);

  /// <summary>
  /// The properties that are no table: an array the file writes an element of or uses as a value
  /// anywhere but in an index, its size, its indices or as the range of a for loop.
  /// </summary>
  private void FindWritten(KFile kotlin) {
    var properties = kotlin.Declarations.OfType<KProperty>().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    foreach (var (node, parent) in KotlinAstWalker.Descendants(kotlin)) {
      switch (node) {
        case KName name when properties.Contains(name.Name) && !(parent is KIndex index && ReferenceEquals(index.Receiver, node)
            || parent is KMember { Name: "size" or "indices" or "lastIndex" } member && ReferenceEquals(member.Receiver, node)
            || parent is KFor loop && ReferenceEquals(loop.Range, node)):
          this._written.Add(name.Name);
          break;
        case KAssignment { Target: KIndex { Receiver: KName target } }:
          this._written.Add(target.Name);
          break;
        case KPrefix { Operator: "++" or "--", Operand: KIndex { Receiver: KName before } }:
          this._written.Add(before.Name);
          break;
        case KPostfix { Operator: "++" or "--", Operand: KIndex { Receiver: KName after } }:
          this._written.Add(after.Name);
          break;
      }
    }
  }

  /// <summary>
  /// A function: its parameters are values, so a parameter is never written; its body lowered with a
  /// scope of its own. An expression body is the return of its value.
  /// </summary>
  private IrFunction LowerFunction(Signature signature) {
    var function = signature.Declaration;
    this.BeginFunction(signature);
    var parameters = new List<IrLocal>();
    for (var index = 0; index < function.Parameters.Count; ++index) {
      var parameter = function.Parameters[index];
      var local = this.NewLocal(parameter.Name, signature.Parameters[index], declare: false);
      this.Scope.Add(parameter.Name, new Variable(local, false));
      parameters.Add(local);
    }

    var body = new List<IrStmt>();
    if (function.Expression is { } expression) {
      if (signature.Result == IrType.Void) {
        this.ExpressionStatement(expression, body);
      } else {
        this.Return(expression, body);
      }
    } else {
      this.Block(function.Body!, body);
      if (signature.Result != IrType.Void && !Terminates(body))
        throw Refuse(KotlinFrontend.Control, function, $"the function '{function.Name}' can reach its end without a return");
    }

    body.InsertRange(0, this._flags.Select(flag => (IrStmt)new IrDeclare(flag, new IrConst(IrType.Bool, 0))));
    return new IrFunction(signature.IrName, signature.IsPublic, signature.Result, parameters, [.. this._locals], new IrBlock(body));
  }

  /// <summary>
  /// Whether statements never complete normally: they end with a return, a throw, an infinite loop
  /// without a break, or an <c>if</c> whose both branches never complete.
  /// </summary>
  private static bool Terminates(IReadOnlyList<IrStmt> statements) => statements.Count > 0 && statements[^1] switch {
    IrReturn or IrThrow => true,
    IrIf { Else: { } otherwise } conditional => Terminates(conditional.Then.Statements) && Terminates(otherwise.Statements),
    IrWhile { Condition: IrConst { Value: 1 } } loop => !Breaks(loop.Body),
    IrDoWhile { Condition: IrConst { Value: 1 } } loop => !Breaks(loop.Body),
    IrSwitch choice => choice.Sections.Any(section => section.IsDefault) && choice.Sections.All(section => Terminates(section.Body.Statements)),
    _ => false
  };

  /// <summary>Whether a loop body has a break of the loop itself: one outside every nested loop and switch.</summary>
  private static bool Breaks(IrStmt statement) => statement switch {
    IrBreak => true,
    IrWhile or IrDoWhile or IrFor or IrSwitch => false,
    IrBlock block => block.Statements.Any(Breaks),
    _ => IrTree.Blocks(statement).Any(Breaks)
  };

}
