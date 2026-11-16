using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// Trait objects in the IR. A trait a <c>dyn</c> type names is an IR interface whose methods are the
/// ones the object dispatches, its supertraits its base interfaces. A type made a trait object of the
/// trait is a sealed class of one field, <c>value</c>, that implements the interface: each method
/// calls the type's impl with the field, and a <c>&amp;mut self</c> method writes the field back from
/// the copy the impl returns. The object is a reference, as a <c>Box</c> or an <c>Rc</c> is: moving a box
/// moves the reference, cloning an <c>Rc</c> shares it, and nothing can write through an <c>Rc</c> or a
/// <c>&amp;dyn</c>, so the copy the object holds is never told apart from the value it was made from.
/// </summary>
internal sealed partial class ModuleLowering {

  private readonly Dictionary<TraitInfo, (IrInterfaceType Type, Dictionary<string, IrMethod> Methods)> _interfaces = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<string, (IrClassType Type, IrConstructor Constructor)> _objectClasses = new(StringComparer.Ordinal);
  private readonly List<IrFunction> _objectFunctions = [];

  /// <summary>A name for a function of the module that no other function has.</summary>
  private string FunctionName(string wanted) {
    var name = wanted;
    for (var suffix = 2; !this._names.Add(name); ++suffix)
      name = wanted + suffix;

    return name;
  }

  /// <summary>The IR interface of a trait, made once with its base interfaces.</summary>
  public IrInterfaceType Interface(TraitInfo trait) => this.InterfaceOf(trait).Type;

  private (IrInterfaceType Type, Dictionary<string, IrMethod> Methods) InterfaceOf(TraitInfo trait) {
    if (this._interfaces.TryGetValue(trait, out var known))
      return known;

    var visibility = trait.Item.IsPublic ? IrVisibility.Public : IrVisibility.Internal;
    var declaration = new IrInterfaceType(this.TypeName(TypeChecker.Mangled(trait.Module, trait.Name))) { Visibility = visibility };
    var methods = new Dictionary<string, IrMethod>(StringComparer.Ordinal);
    this._interfaces[trait] = (declaration, methods);
    var bases = trait.Supertraits.Select(this.Interface).ToList();
    this._types.Add(declaration);
    var own = new List<IrMethod>();
    // A supertrait's methods are listed with the trait objects of the traits that extend it.
    var declared = this._typed.TraitObjects.Values.SelectMany(methods => methods).Where(method => ReferenceEquals(method.Owner, trait)).DistinctBy(method => method.Item.Name);
    foreach (var method in declared.OrderBy(method => method.Item.At.Line).ThenBy(method => method.Item.At.Column)) {
      var function = this.FunctionName(declaration.Name + "_" + method.Item.Name);
      var parameters = new List<IrLocal> { new("self", IrType.Object(declaration)) };
      parameters.AddRange(method.Parameters.Select((parameter, index) => new IrLocal("arg" + index, this.Ir(parameter))));
      this._objectFunctions.Add(new IrFunction(function, false, this.Result(method.Result), parameters, [], new IrBlock([])));
      var made = new IrMethod(method.Item.Name, function, false) { Dispatch = IrDispatch.Abstract };
      methods[method.Item.Name] = made;
      own.Add(made);
    }

    declaration.DefineMembers(own, [], bases);
    return (declaration, methods);
  }

  /// <summary>The method of the interface of a trait object that a method of its trait or of a supertrait is.</summary>
  public IrMethod InterfaceMethod(DynMethod method) => this.InterfaceOf(method.Owner).Methods[method.Item.Name];

  private IrType Result(RType type) => type.Resolved.Kind is RKind.Unit or RKind.Never ? IrType.Void : this.Ir(type);

  /// <summary>The constructor of the class that makes a value of a type a trait object of a trait, the class made once.</summary>
  public IrConstructor ObjectConstructor(Unsizing unsizing, out IrType type) {
    var key = TypeChecker.Mangled(unsizing.Trait.Module, unsizing.Trait.Name) + " for " + unsizing.Concrete;
    if (!this._objectClasses.TryGetValue(key, out var known)) {
      var word = new string([.. unsizing.Concrete.ToString().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')]).Trim('_');
      var declaration = new IrClassType(this.TypeName($"{this.Interface(unsizing.Trait).Name}_for_{word}")) { IsSealed = true, Visibility = IrVisibility.Internal };
      var field = new IrField("value", this.Ir(unsizing.Concrete), false);
      declaration.Define([field]);
      declaration.Inherit(null, [this.Interface(unsizing.Trait)]);
      this._types.Add(declaration);
      var receiver = IrType.Object(declaration);
      var self = new IrLocal("self", receiver);
      var value = new IrLocal("value", field.Type);
      var constructor = new IrConstructor(this.FunctionName(declaration.Name + "_new"));
      this._objectFunctions.Add(new IrFunction(constructor.Function, false, IrType.Void, [self, value], [], new IrBlock([new IrFieldStore(new IrLocalRef(self), field, new IrLocalRef(value))])));
      var methods = this._typed.TraitObjects[unsizing.Trait].Select((method, index) => this.ObjectMethod(declaration, field, method, unsizing.Impls[index])).ToList();
      declaration.DefineMembers([constructor], methods, [], []);
      known = (declaration, constructor);
      this._objectClasses[key] = known;
    }

    type = IrType.Object(known.Type);
    return known.Constructor;
  }

  /// <summary>A method of the class of a trait object: the impl's method called with the field, the field written back after a <c>&amp;mut self</c> one.</summary>
  private IrMethod ObjectMethod(IrClassType declaration, IrField field, DynMethod method, FunctionSignature impl) {
    var self = new IrLocal("self", IrType.Object(declaration));
    var parameters = new List<IrLocal> { self };
    parameters.AddRange(method.Parameters.Select((parameter, index) => new IrLocal("arg" + index, this.Ir(parameter))));
    var result = this.Result(method.Result);
    IrExpr Field() => new IrFieldLoad(new IrLocalRef(self), field);
    var arguments = new List<IrExpr> { Field() };
    arguments.AddRange(parameters.Skip(1).Select(parameter => (IrExpr)new IrLocalRef(parameter)));
    var statements = new List<IrStmt>();
    var locals = new List<IrLocal>();
    if (FunctionLowering.Outputs(impl).Count == 0) {
      var call = new IrCall(impl.IrName, result, arguments);
      statements.Add(result == IrType.Void ? new IrExprStmt(call) : new IrReturn(call));
    } else {
      var output = this.OutputOf(impl);
      var returned = new IrLocal("returned", IrType.Struct(output));
      locals.Add(returned);
      statements.Add(new IrDeclare(returned, new IrCall(impl.IrName, IrType.Struct(output), arguments)));
      var offset = result == IrType.Void ? 0 : 1;
      statements.Add(new IrFieldStore(new IrLocalRef(self), field, new IrFieldLoad(new IrLocalRef(returned), output.Fields[offset])));
      if (result != IrType.Void)
        statements.Add(new IrReturn(new IrFieldLoad(new IrLocalRef(returned), output.Fields[0])));
    }

    var function = this.FunctionName(declaration.Name + "_" + method.Item.Name);
    this._objectFunctions.Add(new IrFunction(function, false, result, parameters, locals, new IrBlock(statements)));
    return new IrMethod(method.Item.Name, function, false) { Implements = [this.InterfaceMethod(method)] };
  }

}
