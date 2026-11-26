using System.Collections.Generic;
using System.Reflection.Metadata;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>The operand shape carried by a decoded <see cref="IlInstruction"/>.</summary>
internal enum IlOperandKind {
  None,
  Int32,
  Int64,
  Float32,
  Float64,
  BranchTarget,
  Token,
  Switch
}

/// <summary>A single decoded ECMA-335 CIL instruction.</summary>
internal sealed record IlInstruction(
  int Offset,
  int NextOffset,
  ILOpCode OpCode,
  IlOperandKind OperandKind,
  long IntValue = 0,
  double FloatValue = 0,
  int Token = 0,
  IReadOnlyList<int>? SwitchTargets = null) {

  /// <summary>Whether this instruction transfers control unconditionally or terminates a basic block.</summary>
  public bool IsBlockTerminator => this.OpCode is
    ILOpCode.Br or ILOpCode.Br_s or
    ILOpCode.Brfalse or ILOpCode.Brfalse_s or
    ILOpCode.Brtrue or ILOpCode.Brtrue_s or
    ILOpCode.Beq or ILOpCode.Beq_s or
    ILOpCode.Bge or ILOpCode.Bge_s or
    ILOpCode.Bge_un or ILOpCode.Bge_un_s or
    ILOpCode.Bgt or ILOpCode.Bgt_s or
    ILOpCode.Bgt_un or ILOpCode.Bgt_un_s or
    ILOpCode.Ble or ILOpCode.Ble_s or
    ILOpCode.Ble_un or ILOpCode.Ble_un_s or
    ILOpCode.Blt or ILOpCode.Blt_s or
    ILOpCode.Blt_un or ILOpCode.Blt_un_s or
    ILOpCode.Bne_un or ILOpCode.Bne_un_s or
    ILOpCode.Switch or
    ILOpCode.Ret or
    ILOpCode.Throw or ILOpCode.Rethrow or
    ILOpCode.Leave or ILOpCode.Leave_s or
    ILOpCode.Endfinally or ILOpCode.Endfilter;

  /// <summary>Whether execution can fall through to <see cref="NextOffset"/>.</summary>
  public bool FallsThrough => this.OpCode is not (
    ILOpCode.Br or ILOpCode.Br_s or
    ILOpCode.Ret or
    ILOpCode.Throw or ILOpCode.Rethrow or
    ILOpCode.Leave or ILOpCode.Leave_s or
    ILOpCode.Endfinally or ILOpCode.Endfilter);

}

/// <summary>A decoded exception handling region from a method's fat header.</summary>
internal sealed record IlExceptionRegion(
  ExceptionRegionKind Kind,
  int TryOffset,
  int TryEnd,
  int HandlerOffset,
  int HandlerEnd,
  int FilterOffset,
  EntityHandle CatchType);
