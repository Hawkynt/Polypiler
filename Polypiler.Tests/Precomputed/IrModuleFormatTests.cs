using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Precomputed.Support;

namespace Polypiler.Tests.Precomputed;

/// <summary>
/// <see cref="IrModuleFormat"/> keeps a module exactly - every kind of node, type and declaration, and
/// which nodes are one and the same object - and refuses bytes that are no intact module of its version.
/// </summary>
[TestFixture]
public sealed class IrModuleFormatTests {

  private static readonly IrType _i32 = IrType.I32;

  /// <summary>A module that has every kind of node, type and declaration, and nodes used in several places.</summary>
  private static IrModule KitchenSink() {
    var x = new IrField("X", _i32, false);
    var y = new IrField("Y", IrType.F64, true);
    var point = new IrStructType("Point", [x, y]) { IsEquatable = true };
    var opaque = new IrStructType("Opaque");
    var pointType = IrType.Struct(point);
    var table = new IrModuleConst("Table", IrType.Array(IrType.U8), [1, 2, 255]);
    var limit = new IrModuleConst("Limit", IrType.I64, [long.MinValue]);
    var array = new IrLocal("a", IrType.Array(_i32));
    var index = new IrLocal("i", _i32);
    var value = new IrLocal("p", pointType);
    var number = new IrLocal("f", IrType.F64);
    var shared = new IrBinary(IrBinaryOp.Add, new IrLocalRef(index), new IrConst(_i32, 1));
    var condition = new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), new IrConst(_i32, 10));
    var leave = new IrBlock([new IrBreak()]);
    var body = new IrBlock([
      new IrDeclare(number, null),
      new IrDeclare(value, new IrStructNew(pointType, [new IrConst(_i32, 3), new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(1.5))])),
      new IrAssign(index, new IrUnary(IrUnaryOp.Negate, shared)),
      new IrStore(new IrLocalRef(array), new IrConst(_i32, 0), shared),
      new IrFieldStore(new IrLocalRef(value), x, new IrFieldLoad(new IrLocalRef(value), x)),
      new IrExprStmt(new IrStep(new IrLocalRef(index), true, false, true)),
      new IrExprStmt(new IrCall("Other", IrType.Void, [new IrConstRef(table), new IrConstRef(limit)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [new IrLocalRef(array), new IrConst(_i32, 7)])),
      new IrExprStmt(new IrBinary(IrBinaryOp.Add, new IrConst(_i32, 1), new IrConst(_i32, 2)) with { Type = IrType.I64 }),
      new IrIf(new IrStructEquals(new IrLocalRef(value), new IrStructDefault(pointType)), leave, null),
      new IrIf(new IrConditional(condition, new IrConst(IrType.Bool, 1), new IrConst(IrType.Bool, 0)), new IrBlock([]), leave),
      new IrWhile(condition, new IrBlock([new IrContinue()])),
      new IrDoWhile(new IrBlock([new IrAssign(index, shared)]), condition),
      new IrFor(condition, new IrBlock([]), new IrBlock([new IrExprStmt(new IrStep(new IrLocalRef(index), false, true, false))])),
      new IrSwitch(new IrLocalRef(index), [new IrSwitchSection([1, 2], false, leave), new IrSwitchSection([], true, new IrBlock([]))]),
      new IrAssign(number, new IrConvert(IrType.F64, new IrArrayLength(new IrNewArray(IrType.Array(IrType.F32), new IrConst(_i32, 4))))),
      new IrAssign(index, new IrConvert(_i32, new IrLocalRef(number), IrConversion.Saturate)),
      new IrExprStmt(new IrArrayLoad(new IrLocalRef(array), new IrConst(_i32, 0))),
      new IrReturn(null)
    ]);
    var other = new IrFunction("Other", false, _i32, [], [], new IrBlock([new IrReturn(new IrConst(_i32, -1))]));
    return new IrModule("Sink", [new IrFunction("Main", true, IrType.Void, [array, index], [value, number], body), other], [table, limit]) { Types = [point, opaque] };
  }

  [Test]
  public void EveryKindOfNode_ReadsBackAsTheSameGraph() {
    var module = KitchenSink();
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(IrGraph.Difference(module, read), Is.Null);
  }

  [Test]
  public void SharedNodes_StayShared() {
    var read = IrModuleFormat.Read(IrModuleFormat.Write(KitchenSink()));
    var main = read.Functions[0];
    var assign = (IrAssign)main.Body.Statements[2];
    var store = (IrStore)main.Body.Statements[3];
    Assert.That(((IrUnary)assign.Value).Operand, Is.SameAs(store.Value));
    Assert.That(((IrLocalRef)store.Array).Local, Is.SameAs(main.Parameters[0]));
    Assert.That(((IrConstRef)((IrCall)((IrExprStmt)main.Body.Statements[6]).Expression).Arguments[0]).Constant, Is.SameAs(read.Constants[0]));
    var fieldStore = (IrFieldStore)main.Body.Statements[4];
    Assert.That(fieldStore.Field, Is.SameAs(((IrStructType)read.Types[0]).Fields[0]));
  }

  [Test]
  public void AChangedType_IsKept() {
    var read = IrModuleFormat.Read(IrModuleFormat.Write(KitchenSink()));
    Assert.That(((IrExprStmt)read.Functions[0].Body.Statements[8]).Expression.Type, Is.EqualTo(IrType.I64));
  }

  [Test]
  public void AnEmptyModule_ReadsBack() {
    var module = new IrModule("Empty", []);
    Assert.That(IrGraph.Difference(module, IrModuleFormat.Read(IrModuleFormat.Write(module))), Is.Null);
  }

  [Test]
  public void Writing_IsDeterministic() {
    var first = IrModuleFormat.Write(KitchenSink());
    Assert.That(IrModuleFormat.Write(KitchenSink()), Is.EqualTo(first));
  }

  // --- what Write refuses ---------------------------------------------------------------------------

  private sealed class Alias() : IrTypeDeclaration("Alias");

  private sealed record Foreign() : IrExpr(IrType.I32);

  private sealed record ForeignStatement : IrStmt;

  private static IrModule With(IrStmt statement, params IrLocal[] locals)
    => new("M", [new IrFunction("F", true, IrType.Void, [], locals, new IrBlock([statement]))]);

  [Test]
  public void ADeclarationThatIsNoStruct_IsRefused()
    => Assert.That(() => IrModuleFormat.Write(new IrModule("M", []) { Types = [new Alias()] }), Throws.ArgumentException.With.Message.Contains("'Alias' is no struct"));

  [Test]
  public void AStructThatIsNotTheModules_IsRefused() {
    var local = new IrLocal("s", IrType.Struct(new IrStructType("Stray", [])));
    Assert.That(() => IrModuleFormat.Write(With(new IrDeclare(local, null), local)), Throws.ArgumentException.With.Message.Contains("'Stray' is not one of the module's types"));
  }

  [Test]
  public void AFieldThatIsNoStructsOfTheModule_IsRefused() {
    var point = new IrStructType("Point", [new IrField("X", _i32, false)]);
    var local = new IrLocal("p", IrType.Struct(point));
    var stray = new IrField("X", _i32, false);
    var module = With(new IrExprStmt(new IrFieldLoad(new IrLocalRef(local), stray)), local) with { Types = [point] };
    Assert.That(() => IrModuleFormat.Write(module), Throws.ArgumentException.With.Message.Contains("field 'X'"));
  }

  [Test]
  public void AConstantThatIsNotTheModules_IsRefused() {
    var stray = new IrModuleConst("Stray", _i32, [1]);
    Assert.That(() => IrModuleFormat.Write(With(new IrExprStmt(new IrConstRef(stray)))), Throws.ArgumentException.With.Message.Contains("'Stray'"));
  }

  [Test]
  public void AnExpressionOfAnotherKind_IsRefused()
    => Assert.That(() => IrModuleFormat.Write(With(new IrExprStmt(new Foreign()))), Throws.ArgumentException.With.Message.Contains("Foreign"));

  [Test]
  public void AStatementOfAnotherKind_IsRefused()
    => Assert.That(() => IrModuleFormat.Write(With(new ForeignStatement())), Throws.ArgumentException.With.Message.Contains("ForeignStatement"));

  // --- what Read refuses ----------------------------------------------------------------------------

  private const int HeaderSize = 8 + 4 + 32 + 4;

  private static byte[] Valid() => IrModuleFormat.Write(KitchenSink());

  [TestCase(0, TestName = "No bytes are no module")]
  [TestCase(HeaderSize - 1, TestName = "One byte short of a header is no module")]
  public void TooFewBytes_AreRefused(int length)
    => Assert.That(() => IrModuleFormat.Read(Valid()[..length]), Throws.TypeOf<InvalidDataException>().With.Message.Contains("no IR module"));

  [Test]
  public void AnotherMagic_IsRefused() {
    var bytes = Valid();
    bytes[0] ^= 1;
    Assert.That(() => IrModuleFormat.Read(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains("no IR module"));
  }

  [Test]
  public void AnotherVersion_IsRefused() {
    var bytes = Valid();
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), IrModuleFormat.Version + 1);
    Assert.That(() => IrModuleFormat.Read(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains($"version {IrModuleFormat.Version + 1}"));
  }

  [TestCase(HeaderSize, TestName = "The first byte of the content flipped")]
  [TestCase(-1, TestName = "The last byte of the content flipped")]
  [TestCase(20, TestName = "A byte of the checksum flipped")]
  public void AFlippedByte_IsRefused(int position) {
    var bytes = Valid();
    bytes[position < 0 ? bytes.Length + position : position] ^= 0x40;
    Assert.That(() => IrModuleFormat.Read(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains("damaged"));
  }

  [TestCase(-1, TestName = "A content one byte shorter than its length says")]
  [TestCase(1, TestName = "A content one byte longer than its length says")]
  public void AContentOfAnotherLength_IsRefused(int change) {
    var bytes = Valid();
    var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(HeaderSize - 4));
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HeaderSize - 4), length - change);
    Assert.That(() => IrModuleFormat.Read(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains("damaged"));
  }

  /// <summary>Bytes with a valid header around a content written by hand, as a damaged build could leave them.</summary>
  private static byte[] Sealed(Action<BinaryWriter> content) {
    using var payload = new MemoryStream();
    using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
      content(writer);

    var bytes = payload.ToArray();
    using var result = new MemoryStream();
    using (var writer = new BinaryWriter(result)) {
      writer.Write("PLYIRMOD"u8);
      writer.Write(IrModuleFormat.Version);
      writer.Write(SHA256.HashData(bytes));
      writer.Write(bytes.Length);
      writer.Write(bytes);
    }

    return result.ToArray();
  }

  // The numbers of the layout, as IrModuleFormat writes them.
  private const byte Null = 0, Shared = 1, Const = 2, LocalRef = 4, Unary = 5, Block = 19, ExprStmt = 24, Return = 32;

  private const byte I32 = (byte)IrTypeKind.I32;

  /// <summary>A module of one function <c>void F()</c> with no constants or types, whose body the action writes.</summary>
  private static byte[] Function(Action<BinaryWriter> body, Action<BinaryWriter>? locals = null) => Sealed(writer => {
    writer.Write("M");
    writer.Write7BitEncodedInt(0);
    writer.Write7BitEncodedInt(0);
    writer.Write7BitEncodedInt(1);
    writer.Write("F");
    writer.Write(true);
    writer.Write((byte)IrTypeKind.Void);
    writer.Write7BitEncodedInt(0);
    if (locals is null)
      writer.Write7BitEncodedInt(0);
    else
      locals(writer);
    body(writer);
  });

  /// <summary>A body of one expression statement whose expression the action writes.</summary>
  private static byte[] Expression(Action<BinaryWriter> expression) => Function(writer => {
    writer.Write(Block);
    writer.Write7BitEncodedInt(1);
    writer.Write(ExprStmt);
    expression(writer);
  });

  private static void Constant(BinaryWriter writer) {
    writer.Write(Const);
    writer.Write(I32);
    writer.Write(7L);
  }

  [Test]
  public void AHandWrittenModule_Reads() {
    var module = IrModuleFormat.Read(Expression(Constant));
    Assert.That(((IrExprStmt)module.Functions[0].Body.Statements[0]).Expression, Is.EqualTo(new IrConst(_i32, 7)));
  }

  private static object[] Malformed() => [
    new TestCaseData(Expression(writer => writer.Write((byte)(Return + 1))), "is no kind of node").SetName("A node of no kind"),
    new TestCaseData(Expression(writer => { writer.Write(Const); writer.Write((byte)99); }), "is no kind of type").SetName("A type of no kind"),
    new TestCaseData(Expression(writer => { writer.Write(Const); writer.Write((byte)IrTypeKind.Struct); writer.Write7BitEncodedInt(0); }), "struct 0 does not exist").SetName("A struct that does not exist"),
    new TestCaseData(Expression(writer => { writer.Write(Unary); writer.Write(I32); writer.Write7BitEncodedInt(99); Constant(writer); }), "99 is no IrUnaryOp").SetName("An operator that does not exist"),
    new TestCaseData(Expression(writer => writer.Write(Null)), "IrExpr is missing").SetName("A missing expression"),
    new TestCaseData(Expression(writer => { writer.Write(Shared); writer.Write7BitEncodedInt(99); }), "node 99 does not exist").SetName("A shared node that does not exist"),
    new TestCaseData(Expression(writer => { writer.Write(Shared); writer.Write7BitEncodedInt(0); }), "node 0 is no IrExpr").SetName("A shared node of another kind"),
    new TestCaseData(Expression(writer => { writer.Write(Unary); writer.Write(I32); writer.Write7BitEncodedInt(0); writer.Write(Shared); writer.Write7BitEncodedInt(2); }), "node 2 is no IrExpr").SetName("A node that contains itself"),
    new TestCaseData(Expression(writer => { writer.Write(Block); writer.Write7BitEncodedInt(0); }), "IrBlock stands where a IrExpr belongs").SetName("A statement where an expression belongs"),
    new TestCaseData(Expression(writer => { writer.Write(LocalRef); writer.Write(I32); writer.Write7BitEncodedInt(5); }), "local 5 does not exist").SetName("A local that does not exist"),
    new TestCaseData(Function(writer => writer.Write(Block), writer => writer.Write([0xFF, 0xFF, 0xFF, 0xFF, 0x0F])), "count is negative").SetName("A negative count"),
    new TestCaseData(Function(writer => { writer.Write(Block); writer.Write7BitEncodedInt(0); writer.Write((byte)0); }), "bytes after its end").SetName("Bytes after the end"),
    new TestCaseData(Function(writer => writer.Write(Block)), "malformed").SetName("Bytes that end too early"),
    new TestCaseData(Function(writer => writer.Write([Block, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])), "malformed").SetName("A count of too many bytes"),
    new TestCaseData(Sealed(writer => {
      writer.Write("M");
      writer.Write7BitEncodedInt(1);
      writer.Write("Opaque");
      writer.Write(false);
      writer.Write(false);
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(1);
      writer.Write("F");
      writer.Write(true);
      writer.Write((byte)IrTypeKind.Void);
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(0);
      writer.Write(Block);
      writer.Write7BitEncodedInt(1);
      writer.Write(ExprStmt);
      writer.Write((byte)16);
      writer.Write(I32);
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(0);
    }), "malformed").SetName("A field of a struct whose fields are not defined"),
  ];

  [TestCaseSource(nameof(Malformed))]
  public void MalformedContent_IsRefused(byte[] bytes, string message)
    => Assert.That(() => IrModuleFormat.Read(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains(message));

  [Test]
  public void TheFieldsOfAStruct_ReadBackInOrder() {
    var read = IrModuleFormat.Read(IrModuleFormat.Write(KitchenSink()));
    var point = (IrStructType)read.Types[0];
    Assert.That(point.Fields.Select(field => field.Name), Is.EqualTo(new[] { "X", "Y" }));
    Assert.That(point.IsEquatable, Is.True);
    Assert.That(read.Types[1].IsDefined, Is.False);
  }

  [Test]
  public void EveryLocal_IsReadOnce() {
    var read = IrModuleFormat.Read(IrModuleFormat.Write(KitchenSink()));
    var main = read.Functions[0];
    var declared = (IrDeclare)main.Body.Statements[0];
    Assert.That(declared.Local, Is.SameAs(main.Locals[1]));
  }

}
