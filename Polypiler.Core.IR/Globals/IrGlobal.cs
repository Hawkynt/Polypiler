namespace Polypiler.Core.IR;

/// <summary>
/// A variable of a module: state that every function of the module reads and writes and that keeps
/// its value from one call to the next. Both languages share the smallest semantics of it: the
/// globals of a module instance are initialised once, in the order of <see cref="IrModule.Globals"/>,
/// before any function of the instance runs; every function sees and changes the same value; there
/// is one thread. A global of an array type holds one array for the whole life of the instance: its
/// elements may change, the array itself is never replaced.
/// <para>
/// The initializer is static: constants, operators on them and on globals declared earlier, a new
/// array of such a length, an array of listed values, and calls of functions of the module that are
/// pure - that read and write no global, directly or through the functions they call. So it observes
/// no global that is not initialised yet, and the order in which a target language runs the
/// initializers of its module cannot be observed.
/// </para>
/// </summary>
/// <param name="Name">The name, unique among the functions, constants and globals of the module.</param>
/// <param name="Type">A truth value, a number, or an array of numbers.</param>
/// <param name="Initializer">
/// The first value, of <paramref name="Type"/>; <see langword="null"/> for the default value, zero or
/// false, which an array has none of.
/// </param>
public sealed record IrGlobal(string Name, IrType Type, IrExpr? Initializer);

/// <summary>The current value of a global of the module; of an array global, the array it holds.</summary>
/// <param name="Global">The global read.</param>
public sealed record IrGlobalRef(IrGlobal Global) : IrExpr(Global.Type);

/// <summary>An assignment to a global of the module that is no array; the value is evaluated first, then stored.</summary>
/// <param name="Global">The global written.</param>
/// <param name="Value">The value stored, of the global's type.</param>
public sealed record IrGlobalAssign(IrGlobal Global, IrExpr Value) : IrStmt;
