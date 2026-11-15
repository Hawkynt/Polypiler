using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// Throws an exception: the object is evaluated and thrown, and control leaves every statement up to
/// the nearest handler whose catch clause selects it, running the <c>finally</c> blocks on the way;
/// without one it leaves the function, and the functions that called it, the same way. A <c>null</c>
/// reference traps with <see cref="IrTrap.NullReference"/>, as C#'s <c>throw null</c> throws a
/// <c>NullReferenceException</c>.
/// <para>
/// The IR states the meaning of exception handling once, here, for every node and every
/// implementation. A trap is the throw of an object of its class (<see cref="IrExceptionClass.Of"/>).
/// The catch clauses of an <see cref="IrTry"/> are tested in order; a clause selects an exception
/// that is an object of its class and whose filter, if it has one, is true. The filters run in a
/// first pass, before any <c>finally</c> between the throw and the handler - the two passes of the
/// CLR (ECMA-335 Partition I, 12.4.2); a filter that throws counts as false. A <c>finally</c> runs on
/// every way out of its <c>try</c>: the end, a <c>break</c>, a <c>continue</c>, a <c>return</c> -
/// whose value is computed before it runs - and an exception; an exception it throws replaces the one
/// in flight. Control never leaves a <c>finally</c> by a jump.
/// </para>
/// </summary>
/// <param name="Exception">The object thrown, a reference to an exception class.</param>
public sealed record IrThrow(IrExpr Exception) : IrStmt;

/// <summary>Throws the exception the innermost enclosing catch clause caught, unchanged: C#'s <c>throw;</c>.</summary>
public sealed record IrRethrow : IrStmt;

/// <summary>
/// A catch clause: selects an exception of <paramref name="Class"/> - an exception class - for which
/// <paramref name="Filter"/> is true, holds it in <paramref name="Local"/> and runs
/// <paramref name="Body"/>. What selecting means is stated at <see cref="IrThrow"/>.
/// </summary>
/// <param name="Class">The class it catches; <see cref="IrExceptionClass.Exception"/> catches every exception.</param>
/// <param name="Local">The local that holds the exception caught, of the type of the class, or <see langword="null"/>; the filter and the body see it.</param>
/// <param name="Filter">A truth value the clause also requires, or <see langword="null"/>.</param>
/// <param name="Body">What runs when the clause selects the exception.</param>
public sealed record IrCatch(IrClassType Class, IrLocal? Local, IrExpr? Filter, IrBlock Body);

/// <summary>
/// Runs <paramref name="Body"/>; an exception it throws is handled by the first of
/// <paramref name="Catches"/> that selects it, and <paramref name="Finally"/> runs on every way out.
/// What that means is stated at <see cref="IrThrow"/>. A try has a catch clause or a finally block, or both.
/// </summary>
/// <param name="Body">The protected statements.</param>
/// <param name="Catches">The catch clauses, in the order they are tested.</param>
/// <param name="Finally">The statements that run on every way out, or <see langword="null"/>.</param>
public sealed record IrTry(IrBlock Body, IReadOnlyList<IrCatch> Catches, IrBlock? Finally) : IrStmt;

/// <summary>
/// A throw that stands where a value is expected - C#'s <c>x ?? throw e</c> and <c>c ? v : throw e</c>:
/// evaluates the exception and throws it, as <see cref="IrThrow"/> does; it has a value of no type, so
/// it may stand for one of any.
/// </summary>
/// <param name="Type">The type of the value it stands for.</param>
/// <param name="Exception">The object thrown.</param>
public sealed record IrThrowExpression(IrType Type, IrExpr Exception) : IrExpr(Type);
