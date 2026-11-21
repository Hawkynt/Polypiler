using System.Collections.Generic;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// The operations of MachineIR. The pure ones compute on block-local values; the machine operations
/// read and write the named storage of the profile — registers, flags and memory — in program order;
/// the terminators end a block.
/// </summary>
public enum MachineOpcode {

  /// <summary><c>%x = Const.T imm</c>.</summary>
  Const,

  /// <summary><c>%x = Add.T %a, %b</c>, wrapping.</summary>
  Add,

  /// <summary><c>%x = Sub.T %a, %b</c>, wrapping.</summary>
  Sub,

  /// <summary><c>%x = Mul.T %a, %b</c>, the low half of the product.</summary>
  Mul,

  /// <summary><c>%x = UDiv.T %a, %b</c>: unsigned quotient; a zero divisor raises the trap <c>divide-error</c>.</summary>
  UDiv,

  /// <summary><c>%x = URem.T %a, %b</c>: unsigned remainder; a zero divisor raises the trap <c>divide-error</c>.</summary>
  URem,

  /// <summary><c>%x = SDiv.T %a, %b</c>: two's-complement quotient towards zero; a zero divisor or the smallest value divided by -1 raises <c>divide-error</c>.</summary>
  SDiv,

  /// <summary><c>%x = SRem.T %a, %b</c>: two's-complement remainder with the sign of the dividend; traps as <see cref="SDiv"/>.</summary>
  SRem,

  /// <summary><c>%x = And.T %a, %b</c>.</summary>
  And,

  /// <summary><c>%x = Or.T %a, %b</c>.</summary>
  Or,

  /// <summary><c>%x = Xor.T %a, %b</c>.</summary>
  Xor,

  /// <summary><c>%x = Shl.T %a, %n</c>: a count of the width or more gives 0.</summary>
  Shl,

  /// <summary><c>%x = LShr.T %a, %n</c>: logical; a count of the width or more gives 0.</summary>
  LShr,

  /// <summary><c>%x = AShr.T %a, %n</c>: arithmetic; a count of the width or more gives the sign in every bit.</summary>
  AShr,

  /// <summary><c>%x = Not.T %a</c>: every bit inverted.</summary>
  Not,

  /// <summary><c>%x = Neg.T %a</c>: the two's complement.</summary>
  Neg,

  /// <summary><c>%c = Eq.T %a, %b</c>: a <c>bool</c>.</summary>
  Eq,

  /// <summary><c>%c = Ne.T %a, %b</c>.</summary>
  Ne,

  /// <summary><c>%c = ULt.T %a, %b</c>: unsigned less than.</summary>
  ULt,

  /// <summary><c>%c = ULe.T %a, %b</c>: unsigned less than or equal.</summary>
  ULe,

  /// <summary><c>%c = SLt.T %a, %b</c>: two's-complement less than.</summary>
  SLt,

  /// <summary><c>%c = SLe.T %a, %b</c>: two's-complement less than or equal.</summary>
  SLe,

  /// <summary><c>%x = ZeroExtend.T %v</c>: to a wider type.</summary>
  ZeroExtend,

  /// <summary><c>%x = SignExtend.T %v</c>: to a wider type.</summary>
  SignExtend,

  /// <summary><c>%x = Truncate.T %v</c>: to a narrower type, keeping the low bits.</summary>
  Truncate,

  /// <summary><c>%x = ExtractBits.T %v, offset, bits</c>: the field moved to bit 0, the rest clear.</summary>
  ExtractBits,

  /// <summary><c>%x = InsertBits.T %into, %v, offset, bits</c>: the low bits of <c>%v</c> replace the field.</summary>
  InsertBits,

  /// <summary><c>%x = Select.T %c, %a, %b</c>.</summary>
  Select,

  /// <summary><c>%x = RegRead.T @r</c>: a machine operation.</summary>
  RegRead,

  /// <summary><c>RegWrite.T @r, %v</c>: a machine operation.</summary>
  RegWrite,

  /// <summary><c>%c = FlagRead.bool @f</c>: a machine operation.</summary>
  FlagRead,

  /// <summary><c>FlagWrite @f, %c</c>: a machine operation.</summary>
  FlagWrite,

  /// <summary><c>FlagUndef @f</c>: the flag has no defined value afterwards; a machine operation.</summary>
  FlagUndef,

  /// <summary><c>%x = Load.T %address</c>: in the profile's byte order; a machine operation.</summary>
  Load,

  /// <summary><c>Store.T %address, %v</c>: in the profile's byte order; a machine operation.</summary>
  Store,

  /// <summary><c>Jump L</c>: a terminator.</summary>
  Jump,

  /// <summary><c>Branch %c, Ltrue, Lfalse</c>: a terminator.</summary>
  Branch,

  /// <summary><c>IndirectJump %address -> L1, L2</c> or <c>-> unresolved</c>: a terminator and a machine operation.</summary>
  IndirectJump,

  /// <summary><c>Ret</c>: a terminator.</summary>
  Ret,

  /// <summary>
  /// <c>MachineTrap[kind=…, vector=…, resumes=…, id=…]</c>, optionally with a service contract: the CPU's
  /// exception mechanism, taken unconditionally. A trap that does not resume is a terminator without
  /// successors; a machine operation.
  /// </summary>
  MachineTrap

}

/// <summary>The classes of <see cref="MachineOpcode"/>.</summary>
public static class MachineOpcodes {

  private static readonly HashSet<MachineOpcode> _binary = [
    MachineOpcode.Add, MachineOpcode.Sub, MachineOpcode.Mul, MachineOpcode.UDiv, MachineOpcode.URem, MachineOpcode.SDiv, MachineOpcode.SRem, MachineOpcode.And, MachineOpcode.Or, MachineOpcode.Xor,
    MachineOpcode.Shl, MachineOpcode.LShr, MachineOpcode.AShr
  ];

  private static readonly HashSet<MachineOpcode> _compare = [MachineOpcode.Eq, MachineOpcode.Ne, MachineOpcode.ULt, MachineOpcode.ULe, MachineOpcode.SLt, MachineOpcode.SLe];

  private static readonly HashSet<MachineOpcode> _machine = [
    MachineOpcode.RegRead, MachineOpcode.RegWrite, MachineOpcode.FlagRead, MachineOpcode.FlagWrite, MachineOpcode.FlagUndef,
    MachineOpcode.Load, MachineOpcode.Store, MachineOpcode.IndirectJump, MachineOpcode.MachineTrap
  ];

  /// <summary>Whether an opcode takes two operands of its type and yields its type.</summary>
  public static bool IsBinary(MachineOpcode opcode) => _binary.Contains(opcode);

  /// <summary>Whether an opcode compares two operands of its type and yields a <c>bool</c>.</summary>
  public static bool IsComparison(MachineOpcode opcode) => _compare.Contains(opcode);

  /// <summary>Whether an opcode touches machine state and therefore needs a machine profile.</summary>
  public static bool IsMachineOperation(MachineOpcode opcode) => _machine.Contains(opcode);

  /// <summary>Whether an opcode is a division that raises <c>divide-error</c>.</summary>
  public static bool IsDivision(MachineOpcode opcode) => opcode is MachineOpcode.UDiv or MachineOpcode.URem or MachineOpcode.SDiv or MachineOpcode.SRem;

  /// <summary>Whether an opcode always ends a block; a <see cref="MachineOpcode.MachineTrap"/> ends it when it does not resume.</summary>
  public static bool IsTerminator(MachineOpcode opcode) => opcode is MachineOpcode.Jump or MachineOpcode.Branch or MachineOpcode.IndirectJump or MachineOpcode.Ret;

  /// <summary>Whether an opcode yields a value.</summary>
  public static bool HasResult(MachineOpcode opcode)
    => !IsTerminator(opcode) && opcode is not (MachineOpcode.RegWrite or MachineOpcode.FlagWrite or MachineOpcode.FlagUndef or MachineOpcode.Store or MachineOpcode.MachineTrap);

}
