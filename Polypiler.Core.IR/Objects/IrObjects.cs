using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// A new object of a class, made by one of its constructors; the arguments are evaluated left to
/// right first.
/// <para>
/// An object is never a value, always a reference. This is stated here once for every node of the IR
/// and every implementation of it: the object has an identity; an assignment, an argument, a returned
/// value and a field all copy the reference, never the object, so a write of a field through one copy
/// is seen through every other (<see cref="IrReferenceEquals"/> tells copies apart from other objects).
/// A new object's fields hold their defaults - zero, false, the default struct, <c>null</c>, and no
/// array - before the constructor runs; its function is then called with the new object as its first
/// argument and the arguments after it, and the object is the value of the expression.
/// </para>
/// <para>
/// A reference may be <c>null</c> (<see cref="IrNull"/>). Reading or writing a field of
/// <c>null</c>, calling an instance method on it and reading an element or the length of an array
/// field that holds no array trap with <see cref="IrTrap.NullReference"/>: a field read and a method
/// call trap once the target is evaluated, before any argument; a field write - like the setter of a
/// property, which C# and JavaScript both write as an assignment - evaluates its value first.
/// </para>
/// </summary>
/// <param name="Type">The type of the object: a reference to the class.</param>
/// <param name="Constructor">The constructor, one of the class's own.</param>
/// <param name="Arguments">One value per parameter of the constructor after the new object.</param>
public sealed record IrNew(IrType Type, IrConstructor Constructor, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>The reference that names no object.</summary>
/// <param name="Type">The reference type it is a value of.</param>
public sealed record IrNull(IrType Type) : IrExpr(Type);

/// <summary>
/// A call of a method of a class. An instance method is called on the value of <paramref name="Target"/>,
/// which is evaluated first and traps when it is <c>null</c> (see <see cref="IrNew"/>); then the
/// arguments are evaluated left to right and the function of the method is called with the receiver
/// in front of them. A static method has no target. Which method's function runs is what
/// <see cref="Kind"/> selects: the one named for a direct call and a base call, an override of it for
/// a virtual call and an interface call.
/// </summary>
/// <param name="Type">The return type of the method's function.</param>
/// <param name="Method">The method, one of a class of the module.</param>
/// <param name="Target">The receiver of an instance method; <see langword="null"/> for a static one.</param>
/// <param name="Arguments">One value per parameter of the method's function after the receiver.</param>
public sealed record IrMethodCall(IrType Type, IrMethod Method, IrExpr? Target, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type) {

  /// <summary>How the call selects the method that runs.</summary>
  public IrMethodCallKind Kind { get; init; } = IrMethodCallKind.Direct;

}

/// <summary>
/// Whether two references name the same object, or are both <c>null</c>: identity, as C#'s
/// <c>ReferenceEquals</c> and <c>==</c> on a class and JavaScript's <c>===</c> on objects say.
/// Both operands are evaluated, the left first.
/// </summary>
/// <param name="Left">The reference evaluated first.</param>
/// <param name="Right">The reference evaluated second.</param>
public sealed record IrReferenceEquals(IrExpr Left, IrExpr Right) : IrExpr(IrType.Bool);
