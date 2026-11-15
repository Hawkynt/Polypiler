using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Views;

/// <summary>
/// <c>Memory&lt;T&gt;</c> and <c>ReadOnlyMemory&lt;T&gt;</c> (#1325, PR 2): the view a class keeps in a field. The IR
/// types and their operations, the verifier's rules for fields, the interpreter against .NET's build of
/// <c>MemoryHolder.cs</c>, both frontends - a JavaScript view that escapes into a field is a memory - and
/// both backends in every profile; and <c>for…of</c> over a typed array or a view.
/// </summary>
[TestFixture]
internal sealed class MemoryTests {

  private static readonly IrType _bytes = IrType.Array(IrType.U8);
  private static readonly IrType _span = IrType.Span(IrType.U8);
  private static readonly IrType _memory = IrType.Memory(IrType.U8);
  private static readonly IrType _readOnly = IrType.ReadOnlyMemory(IrType.U8);
  private static readonly IrConst _zero = new(IrType.I32, 0);

  // ---------------------------------------------------------------- types and typing

  /// <summary>A memory is no sequence and no view: its elements are reached through its span. It is storable of numbers only.</summary>
  [Test]
  public void MemoryTypes_AreStorableViewsOfNumbers() {
    Assert.That((_memory.IsMemory, _readOnly.IsMemory, _memory.IsView, _memory.IsSequence, _memory.IsStorable), Is.EqualTo((true, true, false, false, true)));
    Assert.That((_span.IsMemory, _bytes.IsMemory), Is.EqualTo((false, false)));
    Assert.That(IrType.Memory(IrType.Struct(new IrStructType("S"))).IsStorable, Is.False);
    Assert.That((_memory.ToString(), _readOnly.ToString()), Is.EqualTo(("memory<u8>", "readonly memory<u8>")));
    Assert.That(_memory, Is.EqualTo(IrType.Memory(IrType.U8)).And.Not.EqualTo(_readOnly).And.Not.EqualTo(_span));
  }

  /// <summary>
  /// The operations a memory takes: made of an array or a memory, never of a span; sliced, measured and copied
  /// out as itself; read as a span. Nothing else of the views takes it, and nothing writable is made of a read-only one.
  /// </summary>
  [TestCase(IrIntrinsicOp.ViewOf, "mem<u8>", "u8[]", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "mem<u8>", "u8[] i32 i32", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "romem<u8>", "u8[] i32", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "romem<u8>", "mem<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "mem<u8>", "romem<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "mem<u8>", "span<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "romem<u8>", "ro<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "mem<u32>", "u8[]", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "mem<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "ro<u8>", "romem<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "ro<u8>", "mem<u8> i32 i32", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "romem<u8>", false)]
  [TestCase(IrIntrinsicOp.Slice, "mem<u8>", "mem<u8> i32", true)]
  [TestCase(IrIntrinsicOp.Slice, "romem<u8>", "romem<u8> i32 i32", true)]
  [TestCase(IrIntrinsicOp.Slice, "span<u8>", "mem<u8> i32", false)]
  [TestCase(IrIntrinsicOp.ViewLength, "i32", "romem<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewToArray, "u8[]", "mem<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewToArray, "u32[]", "mem<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewCopy, "void", "mem<u8> span<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewFill, "void", "mem<u8> u8", false)]
  [TestCase(IrIntrinsicOp.ViewEqual, "bool", "mem<u8> mem<u8>", false)]
  [TestCase(IrIntrinsicOp.Reinterpret, "romem<u32>", "romem<u8>", false)]
  [TestCase(IrIntrinsicOp.LoadLittle, "u32", "romem<u8> i32", false)]
  public void Operation_TakesItsTypes(IrIntrinsicOp op, string result, string arguments, bool accepted)
    => Assert.That(IrOperators.Accepts(op, Type(result), [.. arguments.Split(' ').Select(Type)]), Is.EqualTo(accepted));

  /// <summary>A type as the test cases spell it: <c>mem&lt;…&gt;</c>, <c>romem&lt;…&gt;</c>, <c>span&lt;…&gt;</c>, <c>ro&lt;…&gt;</c>, an array or a number.</summary>
  private static IrType Type(string spelled) {
    foreach (var (prefix, make) in new (string, Func<IrType, IrType>)[] {
      ("romem<", IrType.ReadOnlyMemory), ("mem<", IrType.Memory), ("ro<", IrType.ReadOnlySpan), ("span<", IrType.Span)
    })
      if (spelled.StartsWith(prefix, StringComparison.Ordinal))
        return make(Type(spelled[prefix.Length..^1]));

    if (spelled.EndsWith("[]", StringComparison.Ordinal))
      return IrType.Array(Type(spelled[..^2]));

    return spelled switch { "void" => IrType.Void, "bool" => IrType.Bool, "u8" => IrType.U8, "i32" => IrType.I32, _ => IrType.U32 };
  }

  // ---------------------------------------------------------------- the verifier

  private static IReadOnlyList<string> Verified(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  /// <summary>A module of one class with one field of a type, and nothing else.</summary>
  private static IrModule Holding(IrType type, bool record) {
    var holder = new IrClassType("Holder") { IsRecord = record };
    holder.Define([new IrField("Window", type, false)]);
    var constructor = new IrConstructor("Holder.ctor");
    holder.DefineMembers([constructor], [], [], []);
    var function = new IrFunction("Holder.ctor", false, IrType.Void, [new IrLocal("this", IrType.Object(holder))], [], new IrBlock([]));
    return new IrModule("M", [function]) { Types = [holder] };
  }

  /// <summary>Test design 17: a memory is a field of a class; a span, a memory of a record and one of a struct are not.</summary>
  [Test]
  public void Memory_IsAFieldOfAClassThatIsNoRecord() {
    Assert.That(Verified(Holding(_readOnly, false)), Is.Empty);
    Assert.That(Verified(Holding(_memory, false)), Is.Empty);
    Assert.That(Verified(Holding(_memory, true)), Has.Some.Contains("the field 'Window' has the type memory<u8>"));
    Assert.That(Verified(Holding(_span, false)), Has.Some.Contains("a span is stored outside the stack"));

    var value = new IrStructType("Value");
    value.Define([new IrField("Window", _memory, false)]);
    Assert.That(Verified(new IrModule("M", []) { Types = [value] }), Has.Some.Contains("the field 'Window' has the type memory<u8>"));
  }

  /// <summary>A memory is neither a global nor a constant of the module, and its elements are not taken directly.</summary>
  [Test]
  public void Memory_IsNoGlobalConstantOrSequence() {
    var global = new IrModule("M", []) { Globals = [new IrGlobal("G", _memory, null)] };
    Assert.That(Verified(global), Has.Some.Contains("a global of the type memory<u8>"));
    Assert.That(Verified(new IrModule("M", [], [new IrModuleConst("K", _memory, [0])])), Has.Some.Contains("a constant of the type memory<u8>"));

    var memory = new IrLocal("m", _memory);
    var element = new IrModule("M", [new IrFunction("F", true, IrType.U8, [memory], [], new IrBlock([new IrReturn(new IrArrayLoad(new IrLocalRef(memory), _zero))]))]);
    Assert.That(Verified(element), Has.Some.Contains("an element is taken of the type memory<u8>"));
  }

  /// <summary>A memory crosses the module format with its kind and element type.</summary>
  [Test]
  public void ModuleFormat_KeepsMemories() {
    var memory = new IrLocal("m", _readOnly);
    var local = new IrLocal("w", IrType.Memory(IrType.U32));
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [memory], [local], new IrBlock([
      new IrDeclare(local, new IrIntrinsic(IrIntrinsicOp.ViewOf, local.Type, [new IrNewArray(IrType.Array(IrType.U32), new IrConst(IrType.I32, 2))])),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [new IrLocalRef(memory)]))
    ]))]);
    Assert.That(Verified(module), Is.Empty);
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That((read.Functions[0].Parameters[0].Type, read.Functions[0].Locals[0].Type), Is.EqualTo((_readOnly, IrType.Memory(IrType.U32))));
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(IrModuleFormat.Write(module)));
  }

  /// <summary>A memory crosses into the interpreter as the whole of an array of its element type, and out of it as a copy.</summary>
  [Test]
  public void Memory_CrossesTheBoundaryAsAnArray() {
    var memory = new IrLocal("m", _memory);
    var module = new IrModule("M", [new IrFunction("F", true, _memory, [memory], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.Slice, _memory, [new IrLocalRef(memory), new IrConst(IrType.I32, 1)]))
    ]))]);
    Assert.That(IrInterpreter.Call(module, "F", [new byte[] { 1, 2, 3 }]), Is.EqualTo(new byte[] { 2, 3 }));
    Assert.That(() => IrInterpreter.Call(module, "F", [new int[] { 1 }]), Throws.ArgumentException);
  }

  // ---------------------------------------------------------------- MemoryHolder.cs

  private const string Holder = SpanFixtures.Holder;

  private static IEnumerable<TestCaseData> Calls() => SpanFixtures.HolderCalls().Select(call => new TestCaseData(call).SetArgDisplayNames(call.ToString()));

  /// <summary>The fixture imports into IR the verifier accepts, its fields a read-only and a writable memory.</summary>
  [Test]
  public void Holder_ImportsWithMemoryFields() {
    var module = SpanFixtures.Imported("MemoryHolder");
    Assert.That(Verified(module), Is.Empty);
    var holder = module.Types.OfType<IrClassType>().Single();
    Assert.That(holder.Fields.Select(field => field.Type), Is.EqualTo(new[] { _readOnly, _memory }));
  }

  /// <summary>Test design 17 in the interpreter: every call does what .NET does; the window 4..7 sums to 22.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_IsDotNet(PrototypeCall call)
    => Assert.That(PrototypeFixture.InterpretedOutcome(SpanFixtures.Imported("MemoryHolder"), call), Is.EqualTo(SpanFixtures.DotNet(Holder, call)));

  /// <summary>The value of the test design, as .NET computes it.</summary>
  [Test]
  public void DotNet_SumsTheWindowTo22()
    => Assert.That(SpanFixtures.DotNet(Holder, new PrototypeCall("Window", [SpanFixtures.D, 4, 4])), Does.StartWith("22 | "));

  private static IEnumerable<TestCaseData> CSharpProfiles() => CSharpProfile.All.Select(profile => new TestCaseData(profile.Name).SetArgDisplayNames(profile.Name));

  /// <summary>
  /// Test design 17 in every C# profile: the file compiles against the profile's reference assemblies and every
  /// call computes what .NET computes - a <c>Memory&lt;T&gt;</c> from .NET Standard 2.1 on, an <c>ArraySegment&lt;T&gt;</c> before.
  /// </summary>
  [TestCaseSource(nameof(CSharpProfiles)), Category("Slow")]
  public void Holder_ComputesInEveryCSharpProfileWhatDotNetComputes(string name) {
    var profile = CSharpProfile.Parse(name);
    var text = CSharpEmitter.Emit(SpanFixtures.Imported("MemoryHolder"), profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    foreach (var call in SpanFixtures.HolderCalls())
      Assert.That(SpanFixtures.Outcome(build.Method(call.Method), call), Is.EqualTo(SpanFixtures.DotNet(Holder, call)), $"{call} in {name}");
  }

  /// <summary>The forms of a memory per profile: the type, its making, its span; an empty segment as the first value of a field without spans.</summary>
  [TestCase("net2.0", "private readonly global::System.ArraySegment<byte> _window = new global::System.ArraySegment<byte>(new byte[0]);", "this._window = MemoryHolderDemo.ViewOf(data, start, length);")]
  [TestCase("netstandard2.0", "global::System.ArraySegment<byte> s = this._window;", "global::System.ArraySegment<byte> part = MemoryHolderDemo.Slice(this._window, start, length);")]
  [TestCase("netstandard2.1", "private readonly global::System.ReadOnlyMemory<byte> _window;", "this._window = global::System.MemoryExtensions.AsMemory(data, start, length);")]
  [TestCase("net10.0", "global::System.ReadOnlySpan<byte> s = this._window.Span;", "global::System.ReadOnlyMemory<byte> whole = global::System.MemoryExtensions.AsMemory(d);")]
  public void Form_IsTheOneOfTheProfile(string name, string first, string second) {
    var text = CSharpEmitter.Emit(SpanFixtures.Imported("MemoryHolder"), CSharpProfile.Parse(name)).Single().Content;
    Assert.That(text, Does.Contain(first).And.Contain(second));
  }

  /// <summary>The span of a part of a memory: its span, sliced, where the profile has spans.</summary>
  [Test]
  public void SpanOfAPartOfAMemory_IsItsSpanSliced() {
    var memory = new IrLocal("m", _memory);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [memory], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.ViewOf, _span, [new IrLocalRef(memory), new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2)])]))
    ]))]);
    Assert.That(Verified(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", [new byte[] { 1, 2, 3 }]), Is.EqualTo(2));
    Assert.That(CSharpEmitter.Emit(module, CSharpProfile.Net100).Single().Content, Does.Contain("m.Span.Slice(1, 2).Length"));
    Assert.That(CSharpEmitter.Emit(module, CSharpProfile.Net20).Single().Content, Does.Contain("ViewOf(m, 1, 2).Count").Or.Contain("Slice(m, 1, 2).Count"));
  }

  private static readonly JavaScriptProfile[] _javaScript = [
    JavaScriptProfile.Parse("es5"), JavaScriptProfile.Parse("es5", typedArrays: true), JavaScriptProfile.Parse("es2015"),
    JavaScriptProfile.Parse("es2017"), JavaScriptProfile.Parse("es2020"), JavaScriptProfile.Parse("es2022")
  ];

  /// <summary>
  /// A memory in JavaScript is a view: every level writes the fixture without a refusal, at its level, an empty
  /// typed array or an empty view object the first value of a field. node runs it in the acceptance tests.
  /// </summary>
  [Test]
  public void Holder_IsWrittenInEveryJavaScriptProfile() {
    var module = SpanFixtures.Imported("MemoryHolder");
    foreach (var profile in _javaScript) {
      Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty, profile.ToString());
      var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
      Assert.That(text, Does.Contain(profile.TypedArrays ? "new Uint8Array(0)" : "$vobj([])"), profile.ToString());
    }
  }

  // ---------------------------------------------------------------- the C# frontend

  private const string Usings = "using System; ";

  private static string Refused(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", Usings + source);
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  /// <summary>Test design 14 for the memory types: members the table does not list, a field of a record, a static field, a memory of char.</summary>
  [TestCase("public static class P { public static int F(byte[] a, byte[] b) { a.AsMemory().CopyTo(b.AsMemory()); return 0; } }", "Memory.CopyTo")]
  [TestCase("public static class P { public static bool F(byte[] a) => a.AsMemory().Equals(a.AsMemory()); }", "Memory.Equals")]
  [TestCase("public sealed record R(ReadOnlyMemory<byte> M); public static class P { public static int F(byte[] a) => new R(a).M.Length; }", "in a record")]
  [TestCase("public sealed class C { public static Memory<byte> M; } public static class P { public static int F() => C.M.Length; }", "a static field of the type System.Memory<byte>")]
  [TestCase("public static class P { public static int F(Memory<char> m) => m.Length; }", "System.Memory<char>")]
  [TestCase("public static class P { public static int F(Memory<bool> m) => m.Length; }", "System.Memory<bool>")]
  public void DiagnosedConstruct_IsRefusedByName(string source, string named)
    => Assert.That(Refused(source), Does.Contain(CSharpFrontend.Unsupported).And.Contain(named));

  /// <summary>The table lists the members of the memory types, each found by its type, name and number of arguments.</summary>
  [Test]
  public void Table_FindsTheMembersOfMemory() {
    Assert.That(CSharpViewApi.Find("System.MemoryExtensions", "AsMemory", 3)!.Ir, Is.EqualTo("ViewOf"));
    Assert.That(CSharpViewApi.Find("System.Memory<T>", "Slice", 2)!.Ir, Is.EqualTo("Slice"));
    Assert.That(CSharpViewApi.Find("System.ReadOnlyMemory<T>", "ToArray", 1)!.Ir, Is.EqualTo("ViewToArray"));
    Assert.That(CSharpViewApi.Find("System.Memory<T>", "Pin", 1), Is.Null);
    Assert.That(CSharpViewApi.Syntax.Select(row => row.Construct), Has.Some.Contains("memory.Span"));
  }

  // ---------------------------------------------------------------- the JavaScript frontend

  private static IrModule Window => JavaScriptFrontend.Import("window.mjs", File.ReadAllText(SpanFixtures.PathOf("window.mjs"))).Module!;

  /// <summary>
  /// Test design 18: a view stored into a field escapes, and so does the view in the variable it was read
  /// from: both are memories; a view that stays in a function is a span.
  /// </summary>
  [Test]
  public void EscapingView_IsAMemory() {
    var module = Window;
    Assert.That(Verified(module), Is.Empty);
    var window = module.Types.OfType<IrClassType>().Single();
    Assert.That(window.Fields.Select(field => field.Type), Is.EqualTo(new[] { _memory, _memory }));
    var constructor = module.Functions.Single(function => function.Name == window.Constructors.Single().Function);
    Assert.That(constructor.Locals.Single(local => local.Name == "part").Type, Is.EqualTo(_memory));
    var loops = module.Functions.Single(function => function.Name == "loops");
    Assert.That(loops.Locals.Select(local => local.Type), Has.Some.EqualTo(_span).And.None.EqualTo(_memory));
  }

  /// <summary>Test design 18 in C#: for net10.0 the field is a <c>Memory&lt;byte&gt;</c>, for net4.8 an <c>ArraySegment&lt;byte&gt;</c>.</summary>
  [TestCase("net10.0", "public global::System.Memory<byte> bytes;")]
  [TestCase("net4.8", "public global::System.ArraySegment<byte> bytes = new global::System.ArraySegment<byte>(new byte[0]);")]
  public void EscapingView_IsDeclaredAsTheMemoryOfTheProfile(string name, string declaration)
    => Assert.That(CSharpEmitter.Emit(Window, CSharpProfile.Parse(name)).Single().Content, Does.Contain(declaration));

  /// <summary>The values node computes of the original: the window sums to 22, the clamped narrowings, the loops.</summary>
  [Test]
  public void Window_InterpretsAsNodeComputes()
    => Assert.That(SpanFixtures.WindowCalls.Select(call => PrototypeFixture.InterpretedOutcome(Window, call).Split(" | ")[0]), Is.EqualTo(new[] { "22", "29", "193080", "2011", "0", "3018", "57" }));

  /// <summary>The C# of window.mjs computes in every profile what the interpreter computes of it.</summary>
  [TestCaseSource(nameof(CSharpProfiles)), Category("Slow")]
  public void Window_ComputesInEveryCSharpProfile(string name) {
    var profile = CSharpProfile.Parse(name);
    var module = Window;
    var text = CSharpEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    foreach (var call in SpanFixtures.WindowCalls)
      Assert.That(SpanFixtures.Outcome(build.Method(call.Method), call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)), $"{call} in {name}");
  }

  private static string RefusedScript(string script) {
    var result = JavaScriptFrontend.Import("snippet.mjs", script);
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  /// <summary>A view kept in a field that is made of anything but a typed array where it is stored is refused; so is a for…of over a plain Array.</summary>
  [TestCase("export class W { /** @param {uint8[]} a @param {boolean} c */ constructor(a, c) { this.v = c ? a.subarray(1) : a.subarray(2); } }",
    "kept in a field, which is a view not made of a typed array here")]
  [TestCase("/** @returns {number} */ export function f() { const a = [1, 2]; let t = 0; for (const x of a) { t = t + x; } return t; }", "a for...of over")]
  public void Construct_IsRefused(string script, string named)
    => Assert.That(RefusedScript(script), Does.Contain("PLY1102").And.Contain(named));

  /// <summary>
  /// Every way a view reaches a field: the whole of a typed array, a part of a view in a variable - which
  /// escapes with it -, another field, and a copy out of a field into a typed array with <c>set</c>.
  /// </summary>
  [Test]
  public void EveryOriginOfAKeptView_IsAMemory() {
    const string script = """
      export class H {
        /** @param {uint8[]} d */
        constructor(d) {
          this.a = d.subarray();
          const v = d.subarray(2);
          this.b = v.subarray(1, 3);
          this.c = this.b;
        }
      }

      /**
       * @param {uint8[]} d
       * @returns {int32}
       */
      export function f(d) {
        const h = new H(d);
        const t = new Uint8Array(2);
        t.set(h.c);
        return h.a.length * 1000 + t[0] * 10 + t[1];
      }
      """;
    var module = JavaScriptFrontend.Import("h.mjs", script).Module!;
    Assert.That(Verified(module), Is.Empty);
    Assert.That(module.Types.OfType<IrClassType>().Single().Fields.Select(field => field.Type), Is.All.EqualTo(_memory));
    Assert.That(IrInterpreter.Call(module, "f", [SpanFixtures.D]), Is.EqualTo(16034));
  }

  /// <summary>A field that a view and a number reach holds two kinds, PLY1105, naming the view; views of two element types do not join either.</summary>
  [TestCase("this.v = d.subarray(1); this.v = 5;", "uint8 view")]
  [TestCase("let v = d.subarray(1); v = d; this.v = v;", "uint8[]")]
  [TestCase("let v = d.subarray(1); v = w.subarray(1); this.v = v;", "uint16 view")]
  public void FieldOfTwoKinds_IsRefused(string body, string named) {
    var script = $"export class H {{ /** @param {{uint8[]}} d @param {{uint16[]}} w */ constructor(d, w) {{ {body} }} }}";
    var refused = string.Join("\n", JavaScriptFrontend.Import("h.mjs", script).Diagnostics);
    Assert.That(refused, Does.Contain("PLY1105").And.Contain(named));
  }

  /// <summary>A for…of over a Float32Array yields its elements as the doubles JavaScript reads.</summary>
  [Test]
  public void ForOfOverFloats_YieldsDoubles() {
    var module = JavaScriptFrontend.Import("f.mjs", "/** @param {float32[]} a @returns {number} */ export function f(a) { let t = 0; for (const x of a) { t = t + x; } return t; }").Module!;
    Assert.That(Verified(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "f", [new[] { 0.5f, 0.25f }]), Is.EqualTo(0.75));
  }

}
