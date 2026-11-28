using System.Collections.Generic;

namespace Polypiler.Core.Frontends.VisualBasic.Parsing;

// The syntax of a classic Visual Basic (VB6 / VBA) standard module as [MS-VBAL] section 5 gives it,
// for what the parser reads. Every node carries the line it starts on; a construct the parser
// recognizes but the frontend does not import is kept as VbaUnsupported with its name.

internal abstract record VbaNode(int Line);

internal sealed record VbaTypeRef(string Name, int Line) : VbaNode(Line);

internal sealed record VbaBound(VbaExpr? Lower, VbaExpr Upper);

internal sealed record VbaVariable(string Name, VbaTypeRef? Type, bool IsDynamicArray, IReadOnlyList<VbaBound> Bounds, int Line) : VbaNode(Line) {

  public bool IsArray => this.IsDynamicArray || this.Bounds.Count > 0;

}

internal sealed record VbaParameter(bool ByVal, bool Optional, string Name, VbaTypeRef? Type, bool IsArray, VbaExpr? Default, int Line) : VbaNode(Line);

#region Declarations

internal abstract record VbaDeclaration(int Line) : VbaNode(Line);

internal sealed record VbaOption(string Text, int Line) : VbaDeclaration(Line);

internal sealed record VbaTypeDeclaration(bool IsPublic, string Name, IReadOnlyList<VbaVariable> Fields, int Line) : VbaDeclaration(Line);

internal sealed record VbaEnumMember(string Name, VbaExpr? Value, int Line);

internal sealed record VbaEnumDeclaration(bool IsPublic, string Name, IReadOnlyList<VbaEnumMember> Members, int Line) : VbaDeclaration(Line);

internal sealed record VbaConstDeclaration(bool IsPublic, string Name, VbaTypeRef? Type, VbaExpr Value, int Line) : VbaDeclaration(Line);

internal sealed record VbaVariableDeclaration(bool IsPublic, IReadOnlyList<VbaVariable> Variables, int Line) : VbaDeclaration(Line);

internal sealed record VbaProcedure(bool IsPublic, bool IsFunction, string Name, IReadOnlyList<VbaParameter> Parameters, VbaTypeRef? Returns, bool ReturnsArray, IReadOnlyList<VbaStatement> Body, int Line) : VbaDeclaration(Line);

internal sealed record VbaUnsupportedDeclaration(string Construct, int Line) : VbaDeclaration(Line);

internal sealed record VbaModule(string? Name, IReadOnlyList<VbaDeclaration> Declarations);

#endregion

#region Statements

internal abstract record VbaStatement(int Line) : VbaNode(Line);

internal sealed record VbaDim(IReadOnlyList<VbaVariable> Variables, int Line) : VbaStatement(Line);

internal sealed record VbaLocalConst(string Name, VbaTypeRef? Type, VbaExpr Value, int Line) : VbaStatement(Line);

internal sealed record VbaAssign(VbaExpr Target, VbaExpr Value, int Line) : VbaStatement(Line);

internal sealed record VbaCallStatement(VbaExpr Target, IReadOnlyList<VbaExpr> Arguments, int Line) : VbaStatement(Line);

internal sealed record VbaIfBranch(VbaExpr Condition, IReadOnlyList<VbaStatement> Body);

internal sealed record VbaIf(IReadOnlyList<VbaIfBranch> Branches, IReadOnlyList<VbaStatement>? Else, int Line) : VbaStatement(Line);

/// <summary>A clause of a <c>Case</c>: a value, <c>lower To upper</c>, or <c>Is op value</c>.</summary>
internal sealed record VbaCaseClause(VbaExpr Value, VbaExpr? Upper, string? Is);

internal sealed record VbaCase(IReadOnlyList<VbaCaseClause> Clauses, IReadOnlyList<VbaStatement> Body);

internal sealed record VbaSelect(VbaExpr Value, IReadOnlyList<VbaCase> Cases, IReadOnlyList<VbaStatement>? Else, int Line) : VbaStatement(Line);

internal sealed record VbaFor(string Variable, VbaExpr Start, VbaExpr End, VbaExpr? Step, IReadOnlyList<VbaStatement> Body, int Line) : VbaStatement(Line);

/// <summary>A <c>Do</c> loop, or <c>While … Wend</c> (a pretest while).</summary>
internal sealed record VbaDo(bool PreTest, bool Until, VbaExpr? Condition, IReadOnlyList<VbaStatement> Body, int Line) : VbaStatement(Line);

internal sealed record VbaExit(string Kind, int Line) : VbaStatement(Line);

internal sealed record VbaGoTo(string Label, int Line) : VbaStatement(Line);

internal sealed record VbaLabel(string Name, int Line) : VbaStatement(Line);

/// <summary><c>On Error GoTo label</c>, <c>On Error GoTo 0</c> (<see cref="Label"/> <c>0</c>) or <c>On Error Resume Next</c> (<see cref="Label"/> <see langword="null"/>).</summary>
internal sealed record VbaOnError(string? Label, int Line) : VbaStatement(Line);

/// <summary><c>Resume</c> (empty), <c>Resume Next</c> or <c>Resume label</c>.</summary>
internal sealed record VbaResume(string Target, int Line) : VbaStatement(Line);

internal sealed record VbaReDimItem(VbaExpr Target, IReadOnlyList<VbaBound> Bounds, VbaTypeRef? Type);

internal sealed record VbaReDim(bool Preserve, IReadOnlyList<VbaReDimItem> Items, int Line) : VbaStatement(Line);

internal sealed record VbaErase(IReadOnlyList<VbaExpr> Targets, int Line) : VbaStatement(Line);

/// <summary><c>Error n</c>: raises the run-time error <c>n</c>.</summary>
internal sealed record VbaError(VbaExpr Number, int Line) : VbaStatement(Line);

internal sealed record VbaUnsupported(string Construct, int Line) : VbaStatement(Line);

#endregion

#region Expressions

internal abstract record VbaExpr(int Line) : VbaNode(Line);

/// <summary>An integer literal with its type character (<c>%</c>, <c>&amp;</c>, <c>^</c>) or none, and whether it was written in hexadecimal or octal with how many digits.</summary>
internal sealed record VbaInteger(long Value, string Suffix, int Digits, bool Radix, int Line) : VbaExpr(Line);

internal sealed record VbaFloat(double Value, string Suffix, int Line) : VbaExpr(Line);

internal sealed record VbaString(string Value, int Line) : VbaExpr(Line);

/// <summary>A date literal, <c>#1/2/2000#</c>: its value, days since 1899-12-30.</summary>
internal sealed record VbaDate(double Value, int Line) : VbaExpr(Line);

internal sealed record VbaBoolean(bool Value, int Line) : VbaExpr(Line);

/// <summary>The literal <c>Empty</c>: the value of a <c>Variant</c> that holds nothing yet.</summary>
internal sealed record VbaEmpty(int Line) : VbaExpr(Line);

/// <summary>The literal <c>Null</c>: the value of a <c>Variant</c> known to be missing.</summary>
internal sealed record VbaNull(int Line) : VbaExpr(Line);

/// <summary>A name with the type character it was written with, or none.</summary>
internal sealed record VbaName(string Name, string Suffix, int Line) : VbaExpr(Line);

internal sealed record VbaMember(VbaExpr Target, string Name, int Line) : VbaExpr(Line);

internal sealed record VbaCall(VbaExpr Target, IReadOnlyList<VbaExpr> Arguments, int Line) : VbaExpr(Line);

internal sealed record VbaUnary(string Operator, VbaExpr Operand, int Line) : VbaExpr(Line);

internal sealed record VbaBinary(VbaExpr Left, string Operator, VbaExpr Right, int Line) : VbaExpr(Line);

/// <summary>An expression in parentheses, which a call statement's argument list tells apart.</summary>
internal sealed record VbaParenthesized(VbaExpr Inner, int Line) : VbaExpr(Line);

#endregion
