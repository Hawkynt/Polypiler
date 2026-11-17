using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Records;

/// <summary>
/// Records in memory in the IR (#110): the layout queries <see cref="IrLayoutOf"/> and the field address
/// <see cref="IrFieldAddress"/> - their verification, the <see cref="RecordLayoutRule"/>, their interpretation
/// with and without a profile, their lowering to pointer arithmetic for a target, and their form in bytes.
/// </summary>
[TestFixture]
public sealed class RecordLayoutIrTests {

  private static readonly IrType _bytes = IrType.Array(IrType.U8);
  private static readonly IrType _bytePointer = IrType.Pointer(IrType.U8);

  private static IrField Field(string name, IrType type) => new(name, type, false);

  /// <summary><c>H { a: u8, b: u32, c: u16 }</c> under a rule.</summary>
  private static IrStructType H(IrStructLayout? rule = null) => new("H", [Field("a", IrType.U8), Field("b", IrType.U32), Field("c", IrType.U16)]) { Layout = rule };

  private static IrStructLayout Packed(int pack) => new(IrLayoutKind.Sequential, pack, 0, []);

  private static IrModule Module(IrStructType record, IrType returns, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] body)
    => new("M", [new IrFunction("F", true, returns, parameters, locals, new IrBlock(body))]) { Types = [record] };

  /// <summary><c>F(bytes)</c>: pins the bytes and returns what the expression made of the pointer reads.</summary>
  private static IrModule Reading(IrStructType record, IrType returns, Func<IrExpr, IrExpr> read) {
    var array = new IrLocal("bytes", _bytes);
    var pointer = new IrLocal("p", _bytePointer);
    return Module(record, returns, [array], [pointer],
      new IrDeclare(pointer, new IrIntrinsic(IrIntrinsicOp.Pin, _bytePointer, [new IrLocalRef(array)])),
      new IrReturn(read(new IrLocalRef(pointer))));
  }

  private static IrExpr Load(IrExpr pointer) => new IrIntrinsic(IrIntrinsicOp.PointerLoad, pointer.Type.Element!, [pointer]);

  private static IReadOnlyList<string> Verified(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static long Number(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>The bytes of an <c>H</c> under the natural layout on lp64: a = 1, b = 0x12345678, c = 515.</summary>
  private static readonly byte[] _natural = [1, 0xEE, 0xEE, 0xEE, 0x78, 0x56, 0x34, 0x12, 0x03, 0x02, 0xEE, 0xEE];

  /// <summary>The same values under packed(1).</summary>
  private static readonly byte[] _packed = [1, 0x78, 0x56, 0x34, 0x12, 0x03, 0x02];

  /// <summary>Test design 4: a field read through its address is the field of the value built from the same numbers.</summary>
  [TestCase(1)]
  [TestCase(0)]
  public void FieldAddress_ReadsWhatExtractFieldReads(int pack) {
    var record = H(pack == 0 ? null : Packed(pack));
    var b = record.Fields[1];
    var c = record.Fields[2];
    var viaMemory = Reading(record, IrType.U32, pointer => Load(new IrFieldAddress(pointer, record, b)));
    var viaValue = Module(record, IrType.U16, [], [],
      new IrReturn(new IrFieldLoad(new IrStructNew(IrType.Struct(record), [new IrConst(IrType.U8, 1), new IrConst(IrType.U32, 0x12345678), new IrConst(IrType.U16, 515)]), c)));
    var cViaMemory = Reading(record, IrType.U16, pointer => Load(new IrFieldAddress(pointer, record, c)));
    Assert.That(Verified(viaMemory), Is.Empty);
    var bytes = pack == 1 ? _packed : _natural;
    var profile = LayoutProfile.Lp64SysV;
    Assert.That(Number(IrInterpreter.Call(viaMemory, "F", [bytes], profile)), Is.EqualTo(0x12345678));
    Assert.That(Number(IrInterpreter.Call(cViaMemory, "F", [bytes], profile)), Is.EqualTo(Number(IrInterpreter.Call(viaValue, "F", []))));
  }

  /// <summary>A packed(1) record is laid out alike under every profile, so the interpreter needs none for it.</summary>
  [Test]
  public void ProfileIndependentLayout_NeedsNoProfile() {
    var record = H(Packed(1));
    var module = Reading(record, IrType.U32, pointer => Load(new IrFieldAddress(pointer, record, record.Fields[1])));
    Assert.That(Number(IrInterpreter.Call(module, "F", [_packed])), Is.EqualTo(0x12345678));
  }

  /// <summary>Test design 13 in the tree IR: a natural layout is not answered before the profile is known.</summary>
  [Test]
  public void ProfileDependentLayout_NeedsAProfile() {
    var record = H();
    var module = Reading(record, IrType.U32, pointer => Load(new IrFieldAddress(pointer, record, record.Fields[1])));
    Assert.That(() => IrInterpreter.Call(module, "F", [_natural]), Throws.InvalidOperationException.With.Message.Contains("depends on the target's layout profile"));
    Assert.That(() => IrInterpreter.Call(module, "F", [_natural], null!), Throws.ArgumentNullException);
  }

  /// <summary>A field of a record within a record, of a truth value and of a stated byte order is addressed as bytes.</summary>
  [Test]
  public void FieldAddress_OfBytes_IsAByteAddress() {
    var inner = H(Packed(1));
    var flag = Field("flag", IrType.Bool);
    var ordered = new IrField("magic", IrType.U32, false) { ByteOrder = IrByteOrder.Big };
    var holding = Field("h", IrType.Struct(inner));
    var outer = new IrStructType("O", [flag, ordered, holding]) { Layout = Packed(1) };
    Assert.That(new[] { flag, ordered, holding }.Select(field => new IrFieldAddress(new IrNull(_bytePointer), outer, field).Type), Is.All.EqualTo(_bytePointer));
    var colour = new IrEnumType("Colour", IrType.U16, [new IrEnumMember("Red", 0)], false);
    Assert.That(IrFieldAddress.AddressType(Field("e", IrType.Enum(colour))), Is.EqualTo(IrType.Pointer(IrType.U16)));

    var array = new IrLocal("bytes", _bytes);
    var pointer = new IrLocal("p", _bytePointer);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.U32, [array], [pointer], new IrBlock([
      new IrDeclare(pointer, new IrIntrinsic(IrIntrinsicOp.Pin, _bytePointer, [new IrLocalRef(array)])),
      new IrReturn(Load(new IrFieldAddress(new IrFieldAddress(new IrLocalRef(pointer), outer, holding), inner, inner.Fields[1])))]))]) { Types = [inner, outer] };
    Assert.That(Verified(module), Is.Empty);
    Assert.That(Number(IrInterpreter.Call(module, "F", [new byte[] { 0, 0, 0, 0, 0, 9, 0x78, 0x56, 0x34, 0x12, 0, 0 }])), Is.EqualTo(0x12345678));
  }

  /// <summary>Size, alignment and offset queries, under each profile and without one where no profile changes them.</summary>
  [TestCase("lp64-sysv", IrLayoutQuery.Size, 12)]
  [TestCase("dos16", IrLayoutQuery.Size, 8)]
  [TestCase("ilp32-sysv", IrLayoutQuery.Alignment, 4)]
  [TestCase("dos16", IrLayoutQuery.Offset, 6)]
  public void LayoutQuery_IsAnsweredUnderTheProfile(string profile, IrLayoutQuery query, long expected) {
    var record = H();
    var module = Module(record, IrType.I64, [], [], new IrReturn(new IrLayoutOf(query, IrType.Struct(record), query == IrLayoutQuery.Offset ? record.Fields[2] : null)));
    Assert.That(Verified(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", [], LayoutProfile.Find(profile)!), Is.EqualTo(expected));
    Assert.That(IrInterpreter.Call(IrLayoutLowering.Lower(module, LayoutProfile.Find(profile)!), "F", []), Is.EqualTo(expected));
    Assert.That(() => IrInterpreter.Call(module, "F", []), Throws.InvalidOperationException);
  }

  /// <summary>The size of a number and the alignment of a byte are the same everywhere and answered without a profile.</summary>
  [Test]
  public void LayoutQuery_OfANumber_NeedsNoProfile() {
    var record = H();
    var module = Module(record, IrType.I64, [], [],
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrLayoutOf(IrLayoutQuery.Size, IrType.F64), new IrLayoutOf(IrLayoutQuery.Alignment, IrType.U8))));
    Assert.That(Verified(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(9L));
  }

  /// <summary>Lowering leaves only pointer arithmetic and constants, which run as the queries and addresses did.</summary>
  [Test]
  public void Lowering_LeavesPointerArithmetic() {
    var record = H();
    var module = Reading(record, IrType.U32, pointer => new IrConvert(IrType.U32, new IrBinary(IrBinaryOp.Add,
      new IrConvert(IrType.I64, Load(new IrFieldAddress(pointer, record, record.Fields[1])), IrConversion.Wrap),
      new IrBinary(IrBinaryOp.Add,
        new IrConvert(IrType.I64, Load(new IrFieldAddress(pointer, record, record.Fields[0])), IrConversion.Wrap),
        new IrLayoutOf(IrLayoutQuery.Size, IrType.Struct(record)))), IrConversion.Wrap));
    Assert.That(Verified(module), Is.Empty);
    var lowered = IrLayoutLowering.Lower(module, LayoutProfile.Lp64SysV);
    Assert.That(Verified(lowered), Is.Empty);
    var nodes = lowered.Functions[0].Body.Statements.SelectMany(IrTree.Nodes).ToList();
    Assert.That(nodes.OfType<IrLayoutOf>().Concat<IrExpr>(nodes.OfType<IrFieldAddress>()), Is.Empty);
    Assert.That(nodes.OfType<IrIntrinsic>().Count(node => node.Op == IrIntrinsicOp.PointerAdd), Is.EqualTo(1), "a field at offset 0 is the base itself");
    Assert.That(nodes.OfType<IrIntrinsic>().Count(node => node.Op == IrIntrinsicOp.PointerCast), Is.EqualTo(1), "a byte field needs no cast");
    var expected = 0x12345678L + 1 + 12;
    Assert.That(Number(IrInterpreter.Call(lowered, "F", [_natural])), Is.EqualTo(expected));
    Assert.That(Number(IrInterpreter.Call(module, "F", [_natural], LayoutProfile.Lp64SysV)), Is.EqualTo(expected));
  }

  /// <summary>A module without layout nodes is returned as it is; lambdas and nested statements are lowered too.</summary>
  [Test]
  public void Lowering_ReachesEveryStatementAndLambda() {
    var record = H();
    var plain = Module(record, IrType.I64, [], [], new IrReturn(new IrConst(IrType.I64, 1)));
    Assert.That(IrLayoutLowering.Lower(plain, LayoutProfile.Dos16), Is.SameAs(plain));
    Assert.That(() => IrLayoutLowering.Lower(null!, LayoutProfile.Dos16), Throws.ArgumentNullException);
    Assert.That(() => IrLayoutLowering.Lower(plain, null!), Throws.ArgumentNullException);

    var size = new IrLayoutOf(IrLayoutQuery.Size, IrType.Struct(record));
    var function = IrType.Function(IrType.I64);
    var lambda = new IrLambda(function, [], [], [], new IrBlock([new IrReturn(size)]));
    var x = new IrLocal("x", IrType.I64);
    var f = new IrLocal("f", function);
    var module = Module(record, IrType.I64, [], [x, f],
      new IrDeclare(x, size),
      new IrDeclare(f, lambda),
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(x), size), new IrBlock([new IrAssign(x, new IrBinary(IrBinaryOp.Add, new IrLocalRef(x), new IrInvoke(IrType.I64, new IrLocalRef(f), [])))]), null),
      new IrReturn(new IrLocalRef(x)));
    Assert.That(Verified(module), Is.Empty);
    var lowered = IrLayoutLowering.Lower(module, LayoutProfile.Dos16);
    var nodes = lowered.Functions[0].Body.Statements.SelectMany(IrTree.Nodes).ToList();
    Assert.That(nodes.OfType<IrLayoutOf>().Concat(nodes.OfType<IrLambda>().SelectMany(inner => inner.Body.Statements.SelectMany(IrTree.Nodes))).OfType<IrLayoutOf>(), Is.Empty);
    Assert.That(IrInterpreter.Call(lowered, "F", []), Is.EqualTo(16L));
  }

  /// <summary>Every statement form is rebuilt by the lowering with its parts lowered.</summary>
  [Test]
  public void Map_RebuildsEveryStatement() {
    var size = new IrLayoutOf(IrLayoutQuery.Size, IrType.U32);
    var x = new IrLocal("x", IrType.I64);
    var block = new IrBlock([new IrExprStmt(size)]);
    IrStmt[] statements = [
      block, new IrDeclare(x, null), new IrDeclare(x, size), new IrAssign(x, size), new IrExprStmt(size), new IrIf(size, block, null), new IrIf(size, block, block),
      new IrWhile(size, block), new IrDoWhile(block, size), new IrFor(size, block, block), new IrReturn(null), new IrReturn(size), new IrBreak()
    ];
    foreach (var statement in statements) {
      var mapped = IrLayoutLowering.Map(statement, _ => new IrConst(IrType.I64, 4), inner => new IrBlock([]));
      Assert.That(IrTree.Expressions(mapped).OfType<IrLayoutOf>(), Is.Empty, statement.GetType().Name);
    }
  }

  /// <summary>Test design 10: what the verifier refuses of a layout query and a field address.</summary>
  [Test]
  public void Verifier_RefusesMalformedNodes() {
    var record = H();
    var stranger = Field("z", IrType.U8);
    IReadOnlyList<string> Problems(IrExpr value) => Verified(Module(record, IrType.Void, [], [], new IrExprStmt(value)));

    Assert.That(Problems(new IrLayoutOf(IrLayoutQuery.Offset, IrType.Struct(record), stranger)), Has.Some.Contains("the offset of 'z', which is not a field of H"));
    Assert.That(Problems(new IrLayoutOf(IrLayoutQuery.Offset, IrType.Struct(record))), Has.Some.Contains("the offset of '', which is not a field of H"));
    Assert.That(Problems(new IrLayoutOf(IrLayoutQuery.Offset, IrType.U32, stranger)), Has.Some.Contains("a struct is expected"));
    Assert.That(Problems(new IrLayoutOf(IrLayoutQuery.Size, IrType.Struct(record), record.Fields[0])), Has.Some.Contains("names the field 'a'"));
    Assert.That(Problems(new IrLayoutOf(IrLayoutQuery.Size, IrType.String)), Has.Some.Contains("the Size of the type str, which has no byte layout"));
    Assert.That(Problems(new IrLayoutOf(IrLayoutQuery.Alignment, IrType.Struct(H()))), Has.Some.Contains("not among the types of the module"));
    Assert.That(Problems(new IrLayoutOf(IrLayoutQuery.Size, _bytePointer)), Is.Empty);
    Assert.That(Problems(new IrLayoutOf(IrLayoutQuery.Offset, _bytePointer)), Has.Some.Contains("which has no byte layout"));

    var pointer = new IrNull(_bytePointer);
    Assert.That(Problems(new IrFieldAddress(pointer, record, stranger)), Has.Some.Contains("the field 'z' is not a field of H"));
    Assert.That(Problems(new IrFieldAddress(new IrNull(IrType.Pointer(IrType.U32)), record, record.Fields[1])), Has.Some.Contains("taken from a u32*, not a u8*"));
    Assert.That(Problems(new IrFieldAddress(pointer, record, record.Fields[1]) with { Type = _bytePointer }), Has.Some.Contains("expected u32*"));
    Assert.That(Problems(new IrFieldAddress(pointer, H(), record.Fields[1])), Has.Some.Contains("not among the types of the module"));
    Assert.That(Problems(new IrFieldAddress(pointer, record, record.Fields[1])), Is.Empty);
  }

  /// <summary>
  /// Scenario "Overlapping explicit fields are rejected" and test design 11: the registered rule reports the
  /// layout problems of every record a function lays out, once per record, and none of one it does not lay out.
  /// </summary>
  [Test]
  public void Rule_ReportsLayoutProblems() {
    var overlapping = new IrStructType("X", [Field("a", IrType.U32), Field("b", IrType.U16)]) { Layout = new IrStructLayout(IrLayoutKind.Explicit, 0, 0, [0, 2]) };
    var pointer = new IrNull(_bytePointer);
    var module = Module(overlapping, IrType.Void, [], [],
      new IrExprStmt(new IrLayoutOf(IrLayoutQuery.Size, IrType.Struct(overlapping))),
      new IrExprStmt(new IrFieldAddress(pointer, overlapping, overlapping.Fields[1])));
    var problems = Verified(module).Where(message => message.Contains("ir.verifier.records.layout", StringComparison.Ordinal)).ToList();
    Assert.That(problems, Has.Count.EqualTo(1));
    Assert.That(problems[0], Does.Contain("layout of 'X': the fields 'a' and 'b' overlap"));

    var unused = Module(overlapping, IrType.Void, [], [], new IrExprStmt(new IrLayoutOf(IrLayoutQuery.Size, IrType.U8)));
    Assert.That(Verified(unused), Is.Empty);

    var rule = new RecordLayoutRule([LayoutProfile.Dos16]);
    Assert.That((rule.Id, rule.Family, rule.Description), Is.EqualTo(("ir.verifier.records.layout", "records", "a record whose layout a function asks for, or whose field it addresses, has a layout under every profile")));
    Assert.That(VerifierRuleRegistry.Standard.Find(rule.Id), Is.InstanceOf<RecordLayoutRule>());
    Assert.That(() => new RecordLayoutRule([]), Throws.ArgumentException);
    Assert.That(() => new RecordLayoutRule(null!), Throws.ArgumentNullException);
    Assert.That(() => rule.Check(null!), Throws.ArgumentNullException);

    // A record that has its fields only after the function was built is reported once it is checked.
    var undefined = new IrStructType("U");
    var later = Module(undefined, IrType.Void, [], [], new IrExprStmt(new IrLayoutOf(IrLayoutQuery.Size, IrType.Struct(undefined))));
    Assert.That(IrVerifier.Verify(later, new VerifierRuleRegistry().Register(rule)).Select(diagnostic => diagnostic.Message), Has.None.Contains("ir.verifier.records.layout"));
  }

  /// <summary>Test design 5: the flag, a field's byte order and both nodes come back from the form in bytes.</summary>
  [Test]
  public void Format_RoundTripsLayouts() {
    var magic = new IrField("magic", IrType.U32, false) { ByteOrder = IrByteOrder.Big };
    var size = new IrField("size", IrType.U16, false) { ByteOrder = IrByteOrder.Little };
    var plain = Field("plain", IrType.U8);
    var record = new IrStructType("Hdr", [magic, size, plain]) { Layout = new IrStructLayout(IrLayoutKind.Sequential, 1, 0, []), LayoutObservable = true };
    var module = Reading(record, IrType.I64, pointer => new IrBinary(IrBinaryOp.Add,
      new IrLayoutOf(IrLayoutQuery.Offset, IrType.Struct(record), size),
      new IrConvert(IrType.I64, Load(new IrFieldAddress(pointer, record, plain)), IrConversion.Wrap)));
    Assert.That(Verified(module), Is.Empty);
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    var back = (IrStructType)read.Types[0];
    Assert.That((back.LayoutObservable, back.Fields[0].ByteOrder, back.Fields[1].ByteOrder, back.Fields[2].ByteOrder), Is.EqualTo((true, IrByteOrder.Big, IrByteOrder.Little, (IrByteOrder?)null)));
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(IrModuleFormat.Write(module)));
    Assert.That(Number(IrInterpreter.Call(read, "F", [new byte[] { 0, 0, 0, 0, 0, 0, 7 }])), Is.EqualTo(4 + 7));
    var unobserved = new IrStructType("Hdr", [Field("a", IrType.U8)]);
    var plainModule = Module(unobserved, IrType.Void, [], []);
    Assert.That(((IrStructType)IrModuleFormat.Read(IrModuleFormat.Write(plainModule)).Types[0]).LayoutObservable, Is.False);
  }

}
