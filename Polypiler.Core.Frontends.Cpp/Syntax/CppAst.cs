using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Cpp.Syntax;

internal sealed record CppTranslationUnit(IReadOnlyList<CppDecl> Declarations);

internal abstract record CppDecl(SourceRange Span);

internal sealed record CppNamespaceDecl(string? Name, IReadOnlyList<CppDecl> Declarations, SourceRange Span) : CppDecl(Span);

internal sealed record CppUsingNamespaceDecl(string Name, SourceRange Span) : CppDecl(Span);

internal sealed record CppTypeAliasDecl(string Name, CppTypeRef Target, SourceRange Span) : CppDecl(Span);

internal sealed record CppFieldDecl(CppTypeRef Type, string Name, CppExpr? DefaultValue, SourceRange Span);

internal sealed record CppStructDecl(
  string Name,
  IReadOnlyList<CppFieldDecl> Fields,
  bool HasDefaultEquals,
  SourceRange Span) : CppDecl(Span);

internal sealed record CppEnumMemberDecl(string Name, CppExpr? Value, SourceRange Span);

internal sealed record CppEnumDecl(
  string Name,
  bool IsScoped,
  CppTypeRef? UnderlyingType,
  IReadOnlyList<CppEnumMemberDecl> Members,
  SourceRange Span) : CppDecl(Span);

internal sealed record CppGlobalVarDecl(
  CppTypeRef Type,
  string Name,
  bool IsConst,
  CppExpr? Initializer,
  SourceRange Span) : CppDecl(Span);

internal sealed record CppParamDecl(CppTypeRef Type, string Name, SourceRange Span);

internal sealed record CppFuncDecl(
  IReadOnlyList<string> TypeParams,
  CppTypeRef ReturnType,
  string Name,
  IReadOnlyList<CppParamDecl> Params,
  CppBlockStmt? Body,
  bool IsStatic,
  SourceRange Span) : CppDecl(Span);

internal sealed record CppTypeRef(
  string Name,
  IReadOnlyList<CppTypeArg> TypeArgs,
  bool IsConst,
  bool IsPointer,
  bool IsReference,
  int? ArraySize,
  SourceRange Span);

internal sealed record CppTypeArg(CppTypeRef? Type, long? IntValue);

internal abstract record CppStmt(SourceRange Span);

internal sealed record CppBlockStmt(IReadOnlyList<CppStmt> Statements, SourceRange Span) : CppStmt(Span);

internal sealed record CppVarDeclStmt(
  CppTypeRef? Type,
  string Name,
  CppExpr? Initializer,
  SourceRange Span) : CppStmt(Span);

internal sealed record CppExprStmt(CppExpr Expr, SourceRange Span) : CppStmt(Span);

internal sealed record CppIfStmt(CppExpr Condition, CppStmt Then, CppStmt? Else, SourceRange Span) : CppStmt(Span);

internal sealed record CppWhileStmt(CppExpr Condition, CppStmt Body, SourceRange Span) : CppStmt(Span);

internal sealed record CppDoWhileStmt(CppStmt Body, CppExpr Condition, SourceRange Span) : CppStmt(Span);

internal sealed record CppForStmt(CppStmt? Init, CppExpr? Condition, CppExpr? Step, CppStmt Body, SourceRange Span) : CppStmt(Span);

internal sealed record CppRangeForStmt(CppTypeRef? VarType, string VarName, CppExpr Iterable, CppStmt Body, SourceRange Span) : CppStmt(Span);

internal sealed record CppSwitchCase(IReadOnlyList<CppExpr?> Labels, IReadOnlyList<CppStmt> Statements, SourceRange Span);

internal sealed record CppSwitchStmt(CppExpr Value, IReadOnlyList<CppSwitchCase> Cases, SourceRange Span) : CppStmt(Span);

internal sealed record CppBreakStmt(SourceRange Span) : CppStmt(Span);

internal sealed record CppContinueStmt(SourceRange Span) : CppStmt(Span);

internal sealed record CppReturnStmt(CppExpr? Value, SourceRange Span) : CppStmt(Span);

internal sealed record CppCatchClause(CppTypeRef? ExceptionType, string? VarName, CppBlockStmt Body, SourceRange Span);

internal sealed record CppTryStmt(CppBlockStmt Body, IReadOnlyList<CppCatchClause> Catches, SourceRange Span) : CppStmt(Span);

internal sealed record CppThrowStmt(CppExpr? Value, SourceRange Span) : CppStmt(Span);

internal abstract record CppExpr(SourceRange Span);

internal sealed record CppIntLiteral(ulong Value, bool IsUnsigned, int WidthBits, SourceRange Span) : CppExpr(Span);

internal sealed record CppFloatLiteral(double Value, bool IsFloat32, SourceRange Span) : CppExpr(Span);

internal sealed record CppBoolLiteral(bool Value, SourceRange Span) : CppExpr(Span);

internal sealed record CppCharLiteral(sbyte Value, SourceRange Span) : CppExpr(Span);

internal sealed record CppStringLiteral(string Value, SourceRange Span) : CppExpr(Span);

internal sealed record CppNullLiteral(bool IsNullopt, SourceRange Span) : CppExpr(Span);

internal sealed record CppIdentExpr(string Name, SourceRange Span) : CppExpr(Span);

internal sealed record CppUnaryExpr(string Op, CppExpr Operand, bool IsPostfix, SourceRange Span) : CppExpr(Span);

internal sealed record CppBinaryExpr(string Op, CppExpr Left, CppExpr Right, SourceRange Span) : CppExpr(Span);

internal sealed record CppTernaryExpr(CppExpr Condition, CppExpr Then, CppExpr Else, SourceRange Span) : CppExpr(Span);

internal sealed record CppAssignExpr(string Op, CppExpr Target, CppExpr Value, SourceRange Span) : CppExpr(Span);

internal sealed record CppCastExpr(string Kind, CppTypeRef TargetType, CppExpr Operand, SourceRange Span) : CppExpr(Span);

internal sealed record CppCallExpr(CppExpr Callee, IReadOnlyList<CppTypeArg> TypeArgs, IReadOnlyList<CppExpr> Arguments, SourceRange Span) : CppExpr(Span);

internal sealed record CppIndexExpr(CppExpr Target, CppExpr Index, SourceRange Span) : CppExpr(Span);

internal sealed record CppMemberExpr(CppExpr Target, string Member, SourceRange Span) : CppExpr(Span);

internal sealed record CppSizeofExpr(CppTypeRef TargetType, SourceRange Span) : CppExpr(Span);

internal sealed record CppInitListExpr(CppTypeRef? Type, IReadOnlyList<CppExpr> Elements, SourceRange Span) : CppExpr(Span);
