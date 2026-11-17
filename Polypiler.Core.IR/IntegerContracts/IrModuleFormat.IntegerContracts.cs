using System.IO;

namespace Polypiler.Core.IR;

public static partial class IrModuleFormat {

  private sealed partial class Writer {

    /// <summary>An integer operation with its contract (#118): the opcode, each of the three contracts as 0 for none or its kind plus one followed by its trap label, then the operands.</summary>
    private void IntegerOp(IrIntegerOp operation) {
      this.Count((int)operation.Op);
      var contract = operation.Contract;
      this.Part(contract.Overflow is { } overflow ? ((int)overflow.Kind, overflow.Label) : null);
      this.Part(contract.Divisor is { } divisor ? ((int)divisor.Kind, divisor.Label) : null);
      this.Part(contract.Shift is { } shift ? ((int)shift.Kind, shift.Label) : null);
      this.Nodes(operation.Operands);
    }

    private void Part((int Kind, IrTrap Label)? part) {
      if (part is not { } present) {
        this.Count(0);
        return;
      }

      this.Count(present.Kind + 1);
      this.Count((int)present.Label);
    }

  }

  private sealed partial class Reader {

    private IrIntegerOp IntegerOp(IrType type) {
      var op = this.Enum<IrIntegerOpcode>();
      var overflow = this.Part<IrOverflowKind>() is { } o ? o.Kind switch {
        IrOverflowKind.Wrap => IrOverflowContract.Wrap,
        IrOverflowKind.Saturate => IrOverflowContract.Saturate,
        _ => IrOverflowContract.Trap(o.Label)
      } : null;
      var divisor = this.Part<IrDivisorKind>() is { } d ? d.Kind == IrDivisorKind.Zero ? IrDivisorContract.Zero : IrDivisorContract.Trap(d.Label) : null;
      var shift = this.Part<IrShiftKind>() is { } s ? s.Kind switch {
        IrShiftKind.Mask => IrShiftContract.Mask,
        IrShiftKind.Unbounded => IrShiftContract.Unbounded,
        IrShiftKind.Strict => IrShiftContract.Strict,
        _ => IrShiftContract.Trap(s.Label)
      } : null;
      return new IrIntegerOp(op, type, this.Nodes<IrExpr>(), new IrIntegerContract(overflow, divisor, shift));
    }

    private (TKind Kind, IrTrap Label)? Part<TKind>() where TKind : struct, System.Enum {
      var kind = this.Count();
      if (kind == 0)
        return null;

      var value = (TKind)(object)(kind - 1);
      if (!System.Enum.IsDefined(value))
        throw new InvalidDataException($"{kind - 1} is no {typeof(TKind).Name}.");

      return (value, this.Enum<IrTrap>());
    }

  }

}
