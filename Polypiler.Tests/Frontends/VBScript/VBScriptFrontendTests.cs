using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.VBScript;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.VBScript;

/// <summary>Unit tests for the VBScript 5.8 frontend (<see cref="VBScriptFrontend"/>).</summary>
[TestFixture]
public sealed class VBScriptFrontendTests {

  private static IrModule ImportOk(string source, string? contracts = null, string fileName = "Sample.vbs") {
    var result = VBScriptFrontend.Import(fileName, source, contracts is null ? null : new VBScriptImportOptions { Contracts = contracts });
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join(Environment.NewLine, result.Diagnostics));
    Assert.That(result.Module, Is.Not.Null);
    var verifyErrors = IrVerifier.Verify(result.Module!);
    Assert.That(verifyErrors, Is.Empty, () => string.Join(Environment.NewLine, verifyErrors));
    return result.Module!;
  }

  private static object? Call(IrModule module, string function, params object[] args)
    => IrInterpreter.Call(module, function, args);

  private static void AssertError(string source, string expectedCode, string? contracts = null) {
    var result = VBScriptFrontend.Import("Error.vbs", source, contracts is null ? null : new VBScriptImportOptions { Contracts = contracts });
    Assert.That(result.Module, Is.Null, $"Expected {expectedCode} for:\n{source}");
    Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain(expectedCode),
      () => string.Join(Environment.NewLine, result.Diagnostics));
  }

  [Test]
  public void Lexer_CommentsContinuationsColonsAndLiterals_Work() {
    var module = ImportOk(""""
      Option Explicit
      Rem This is a Rem comment at start of line
      ' Regular comment
      Const HEX16 = &HFFFF
      Const HEX32 = &HFFFF&
      Const HEX_BYTE = &HFF
      Const NEG_CONST = -42
      Const NEG_FLT = -1.5
      Const ALIAS_CONST = HEX_BYTE
      Const STR_CONST = "say ""hi"""
      Const BOOL_CONST = True

      Function Compute()
        Dim a, b : a = 10 : Rem inline rem after colon
        b = a + _
          HEX_BYTE + HEX32 + HEX16
        Compute = b + NEG_CONST + ALIAS_CONST
      End Function

      Function Floats()
        Floats = .5 + 1.5e2 + 2E-1 + NEG_FLT
      End Function

      Function BigIntLiteral()
        BigIntLiteral = 3000000000
      End Function

      Function StringAndBool()
        If BOOL_CONST Then
          StringAndBool = STR_CONST
        Else
          StringAndBool = ""
        End If
      End Function
      """");

    Assert.That(Call(module, "Compute"), Is.EqualTo(10 + 255 + 65535 - 1 - 42 + 255));
    Assert.That((double)Call(module, "Floats")!, Is.EqualTo(0.5 + 150.0 + 0.2 - 1.5).Within(1e-9));
    Assert.That(Call(module, "BigIntLiteral"), Is.EqualTo(3000000000L));
    Assert.That(Call(module, "StringAndBool"), Is.EqualTo("say \"hi\""));
  }

  [Test]
  public void Operators_PrecedenceAndBitwiseVsLogical_MatchVBScript58() {
    var module = ImportOk("""
      Function Precedence()
        Dim a, b, c, d, e
        a = 2 ^ 3 ^ 2
        b = -2 ^ 2
        c = 2 ^ -2
        d = +5 + 7 \ 2 + (-7 Mod 3)
        e = 7 / 2
        Precedence = a + b + c + d + e
      End Function

      Function BitwiseOps(ByVal x, ByVal y)
        BitwiseOps = ((x And y) Or (x Xor y)) + (Not x)
      End Function

      Function LogicalOps(a, b)
        '@param a: bool
        '@param b: bool
        '@return: bool
        LogicalOps = (a And Not b) Or (a Xor b)
      End Function

      Function Comparisons(x, y)
        Dim c
        c = 0
        If x = y Then c = c + 1
        If x <> y Then c = c + 2
        If x < y Then c = c + 4
        If x <= y Then c = c + 8
        If x > y Then c = c + 16
        If x >= y Then c = c + 32
        Comparisons = c
      End Function

      Function StringComparisons(a, b)
        '@param a: str
        '@param b: str
        Dim c
        c = 0
        If a = b Then c = c + 1
        If a <> b Then c = c + 2
        If a < b Then c = c + 4
        If a <= b Then c = c + 8
        If a > b Then c = c + 16
        If a >= b Then c = c + 32
        StringComparisons = c
      End Function

      Function ConcatAndPlus(n)
        ConcatAndPlus = "v=" & n & "," & True & ":" & ("ab" + "cd")
      End Function
      """);

    // 64 + 4 + 0.25 + (5 + 3 + -1) + 3.5 = 78.75
    Assert.That((double)Call(module, "Precedence")!, Is.EqualTo(78.75).Within(1e-9));
    Assert.That(Call(module, "BitwiseOps", 5, 3), Is.EqualTo(((5 & 3) | (5 ^ 3)) + (~5)));
    Assert.That(Call(module, "LogicalOps", true, false), Is.EqualTo(true));
    Assert.That(Call(module, "LogicalOps", true, true), Is.EqualTo(false));
    Assert.That(Call(module, "Comparisons", 3, 5), Is.EqualTo(2 + 4 + 8));
    Assert.That(Call(module, "Comparisons", 5, 5), Is.EqualTo(1 + 8 + 32));
    Assert.That(Call(module, "StringComparisons", "apple", "banana"), Is.EqualTo(2 + 4 + 8));
    Assert.That(Call(module, "ConcatAndPlus", 42), Is.EqualTo("v=42,True:abcd"));
  }

  [Test]
  public void ControlFlow_IfSelectForForEachDoWhileExit_Works() {
    var module = ImportOk("""
      Option Explicit

      Function Classify(n)
        If n < 0 Then Classify = -1 Else Classify = 1
        If n = 0 Then
          Classify = 0
        ElseIf n > 100 Then
          Classify = 2
        ElseIf n > 10 Then
          Classify = 1
        Else
          Classify = Classify
        End If
      End Function

      Function SelectDemo(k)
        Select Case k
          Case 1, 2
            SelectDemo = 10
          Case 3
            SelectDemo = 30
          Case Else
            SelectDemo = -1
        End Select
      End Function

      Function SelectStr(s)
        '@param s: str
        Select Case s
          Case "a", "b"
            SelectStr = 1
          Case Else
            SelectStr = 0
        End Select
      End Function

      Function ForLoops()
        Dim i, sum
        sum = 0
        For i = 1 To 5
          sum = sum + i
        Next i
        For i = 10 To 2 Step -2
          If i = 4 Then Exit For
          sum = sum + i
        Next
        ForLoops = sum
      End Function

      Function ForFloatStep()
        Dim x, total
        total = 0.0
        For x = 0.0 To 1.0 Step 0.5
          total = total + x
        Next
        ForFloatStep = total
      End Function

      Function ForEachSum()
        Dim arr, x, total
        arr = Array(10, 20, 30)
        total = 0
        For Each x In arr
          total = total + x
        Next x
        ForEachSum = total
      End Function

      Function DoLoops(limit)
        Dim i, sum
        i = 0 : sum = 0
        Do While i < limit
          i = i + 1
          sum = sum + i
        Loop
        Do Until i <= 0
          i = i - 1
          sum = sum + 1
        Loop
        Do
          i = i + 1
          If i = 3 Then Exit Do
        Loop
        Do
          i = i + 1
        Loop While i < 5
        Do
          i = i + 1
        Loop Until i >= 7
        While i < 9
          i = i + 1
        Wend
        DoLoops = sum + i
      End Function

      Function EarlyExit(x)
        If x < 0 Then
          EarlyExit = -99
          Exit Function
        End If
        EarlyExit = x * 2
      End Function

      Dim g_acc
      Private Sub AddGlobal(ByVal v)
        If v = 0 Then Exit Sub
        g_acc = g_acc + v
      End Sub

      Public Function TestSubCall()
        g_acc = 0
        Call AddGlobal(5)
        AddGlobal 7
        Call AddGlobal(0)
        TestSubCall = g_acc
      End Function
      """);

    Assert.That(Call(module, "Classify", 0), Is.EqualTo(0));
    Assert.That(Call(module, "Classify", 200), Is.EqualTo(2));
    Assert.That(Call(module, "Classify", 20), Is.EqualTo(1));
    Assert.That(Call(module, "Classify", -5), Is.EqualTo(-1));
    Assert.That(Call(module, "SelectDemo", 2), Is.EqualTo(10));
    Assert.That(Call(module, "SelectDemo", 3), Is.EqualTo(30));
    Assert.That(Call(module, "SelectDemo", 9), Is.EqualTo(-1));
    Assert.That(Call(module, "SelectStr", "b"), Is.EqualTo(1));
    Assert.That(Call(module, "SelectStr", "z"), Is.EqualTo(0));
    // 1+2+3+4+5 = 15; 10+8+6 = 24 => 39
    Assert.That(Call(module, "ForLoops"), Is.EqualTo(39));
    Assert.That((double)Call(module, "ForFloatStep")!, Is.EqualTo(1.5).Within(1e-9));
    Assert.That(Call(module, "ForEachSum"), Is.EqualTo(60));
    // sum = (1+2+3) + 3 = 9; i ends at 9 => 18
    Assert.That(Call(module, "DoLoops", 3), Is.EqualTo(18));
    Assert.That(Call(module, "EarlyExit", -1), Is.EqualTo(-99));
    Assert.That(Call(module, "EarlyExit", 4), Is.EqualTo(8));
    var inst = IrInterpreter.Instantiate(module);
    Assert.That(inst.Call("TestSubCall"), Is.EqualTo(12));
  }

  [Test]
  public void Arrays_DimReDimPreserveBoundsAndStrings_Work() {
    var module = ImportOk("""
      Option Explicit

      Function NumericReDim()
        Dim a(2), i, sum
        a(0) = 10 : a(1) = 20 : a(2) = 30
        ReDim Preserve a(4)
        a(3) = 40 : a(4) = 50
        ReDim Preserve a(3)
        sum = 0
        For i = LBound(a) To UBound(a)
          sum = sum + a(i)
        Next
        ReDim a(1)
        a(0) = 1 : a(1) = 2
        NumericReDim = sum + a(0) + a(1) + UBound(a)
      End Function

      Function StringArrayReDim()
        Dim s(1)
        s(0) = "hello"
        s(1) = "world"
        ReDim Preserve s(2)
        s(2) = "!"
        StringArrayReDim = Join(s, "-")
      End Function

      Function DynamicEmptyArray()
        Dim d()
        ReDim d(2)
        d(0) = 5 : d(1) = 6 : d(2) = 7
        DynamicEmptyArray = d(0) + d(1) + d(2) + UBound(Array())
      End Function
      """);

    // 10+20+30+40 = 100; + 1 + 2 + 1 = 104
    Assert.That(Call(module, "NumericReDim"), Is.EqualTo(104));
    Assert.That(Call(module, "StringArrayReDim"), Is.EqualTo("hello-world-!"));
    Assert.That(Call(module, "DynamicEmptyArray"), Is.EqualTo(17));
  }

  [Test]
  public void Builtins_AllMathStringConversionAndPredicates_Work() {
    var module = ImportOk("""
      Function MathBuiltins()
        Dim a, b, c, d, e, f, g
        a = Abs(-7) + Abs(-2.5)
        b = Sqr(9)
        c = Int(-2.7) + Fix(-2.7)
        d = Round(2.5) + Round(3.5) + Round(2.345, 2)
        e = Sgn(-10) + Sgn(0) + Sgn(4.5)
        MathBuiltins = a + b + c + d + e
      End Function

      Function StringBuiltins()
        Dim s, parts, res
        s = "  Hello World  "
        res = Trim(s) & "|" & LTrim("  hi") & "|" & RTrim("hi  ")
        res = res & "|" & UCase("aBc") & "|" & LCase("aBc")
        res = res & "|" & Left("abcdef", 3) & "|" & Left("ab", 10) & "|" & Left("ab", 0)
        res = res & "|" & Right("abcdef", 3) & "|" & Right("ab", 10) & "|" & Right("ab", 0)
        res = res & "|" & Mid("abcdef", 2, 3) & "|" & Mid("abcdef", 4) & "|" & Mid("ab", 9) & "|" & Mid("ab", 9, 2)
        res = res & "|" & Replace("foo bar foo", "foo", "baz") & "|" & Replace("abc", "", "x")
        res = res & "|" & Chr(65) & "|" & CStr(Asc("A")) & "|" & CStr(Len("abcd"))
        parts = Split("x,y,z", ",")
        res = res & "|" & Join(parts, ";") & "|" & Join(Split("a b"))
        res = res & "|" & CStr(UBound(Split("", ",")))
        StringBuiltins = res
      End Function

      Function InStrBuiltins()
        InStrBuiltins = InStr("hello world", "world") * 1000 _
          + InStr(3, "banana", "an") * 100 _
          + InStr(1, "Hello", "hello", 1) * 10 _
          + InStr(99, "abc", "a")
      End Function

      Function ConversionBuiltins()
        Dim a, b, c, d, e, f
        a = CInt(2.5) + CInt(3.5) + CLng("10") + CInt(True) + CInt(False)
        b = CDbl(4) + CDbl("1.5") + CDbl(True)
        c = 0
        If CBool(1) Then c = c + 1
        If Not CBool(0) Then c = c + 2
        If CBool("True") Then c = c + 4
        If Not CBool("False") Then c = c + 8
        If CBool("3") Then c = c + 16
        ConversionBuiltins = a + b + c
      End Function

      Function PredicateBuiltins()
        Dim unassigned, assigned
        assigned = 5
        Dim c
        c = 0
        If IsEmpty(Empty) Then c = c + 1
        If IsEmpty(unassigned) Then c = c + 2
        If Not IsEmpty(assigned) Then c = c + 4
        If IsNull(Null) Then c = c + 8
        If Not IsNull(assigned) Then c = c + 16
        If IsNumeric(42) Then c = c + 32
        If IsNumeric("12.5") Then c = c + 64
        If Not IsNumeric("abc") Then c = c + 128
        If IsNumeric(Empty) Then c = c + 256
        If Not IsNumeric(Null) Then c = c + 512
        PredicateBuiltins = c
      End Function
      """);

    // Math: (7+2.5) + 3 + (-3 + -2) + (2+4+2.35) + (-1+0+1) = 9.5 + 3 - 5 + 8.35 + 0 = 15.85
    Assert.That((double)Call(module, "MathBuiltins")!, Is.EqualTo(15.85).Within(1e-9));
    Assert.That(Call(module, "StringBuiltins"),
      Is.EqualTo("Hello World|hi|hi|ABC|abc|abc|ab||def|ab||bcd|def|||baz bar baz|abc|A|65|4|x;y;z|a b|-1"));
    // 7*1000 + 4*100 + 1*10 + 0 = 7410
    Assert.That(Call(module, "InStrBuiltins"), Is.EqualTo(7410));
    // a = 2 + 4 + 10 - 1 + 0 = 15; b = 4 + 1.5 - 1 = 4.5; c = 1+2+4+8+16 = 31 => 50.5
    Assert.That((double)Call(module, "ConversionBuiltins")!, Is.EqualTo(50.5).Within(1e-9));
    Assert.That(Call(module, "PredicateBuiltins"), Is.EqualTo(1023));
  }

  [Test]
  public void Classes_FieldsPropertiesMethodsInitializeAndNothing_Work() {
    var module = ImportOk("""
      Option Explicit

      Class Counter
        Private m_count
        Public Label
        Private m_items(1)

        Private Sub Class_Initialize()
          m_count = 10
          Label = "init"
          m_items(0) = 1
          m_items(1) = 2
        End Sub

        Public Property Get Value()
          Value = m_count
        End Property

        Public Property Let Value(ByVal v)
          If v < 0 Then Exit Property
          m_count = v
        End Property

        Public Sub Add(ByVal delta)
          m_count = m_count + delta
        End Sub

        Public Function Describe()
          Describe = Me.Label & ":" & CStr(Value + m_items(0) + m_items(1))
        End Function
      End Class

      Function RunCounter()
        Dim c, c2
        Set c = New Counter
        c.Add 5
        c.Value = 20
        c.Value = -10
        c.Label = "ok"
        Set c2 = c
        Dim same, isNotNull
        same = (c Is c2)
        Set c2 = Nothing
        isNotNull = (Not (c Is Nothing)) And (Nothing Is c2)
        If same And isNotNull Then
          RunCounter = c.Describe() & "=" & CStr(c.Value)
        Else
          RunCounter = "fail"
        End If
      End Function
      """);

    Assert.That(Call(module, "RunCounter"), Is.EqualTo("ok:23=20"));
  }

  [Test]
  public void ErrorHandling_OnErrorResumeNextGoTo0AndErrRaise_Works() {
    var module = ImportOk("""
      Function SafeDivide(a, b)
        On Error Resume Next
        Dim q
        q = a \ b
        If Err.Number <> 0 Then
          SafeDivide = -1
          Err.Clear
        Else
          SafeDivide = q
        End If
        On Error GoTo 0
      End Function

      Function CatchCustomRaise()
        On Error Resume Next
        Err.Raise 513, "Test", "boom"
        CatchCustomRaise = CStr(Err.Number) & ":" & Err.Description
      End Function

      Sub UncaughtRaise()
        Err.Raise 42
      End Sub
      """);

    var inst = IrInterpreter.Instantiate(module);
    Assert.That(inst.Call("SafeDivide", 10, 2), Is.EqualTo(5));
    Assert.That(inst.Call("SafeDivide", 10, 0), Is.EqualTo(-1));
    Assert.That(inst.Call("CatchCustomRaise"), Is.EqualTo("513:boom"));
    Assert.Throws<IrException>(() => inst.Call("UncaughtRaise"));
  }

  [Test]
  public void ContractsAndDocComments_AndTopLevelStatements_Work() {
    var module = ImportOk("""
      Dim total
      total = 0

      '@param a i32
      '@param b: i32
      '@return: i32
      Function Add(a, b)
        Add = a + b
      End Function

      Function Scale(x, factor)
        Scale = x * factor
      End Function

      total = Add(3, 4)
      """,
      contracts: """
        # contract comments are ignored
        ' vbs style comment too
        Scale.x: f64
        Scale.factor: f64
        Scale.return: f64
        total: i32
        """,
      fileName: "123-calc.vbs");

    Assert.That(module.Name, Is.EqualTo("_123calc"));
    var inst = IrInterpreter.Instantiate(module);
    Assert.That(inst.Call("Add", 3, 4), Is.EqualTo(7));
    Assert.That((double)inst.Call("Scale", 2.5, 4.0)!, Is.EqualTo(10.0).Within(1e-9));
    inst.Call("Main");
  }

  [Test]
  public void CrossTranspile_ToCSharpAndJavaScript_EmitsValidCode() {
    var module = ImportOk("""
      Option Explicit

      Public Function Factorial(ByVal n)
        Dim acc, i
        acc = 1
        For i = 2 To n
          acc = acc * i
        Next
        Factorial = acc
      End Function

      Public Function Greet(ByVal name)
        '@param name: str
        '@return: str
        Greet = "Hello, " & UCase(Trim(name)) & "!"
      End Function
      """, fileName: "Greeter.vbs");

    var csFiles = CSharpEmitter.Emit(module, "Demo");
    Assert.That(csFiles, Is.Not.Empty);
    var csCode = csFiles[0].Content;
    Assert.That(csCode, Does.Contain("Factorial"));
    Assert.That(csCode, Does.Contain("Greet"));

    var jsFiles = JavaScriptEmitter.Emit(module);
    Assert.That(jsFiles, Is.Not.Empty);
    var jsCode = jsFiles[0].Content;
    Assert.That(jsCode, Does.Contain("Factorial"));
    Assert.That(jsCode, Does.Contain("Greet"));
  }

  [Test]
  public void ErrorPaths_AllDiagnosticCodes_AreReported() {
    // PLY2600: Syntax errors
    AssertError("Function F()\n  F = \"unterminated\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  F = \"eof", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  F = &H\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  F = 1e+\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  F = 1 _ 2\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  F = @\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  Do While True\n  Loop While True\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  Exit Invalid\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  On Error Foo\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  ReDim a()\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  F = 1 + )\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function F()\n  Dim x\n  x = a.\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Class C\n  Property Bad P()\n  End Property\nEnd Class", VBScriptFrontend.SyntaxErrorCode);

    // PLY2601: Unsupported constructs
    AssertError("Sub S()\n  With x\n  End With\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  Erase a\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  Execute \"x=1\"\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  ExecuteGlobal \"x=1\"\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  Eval \"1+1\"\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  On Error GoTo Handler\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  Dim a(2, 3)\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Function F()\n  F = CreateObject(\"Scripting.Dictionary\")\nEnd Function", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  Exit For\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  Dim x : x = 1 : ReDim x(5)\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  Dim x : x = 1 : For Each y In x : Next\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Const BAD = Foo()", VBScriptFrontend.UnsupportedConstructCode);

    // PLY2602: Unsupported calls
    AssertError("Function F()\n  F = UnknownHelper(1, 2)\nEnd Function", VBScriptFrontend.UnsupportedCallCode);
    AssertError("Sub S()\n  WScript.Echo \"hi\"\nEnd Sub", VBScriptFrontend.UnsupportedCallCode);
    AssertError("Function F()\n  F = Abs(1, 2)\nEnd Function", VBScriptFrontend.UnsupportedCallCode);
    AssertError("Function F()\n  F = Err.Source\nEnd Function", VBScriptFrontend.UnsupportedCallCode);

    // PLY2603: Unresolved symbols
    AssertError("Option Explicit\nFunction F()\n  F = notDeclared\nEnd Function", VBScriptFrontend.UnresolvedSymbolCode);
    AssertError("Option Explicit\nSub S()\n  notDeclared = 1\nEnd Sub", VBScriptFrontend.UnresolvedSymbolCode);
    AssertError("Function F()\n  Set F = New MissingClass\nEnd Function", VBScriptFrontend.UnresolvedSymbolCode);
    AssertError("Function F()\n  F = Me\nEnd Function", VBScriptFrontend.UnresolvedSymbolCode);
    AssertError("Class C\n  Public X\nEnd Class\nFunction F()\n  Dim c : Set c = New C : F = c.NoSuchMember\nEnd Function", VBScriptFrontend.UnresolvedSymbolCode);
    AssertError("Class C\n  Public X\nEnd Class\nSub S()\n  Dim c : Set c = New C : c.NoSuchMember = 1\nEnd Sub", VBScriptFrontend.UnresolvedSymbolCode);

    // PLY2604: Invalid contracts
    AssertError("Function F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode, contracts: "badline");
    AssertError("Function F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode, contracts: "F.x: NoSuchType");
    AssertError("Function F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode, contracts: "F.x: i32\nF.x: i32");
    AssertError("'@param badformat\nFunction F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode);
    AssertError("'@param x: Nope\nFunction F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode);
    AssertError("'@return: Nope\nFunction F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode);
    AssertError("Function F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode, contracts: "# comment\n' comment\nF.x: ");
    AssertError("Function F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode, contracts: "F.x: void[]");
    AssertError("Function F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode, contracts: "F.x: seq<void>");
    AssertError("Function F(x)\n  F = x\nEnd Function", VBScriptFrontend.InvalidContractCode, contracts: "F.x: seq<Nope>");

    var docRes = VBScriptFrontend.Import("Doc.vbs", """
      ' @param a i32
      ' @param b: seq<i32>
      ' @param c: i32?
      ' @param d: string?
      ' @return i32
      Function SumDoc(a, b, c, d)
        SumDoc = a + b(0)
      End Function
      """);
    Assert.That(docRes.Diagnostics, Is.Empty);
    var docMod = docRes.Module!;
    Assert.That(docMod.Functions[0].Parameters[0].Type, Is.EqualTo(IrType.I32));
    Assert.That(docMod.Functions[0].Parameters[1].Type, Is.EqualTo(IrType.Array(IrType.I32)));
    Assert.That(docMod.Functions[0].Parameters[2].Type, Is.EqualTo(IrType.Optional(IrType.I32)));
    Assert.That(docMod.Functions[0].Parameters[3].Type, Is.EqualTo(IrType.String));
    Assert.Throws<ArgumentNullException>(() => VBScriptFrontend.Import(null!, ""));
    Assert.Throws<ArgumentNullException>(() => VBScriptFrontend.Import("a.vbs", null!));

    // Additional parser/lowerer error paths
    AssertError("Function F(a, b)\n  F = a + b\nEnd Function\nFunction G()\n  G = F(1)\nEnd Function", VBScriptFrontend.UnsupportedCallCode);
    AssertError("Class C\n  Public X\nEnd Class\nFunction F()\n  Dim c : Set c = New C : F = c.UnknownMethod(1)\nEnd Function", VBScriptFrontend.UnsupportedCallCode);
    AssertError("Function F()\n  Dim a(2) : F = a(0, 1)\nEnd Function", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Sub S()\n  Dim a(2) : a(0, 1) = 5\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Option Explicit\nSub S()\n  ReDim missing(3)\nEnd Sub", VBScriptFrontend.UnresolvedSymbolCode);
    AssertError("Sub S()\n  Exit Do\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Const BAD = -\"str\"", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Function F()\n  F = 1 2\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Class C\n  1 + 2\nEnd Class", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Function 123()\nEnd Function", VBScriptFrontend.SyntaxErrorCode);
    AssertError("Sub S()\n  Dim x : x = 1\n  (x) = 2\nEnd Sub", VBScriptFrontend.UnsupportedConstructCode);
    AssertError("Function F()\n  F = (1)(2)\nEnd Function", VBScriptFrontend.UnsupportedCallCode);
  }

  [Test]
  public void EdgeCases_ContractsGlobalsClassesOptionalsAndCrlf_Work() {
    var crlfSource = string.Join("\r\n", [
      "Dim gArr(2)",
      "Dim gStrArr(1)",
      "Dim gEmpty()",
      "Dim gFloat",
      "Dim gBool",
      "Dim gStr",
      "Dim gObj",
      "",
      "Class Box",
      "  Public Items(1)",
      "  Private m_v",
      "  Public Default Property Get Val()",
      "    Val = m_v",
      "  End Property",
      "  Public Property Let OnlySet(ByRef x)",
      "    m_v = x",
      "  End Property",
      "  Private Function Helper()",
      "    Helper = m_v + 1",
      "  End Function",
      "  Public Function Run()",
      "    OnlySet = 7",
      "    Items(0) = 3",
      "    Run = Helper + Helper() + Val + Items(0)",
      "  End Function",
      "End Class",
      "",
      "Function ZeroFn()",
      "  ZeroFn = 5",
      "End Function",
      "",
      "Function UseLocalsAndTypes(ByRef arr(), opt, f32v, i64v, objArg)",
      "  Const LOCAL_K = 4",
      "  Dim s, b1, b2, uninit",
      "  s = \"hi\"",
      "  b1 = (True = True) And (True <> False)",
      "  b2 = False",
      "  If s Then b2 = True",
      "  Dim tmp : tmp = 0",
      "  If objArg Then tmp = tmp + 1",
      "  If IsEmpty(opt) And IsNull(opt) And Not IsNull(s) And Not IsNumeric(objArg) Then tmp = tmp + 2",
      "  If Not (Empty Is Nothing) And Not (Nothing Is Empty) Then tmp = tmp + 4",
      "  gArr(0) = 10",
      "  gStrArr(0) = \"ok\"",
      "  Dim big : big = 99999999999999999999",
      "  Dim bits64 : bits64 = (i64v And 7) Or (i64v Xor 3) + (Not i64v)",
      "  Dim parts : parts = Split(\"a,b,c\", \",\", 2)",
      "  Dim pos : pos = InStr(1, \"abc\", \"b\", 0)",
      "  Dim singleLine : singleLine = 0",
      "  If b1 And b2 Then singleLine = 1 : singleLine = singleLine + 1 Else singleLine = 9 : singleLine = 10",
      "  Select Case pos",
      "    Case 2",
      "      singleLine = singleLine + 1",
      "  End Select",
      "  Dim box : Set box = New Box",
      "  UseLocalsAndTypes = LOCAL_K + ZeroFn + gArr(0) + arr(0) + box.Run + box.Items(0) + tmp + singleLine + UBound(parts) + CInt(f32v) + CInt(bits64 - bits64) + CInt(big - big) + uninit",
      "End Function",
      "",
      "Sub Main()",
      "  Dim x",
      "  x = ZeroFn",
      "End Sub",
      "",
      "gFloat = 1.5",
      "gBool = True",
      "gStr = \"top\"",
      "Set gObj = Nothing"
    ]);

    var contracts = """
      gFloat: f64
      gBool: bool
      gStr: str
      gStrArr: str[]
      gObj: Box
      UseLocalsAndTypes.arr: seq<i32>
      UseLocalsAndTypes.opt: i32?
      UseLocalsAndTypes.f32v: f32
      UseLocalsAndTypes.i64v: i64
      UseLocalsAndTypes.objArg: Box
      UseLocalsAndTypes.return: i32
      """;

    var module = ImportOk(crlfSource, contracts, fileName: "");
    Assert.That(module.Name, Is.EqualTo("VBScriptModule"));
    var symModule = ImportOk("Function F()\n  F = 1\nEnd Function", fileName: "---.vbs");
    Assert.That(symModule.Name, Is.EqualTo("_"));

    var inst = IrInterpreter.Instantiate(module);
    inst.Call("_TopLevel");
    inst.Call("Main");

    // Test tokens directly at EOF (no trailing newline) and implicit locals without Option Explicit.
    var eofMod = ImportOk("Function F(x)\n  y = x + 1\n  F = y + z\nEnd Function\nDim t : t = &H1A");
    Assert.That(Call(eofMod, "F", 5), Is.EqualTo(6));
    var eofFloat = ImportOk("Const A = 1.25");
    Assert.That(eofFloat.Constants[0].Name, Is.EqualTo("A"));
    var eofExp = ImportOk("Const A = 1e2");
    Assert.That(eofExp.Constants[0].Name, Is.EqualTo("A"));
  }

}
