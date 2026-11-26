using System.Collections.Generic;

namespace Polypiler.Core.Frontends.VBScript.Parsing;

internal sealed record VbsProgram(
  bool OptionExplicit,
  IReadOnlyList<VbsStmt> TopLevelStatements);

internal enum VbsProcedureKind {
  Function,
  Sub,
  PropertyGet,
  PropertyLet
}

internal enum VbsExitKind {
  For,
  Do,
  Function,
  Sub,
  Property
}

internal enum VbsUnaryOp {
  Negate,
  Plus,
  Not
}

internal enum VbsBinaryOp {
  Power,
  Multiply,
  Divide,
  IntDivide,
  Mod,
  Add,
  Subtract,
  Concat,
  Equal,
  NotEqual,
  Less,
  LessEqual,
  Greater,
  GreaterEqual,
  Is,
  And,
  Or,
  Xor
}

internal sealed record VbsDimVar(string Name, VbsExpr? UpperBound, bool IsArray, int Line, int Column);

internal sealed record VbsParam(string Name, bool ByRef, int Line, int Column);

internal sealed record VbsElseIf(VbsExpr Condition, IReadOnlyList<VbsStmt> Body);

internal sealed record VbsCaseClause(IReadOnlyList<VbsExpr> Expressions, IReadOnlyList<VbsStmt> Body);

internal abstract record VbsStmt(int Line, int Column);

internal sealed record VbsConstDecl(string Name, VbsExpr Value, bool IsPublic, int Line, int Column) : VbsStmt(Line, Column);

internal sealed record VbsDimDecl(IReadOnlyList<VbsDimVar> Variables, bool IsPublic, int Line, int Column) : VbsStmt(Line, Column);

internal sealed record VbsReDimStmt(bool Preserve, IReadOnlyList<VbsDimVar> Variables, int Line, int Column) : VbsStmt(Line, Column);

internal sealed record VbsProcedureDecl(
  VbsProcedureKind Kind,
  string Name,
  IReadOnlyList<VbsParam> Parameters,
  IReadOnlyList<VbsStmt> Body,
  bool IsPublic,
  IReadOnlyList<string> DocComments,
  int Line,
  int Column) : VbsStmt(Line, Column);

internal sealed record VbsClassDecl(
  string Name,
  IReadOnlyList<VbsDimDecl> Fields,
  IReadOnlyList<VbsProcedureDecl> Members,
  int Line,
  int Column) : VbsStmt(Line, Column);

internal sealed record VbsAssignStmt(VbsExpr Target, VbsExpr Value, bool IsSet, int Line, int Column) : VbsStmt(Line, Column);

internal sealed record VbsCallStmt(VbsExpr Target, IReadOnlyList<VbsExpr> Arguments, int Line, int Column) : VbsStmt(Line, Column);

internal sealed record VbsIfStmt(
  VbsExpr Condition,
  IReadOnlyList<VbsStmt> ThenBody,
  IReadOnlyList<VbsElseIf> ElseIfs,
  IReadOnlyList<VbsStmt>? ElseBody,
  int Line,
  int Column) : VbsStmt(Line, Column);

internal sealed record VbsSelectStmt(
  VbsExpr Expression,
  IReadOnlyList<VbsCaseClause> Cases,
  IReadOnlyList<VbsStmt>? ElseBody,
  int Line,
  int Column) : VbsStmt(Line, Column);

internal sealed record VbsForStmt(
  string Variable,
  VbsExpr Start,
  VbsExpr End,
  VbsExpr? Step,
  IReadOnlyList<VbsStmt> Body,
  int Line,
  int Column) : VbsStmt(Line, Column);

internal sealed record VbsForEachStmt(
  string Variable,
  VbsExpr Collection,
  IReadOnlyList<VbsStmt> Body,
  int Line,
  int Column) : VbsStmt(Line, Column);

internal sealed record VbsDoLoopStmt(
  bool IsPostCondition,
  bool IsUntil,
  VbsExpr? Condition,
  IReadOnlyList<VbsStmt> Body,
  int Line,
  int Column) : VbsStmt(Line, Column);

internal sealed record VbsExitStmt(VbsExitKind Kind, int Line, int Column) : VbsStmt(Line, Column);

internal sealed record VbsOnErrorStmt(bool ResumeNext, int Line, int Column) : VbsStmt(Line, Column);

internal sealed record VbsUnsupportedStmt(string Construct, int Line, int Column) : VbsStmt(Line, Column);

internal abstract record VbsExpr(int Line, int Column);

internal sealed record VbsIntLit(long Value, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsFloatLit(double Value, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsStringLit(string Value, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsBoolLit(bool Value, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsEmptyLit(int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsNullLit(int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsNothingLit(int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsIdentExpr(string Name, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsMeExpr(int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsUnaryExpr(VbsUnaryOp Op, VbsExpr Operand, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsBinaryExpr(VbsBinaryOp Op, VbsExpr Left, VbsExpr Right, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsCallOrIndexExpr(VbsExpr Target, IReadOnlyList<VbsExpr> Arguments, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsMemberExpr(VbsExpr Target, string Member, int Line, int Column) : VbsExpr(Line, Column);

internal sealed record VbsNewExpr(string ClassName, int Line, int Column) : VbsExpr(Line, Column);
