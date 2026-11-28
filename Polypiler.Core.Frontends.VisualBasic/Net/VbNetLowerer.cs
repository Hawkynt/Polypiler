using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Net;

/// <summary>
/// Lowers the bound operations of one Visual Basic .NET module to the IR. Every local of a procedure is
/// declared at its start with its default value, as Visual Basic gives a local the lifetime of the
/// procedure: a <c>Dim</c> without an initializer inside a loop keeps its value from the pass before.
/// A <c>Dim</c> with one assigns it where it stands.
/// </summary>
internal sealed partial class VbNetLowerer(string file, VisualBasicCompilation compilation, SyntaxTree tree, bool checks, List<Diagnostic> diagnostics) {

  private readonly SemanticModel _model = compilation.GetSemanticModel(tree);
  private readonly Dictionary<ITypeSymbol, IrStructType> _structs = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<IFieldSymbol, IrField> _fields = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<IFieldSymbol, IrGlobal> _globals = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<IMethodSymbol, (string Name, IrType Returns)> _functions = new(SymbolEqualityComparer.Default);
  private readonly HashSet<string> _caveats = new(StringComparer.Ordinal);
  private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
  private INamedTypeSymbol? _module;

  /// <summary>Whether the file compares strings as text, which the IR has no comparison of.</summary>
  private bool CompareText => tree.GetRoot().DescendantNodes().OfType<OptionStatementSyntax>().LastOrDefault(option => option.NameKeyword.IsKind(SyntaxKind.CompareKeyword)) is { } compare
    ? compare.ValueKeyword.IsKind(SyntaxKind.TextKeyword) : compilation.Options.OptionCompareText;

  /// <summary>Reports a construct outside the subset, once per construct and line.</summary>
  private void Refuse(SyntaxNode? node, string construct) {
    var line = node?.GetLocation().GetLineSpan().StartLinePosition.Line + 1 ?? 1;
    if (this._refused.Add($"{line}:{construct}"))
      diagnostics.Add(VbNetFrontend.Unsupported.At(construct, file, line, (node?.GetLocation().GetLineSpan().StartLinePosition.Character ?? 0) + 1));
  }

  public IrModule? Lower() {
    var modules = this.Types().Where(type => type.TypeKind == TypeKind.Module).ToList();
    if (modules.Count != 1) {
      this.Refuse(tree.GetRoot(), modules.Count == 0 ? "a file without a Module" : "a file of several Modules");
      return null;
    }

    this._module = modules[0];
    foreach (var type in this.Types().Where(type => type.TypeKind != TypeKind.Module))
      if (type.TypeKind == TypeKind.Structure)
        this._structs[type] = new IrStructType(type.Name);
      else if (type.TypeKind != TypeKind.Enum)
        this.Refuse(type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(), $"the {type.TypeKind.ToString().ToLowerInvariant()} '{type.Name}'");

    foreach (var (symbol, declaration) in this._structs)
      declaration.Define([.. symbol.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic && !field.IsImplicitlyDeclared).Select(field => this._fields[field] = new IrField(field.Name, this.Type(field.Type, field), field.IsReadOnly))]);
    foreach (var symbol in this._structs.Keys)
      foreach (var member in symbol.GetMembers().Where(member => !member.IsImplicitlyDeclared && member is not IFieldSymbol { IsStatic: false } && !(member is IMethodSymbol { MethodKind: MethodKind.Constructor, Parameters.Length: 0 })))
        this.Refuse(member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(), $"the member '{member.Name}' of the structure '{symbol.Name}'");

    var methods = this._module.GetMembers().OfType<IMethodSymbol>().Where(method => method.MethodKind == MethodKind.Ordinary).ToList();
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var method in methods) {
      var name = method.Name;
      for (var index = 2; !names.Add(name); ++index)
        name = method.Name + "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
      this._functions[method] = (name, method.ReturnsVoid ? IrType.Void : this.Type(method.ReturnType, method));
    }

    foreach (var member in this._module.GetMembers().Where(member => !member.IsImplicitlyDeclared && member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } and not IFieldSymbol and not INamedTypeSymbol))
      this.Refuse(member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(), $"the member '{member.Name}' of the module");

    // A module variable is initialized in the order of the source; an initializer reads the ones before it.
    var globals = new List<IrGlobal>();
    foreach (var field in this._module.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsConst && !field.IsImplicitlyDeclared)) {
      var type = this.Type(field.Type, field);
      IrExpr? value = null;
      if (field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ModifiedIdentifierSyntax { Parent: VariableDeclaratorSyntax declarator } identifier) {
        this.Begin(null);
        if (declarator.Initializer is { } initializer && this._model.GetOperation(initializer) is IFieldInitializerOperation operation)
          value = this.Value(operation.Value);
        else if (identifier.ArrayBounds is { Arguments: [var bound] } && this._model.GetConstantValue(bound.GetExpression()) is { HasValue: true, Value: int upper } && type.Element is { IsString: false })
          value = new IrNewArray(type, new IrConst(IrType.I32, upper + 1L));
        else if (identifier.ArrayBounds is not null)
          this.Refuse(identifier, "the bounds of a module array that are no constant, or of an array of strings");
      }

      var global = new IrGlobal(field.Name, type, value);
      this._globals[field] = global;
      globals.Add(global);
    }

    var functions = methods.Select(this.Function).ToList();
    return new IrModule(this._module.Name, functions, []) {
      Types = [.. this._structs.Values],
      Globals = globals,
      Caveats = [.. this._caveats.Order(StringComparer.Ordinal)]
    };
  }

  private IEnumerable<INamedTypeSymbol> Types() {
    return tree.GetRoot().DescendantNodes().OfType<TypeBlockSyntax>().Select(block => this._model.GetDeclaredSymbol(block)).OfType<INamedTypeSymbol>()
      .Concat(tree.GetRoot().DescendantNodes().OfType<EnumBlockSyntax>().Select(block => this._model.GetDeclaredSymbol(block)).OfType<INamedTypeSymbol>())
      .Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>();
  }

  /// <summary>The IR type of a Visual Basic type; <c>Integer</c> after refusing one the IR has no form of.</summary>
  private IrType Type(ITypeSymbol type, ISymbol? where = null) {
    switch (type) {
      case IArrayTypeSymbol { Rank: 1, ElementType: IArrayTypeSymbol }:
        this.Refuse(where?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(), "an array of arrays");
        return IrType.Array(IrType.I32);
      case IArrayTypeSymbol { Rank: 1 } array:
        return IrType.Array(this.Type(array.ElementType, where));
      case INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }:
        return this.Type(underlying, where);
      case INamedTypeSymbol named when this._structs.TryGetValue(named, out var declaration):
        return IrType.Struct(declaration);
      case INamedTypeSymbol named when ExceptionClass(named) is { } exception:
        return IrType.Object(exception);
    }

    switch (type.SpecialType) {
      case SpecialType.System_Boolean: return IrType.Bool;
      case SpecialType.System_SByte: return IrType.I8;
      case SpecialType.System_Byte: return IrType.U8;
      case SpecialType.System_Int16: return IrType.I16;
      case SpecialType.System_UInt16: return IrType.U16;
      case SpecialType.System_Char: return IrType.U16;
      case SpecialType.System_Int32: return IrType.I32;
      case SpecialType.System_UInt32: return IrType.U32;
      case SpecialType.System_Int64: return IrType.I64;
      case SpecialType.System_UInt64: return IrType.U64;
      case SpecialType.System_Single: return IrType.F32;
      case SpecialType.System_Double: return IrType.F64;
      case SpecialType.System_String:
        this._caveats.Add(VbNetFrontend.NothingString);
        return IrType.String;
      default:
        this.Refuse(where?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(), $"the type {type.ToDisplayString()}");
        return IrType.I32;
    }
  }

  /// <summary>The library exception class of the IR a .NET exception type is, or <see langword="null"/>.</summary>
  private static IrClassType? ExceptionClass(ITypeSymbol type)
    => type.ContainingNamespace?.ToDisplayString() is "System" or "System.Collections.Generic"
      ? IrExceptionClass.All.FirstOrDefault(candidate => candidate.Name == type.Name && candidate != IrExceptionClass.AssertionException) : null;

  /// <summary>The default value of a type: zero, <c>False</c>, the empty string, a structure of defaults, or <c>Nothing</c> of an array or an exception.</summary>
  private static IrExpr Default(IrType type) => type switch {
    { IsStruct: true } => new IrStructDefault(type),
    { IsString: true } => new IrStrConst(""), { IsArray: true } or { IsObject: true } => new IrNull(type),
    { IsFloat: true } => new IrConst(type, IrOperators.Bits(type, 0.0)),
    _ => new IrConst(type, 0)
  };

  private IrFunction Function(IMethodSymbol method) {
    this.Begin(method);
    var syntax = method.DeclaringSyntaxReferences[0].GetSyntax();
    var block = syntax.Parent as MethodBlockBaseSyntax ?? syntax as MethodBlockBaseSyntax;
    var parameters = method.Parameters.Select(parameter => this.Local(parameter, parameter.Type)).ToList();
    foreach (var parameter in method.Parameters.Where(parameter => parameter.RefKind != RefKind.None))
      if (block is not null && this.Assigned(block, parameter))
        this.Refuse(parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(), $"the ByRef parameter '{parameter.Name}', which the procedure assigns");
    foreach (var parameter in method.Parameters.Where(parameter => parameter.IsParams))
      this.Refuse(parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(), $"the ParamArray parameter '{parameter.Name}'");

    List<IrStmt> body = [];
    if (block is not null && this._model.GetOperation(block) is IBlockOperation operation) {
      this._exit = operation.Descendants().OfType<ILabeledOperation>().LastOrDefault(label => label.IsImplicit)?.Label;
      this.Block(operation, body);
    }

    // Every local lives as long as the procedure: declared first, with its default value; one nothing
    // reads - the function's name of a function that ends with Return - is left out with its assignments.
    var read = body.SelectMany(IrTree.Nodes).OfType<IrLocalRef>().Select(reference => reference.Local).ToHashSet();
    body = [.. body.Select(statement => Unread(statement, read)).OfType<IrStmt>()];
    var declared = this._locals.Where(pair => pair.Key is ILocalSymbol && !this._caught.Contains(pair.Value) && read.Contains(pair.Value)).Select(pair => pair.Value).ToList();
    var declarations = declared.Select(local => (IrStmt)new IrDeclare(local, Default(local.Type))).ToList();
    var locals = declared.Concat(this._caught).Concat(this._temporaries).ToList();
    var (name, returns) = this._functions[method];
    return new IrFunction(name, method.DeclaredAccessibility == Accessibility.Public, returns, parameters, locals, new IrBlock([.. declarations, .. body]));
  }

  /// <summary>A statement without its assignments to locals nothing reads, their values kept where they could have an effect.</summary>
  private static IrStmt? Unread(IrStmt statement, HashSet<IrLocal> read) {
    if (statement is IrAssign assign && !read.Contains(assign.Local) && !assign.Local.Name.StartsWith("ply", StringComparison.Ordinal))
      return assign.Value is IrConst or IrStrConst or IrLocalRef ? null : new IrExprStmt(assign.Value);

    var blocks = IrTree.Blocks(statement);
    return blocks.Count == 0 ? statement
      : IrTree.With(statement, IrTree.Expressions(statement), [.. blocks.Select(block => new IrBlock([.. block.Statements.Select(inner => Unread(inner, read)).OfType<IrStmt>()]))]);
  }

  /// <summary>Whether a parameter is assigned or passed on by reference in a procedure.</summary>
  private bool Assigned(MethodBlockBaseSyntax block, IParameterSymbol parameter)
    => this._model.GetOperation(block)?.Descendants().Any(operation => operation switch {
      IAssignmentOperation { Target: IParameterReferenceOperation target } => SymbolEqualityComparer.Default.Equals(target.Parameter, parameter),
      IArgumentOperation { Parameter.RefKind: not RefKind.None, Value: IParameterReferenceOperation passed } => SymbolEqualityComparer.Default.Equals(passed.Parameter, parameter),
      _ => false
    }) == true;

}
