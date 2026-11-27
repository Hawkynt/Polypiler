using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Batch;

public static partial class BatchEmitter {

  private static readonly IrType _stringMap = IrType.Map(IrType.String, IrType.I32, MapOrder.SlotReuse);

  private sealed partial class Writer {

    private const string _missingKeyLabel = "_ply_trap_key_not_found";
    private const string _duplicateKeyLabel = "_ply_trap_duplicate_key";

    private readonly Dictionary<string, int>? _mapKeyIds;
    private int _nextMapId;
    private bool _needsMissingKeyTrap;
    private bool _needsDuplicateKeyTrap;

    private static Dictionary<string, int>? CollectMapKeys(IrFunction function) {
      var operations = IrTree.Nodes(function.Body).OfType<IrCollection>().ToArray();
      if (!function.Locals.Any(local => local.Type == _stringMap)
          && !operations.Any(operation => operation.Type == _stringMap))
        return null;

      var keys = new Dictionary<string, int>(System.StringComparer.Ordinal);
      foreach (var operation in operations)
        if (operation.Op is IrCollectionOp.MapGet or IrCollectionOp.MapPut or IrCollectionOp.MapAdd or IrCollectionOp.MapContainsKey
            && operation.Arguments[0].Type == _stringMap)
          Collect(operation.Arguments[1]);

      foreach (var statement in IrSequences.Statements(function.Body))
        switch (statement) {
          case IrDeclare { Local.Type: var type, Initializer: { } initializer } when type == IrType.String:
            Collect(initializer);
            break;
          case IrAssign { Local.Type: var type } assignment when type == IrType.String:
            Collect(assignment.Value);
            break;
        }
      return keys;

      void Collect(IrExpr expression) {
        switch (expression) {
          case IrStrConst constant:
            keys.TryAdd(constant.Value, keys.Count);
            break;
          case IrConditional choice:
            Collect(choice.WhenTrue);
            Collect(choice.WhenFalse);
            break;
        }
      }
    }

    private void CreateMap(string destination) {
      if (this._loopDepth > 0)
        throw new Refusal("map allocation inside a loop");
      var keyCount = this._mapKeyIds!.Count;
      this.ReserveCollectionSlots(keyCount + 1, "map storage");
      var identity = (++this._nextMapId).ToString(CultureInfo.InvariantCulture);
      if (keyCount > 0) {
        var last = (keyCount - 1).ToString(CultureInfo.InvariantCulture);
        this.Line($"for /l %%K in (0,1,{last}) do set \"_ply_m{identity}_k%%K=\"");
      }
      this.Assign($"_ply_mc{identity}", "0");
      this.Assign(destination, identity);
    }

    private string MapReference(IrCollection operation, int slot) {
      if (operation.Arguments[0].Type != _stringMap)
        throw new Refusal($"{operation.Op} on a collection of type {operation.Arguments[0].Type}");
      return this.Value(operation.Arguments[0], slot);
    }

    private (string Map, string Entry) MapEntry(IrCollection operation, int slot) {
      var map = this.MapReference(operation, slot);
      var key = this.Value(operation.Arguments[1], slot + 1);
      return (map, $"_ply_m%{map}%_k%{key}%");
    }

    private void ReadMap(string destination, IrCollection operation, int slot) {
      switch (operation.Op) {
        case IrCollectionOp.MapNew when operation.Type == _stringMap:
          this.CreateMap(destination);
          break;
        case IrCollectionOp.MapCount:
          this.Assign(destination, $"_ply_mc%{this.MapReference(operation, slot)}%");
          break;
        case IrCollectionOp.MapGet:
          var (_, entry) = this.MapEntry(operation, slot);
          this._needsMissingKeyTrap = true;
          this.Line($"if not defined {entry} goto {_missingKeyLabel}");
          this.Assign(destination, entry);
          break;
        default:
          throw new Refusal(operation.Op.ToString());
      }
    }

    private void WriteMap(IrCollection operation) {
      var (map, entry) = this.MapEntry(operation, 0);
      var value = this.Value(operation.Arguments[2], 2);
      var count = $"_ply_mc%{map}%";
      if (operation.Op == IrCollectionOp.MapAdd) {
        this._needsDuplicateKeyTrap = true;
        this.Line($"if defined {entry} goto {_duplicateKeyLabel}");
        this.Assign(count, $"{count}+1");
      } else {
        var write = this.Label();
        this.Line($"if defined {entry} goto {write}");
        this.Assign(count, $"{count}+1");
        this.Mark(write);
      }
      this.Assign(entry, value);
    }

  }

}
