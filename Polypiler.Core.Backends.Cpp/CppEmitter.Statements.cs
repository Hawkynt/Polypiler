using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Cpp;

public static partial class CppEmitter {

  private sealed partial class Writer {

    private void EmitBlockStatements(IrBlock block, StringBuilder text, int indent, int startIndex = 0) {
      for (var i = startIndex; i < block.Statements.Count; ++i)
        this.EmitStatement(block.Statements[i], text, indent);
    }

    private void EmitStatement(IrStmt statement, StringBuilder text, int indent) {
      var pad = Indent(indent);
      switch (statement) {
        case IrBlock inner:
          text.Append(pad).Append("{\n");
          this.EmitBlockStatements(inner, text, indent + 1);
          text.Append(pad).Append("}\n");
          break;

        case IrDeclare declare: {
          var name = this.LocalName(declare.Local);
          var type = this.CppType(declare.Local.Type, inHeader: false);
          var init = declare.Initializer is { } expr ? this.EmitExpr(expr) : this.DefaultValue(declare.Local.Type);
          text.Append(pad).Append(type).Append(' ').Append(name).Append(" = ").Append(init).Append(";\n");
          break;
        }

        case IrAssign assign:
          text.Append(pad).Append(this.LocalName(assign.Local)).Append(" = ").Append(this.EmitExpr(assign.Value)).Append(";\n");
          break;

        case IrGlobalAssign globalAssign:
          text.Append(pad).Append(this._globalNames[globalAssign.Global]).Append(" = ").Append(this.EmitExpr(globalAssign.Value)).Append(";\n");
          break;

        case IrStore store:
          this.UseHelper(CppHelpers.ArrayAt);
          text.Append(pad).Append(CppHelpers.ArrayAt).Append('(').Append(this.EmitExpr(store.Array))
            .Append(", ").Append(this.EmitExpr(store.Index)).Append(") = ").Append(this.EmitExpr(store.Value)).Append(";\n");
          break;

        case IrFieldStore fieldStore:
          this.EmitFieldStore(fieldStore, text, indent);
          break;

        case IrExprStmt exprStmt:
          text.Append(pad).Append(this.EmitExpr(exprStmt.Expression)).Append(";\n");
          break;

        case IrIf @if:
          this.EmitIf(@if, text, indent);
          break;

        case IrWhile @while:
          text.Append(pad).Append("while (").Append(this.EmitExpr(@while.Condition)).Append(") {\n");
          this.EmitBlockStatements(@while.Body, text, indent + 1);
          text.Append(pad).Append("}\n");
          break;

        case IrDoWhile doWhile:
          text.Append(pad).Append("do {\n");
          this.EmitBlockStatements(doWhile.Body, text, indent + 1);
          text.Append(pad).Append("} while (").Append(this.EmitExpr(doWhile.Condition)).Append(");\n");
          break;

        case IrFor @for:
          this.EmitFor(@for, text, indent);
          break;

        case IrSwitch @switch:
          this.EmitSwitch(@switch, text, indent);
          break;

        case IrBreak:
          text.Append(pad).Append("break;\n");
          break;

        case IrContinue:
          text.Append(pad).Append("continue;\n");
          break;

        case IrReturn ret:
          if (ret.Value is { } val)
            text.Append(pad).Append("return ").Append(this.EmitExpr(val)).Append(";\n");
          else
            text.Append(pad).Append("return;\n");
          break;

        case IrThrow @throw:
          text.Append(pad).Append("throw ").Append(this.EmitExceptionValue(@throw.Exception)).Append(";\n");
          break;

        case IrRethrow:
          text.Append(pad).Append("throw;\n");
          break;

        case IrTry @try:
          this.EmitTry(@try, text, indent);
          break;

        case IrForEachCollection each:
          this.EmitForEachCollection(each, text, indent);
          break;

        case IrUsing @using:
          this.EmitUsing(@using, text, indent);
          break;

        default:
          this.ReportUnsupportedConstruct(statement.GetType().Name);
          break;
      }
    }

    private void EmitFieldStore(IrFieldStore store, StringBuilder text, int indent) {
      var pad = Indent(indent);
      if (store.Target.Type.IsObject) {
        this.EmitClassFieldStore(store, text, indent);
        return;
      }
      text.Append(pad).Append(this.EmitExpr(store.Target)).Append('.').Append(this._fieldNames[store.Field])
        .Append(" = ").Append(this.EmitExpr(store.Value)).Append(";\n");
    }

    private void EmitIf(IrIf @if, StringBuilder text, int indent) {
      var pad = Indent(indent);
      text.Append(pad).Append("if (").Append(this.EmitExpr(@if.Condition)).Append(") {\n");
      this.EmitBlockStatements(@if.Then, text, indent + 1);
      var currentElse = @if.Else;
      while (currentElse is { Statements: [IrIf nested] }) {
        text.Append(pad).Append("} else if (").Append(this.EmitExpr(nested.Condition)).Append(") {\n");
        this.EmitBlockStatements(nested.Then, text, indent + 1);
        currentElse = nested.Else;
      }
      if (currentElse is not null) {
        text.Append(pad).Append("} else {\n");
        this.EmitBlockStatements(currentElse, text, indent + 1);
      }
      text.Append(pad).Append("}\n");
    }

    private void EmitFor(IrFor @for, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var cond = this.EmitExpr(@for.Condition);
      if (@for.Step.Statements.Count == 0) {
        text.Append(pad).Append("while (").Append(cond).Append(") {\n");
      } else if (@for.Step.Statements is [IrAssign assign]) {
        text.Append(pad).Append("for (; ").Append(cond).Append("; ")
          .Append(this.LocalName(assign.Local)).Append(" = ").Append(this.EmitExpr(assign.Value)).Append(") {\n");
      } else if (@for.Step.Statements is [IrExprStmt exprStmt]) {
        text.Append(pad).Append("for (; ").Append(cond).Append("; ").Append(this.EmitExpr(exprStmt.Expression)).Append(") {\n");
      } else {
        var stepCode = new StringBuilder();
        this.EmitBlockStatements(@for.Step, stepCode, 0);
        var flat = stepCode.ToString().Replace("\n", " ", StringComparison.Ordinal).Trim();
        text.Append(pad).Append("for (; ").Append(cond).Append("; [&]() { ").Append(flat).Append(" }()) {\n");
      }
      this.EmitBlockStatements(@for.Body, text, indent + 1);
      text.Append(pad).Append("}\n");
    }

    private void EmitSwitch(IrSwitch @switch, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var casePad = Indent(indent + 1);
      var selType = @switch.Value.Type;
      text.Append(pad).Append("switch (").Append(this.EmitExpr(@switch.Value)).Append(") {\n");
      var hasDefault = false;
      foreach (var section in @switch.Sections) {
        foreach (var label in section.Labels)
          text.Append(casePad).Append("case ").Append(this.FormatScalarConstant(selType, label)).Append(":\n");
        if (section.IsDefault) {
          hasDefault = true;
          text.Append(casePad).Append("default:\n");
        }
        text.Append(casePad).Append("{\n");
        this.EmitBlockStatements(section.Body, text, indent + 2);
        text.Append(Indent(indent + 2)).Append("break;\n");
        text.Append(casePad).Append("}\n");
      }
      if (!hasDefault)
        text.Append(casePad).Append("default:\n").Append(Indent(indent + 2)).Append("break;\n");
      text.Append(pad).Append("}\n");
    }

    private void EmitTry(IrTry @try, StringBuilder text, int indent) {
      var pad = Indent(indent);
      if (@try.Finally is not null) {
        this.UseHelper(CppHelpers.ScopeGuard);
        text.Append(pad).Append("{\n");
        var guardVar = this.Temp("ply_guard");
        text.Append(Indent(indent + 1)).Append("auto ").Append(guardVar).Append(" = ").Append(CppHelpers.ScopeGuard).Append("([&]() {\n");
        this.EmitBlockStatements(@try.Finally, text, indent + 2);
        text.Append(Indent(indent + 1)).Append("});\n");
        if (@try.Catches.Count > 0)
          this.EmitTryCatches(@try, text, indent + 1);
        else
          this.EmitBlockStatements(@try.Body, text, indent + 1);
        text.Append(pad).Append("}\n");
        return;
      }

      this.EmitTryCatches(@try, text, indent);
    }

    private void EmitTryCatches(IrTry @try, StringBuilder text, int indent) {
      var pad = Indent(indent);
      text.Append(pad).Append("try {\n");
      this.EmitBlockStatements(@try.Body, text, indent + 1);
      foreach (var @catch in @try.Catches) {
        var exType = this.ExceptionCppType(@catch.Class, inHeader: false);
        text.Append(pad).Append("} catch (const ").Append(exType).Append('&');
        if (@catch.Local is not null)
          text.Append(' ').Append(this.LocalName(@catch.Local));
        text.Append(") {\n");
        if (@catch.Filter is { } filter)
          text.Append(Indent(indent + 1)).Append("if (!(").Append(this.EmitExpr(filter)).Append(")) throw;\n");
        this.EmitBlockStatements(@catch.Body, text, indent + 1);
      }
      text.Append(pad).Append("}\n");
    }

    private void EmitForEachCollection(IrForEachCollection each, StringBuilder text, int indent) {
      if (!each.Collection.Type.IsList) {
        this.ReportUnsupportedConstruct("IrForEachCollection(" + each.Collection.Type + ")");
        return;
      }

      var pad = Indent(indent);
      var elem = this.LocalName(each.Element!);
      var elemType = this.CppType(each.Element!.Type, inHeader: false);
      text.Append(pad).Append("for (").Append(elemType).Append(' ').Append(elem).Append(" : ").Append(this.EmitExpr(each.Collection)).Append(") {\n");
      this.EmitBlockStatements(each.Body, text, indent + 1);
      text.Append(pad).Append("}\n");
    }

    private void EmitUsing(IrUsing @using, StringBuilder text, int indent) {
      var pad = Indent(indent);
      text.Append(pad).Append("{\n");
      var resName = this.LocalName(@using.Resource);
      var resType = this.CppType(@using.Resource.Type, inHeader: false);
      text.Append(Indent(indent + 1)).Append(resType).Append(' ').Append(resName).Append(" = ").Append(this.EmitExpr(@using.Initializer)).Append(";\n");
      this.UseHelper(CppHelpers.ScopeGuard);
      var guardVar = this.Temp("ply_dispose");
      var methodName = this._methodNames[@using.Dispose];
      text.Append(Indent(indent + 1)).Append("auto ").Append(guardVar).Append(" = ").Append(CppHelpers.ScopeGuard)
        .Append("([&]() { if (").Append(resName).Append(" != nullptr) ").Append(resName).Append("->").Append(methodName).Append("(); });\n");
      this.EmitBlockStatements(@using.Body, text, indent + 1);
      text.Append(pad).Append("}\n");
    }

  }

}
