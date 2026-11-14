using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Views;

/// <summary>
/// The views in both backends and every profile (#1325): every operation on every element type has a form
/// in every profile, the C# of each profile compiles against its reference assemblies and computes what
/// .NET's build of the fixture computes. <c>node</c> runs the JavaScript in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class ViewBackendTests {

  private static readonly IrType[] _elements = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.F32, IrType.F64];

  private static readonly JavaScriptProfile[] _javaScript = [
    JavaScriptProfile.Parse("es5"), JavaScriptProfile.Parse("es5", typedArrays: true), JavaScriptProfile.Parse("es2015"),
    JavaScriptProfile.Parse("es2017"), JavaScriptProfile.Parse("es2020"), JavaScriptProfile.Parse("es2022")
  ];

  /// <summary>
  /// One function per element type that uses every operation of the views: made of an array, sliced,
  /// measured, indexed, stepped, allocated, copied, filled, compared, copied out, reinterpreted as bytes and
  /// back, and read and written as the number through a view of bytes.
  /// </summary>
  internal static IrModule EveryOperation() {
    var functions = new List<IrFunction>();
    foreach (var element in _elements) {
      var span = IrType.Span(element);
      var readOnly = IrType.ReadOnlySpan(element);
      var bytes = IrType.ReadOnlySpan(IrType.U8);
      var array = new IrLocal("a", IrType.Array(element));
      var raw = new IrLocal("b", IrType.Array(IrType.U8));
      var value = new IrLocal("v", element);
      var s = new IrLocal("s", span);
      var r = new IrLocal("r", readOnly);
      var t = new IrLocal("t", span);
      var x = new IrLocal("x", bytes);
      var w = new IrLocal("w", IrType.Span(IrType.U8));
      IrConst Word(int number) => new(IrType.I32, number);
      IrIntrinsic Node(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);
      List<IrStmt> body = [
        new IrDeclare(s, Node(IrIntrinsicOp.ViewOf, span, new IrLocalRef(array))),
        new IrDeclare(r, Node(IrIntrinsicOp.ViewOf, readOnly, new IrLocalRef(s))),
        new IrAssign(s, Node(IrIntrinsicOp.ViewOf, span, new IrLocalRef(array), Word(1))),
        new IrAssign(s, Node(IrIntrinsicOp.ViewOf, span, new IrLocalRef(array), Word(0), Word(2))),
        new IrAssign(r, Node(IrIntrinsicOp.ViewOf, readOnly, new IrLocalRef(s), Word(0))),
        new IrAssign(r, Node(IrIntrinsicOp.Slice, readOnly, new IrLocalRef(r), Word(0), Word(1))),
        new IrAssign(s, Node(IrIntrinsicOp.Slice, span, new IrLocalRef(s), Word(1))),
        new IrDeclare(t, Node(IrIntrinsicOp.StackAlloc, span, Word(4))),
        new IrAssign(t, Node(IrIntrinsicOp.StackAlloc, span, Node(IrIntrinsicOp.ViewLength, IrType.I32, new IrLocalRef(s)))),
        new IrExprStmt(Node(IrIntrinsicOp.ViewCopy, IrType.Void, new IrLocalRef(r), new IrLocalRef(t))),
        new IrExprStmt(Node(IrIntrinsicOp.ViewTryCopy, IrType.Bool, new IrLocalRef(s), new IrLocalRef(t))),
        new IrExprStmt(Node(IrIntrinsicOp.ViewFill, IrType.Void, new IrLocalRef(t), new IrLocalRef(value))),
        new IrStore(new IrLocalRef(t), Word(0), new IrArrayLoad(new IrLocalRef(r), Word(0))),
        new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(t), Word(0)), true, false, false)),
        new IrDeclare(x, Node(IrIntrinsicOp.Reinterpret, bytes, new IrLocalRef(r))),
        new IrAssign(r, Node(IrIntrinsicOp.Reinterpret, readOnly, new IrLocalRef(x))),
        new IrDeclare(w, Node(IrIntrinsicOp.ViewOf, IrType.Span(IrType.U8), new IrLocalRef(raw)))
      ];
      if (element.Size > 1) {
        body.Add(new IrExprStmt(Node(IrIntrinsicOp.StoreLittle, IrType.Void, new IrLocalRef(w), Word(1), new IrLocalRef(value))));
        body.Add(new IrExprStmt(Node(IrIntrinsicOp.StoreBig, IrType.Void, new IrLocalRef(w), Word(1), Node(IrIntrinsicOp.LoadLittle, element, new IrLocalRef(x), Word(0)))));
        body.Add(new IrAssign(value, Node(IrIntrinsicOp.LoadBig, element, new IrLocalRef(w), Word(1))));
      }

      body.Add(new IrReturn(new IrConditional(
        Node(IrIntrinsicOp.ViewEqual, IrType.Bool, new IrLocalRef(t), new IrLocalRef(r)),
        Node(IrIntrinsicOp.ViewToArray, IrType.Array(element), new IrLocalRef(t)),
        Node(IrIntrinsicOp.ViewToArray, IrType.Array(element), new IrLocalRef(s)))));
      functions.Add(new IrFunction("F" + element, true, IrType.Array(element), [array, raw, value], [s, r, t, x, w], new IrBlock(body)));
    }

    return new IrModule("Views", functions);
  }

  /// <summary>
  /// Test design 16: every operation on every element type has a form in every C# profile - no PLY1052, no
  /// PLY1053 - and the file compiles against the profile's reference assemblies without a diagnostic.
  /// </summary>
  [Test, Category("Slow")]
  public void EveryOperation_IsWrittenInEveryCSharpProfile() {
    var module = EveryOperation();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    }
  }

  /// <summary>
  /// Test design 16 for JavaScript: every operation has a form in every level, but a reinterpretation of a
  /// 64-bit integer or a floating-point number in a profile that stores it in a plain array, which is
  /// PLY1152; the file is at its level.
  /// </summary>
  [Test]
  public void EveryOperation_IsWrittenInEveryJavaScriptProfile() {
    var module = EveryOperation();
    foreach (var profile in _javaScript) {
      var refused = JavaScriptEmitter.Check(module, profile).Select(diagnostic => diagnostic.Message).ToList();
      var expected = Refusals(profile);
      Assert.That(refused, Is.EquivalentTo(expected), profile.ToString());
      if (expected.Count == 0)
        Assert.That(GeneratedJavaScript.Problems(JavaScriptEmitter.Emit(module, profile).Single().Content, profile), Is.Empty, profile.ToString());
    }
  }

  /// <summary>The reinterpretations a profile cannot write: those of the element types it keeps in plain arrays that are no integers of up to 32 bits.</summary>
  private static List<string> Refusals(JavaScriptProfile profile) {
    bool Plain(IrType element) => !profile.TypedArrays || (element.IsLong && profile.Level < JavaScriptLevel.Es2020);
    return [.. _elements.Where(element => Plain(element) && (element.IsFloat || element.IsLong))
      .SelectMany(element => new[] { $"readonly span<{element}> as readonly span<u8>", $"readonly span<u8> as readonly span<{element}>" })
      .Select(what => $"'Reinterpret of {what}' cannot be expressed in JavaScript profile '{profile.LevelName}'")];
  }

  private static readonly IReadOnlyList<PrototypeCall> _calls = [.. SpanFixtures.Calls()];

  private static IEnumerable<TestCaseData> Profiles() => CSharpProfile.All.Select(profile => new TestCaseData(profile.Name).SetArgDisplayNames(profile.Name));

  /// <summary>Test design 1 to 12: the C# of every profile, compiled, computes every call of the fixtures as .NET's build of them does.</summary>
  [TestCaseSource(nameof(Profiles)), Category("Slow")]
  public void Fixture_ComputesInEveryCSharpProfileWhatDotNetComputes(string name) {
    var profile = CSharpProfile.Parse(name);
    var text = CSharpEmitter.Emit(SpanFixtures.Imported("Spans"), profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    foreach (var call in _calls)
      Assert.That(SpanFixtures.Outcome(build.Method(call.Method), call), Is.EqualTo(SpanFixtures.DotNet("Spans", call)), $"{call} in {name}");

    var adler = CSharpEmitter.Emit(SpanFixtures.Imported("Adler32Span"), profile).Single().Content;
    var check = new PrototypeCall("Compute", [SpanFixtures.Wikipedia, 5, 9]);
    Assert.That(SpanFixtures.Outcome(GeneratedCSharp.Compile(adler, true).Method("Compute"), check), Does.StartWith("300286872 | "));
  }

  /// <summary>The JavaScript fixtures in C# compute what the interpreter computes of them, in every C# profile: <c>subarray</c> keeps its clamping.</summary>
  [TestCaseSource(nameof(Profiles)), Category("Slow")]
  public void JavaScriptFixture_ComputesInEveryCSharpProfile(string name) {
    var profile = CSharpProfile.Parse(name);
    var module = JavaScriptFrontend.Import("views.mjs", File.ReadAllText(SpanFixtures.PathOf("views.mjs"))).Module!;
    var text = CSharpEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    PrototypeCall[] calls = [
      new("word", [SpanFixtures.D]), new("tail", [SpanFixtures.D]), new("lengths", [SpanFixtures.D]), new("sliceVsSub", [SpanFixtures.D]),
      new("copied", [SpanFixtures.D, 2]), new("copied", [SpanFixtures.D, -3]), new("copied", [SpanFixtures.D, 14])
    ];
    foreach (var call in calls)
      Assert.That(SpanFixtures.Outcome(build.Method(call.Method), call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)), $"{call} in {name}");
  }

  /// <summary>
  /// The forms of the profiles: an <c>ArraySegment</c> and its helpers before .NET Standard 2.1, a span from it
  /// on; <c>stackalloc</c> only for a constant length of fewer than 1024 bytes outside every loop.
  /// </summary>
  [TestCase("net2.0", "global::System.ArraySegment<byte> s = ViewOf(d, start, length);", "new global::System.ArraySegment<uint>(new uint[8])")]
  [TestCase("netstandard2.0", "global::System.ArraySegment<byte> s = ViewOf(d, start, length);", "ViewSet<uint>(t, i, ")]
  [TestCase("netstandard2.1", "global::System.ReadOnlySpan<byte> s = global::System.MemoryExtensions.AsSpan(d, start, length);", "global::System.Span<uint> t = stackalloc uint[8];")]
  [TestCase("net10.0", "global::System.Span<int> t = new int[n];", "global::System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(")]
  [TestCase("net10.0", "a.AsSpan(0, 8).CopyTo", "global::System.MemoryExtensions.SequenceEqual(")]
  public void Form_IsTheOneOfTheProfile(string name, string first, string second) {
    var text = CSharpEmitter.Emit(SpanFixtures.Imported("Spans"), CSharpProfile.Parse(name)).Single().Content.Replace("global::System.MemoryExtensions.AsSpan(a, 0, 8)", "a.AsSpan(0, 8)");
    Assert.That(text, Does.Contain(first).And.Contain(second));
  }

  /// <summary>A large or looped <c>stackalloc</c> is an array: 1024 bytes and more, or inside a loop.</summary>
  [TestCase(255, true)]
  [TestCase(256, false)]
  public void StackAlloc_StaysBelow1024Bytes(int count, bool onStack) {
    var t = new IrLocal("t", IrType.Span(IrType.I32));
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [t], new IrBlock([
      new IrDeclare(t, new IrIntrinsic(IrIntrinsicOp.StackAlloc, t.Type, [new IrConst(IrType.I32, count)])),
      new IrWhile(new IrConst(IrType.Bool, 0), new IrBlock([new IrAssign(t, new IrIntrinsic(IrIntrinsicOp.StackAlloc, t.Type, [new IrConst(IrType.I32, 1)]))])),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [new IrLocalRef(t)]))
    ]))]);
    var text = CSharpEmitter.Emit(module, CSharpProfile.Net100).Single().Content;
    Assert.That(text.Contains($"stackalloc int[{count}]"), Is.EqualTo(onStack), text);
    Assert.That(text, Does.Contain("t = new int[1];"));
  }

}
