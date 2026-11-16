namespace Polypiler.Core.IR;

/// <summary>How a call reaches a method of a class or an interface.</summary>
public enum IrDispatch {

  /// <summary>A method without a receiver.</summary>
  Static,

  /// <summary>A method with a receiver that no class derived from its own replaces: a call runs it.</summary>
  Instance,

  /// <summary>A method with a body that a class derived from its own may replace with an <see cref="Override"/>.</summary>
  Virtual,

  /// <summary>A method that replaces a virtual, abstract or overriding method of a base class (<see cref="IrMethod.Overrides"/>) for the objects of its class.</summary>
  Override,

  /// <summary>A method without a body, of an abstract class or of an interface, that every class with objects implements.</summary>
  Abstract

}

/// <summary>
/// How an <see cref="IrMethodCall"/> selects the method that runs.
/// <para>
/// The semantics of inheritance are stated here once. A class derives from at most one base class
/// (<see cref="IrClassType.Base"/>): an object of the derived class has the fields of the base class
/// before its own, is an object of the base class as well, and a reference to it converts to a
/// reference to the base class, or to an interface the class or a base class implements, implicitly
/// and without any effect (an upcast). A method of a class is <see cref="IrDispatch"/>
/// <c>Virtual</c>, <c>Abstract</c> or an <c>Override</c> of one of a base class; the methods of one
/// slot - a method and its overrides (<see cref="IrMethod.Root"/>) - have one signature.
/// </para>
/// <para>
/// An object is constructed base first: its fields hold their defaults; a constructor of a derived
/// class calls a constructor of its base class as its first statement (or chains to another of its
/// own, see <see cref="IrConstructor"/>), then writes the field initializers of its class and runs
/// the rest of its body. A constructor that <see cref="IrConstructor.SkipsBase"/> runs no constructor
/// of its lineage at all, and is the only constructor of its class. A C# source, whose derived field initializers run before the base
/// constructor, is lowered so only where the initializers have no effect a base constructor could
/// observe. While any constructor of an object runs, no virtual or interface call is made on it,
/// directly or through a callee: that is where C# and JavaScript differ, since an override would see
/// the fields of its class before or after their initializers. The verifier refuses such a call.
/// </para>
/// </summary>
public enum IrMethodCallKind {

  /// <summary>The method named runs: a static method, or an instance method that is not virtual.</summary>
  Direct,

  /// <summary>
  /// The receiver is evaluated and checked for <c>null</c>; then the method of the receiver's class
  /// of the slot of the method named runs: the override nearest to that class on its way up to the
  /// class of the method named (<see cref="IrClassType.Select"/>).
  /// </summary>
  Virtual,

  /// <summary>
  /// The method named is one of an interface; the receiver's class or the nearest base class with a
  /// method that implements it (<see cref="IrClassType.Implementation"/>) gives that method, and a
  /// virtual call of it runs.
  /// </summary>
  Interface,

  /// <summary>
  /// The method named, a method with a body of a base class of the caller's class, runs on the
  /// caller's receiver, whatever overrides it: <c>base.M(…)</c> of C#, <c>super.m(…)</c> of JavaScript.
  /// </summary>
  Base

}

/// <summary>
/// Whether a reference names an object of a class or of an interface: of the class itself or of one
/// derived from it, or of a class that implements the interface. <c>null</c> is no object of any
/// type; the test is false for it.
/// </summary>
/// <param name="Value">The reference tested.</param>
/// <param name="Target">The class or interface.</param>
public sealed record IrTypeTest(IrExpr Value, IrType Target) : IrExpr(IrType.Bool);

/// <summary>
/// The reference converted to a class or an interface it may name an object of (a downcast): the
/// reference itself when it names an object of that type or is <c>null</c>; any other reference traps
/// with <see cref="IrTrap.InvalidCast"/> before anything later is evaluated.
/// </summary>
/// <param name="Type">The class or interface.</param>
/// <param name="Value">The reference converted.</param>
public sealed record IrCast(IrType Type, IrExpr Value) : IrExpr(Type);

/// <summary>
/// The reference converted to a class or an interface where it names an object of that type, and
/// <c>null</c> where it does not or is <c>null</c>: the <c>as</c> of C#. It never traps.
/// </summary>
/// <param name="Type">The class or interface.</param>
/// <param name="Value">The reference converted.</param>
public sealed record IrAsCast(IrType Type, IrExpr Value) : IrExpr(Type);
