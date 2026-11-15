using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// The legalization of 64-bit integers into 32-bit limbs (optimization O0075), for a profile without
/// a 64-bit integer of its own: every operation on a <c>long</c> or <c>ulong</c> becomes the call of
/// its helper of the library (<see cref="HelperOperations"/>, the family of <c>Add64</c>), which works
/// on the limbs. What stays is what the profile writes with the limbs themselves: a constant, a
/// variable, a load and a store, <see cref="IrIntrinsicOp.HighWord"/>, <see cref="IrIntrinsicOp.JoinWords"/>,
/// the low limb as the conversion to a narrower integer and the change of signedness between the two
/// 64-bit types. A step of a variable whose value is used is the call of a lambda that steps it, as the
/// IR has no assignment that is an expression.
/// </summary>
public static class WideIntegerLibrary {

  /// <summary>
  /// A node on 64-bit integers as the call of its helper; <see langword="null"/> for a node that is
  /// none, or that the profile writes with the limbs.
  /// </summary>
  /// <param name="node">The node, its operands legalized.</param>
  /// <param name="calls">Makes the calls.</param>
  /// <returns>An expression of the type of the node that evaluates its operands once, in their order.</returns>
  public static IrExpr? Call(IrExpr node, LibraryCalls calls) {
    ArgumentNullException.ThrowIfNull(calls);
    return node switch {
      IrBinary binary when binary.Left.Type.IsLong => Binary(binary, calls),
      IrUnary unary when unary.Type.IsLong => Unary(unary, calls),
      IrConvert conversion when conversion.Type.IsEnum || conversion.Operand.Type.IsEnum => null,
      IrConvert conversion when conversion.Type.IsLong || conversion.Operand.Type.IsLong => Conversion(conversion, calls),
      IrStep step when step.Type.IsLong => Step(step, calls),
      IrIntrinsic { Op: IrIntrinsicOp.EnumHasFlag } flag when flag.Arguments[0].Type.Integral.IsLong
        => calls.Call("HasFlag64", IrType.Bool, Plain(flag.Arguments[0]), Plain(flag.Arguments[1])),
      IrGenericEquals equality when equality.Left.Type.IsLong => calls.Call("Equal64", IrType.Bool, Plain(equality.Left), Plain(equality.Right)),
      IrGenericCompare order when order.Left.Type.IsLong
        => calls.Call(order.Left.Type.IsSigned ? "CompareSigned64" : "CompareUnsigned64", IrType.I32, Plain(order.Left), Plain(order.Right)),
      _ => null
    };
  }

  /// <summary>A value of an enum as one of its underlying type, which changes no bit; any other value as it is.</summary>
  private static IrExpr Plain(IrExpr value) => value.Type.IsEnum ? new IrConvert(value.Type.Integral, value) : value;

  /// <summary>A result of the underlying type as the enum the node has, or as it is.</summary>
  private static IrExpr Typed(IrExpr value, IrType type) => type.IsEnum ? new IrConvert(type, value) : value;

  private static IrExpr Binary(IrBinary binary, LibraryCalls calls) {
    var (left, right) = (Plain(binary.Left), binary.Right.Type.IsEnum ? Plain(binary.Right) : binary.Right);
    var type = left.Type;
    var signed = type.IsSigned;
    var sign = signed ? "Signed64" : "Unsigned64";
    IrExpr Helper(string name) => Typed(calls.Call(name, binary.Type.Integral, left, right), binary.Type);
    IrExpr Order() => calls.Call("Compare" + sign, IrType.I32, left, right);
    return binary.Op switch {
      IrBinaryOp.Add => Helper("Add64"),
      IrBinaryOp.Subtract => Helper("Subtract64"),
      IrBinaryOp.Multiply => Helper("Multiply64"),
      IrBinaryOp.Divide => Helper("Divide" + sign),
      IrBinaryOp.Remainder => Helper("Remainder" + sign),
      IrBinaryOp.AddChecked => Helper("AddChecked" + sign),
      IrBinaryOp.SubtractChecked => Helper("SubtractChecked" + sign),
      IrBinaryOp.MultiplyChecked => Helper("MultiplyChecked" + sign),
      IrBinaryOp.And => Helper("And64"),
      IrBinaryOp.Or => Helper("Or64"),
      IrBinaryOp.Xor => Helper("Xor64"),
      IrBinaryOp.ShiftLeft => Helper("ShiftLeft64"),
      IrBinaryOp.ShiftRight => Helper(signed ? "ShiftRightArithmetic64" : "ShiftRightLogical64"),
      IrBinaryOp.ShiftRightLogical => Helper("ShiftRightLogical64"),
      IrBinaryOp.Equal => Helper("Equal64"),
      IrBinaryOp.NotEqual => new IrUnary(IrUnaryOp.LogicalNot, Helper("Equal64")),
      _ => new IrBinary(binary.Op, Order(), new IrConst(IrType.I32, 0))
    };
  }

  private static IrExpr Unary(IrUnary unary, LibraryCalls calls) => unary.Op switch {
    IrUnaryOp.Negate => calls.Call("Negate64", unary.Type, unary.Operand),
    IrUnaryOp.NegateChecked => calls.Call(unary.Type.IsSigned ? "SubtractCheckedSigned64" : "SubtractCheckedUnsigned64", unary.Type, new IrConst(unary.Type, 0), unary.Operand),
    _ => Typed(calls.Call("Not64", unary.Type.Integral, Plain(unary.Operand)), unary.Type)
  };

  /// <summary>
  /// A conversion with a 64-bit integer on one side. Between the two 64-bit types and to a narrower
  /// integer it is the bits or the low limb, which a check before it keeps within the bounds where it
  /// traps; to a 64-bit integer from a narrower one it fills the high limb; to and from a
  /// floating-point number it is a helper.
  /// </summary>
  private static IrExpr? Conversion(IrConvert conversion, LibraryCalls calls) {
    var (from, to, value) = (conversion.Operand.Type, conversion.Type, conversion.Operand);
    if (to.IsFloat)
      return calls.Call((to == IrType.F32 ? "ToSingle" : "ToDouble") + (from.IsSigned ? "64" : "Unsigned64"), to, value);

    if (from.IsFloat) {
      var name = conversion.Contract == IrConversion.Trap ? "FromDoubleChecked" : "FromDouble";
      return calls.Call(name + (to.IsSigned ? "64" : "Unsigned64"), to, value);
    }

    if (from.IsLong)
      return conversion.Contract == IrConversion.Trap ? TrapLibrary.Range(conversion, calls) : null;

    // A narrower integer to a 64-bit one: only a negative number can lie outside, which the check
    // against the bounds of the source from 0 on traps on.
    if (conversion.Contract == IrConversion.Trap && from.IsSigned && !to.IsSigned)
      value = calls.Call("CheckRange32", IrType.I32, value, new IrConst(IrType.I32, 0), new IrConst(IrType.I32, (long)IrOperators.Maximum(from)));

    if (value.Type.IsSigned)
      return calls.Call("Widen64", to, value);

    var joined = new IrIntrinsic(IrIntrinsicOp.JoinWords, IrType.I64, [new IrConst(IrType.U32, 0), value.Type == IrType.U32 ? value : new IrConvert(IrType.U32, value)]);
    return to == IrType.I64 ? joined : new IrConvert(to, joined);
  }

  /// <summary>
  /// A step by one: of an array element the helper that steps it; of a variable, or of an element of a
  /// view held in one, the call of a lambda that stores the sum or difference of the helpers and gives
  /// the value before or after. A step of an element of a view that is no variable is kept, for the
  /// backend to report.
  /// </summary>
  private static IrExpr? Step(IrStep step, LibraryCalls calls) {
    var type = step.Type;
    if (step.Target is IrArrayLoad { Array.Type.IsArray: true } element)
      return calls.Call((step.Trap ? "StepElementChecked" : "StepElement") + (type.IsSigned ? "64" : "Unsigned64"), type,
        element.Array, element.Index, Truth(step.Increment), Truth(step.Postfix));

    var name = step.Trap ? (step.Increment ? "AddChecked" : "SubtractChecked") + (type.IsSigned ? "Signed64" : "Unsigned64") : step.Increment ? "Add64" : "Subtract64";
    return Stepped(step, before => calls.Call(name, type, before, new IrConst(type, 1)));
  }

  /// <summary>
  /// A step of a variable, or of an element of a view held in one, as the call of a lambda that stores
  /// the value <paramref name="after"/> makes of the one before and gives the value before or after:
  /// the IR has no assignment that is an expression. A step of an element of a view that is no variable
  /// is <see langword="null"/>, kept for the backend to report.
  /// </summary>
  /// <param name="step">The step.</param>
  /// <param name="after">The value after the step, of the value before it.</param>
  internal static IrExpr? Stepped(IrStep step, Func<IrExpr, IrExpr> after) {
    if (step.Target is IrArrayLoad { Array: not (IrLocalRef or IrGlobalRef) })
      return null;

    var type = step.Type;
    var before = new IrLocal("$before", type);
    var stored = new IrLocal("$after", type);
    var index = new IrLocal("$index", IrType.I32);
    var locals = new List<IrLocal> { before, stored };
    var statements = new List<IrStmt>();
    var current = step.Target;
    if (current is IrArrayLoad load) {
      // The view is a variable, which reading twice is reading once; the index is read once, after it.
      locals.Add(index);
      statements.Add(new IrDeclare(index, load.Index));
      current = load with { Index = new IrLocalRef(index) };
    }

    statements.Add(new IrDeclare(before, current));
    statements.Add(new IrDeclare(stored, after(new IrLocalRef(before))));
    statements.Add(current switch {
      IrLocalRef local => new IrAssign(local.Local, new IrLocalRef(stored)),
      IrGlobalRef global => new IrGlobalAssign(global.Global, new IrLocalRef(stored)),
      _ => new IrStore(((IrArrayLoad)current).Array, new IrLocalRef(index), new IrLocalRef(stored))
    });
    statements.Add(new IrReturn(new IrLocalRef(step.Postfix ? before : stored)));
    var captures = IrTree.Nodes(step.Target).OfType<IrLocalRef>().Select(reference => reference.Local).Distinct().ToList();
    var lambda = new IrLambda(IrType.Function(type), captures, [], locals, new IrBlock(statements));
    return new IrInvoke(type, lambda, []);
  }

  private static IrConst Truth(bool value) => new(IrType.Bool, value ? 1 : 0);

}
