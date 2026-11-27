using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Batch;

public static partial class BatchEmitter {

  private sealed partial class UnifiedEmitter {

    private sealed partial class FunctionWriter {

      private sealed record TryContext(string UnwindLabel, string? FinallyLabel, int FinIndex, string? CatchExitLabel);
      private sealed record LoopTargets(string? Break, string? Continue, int TryDepthAtLoop);

      private readonly UnifiedEmitter _owner;
      private readonly IrFunction _function;
      private readonly bool _useSetlocal;
      private readonly bool _isIterator;
      private readonly IReadOnlyList<IrLocal>? _captures;
      private readonly string _label;
      private readonly string _exitLabel;
      private readonly Dictionary<IrLocal, int> _localIds = [];
      private readonly HashSet<IrLocal> _capturedLocals = [];
      private readonly HashSet<IrLocal> _addressTakenLocals = [];
      private readonly Stack<TryContext> _tryStack = new();
      private readonly bool _inForEachCallback;
      private int _nextFinIndex;

      public List<string> Lines { get; } = [];

      public FunctionWriter(
        UnifiedEmitter owner,
        IrFunction function,
        bool useSetlocal,
        bool inForEachCallback,
        IReadOnlyList<IrLocal>? captures,
        string? explicitLabel,
        Dictionary<IrLocal, int>? sharedLocals = null,
        HashSet<IrLocal>? sharedCaptured = null,
        HashSet<IrLocal>? sharedAddressTaken = null) {
        this._owner = owner;
        this._function = function;
        this._useSetlocal = useSetlocal;
        this._isIterator = !inForEachCallback && captures is null && IrSequences.IsIterator(function);
        this._captures = captures;
        this._inForEachCallback = inForEachCallback;
        this._label = explicitLabel ?? owner._functionLabels[function.Name];
        this._exitLabel = "_ret_" + this._label;

        if (sharedLocals is not null) {
          foreach (var (k, v) in sharedLocals)
            this._localIds[k] = v;
        } else {
          if (captures is not null)
            foreach (var cap in captures)
              this.RegisterLocal(cap);
          foreach (var param in function.Parameters)
            this.RegisterLocal(param);
          foreach (var local in function.Locals)
            this.RegisterLocal(local);
          this.CollectExtraLocals(function.Body);
        }

        if (sharedCaptured is not null) {
          this._capturedLocals.UnionWith(sharedCaptured);
        } else {
          if (captures is not null)
            this._capturedLocals.UnionWith(captures);
          this.CollectCapturedLocals(function.Body);
        }

        if (sharedAddressTaken is not null) {
          this._addressTakenLocals.UnionWith(sharedAddressTaken);
        } else {
          this.CollectAddressTakenLocals(function.Body);
        }
      }

      private void RegisterLocal(IrLocal local) {
        if (!this._localIds.ContainsKey(local))
          this._localIds[local] = this._localIds.Count;
      }

      private void CollectExtraLocals(IrBlock block) {
        foreach (var stmt in IrSequences.Statements(block)) {
          switch (stmt) {
            case IrDeclare decl:
              this.RegisterLocal(decl.Local);
              break;
            case IrUsing use:
              this.RegisterLocal(use.Resource);
              break;
            case IrTry attempt:
              foreach (var c in attempt.Catches)
                if (c.Local is { } cl)
                  this.RegisterLocal(cl);
              break;
            case IrForEachCollection fe:
              if (fe.Element is { } el)
                this.RegisterLocal(el);
              if (fe.Value is { } val)
                this.RegisterLocal(val);
              break;
            case IrForEachSequence fes:
              this.RegisterLocal(fes.Element);
              break;
          }
        }
      }

      private void CollectCapturedLocals(IrBlock block) {
        foreach (var node in IrTree.Nodes(block))
          if (node is IrLambda lambda) {
            foreach (var cap in lambda.Captures)
              this._capturedLocals.Add(cap);
            this.CollectCapturedLocals(lambda.Body);
          }
      }

      private void CollectAddressTakenLocals(IrBlock block) {
        foreach (var node in IrTree.Nodes(block))
          if (node is IrIntrinsic { Op: IrIntrinsicOp.AddressOf, Arguments: [IrLocalRef localRef] })
            this._addressTakenLocals.Add(localRef.Local);
      }

      private void Line(string text) => this.Lines.Add(text);
      private void Mark(string label) => this.Line($":{label}");
      private void Jump(string label) => this.Line($"goto :{label}");

      private string SlotVar(int slot)
        => this._useSetlocal ? "_t" + slot.ToString(CultureInfo.InvariantCulture) : "_F%%S_t" + slot.ToString(CultureInfo.InvariantCulture);

      private string LocalVar(IrLocal local)
        => this._useSetlocal ? "_v" + this._localIds[local].ToString(CultureInfo.InvariantCulture) : "_F%%S_v" + this._localIds[local].ToString(CultureInfo.InvariantCulture);

      private string FinVar(int finIndex)
        => this._useSetlocal ? "_fn" + finIndex.ToString(CultureInfo.InvariantCulture) : "_F%%S_fn" + finIndex.ToString(CultureInfo.InvariantCulture);

      private void EmitFrame(string command) {
        if (this._useSetlocal)
          this.Line(command);
        else
          this.Line($"for %%S in (!_SP!) do {command}");
      }

      private void EmitSetSlotLiteral(int slot, string literal) => this.EmitFrame($"set \"{this.SlotVar(slot)}={literal}\"");

      private void EmitCopySlot(int dst, int src) {
        if (dst != src)
          this.EmitFrame($"set \"{this.SlotVar(dst)}=!{this.SlotVar(src)}!\"");
      }

      private void EmitSlotToVar(int slot, string targetVar) => this.EmitFrame($"set \"{targetVar}=!{this.SlotVar(slot)}!\"");

      private void EmitVarToSlot(string sourceVar, int slot) => this.EmitFrame($"set \"{this.SlotVar(slot)}=!{sourceVar}!\"");

      public void EmitReadSlotToGlobal(int slot, string globalVar) => this.EmitSlotToVar(slot, globalVar);

      private string CurrentUnwindLabel()
        => this._tryStack.Count > 0 ? this._tryStack.Peek().UnwindLabel : this._exitLabel;

      private void EmitCheckUnwind() {
        if (!this._useSetlocal)
          this.Line($"if \"!_EX_ACTIVE!\"==\"1\" goto :{this.CurrentUnwindLabel()}");
      }

      private void EmitTrap(IrClassType exClass, string trapName) {
        this._owner.RequireHelper("trap");
        this.Line($"call :__ply_trap {this._owner._classIds[exClass]} {trapName}");
        this.Jump(this.CurrentUnwindLabel());
      }

      public void EmitFunction() {
        this.Mark(this._label);
        if (this._useSetlocal) {
          this.Line("setlocal EnableExtensions EnableDelayedExpansion");
        } else {
          this.Line("set /a \"_SP+=1\"");
        }

        if (this._isIterator) {
          // Sequence call receives sequence record ID in %~1; unpack captured parameters from _O_<id>_a<i>.
          this.Line("set \"_seq_id=%~1\"");
          for (var i = 0; i < this._function.Parameters.Count; ++i) {
            var p = this._function.Parameters[i];
            this.Line($"for %%Q in (!_seq_id!) do set \"_parg=!_O_%%Q_a{i}!\"");
            this.EmitInitParamFromVar(p, "_parg");
          }
        } else if (this._captures is not null) {
          // Closure call: %~1 is closure record ID; %~2.. are parameters (using shift if > 8 params).
          this.Line("set \"_clos_id=%~1\"");
          for (var i = 0; i < this._captures.Count; ++i) {
            var cap = this._captures[i];
            this.EmitFrame($"for %%C in (!_clos_id!) do set \"{this.LocalVar(cap)}=!_O_%%C_c{i}!\"");
          }

          if (this._function.Parameters.Count > 0) {
            this.Line("shift");
            this.EmitBindPositionalParameters();
          }
        } else {
          this.EmitBindPositionalParameters();
        }

        this.EmitBlock(this._function.Body, new(null, null, 0));

        this.Mark(this._exitLabel);
        if (this._useSetlocal) {
          if (this._function.ReturnType == IrType.Void) {
            this.Line("endlocal");
          } else {
            this.Line("(");
            this.Line("  endlocal");
            this.Line("  set \"_RET=%_RET%\"");
            this.Line(")");
          }
        } else {
          this.Line("set /a \"_SP-=1\"");
        }

        this.Line("goto :eof");
      }

      private void EmitBindPositionalParameters() {
        for (var i = 0; i < this._function.Parameters.Count; ++i) {
          var p = this._function.Parameters[i];
          var argPos = i < 9 ? (i + 1).ToString(CultureInfo.InvariantCulture) : "9";
          if (i >= 9)
            this.Line("shift");

          if (this._capturedLocals.Contains(p) || this._addressTakenLocals.Contains(p) || p.Type.IsStruct) {
            this.Line($"set \"_parg=%~{argPos}\"");
            this.EmitInitParamFromVar(p, "_parg");
          } else {
            this.EmitFrame($"set \"{this.LocalVar(p)}=%~{argPos}\"");
          }
        }
      }

      private void EmitInitParamFromVar(IrLocal param, string srcVar) {
        if (param.Type.IsStruct) {
          var sid = this._owner._structIds[param.Type.StructType!];
          this._owner._neededStructCopies.Add(param.Type.StructType!);
          this.Line($"call :__ply_copy_s{sid} \"!{srcVar}!\"");
          this.Line($"set \"{srcVar}=!_RET!\"");
        }

        if (this._capturedLocals.Contains(param)) {
          this.Line("set /a \"_HID+=1\"");
          this.Line($"set \"_O_!_HID!_v=!{srcVar}!\"");
          this.EmitFrame($"set \"{this.LocalVar(param)}=!_HID!\"");
        } else if (this._addressTakenLocals.Contains(param)) {
          this.Line("set /a \"_HID+=1\"");
          this.Line("set \"_AL_!_HID!=1\"");
          this.Line($"set \"_A_!_HID!_0=!{srcVar}!\"");
          this.EmitFrame($"set \"{this.LocalVar(param)}=!_HID!\"");
        } else {
          this.EmitFrame($"set \"{this.LocalVar(param)}=!{srcVar}!\"");
        }
      }

      private void EmitReadLocal(IrLocal local, int dstSlot) {
        if (this._capturedLocals.Contains(local)) {
          this.EmitFrame($"for %%C in (!{this.LocalVar(local)}!) do set \"{this.SlotVar(dstSlot)}=!_O_%%C_v!\"");
        } else if (this._addressTakenLocals.Contains(local)) {
          this.EmitFrame($"for %%A in (!{this.LocalVar(local)}!) do set \"{this.SlotVar(dstSlot)}=!_A_%%A_0!\"");
        } else {
          this.EmitFrame($"set \"{this.SlotVar(dstSlot)}=!{this.LocalVar(local)}!\"");
        }
      }

      private void EmitWriteLocal(IrLocal local, int srcSlot, bool copyStruct = true) {
        var effectiveSlot = srcSlot;
        if (copyStruct && local.Type.IsStruct) {
          var sid = this._owner._structIds[local.Type.StructType!];
          this._owner._neededStructCopies.Add(local.Type.StructType!);
          this.EmitSlotToVar(srcSlot, "_cs_in");
          this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
          this.EmitVarToSlot("_RET", srcSlot);
        }

        if (this._capturedLocals.Contains(local)) {
          this.EmitFrame($"for %%C in (!{this.LocalVar(local)}!) do set \"_O_%%C_v=!{this.SlotVar(effectiveSlot)}!\"");
        } else if (this._addressTakenLocals.Contains(local)) {
          this.EmitFrame($"for %%A in (!{this.LocalVar(local)}!) do set \"_A_%%A_0=!{this.SlotVar(effectiveSlot)}!\"");
        } else {
          this.EmitFrame($"set \"{this.LocalVar(local)}=!{this.SlotVar(effectiveSlot)}!\"");
        }
      }

      private void EmitDeclareLocal(IrLocal local, IrExpr? initializer) {
        if (this._capturedLocals.Contains(local)) {
          if (initializer is not null) {
            this.EmitExpr(initializer, 0);
            if (local.Type.IsStruct) {
              var sid = this._owner._structIds[local.Type.StructType!];
              this._owner._neededStructCopies.Add(local.Type.StructType!);
              this.EmitSlotToVar(0, "_cs_in");
              this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
              this.EmitVarToSlot("_RET", 0);
            }
          } else {
            this.EmitDefaultValue(local.Type, 0);
          }

          this.Line("set /a \"_HID+=1\"");
          this.EmitFrame($"set \"_O_!_HID!_v=!{this.SlotVar(0)}!\"");
          this.EmitFrame($"set \"{this.LocalVar(local)}=!_HID!\"");
        } else if (this._addressTakenLocals.Contains(local)) {
          if (initializer is not null)
            this.EmitExpr(initializer, 0);
          else
            this.EmitDefaultValue(local.Type, 0);

          this.Line("set /a \"_HID+=1\"");
          this.Line("set \"_AL_!_HID!=1\"");
          this.EmitFrame($"set \"_A_!_HID!_0=!{this.SlotVar(0)}!\"");
          this.EmitFrame($"set \"{this.LocalVar(local)}=!_HID!\"");
        } else if (initializer is not null) {
          this.EmitExpr(initializer, 0);
          this.EmitWriteLocal(local, 0, copyStruct: true);
        } else if (local.Type.IsStruct && local.Type.StructType!.HasDefault) {
          this.EmitDefaultValue(local.Type, 0);
          this.EmitWriteLocal(local, 0, copyStruct: false);
        }
      }

      public void EmitDefaultValue(IrType type, int dstSlot) {
        if (type.IsOptional) {
          this.EmitSetSlotLiteral(dstSlot, _noneToken);
        } else if (type.IsNullable) {
          this.EmitSetSlotLiteral(dstSlot, _nullToken);
        } else if (type.Integral.IsLong) {
          this.EmitSetSlotLiteral(dstSlot, "0:0");
        } else if (type.IsStruct) {
          this.EmitStructDefault(type.StructType!, dstSlot);
        } else {
          this.EmitSetSlotLiteral(dstSlot, "0");
        }
      }

      private void EmitBlock(IrBlock block, LoopTargets targets) {
        foreach (var statement in block.Statements)
          this.EmitStatement(statement, targets);
      }

      private void EmitStatement(IrStmt statement, LoopTargets targets) {
        switch (statement) {
          case IrBlock nested:
            this.EmitBlock(nested, targets);
            break;
          case IrDeclare decl:
            this.EmitDeclareLocal(decl.Local, decl.Initializer);
            break;
          case IrAssign assign:
            this.EmitExpr(assign.Value, 0);
            this.EmitWriteLocal(assign.Local, 0, copyStruct: true);
            break;
          case IrGlobalAssign gAssign:
            this.EmitExpr(gAssign.Value, 0);
            this.EmitSlotToVar(0, this._owner._globalVars[gAssign.Global]);
            break;
          case IrStore store:
            this.EmitStore(store);
            break;
          case IrFieldStore fieldStore:
            this.EmitFieldStore(fieldStore);
            break;
          case IrExprStmt exprStmt:
            this.EmitExpr(exprStmt.Expression, 0);
            break;
          case IrWriteLine write:
            this.EmitWriteLine(write);
            break;
          case IrIf conditional:
            this.EmitIf(conditional, targets);
            break;
          case IrSwitch switching:
            this.EmitSwitch(switching, targets);
            break;
          case IrWhile whileLoop:
            this.EmitLoop(whileLoop.Body, whileLoop.Condition, null, [], false);
            break;
          case IrDoWhile doLoop:
            this.EmitLoop(doLoop.Body, doLoop.Condition, null, [], true);
            break;
          case IrFor forLoop:
            this.EmitLoop(forLoop.Body, forLoop.Condition, forLoop.Step, forLoop.PerIteration, false);
            break;
          case IrBreak:
            this.EmitBreak(targets);
            break;
          case IrContinue:
            this.EmitContinue(targets);
            break;
          case IrReturn ret:
            this.EmitReturn(ret);
            break;
          case IrUsing use:
            this.EmitBlock(use.Lowered(), targets);
            break;
          case IrTry attempt:
            this.EmitTry(attempt, targets);
            break;
          case IrThrow thrown:
            this.EmitThrow(thrown.Exception);
            break;
          case IrRethrow:
            this.EmitRethrow();
            break;
          case IrForEachCollection feCol:
            this.EmitForEachCollection(feCol);
            break;
          case IrForEachSequence feSeq:
            this.EmitForEachSequence(feSeq, targets);
            break;
          case IrYield yieldStmt:
            this.EmitYield(yieldStmt);
            break;
          case IrYieldBreak:
            this.EmitYieldBreak();
            break;
          case IrYieldFrom yieldFrom:
            this.EmitYieldFrom(yieldFrom);
            break;
          default:
            throw new Refusal(statement.GetType().Name);
        }
      }

      private void EmitIf(IrIf conditional, LoopTargets targets) {
        var yes = this._owner.NewLabel("_if_t");
        var end = this._owner.NewLabel("_if_e");
        var no = conditional.Else is null ? end : this._owner.NewLabel("_if_f");
        this.EmitBranch(conditional.Condition, yes, no, 0);
        this.Mark(yes);
        this.EmitBlock(conditional.Then, targets);
        if (conditional.Else is { } alternative) {
          this.Jump(end);
          this.Mark(no);
          this.EmitBlock(alternative, targets);
        }

        this.Mark(end);
      }

      private void EmitSwitch(IrSwitch switching, LoopTargets targets) {
        this.EmitExpr(switching.Value, 0);
        this.EmitSlotToVar(0, "_swv");
        var end = this._owner.NewLabel("_sw_e");
        var armLabels = new string[switching.Sections.Count];
        string? defaultLabel = null;

        for (var i = 0; i < switching.Sections.Count; ++i) {
          var section = switching.Sections[i];
          var label = this._owner.NewLabel("_sw_a");
          armLabels[i] = label;
          if (section.IsDefault)
            defaultLabel = label;

          foreach (var raw in section.Labels) {
            var formatted = FormatConstValue(switching.Value.Type, raw);
            this.Line($"if \"!_swv!\"==\"{formatted}\" goto :{label}");
          }
        }

        this.Jump(defaultLabel ?? end);
        var sectionTargets = new LoopTargets(end, targets.Continue, this._tryStack.Count);
        for (var i = 0; i < switching.Sections.Count; ++i) {
          this.Mark(armLabels[i]);
          this.EmitBlock(switching.Sections[i].Body, sectionTargets);
          this.Jump(end);
        }

        this.Mark(end);
      }

      private void EmitLoop(IrBlock body, IrExpr condition, IrBlock? step, IReadOnlyList<IrLocal> perIteration, bool postTest) {
        var start = this._owner.NewLabel("_lp_s");
        var test = this._owner.NewLabel("_lp_t");
        var end = this._owner.NewLabel("_lp_e");
        var advance = step is null && perIteration.Count == 0 ? test : this._owner.NewLabel("_lp_a");

        if (perIteration.Count > 0)
          this.EmitClonePerIteration(perIteration);

        if (!postTest)
          this.Jump(test);

        this.Mark(start);
        var loopTargets = new LoopTargets(end, advance, this._tryStack.Count);
        this.EmitBlock(body, loopTargets);
        if (step is not null || perIteration.Count > 0) {
          this.Mark(advance);
          if (perIteration.Count > 0)
            this.EmitClonePerIteration(perIteration);
          if (step is not null)
            this.EmitBlock(step, loopTargets);
        }

        this.Mark(test);
        this.EmitBranch(condition, start, end, 0);
        this.Mark(end);
      }

      private void EmitClonePerIteration(IReadOnlyList<IrLocal> perIteration) {
        foreach (var local in perIteration) {
          if (!this._capturedLocals.Contains(local))
            continue;

          this.EmitReadLocal(local, 0);
          this.Line("set /a \"_HID+=1\"");
          this.EmitFrame($"set \"_O_!_HID!_v=!{this.SlotVar(0)}!\"");
          this.EmitFrame($"set \"{this.LocalVar(local)}=!_HID!\"");
        }
      }

      private void EmitBreak(LoopTargets targets) {
        var fin = this.FindEnclosingFinally(targets.TryDepthAtLoop);
        if (fin is not null) {
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}=2\"");
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}_tgt={targets.Break ?? ""}\"");
          this.Jump(fin.FinallyLabel!);
          return;
        }

        if (this._inForEachCallback && targets.Break is null) {
          this.Line("for %%C in (!_CS!) do set \"_C%%C_ctl=1\"");
          this.Jump(this._exitLabel);
          return;
        }

        this.Jump(targets.Break!);
      }

      private void EmitContinue(LoopTargets targets) {
        var fin = this.FindEnclosingFinally(targets.TryDepthAtLoop);
        if (fin is not null) {
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}=3\"");
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}_tgt={targets.Continue ?? ""}\"");
          this.Jump(fin.FinallyLabel!);
          return;
        }

        if (this._inForEachCallback && targets.Continue is null) {
          this.Jump(this._exitLabel);
          return;
        }

        this.Jump(targets.Continue!);
      }

      private void EmitReturn(IrReturn ret) {
        if (ret.Value is { } val) {
          this.EmitExpr(val, 0);
          if (val.Type.IsStruct) {
            var sid = this._owner._structIds[val.Type.StructType!];
            this._owner._neededStructCopies.Add(val.Type.StructType!);
            this.EmitSlotToVar(0, "_cs_in");
            this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
          } else {
            this.EmitSlotToVar(0, "_RET");
          }
        }

        var fin = this.FindEnclosingFinally(0);
        if (fin is not null) {
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}=1\"");
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}_val=!_RET!\"");
          this.Jump(fin.FinallyLabel!);
          return;
        }

        if (this._inForEachCallback) {
          this.Line("for %%C in (!_CS!) do (set \"_C%%C_ctl=2\" & set \"_C%%C_ret=!_RET!\")");
          this.Jump(this._exitLabel);
          return;
        }

        this.Jump(this._exitLabel);
      }

      private TryContext? FindEnclosingFinally(int stopDepth) {
        var depth = this._tryStack.Count;
        foreach (var ctx in this._tryStack) {
          if (depth <= stopDepth)
            break;
          if (ctx.FinallyLabel is not null)
            return ctx;
          --depth;
        }

        return null;
      }

      private void EmitTry(IrTry attempt, LoopTargets targets) {
        var hasCatch = attempt.Catches.Count > 0;
        var hasFinally = attempt.Finally is not null;
        var catchLabel = hasCatch ? this._owner.NewLabel("_try_c") : null;
        var finallyLabel = hasFinally ? this._owner.NewLabel("_try_f") : null;
        var endLabel = this._owner.NewLabel("_try_e");
        var finIdx = hasFinally ? this._nextFinIndex++ : -1;

        if (hasFinally)
          this.EmitFrame($"set \"{this.FinVar(finIdx)}=0\"");

        var bodyUnwind = catchLabel ?? finallyLabel ?? this.CurrentUnwindLabel();
        var bodyCtx = new TryContext(bodyUnwind, finallyLabel, finIdx, null);
        this._tryStack.Push(bodyCtx);
        this.EmitBlock(attempt.Body, targets);
        this._tryStack.Pop();

        this.Jump(finallyLabel ?? endLabel);

        if (hasCatch) {
          this.Mark(catchLabel!);
          var catchUnwind = finallyLabel ?? this.CurrentUnwindLabel();
          var catchCtx = new TryContext(catchUnwind, finallyLabel, finIdx, null);
          this._tryStack.Push(catchCtx);

          for (var i = 0; i < attempt.Catches.Count; ++i) {
            var clause = attempt.Catches[i];
            var nextClause = this._owner.NewLabel("_try_cn");
            var classId = this._owner._classIds[clause.Class];
            this.Line($"if not defined _ISA_!_EX_TYPE!_{classId} goto :{nextClause}");

            if (clause.Local is { } local) {
              this.EmitVarToSlot("_EX_OBJ", 0);
              this.EmitWriteLocal(local, 0, copyStruct: false);
            }

            if (clause.Filter is { } filter) {
              // Save outer exception while evaluating filter; any exception thrown by filter counts as false.
              this.EmitFrame("set \"_fex_o=!_EX_OBJ!\" & set \"_fex_t=!_EX_TYPE!\" & set \"_fex_m=!_EX_TRAP!\"");
              this.Line("set \"_EX_ACTIVE=0\"");
              var filterFail = this._owner.NewLabel("_try_ff");
              var filterOk = this._owner.NewLabel("_try_fo");
              var filterCtx = new TryContext(filterFail, null, -1, null);
              this._tryStack.Push(filterCtx);
              this.EmitBranch(filter, filterOk, filterFail, 0);
              this._tryStack.Pop();

              this.Mark(filterFail);
              this.Line("set \"_EX_ACTIVE=1\"");
              this.EmitFrame("set \"_EX_OBJ=!_fex_o!\" & set \"_EX_TYPE=!_fex_t!\" & set \"_EX_TRAP=!_fex_m!\"");
              this.Jump(nextClause);

              this.Mark(filterOk);
              this.EmitFrame("set \"_EX_OBJ=!_fex_o!\" & set \"_EX_TYPE=!_fex_t!\" & set \"_EX_TRAP=!_fex_m!\"");
            }

            // Clause matched: push caught exception for rethrow, clear _EX_ACTIVE, run catch body.
            this.Line("set /a \"_EX_STK_N+=1\"");
            this.Line("for %%E in (!_EX_STK_N!) do (set \"_EX_STK_O_%%E=!_EX_OBJ!\" & set \"_EX_STK_T_%%E=!_EX_TYPE!\" & set \"_EX_STK_M_%%E=!_EX_TRAP!\")");
            this.Line("set \"_EX_ACTIVE=0\"");
            this.EmitBlock(clause.Body, targets);
            this.Line("set /a \"_EX_STK_N-=1\"");
            this.Jump(finallyLabel ?? endLabel);
            this.Mark(nextClause);
          }

          this._tryStack.Pop();
          // No catch matched: propagate exception to finally (if any) or outer unwind.
          if (hasFinally) {
            this.EmitFrame($"set \"{this.FinVar(finIdx)}=4\"");
            this.Jump(finallyLabel!);
          } else {
            this.Jump(this.CurrentUnwindLabel());
          }
        }

        if (hasFinally) {
          this.Mark(finallyLabel!);
          // If we arrived via active exception without fin=4 (e.g. from catch body or body without catches), record fin=4.
          this.EmitFrame($"if \"!_EX_ACTIVE!\"==\"1\" set \"{this.FinVar(finIdx)}=4\"");
          this.EmitFrame($"set \"{this.FinVar(finIdx)}_eo=!_EX_OBJ!\"");
          this.EmitFrame($"set \"{this.FinVar(finIdx)}_et=!_EX_TYPE!\"");
          this.EmitFrame($"set \"{this.FinVar(finIdx)}_em=!_EX_TRAP!\"");
          this.Line("set \"_EX_ACTIVE=0\"");

          this.EmitBlock(attempt.Finally!, targets);

          // Restore saved exception if pending fin==4, else dispatch pending return/break/continue.
          var outerFin = this.FindEnclosingFinally(0);
          var finCheckRet = this._owner.NewLabel("_fin_r");
          var finCheckBrk = this._owner.NewLabel("_fin_b");
          var finCheckCnt = this._owner.NewLabel("_fin_c");
          this.EmitFrame($"set \"_fmode=!{this.FinVar(finIdx)}!\"");
          this.Line($"if \"!_fmode!\"==\"0\" goto :{endLabel}");
          this.Line($"if not \"!_fmode!\"==\"4\" goto :{finCheckRet}");
          this.EmitFrame($"set \"_EX_OBJ=!{this.FinVar(finIdx)}_eo!\"");
          this.EmitFrame($"set \"_EX_TYPE=!{this.FinVar(finIdx)}_et!\"");
          this.EmitFrame($"set \"_EX_TRAP=!{this.FinVar(finIdx)}_em!\"");
          this.Line("set \"_EX_ACTIVE=1\"");
          this.Jump(this.CurrentUnwindLabel());

          this.Mark(finCheckRet);
          this.Line($"if not \"!_fmode!\"==\"1\" goto :{finCheckBrk}");
          this.EmitFrame($"set \"_RET=!{this.FinVar(finIdx)}_val!\"");
          if (outerFin is not null) {
            this.EmitFrame($"set \"{this.FinVar(outerFin.FinIndex)}=1\"");
            this.EmitFrame($"set \"{this.FinVar(outerFin.FinIndex)}_val=!_RET!\"");
            this.Jump(outerFin.FinallyLabel!);
          } else if (this._inForEachCallback) {
            this.Line("for %%C in (!_CS!) do (set \"_C%%C_ctl=2\" & set \"_C%%C_ret=!_RET!\")");
            this.Jump(this._exitLabel);
          } else {
            this.Jump(this._exitLabel);
          }

          this.Mark(finCheckBrk);
          this.Line($"if not \"!_fmode!\"==\"2\" goto :{finCheckCnt}");
          this.EmitFrame($"set \"_ftgt=!{this.FinVar(finIdx)}_tgt!\"");
          if (targets.Break is not null)
            this.Jump(targets.Break);
          else if (this._inForEachCallback) {
            this.Line("for %%C in (!_CS!) do set \"_C%%C_ctl=1\"");
            this.Jump(this._exitLabel);
          } else {
            this.Jump(endLabel);
          }

          this.Mark(finCheckCnt);
          if (targets.Continue is not null)
            this.Jump(targets.Continue);
          else if (this._inForEachCallback)
            this.Jump(this._exitLabel);
          else
            this.Jump(endLabel);
        }

        this.Mark(endLabel);
      }

      private void EmitThrow(IrExpr exception) {
        this.EmitExpr(exception, 0);
        this.EmitSlotToVar(0, "_th_obj");
        var okLbl = this._owner.NewLabel("_th_ok");
        this.Line($"if not \"!_th_obj!\"==\"{_nullToken}\" goto :{okLbl}");
        this.EmitTrap(IrExceptionClass.NullReferenceException, "NullReference");
        this.Mark(okLbl);
        this.Line("for %%O in (!_th_obj!) do (set \"_EX_OBJ=%%O\" & set \"_EX_TYPE=!_O_%%O___t!\" & set \"_EX_TRAP=Exception\")");
        this.Line("set \"_EX_ACTIVE=1\"");
        this.Jump(this.CurrentUnwindLabel());
      }

      private void EmitRethrow() {
        this.Line("for %%E in (!_EX_STK_N!) do (set \"_EX_OBJ=!_EX_STK_O_%%E!\" & set \"_EX_TYPE=!_EX_STK_T_%%E!\" & set \"_EX_TRAP=!_EX_STK_M_%%E!\")");
        this.Line("set \"_EX_ACTIVE=1\"");
        this.Jump(this.CurrentUnwindLabel());
      }

      private void EmitStore(IrStore store) {
        this.EmitExpr(store.Array, 0);
        this.EmitExpr(store.Index, 1);
        this.EmitExpr(store.Value, 2);
        if (store.Value.Type.IsStruct) {
          var sid = this._owner._structIds[store.Value.Type.StructType!];
          this._owner._neededStructCopies.Add(store.Value.Type.StructType!);
          this.EmitSlotToVar(2, "_cs_in");
          this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
          this.EmitVarToSlot("_RET", 2);
        }

        this.EmitSlotToVar(0, "_sa_id");
        this.EmitSlotToVar(1, "_sa_idx");
        this.EmitSlotToVar(2, "_sa_val");

        if (store.Array.Type.IsView) {
          this.Line("for %%V in (!_sa_id!) do (set \"_sa_arr=!_V_%%V_arr!\" & set /a \"_sa_off=_V_%%V_off, _sa_len=_V_%%V_len\")");
          this.EmitBoundsTrap("_sa_idx", "_sa_len");
          this.Line("set /a \"_sa_real=_sa_off+_sa_idx\"");
          this.Line("for %%A in (!_sa_arr!) do for %%I in (!_sa_real!) do set \"_A_%%A_%%I=!_sa_val!\"");
        } else {
          this.EmitNullTrap("_sa_id");
          this.Line("for %%A in (!_sa_id!) do set /a \"_sa_len=_AL_%%A\"");
          this.EmitBoundsTrap("_sa_idx", "_sa_len");
          this.Line("for %%A in (!_sa_id!) do for %%I in (!_sa_idx!) do set \"_A_%%A_%%I=!_sa_val!\"");
        }
      }

      private void EmitFieldStore(IrFieldStore store) {
        if (store.Target.Type.IsObject) {
          this.EmitExpr(store.Target, 0);
          this.EmitExpr(store.Value, 1);
          if (store.Value.Type.IsStruct) {
            var sid = this._owner._structIds[store.Value.Type.StructType!];
            this._owner._neededStructCopies.Add(store.Value.Type.StructType!);
            this.EmitSlotToVar(1, "_cs_in");
            this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
            this.EmitVarToSlot("_RET", 1);
          }

          this.EmitSlotToVar(0, "_fs_obj");
          this.EmitSlotToVar(1, "_fs_val");
          this.EmitNullTrap("_fs_obj");
          var fk = this._owner._fieldKeys[store.Field];
          this.Line($"for %%O in (!_fs_obj!) do set \"_O_%%O_{fk}=!_fs_val!\"");
        } else {
          // Struct place chain: resolve existing place handle WITHOUT copying!
          this.EmitPlaceHandle(store.Target, 0);
          this.EmitExpr(store.Value, 1);
          if (store.Value.Type.IsStruct) {
            var sid = this._owner._structIds[store.Value.Type.StructType!];
            this._owner._neededStructCopies.Add(store.Value.Type.StructType!);
            this.EmitSlotToVar(1, "_cs_in");
            this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
            this.EmitVarToSlot("_RET", 1);
          }

          this.EmitSlotToVar(0, "_fs_obj");
          this.EmitSlotToVar(1, "_fs_val");
          var fk = this._owner._fieldKeys[store.Field];
          this.Line($"for %%O in (!_fs_obj!) do set \"_O_%%O_{fk}=!_fs_val!\"");
        }
      }

      private void EmitPlaceHandle(IrExpr place, int dstSlot) {
        switch (place) {
          case IrLocalRef localRef:
            this.EmitReadLocal(localRef.Local, dstSlot);
            break;
          case IrArrayLoad load:
            this.EmitArrayLoadRaw(load, dstSlot, copyStruct: false);
            break;
          case IrFieldLoad fieldLoad:
            if (fieldLoad.Target.Type.IsObject) {
              this.EmitExpr(fieldLoad.Target, dstSlot);
              this.EmitSlotToVar(dstSlot, "_pl_o");
              this.EmitNullTrap("_pl_o");
            } else {
              this.EmitPlaceHandle(fieldLoad.Target, dstSlot);
              this.EmitSlotToVar(dstSlot, "_pl_o");
            }

            var fk = this._owner._fieldKeys[fieldLoad.Field];
            this.EmitFrame($"for %%O in (!_pl_o!) do set \"{this.SlotVar(dstSlot)}=!_O_%%O_{fk}!\"");
            break;
          default:
            this.EmitExpr(place, dstSlot);
            break;
        }
      }

      private void EmitWriteLine(IrWriteLine write) {
        // Validate expanded line limit for static string/concat/int patterns (enforcing 1024-byte cmd limit).
        var staticLen = EstimateStaticWriteLineLength(write.Value);
        if (staticLen > _cmdLineLimit)
          throw new Refusal($"an expanded ECHO command longer than {_cmdLineLimit} bytes");

        this.EmitExpr(write.Value, 0);
        this.EmitSlotToVar(0, "_wl_h");
        var nullLbl = this._owner.NewLabel("_wl_nl");
        var dnLbl = this._owner.NewLabel("_wl_dn");
        this.Line($"if \"!_wl_h!\"==\"{_nullToken}\" goto :{nullLbl}");
        this.Line("for %%H in (!_wl_h!) do echo(!_S_%%H!");
        this.Jump(dnLbl);
        this.Mark(nullLbl);
        this.Line("echo(");
        this.Mark(dnLbl);
      }

      private static int EstimateStaticWriteLineLength(IrExpr expr) {
        var total = 5; // "echo("
        void Walk(IrExpr e) {
          switch (e) {
            case IrNull:
              break;
            case IrStrConst s:
              total += s.Value.Length;
              break;
            case IrIntrinsic { Op: IrIntrinsicOp.StrConcat, Arguments: var parts }:
              foreach (var p in parts)
                Walk(p);
              break;
            case IrIntrinsic { Op: IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma, Arguments: [var v] } when v.Type.IsInteger:
              total += v.Type.IsLong ? 20 : 11;
              break;
            case IrIntrinsic { Op: IrIntrinsicOp.FormatBoolDotNet or IrIntrinsicOp.FormatBoolEcma }:
              total += 5;
              break;
          }
        }

        Walk(expr);
        return total;
      }

      private void EmitForEachCollection(IrForEachCollection fe) {
        this.EmitExpr(fe.Collection, 0);
        var colSlot = 10;
        var idxSlot = 11;
        var verSlot = 12;
        this.EmitCopySlot(colSlot, 0);

        var lpStart = this._owner.NewLabel("_fec_s");
        var lpNext = this._owner.NewLabel("_fec_n");
        var lpEnd = this._owner.NewLabel("_fec_e");
        var colType = fe.Collection.Type;
        var isChecked = fe.Mode == EnumerationMode.Checked;

        if (colType.IsList) {
          this.EmitSlotToVar(colSlot, "_fc_id");
          if (isChecked) {
            this.Line("for %%L in (!_fc_id!) do set \"_fc_v=!_L_%%L_ver!\"");
            this.EmitVarToSlot("_fc_v", verSlot);
          }

          this.EmitSetSlotLiteral(idxSlot, "0");
          this.Mark(lpStart);
          this.EmitSlotToVar(colSlot, "_fc_id");
          this.EmitSlotToVar(idxSlot, "_fc_i");
          if (isChecked) {
            this.EmitSlotToVar(verSlot, "_fc_v0");
            var verOk = this._owner.NewLabel("_fec_vo");
            this.Line($"for %%L in (!_fc_id!) do if \"!_L_%%L_ver!\"==\"!_fc_v0!\" goto :{verOk}");
            this.EmitTrap(IrExceptionClass.InvalidOperationException, "CollectionModified");
            this.Mark(verOk);
          }

          this.Line($"for %%L in (!_fc_id!) do if !_fc_i! GEQ !_L_%%L_len! goto :{lpEnd}");
          if (fe.Element is { } elemLocal) {
            this.Line("for %%L in (!_fc_id!) do for %%I in (!_fc_i!) do set \"_fc_el=!_L_%%L_%%I!\"");
            this.EmitVarToSlot("_fc_el", 0);
            this.EmitWriteLocal(elemLocal, 0, copyStruct: true);
          }

          this.EmitBlock(fe.Body, new(lpEnd, lpNext, this._tryStack.Count));
          this.Mark(lpNext);
          this.EmitFrame($"set /a \"{this.SlotVar(idxSlot)}+=1\"");
          this.Jump(lpStart);
          this.Mark(lpEnd);
        } else {
          // Map or Set: iterate slots 0..cap-1, skipping unused slots (_M_<id>_u_<i> == 0).
          this.EmitSlotToVar(colSlot, "_fc_id");
          if (isChecked) {
            this.Line("for %%M in (!_fc_id!) do set \"_fc_v=!_M_%%M_ver!\"");
            this.EmitVarToSlot("_fc_v", verSlot);
          }

          this.EmitSetSlotLiteral(idxSlot, "0");
          this.Mark(lpStart);
          this.EmitSlotToVar(colSlot, "_fc_id");
          this.EmitSlotToVar(idxSlot, "_fc_i");
          if (isChecked) {
            this.EmitSlotToVar(verSlot, "_fc_v0");
            var verOk = this._owner.NewLabel("_fec_vo");
            this.Line($"for %%M in (!_fc_id!) do if \"!_M_%%M_ver!\"==\"!_fc_v0!\" goto :{verOk}");
            this.EmitTrap(IrExceptionClass.InvalidOperationException, "CollectionModified");
            this.Mark(verOk);
          }

          this.Line($"for %%M in (!_fc_id!) do if !_fc_i! GEQ !_M_%%M_cap! goto :{lpEnd}");
          this.Line($"for %%M in (!_fc_id!) do for %%I in (!_fc_i!) do if not \"!_M_%%M_u_%%I!\"==\"1\" goto :{lpNext}");
          if (fe.Element is { } keyLocal) {
            this.Line("for %%M in (!_fc_id!) do for %%I in (!_fc_i!) do set \"_fc_k=!_M_%%M_k_%%I!\"");
            this.EmitVarToSlot("_fc_k", 0);
            this.EmitWriteLocal(keyLocal, 0, copyStruct: true);
          }

          if (fe.Value is { } valLocal) {
            this.Line("for %%M in (!_fc_id!) do for %%I in (!_fc_i!) do set \"_fc_val=!_M_%%M_v_%%I!\"");
            this.EmitVarToSlot("_fc_val", 0);
            this.EmitWriteLocal(valLocal, 0, copyStruct: true);
          }

          this.EmitBlock(fe.Body, new(lpEnd, lpNext, this._tryStack.Count));
          this.Mark(lpNext);
          this.EmitFrame($"set /a \"{this.SlotVar(idxSlot)}+=1\"");
          this.Jump(lpStart);
          this.Mark(lpEnd);
        }
      }

      private void EmitForEachSequence(IrForEachSequence fe, LoopTargets targets) {
        this.EmitExpr(fe.Sequence, 0);
        this.EmitSlotToVar(0, "_seq_obj");

        var cbId = this._owner._nextForEachId++;
        var cbLabel = "_ply_feb_" + cbId.ToString(CultureInfo.InvariantCulture);

        // Emit callback subroutine that executes fe.Body inside the caller's stack frame (_SP = %~1).
        var cbFn = new IrFunction(cbLabel, false, IrType.Void, [], [], fe.Body);
        var cbWriter = new FunctionWriter(
          this._owner, cbFn, false, true, null, cbLabel,
          this._localIds, this._capturedLocals, this._addressTakenLocals);

        cbWriter.Mark(cbLabel);
        cbWriter.Line("set \"_cb_save_sp=!_SP!\"");
        cbWriter.Line("set \"_SP=%~1\"");
        cbWriter.EmitVarToSlot("_YVAL", 0);
        cbWriter.EmitWriteLocal(fe.Element, 0, copyStruct: true);
        cbWriter.EmitBlock(fe.Body, new(null, null, 0));
        cbWriter.Mark(cbWriter._exitLabel);
        cbWriter.Line("if \"!_EX_ACTIVE!\"==\"1\" for %%C in (!_CS!) do set \"_C%%C_ctl=3\"");
        cbWriter.Line("set \"_SP=!_cb_save_sp!\"");
        cbWriter.Line("goto :eof");
        this._owner._extraSubroutines.AddRange(cbWriter.Lines);

        // Push consumer onto _CS stack and invoke sequence producer.
        this.Line("set /a \"_CS+=1\"");
        this.Line($"for %%C in (!_CS!) do (set \"_C%%C_cb={cbLabel}\" & set \"_C%%C_sp=!_SP!\" & set \"_C%%C_ctl=0\")");
        this.Line("for %%Q in (!_seq_obj!) do set \"_seq_fn=!_O_%%Q___seq!\"");
        this.Line("call :!_seq_fn! \"!_seq_obj!\"");
        this.Line("for %%C in (!_CS!) do (set \"_fe_ctl=!_C%%C_ctl!\" & set \"_fe_ret=!_C%%C_ret!\")");
        this.Line("set /a \"_CS-=1\"");

        this.EmitCheckUnwind();
        var afterReturn = this._owner.NewLabel("_fes_ok");
        this.Line($"if not \"!_fe_ctl!\"==\"2\" goto :{afterReturn}");
        this.Line("set \"_RET=!_fe_ret!\"");
        var fin = this.FindEnclosingFinally(0);
        if (fin is not null) {
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}=1\"");
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}_val=!_RET!\"");
          this.Jump(fin.FinallyLabel!);
        } else if (this._inForEachCallback) {
          this.Line("for %%C in (!_CS!) do (set \"_C%%C_ctl=2\" & set \"_C%%C_ret=!_RET!\")");
          this.Jump(this._exitLabel);
        } else {
          this.Jump(this._exitLabel);
        }

        this.Mark(afterReturn);
      }

      private void EmitYield(IrYield yieldStmt) {
        this.EmitExpr(yieldStmt.Value, 0);
        this.EmitSlotToVar(0, "_YVAL");
        this.Line("for %%C in (!_CS!) do call :!_C%%C_cb! \"!_C%%C_sp!\"");
        this.Line("for %%C in (!_CS!) do set \"_yctl=!_C%%C_ctl!\"");
        var contLbl = this._owner.NewLabel("_yld_c");
        this.Line($"if \"!_yctl!\"==\"0\" goto :{contLbl}");
        this.EmitYieldBreak();
        this.Mark(contLbl);
      }

      private void EmitYieldBreak() {
        var fin = this.FindEnclosingFinally(0);
        if (fin is not null) {
          this.EmitFrame($"set \"{this.FinVar(fin.FinIndex)}=1\"");
          this.Jump(fin.FinallyLabel!);
        } else {
          this.Jump(this._exitLabel);
        }
      }

      private void EmitYieldFrom(IrYieldFrom yieldFrom) {
        this.EmitExpr(yieldFrom.Sequence, 0);
        this.EmitSlotToVar(0, "_yf_seq");
        this.Line("for %%Q in (!_yf_seq!) do set \"_yf_fn=!_O_%%Q___seq!\"");
        this.Line("call :!_yf_fn! \"!_yf_seq!\"");
        this.EmitCheckUnwind();
        this.Line("for %%C in (!_CS!) do set \"_yctl=!_C%%C_ctl!\"");
        var contLbl = this._owner.NewLabel("_yf_c");
        this.Line($"if \"!_yctl!\"==\"0\" goto :{contLbl}");
        this.EmitYieldBreak();
        this.Mark(contLbl);
      }

    }

  }

}
