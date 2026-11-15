using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>An exception class the module declares beside the library's, with the constructor that takes its message.</summary>
/// <param name="Type">The class.</param>
/// <param name="Constructor">The constructor of one <c>str</c>, the message.</param>
internal sealed record DeclaredException(IrClassType Type, IrConstructor Constructor) {

  /// <summary>The raise of an exception of the class with a message.</summary>
  public IrThrow Raise(IrExpr message) => new(new IrNew(IrType.Object(this.Type), this.Constructor, [message]));

}

/// <summary>
/// The operations of bytes, characters, <c>struct</c> and <c>math</c> that no single IR operation
/// computes: their values and the exceptions CPython raises for them, written from the Library
/// Reference (<c>struct</c>, <c>int.from_bytes</c>, <c>int.to_bytes</c>, <c>str.encode</c>,
/// <c>math</c>) and RFC 3629 for UTF-8.
/// </summary>
internal sealed partial class PythonHelpers {

  private static readonly IrType _bytes = IrType.Array(IrType.U8);

  /// <summary><c>bytes(n)</c>: <c>n</c> zero bytes; <c>ValueError</c> for a negative count.</summary>
  public IrCall ZeroBytes(IrExpr count)
    => this.Call("PyZeroBytes", _bytes, [count], name => {
      var n = Local("count", IrType.I64);
      return Function(name, _bytes, [n], [],
        If(Less(Ref(n), Int(0)), Raise(IrExceptionClass.ArgumentException, "negative count")),
        If(Binary(IrBinaryOp.Greater, Ref(n), Int(int.MaxValue)), Raise(IrExceptionClass.OverflowException, "cannot fit 'int' into an index-sized integer")),
        Return(new IrNewArray(_bytes, Convert(IrType.I32, Ref(n)))));
    });

  /// <summary><c>bytes(b)</c> and <c>bytearray(b)</c> of bytes: a copy.</summary>
  public IrCall BytesCopy(IrExpr bytes)
    => this.Call("PyBytesCopy", _bytes, [bytes], name => {
      var (b, result, i) = (Local("bytes", _bytes), Local("result", _bytes), Local("i", IrType.I32));
      return Function(name, _bytes, [b], [result, i],
        new IrDeclare(result, new IrNewArray(_bytes, new IrArrayLength(Ref(b)))),
        new IrDeclare(i, Int32(0)),
        Counted(i, new IrArrayLength(Ref(b)), new IrStore(Ref(result), Ref(i), new IrArrayLoad(Ref(b), Ref(i)))),
        Return(Ref(result)));
    });

  /// <summary><c>bytes(xs)</c> of integers: <c>ValueError</c> for one outside 0 to 255.</summary>
  public IrCall BytesOf(IrExpr values)
    => this.Call($"PyBytesOf_{Name(values.Type)}", _bytes, [values], name => {
      var (s, list, result, item, i) = (Local("values", values.Type), Local("list", IrType.List(IrType.I64)), Local("result", _bytes), Local("item", IrType.I64), Local("i", IrType.I32));
      return Function(name, _bytes, [s], [list, result, item, i],
        new IrDeclare(list, NewList(IrType.List(IrType.I64))),
        new IrForEachSequence(item, Ref(s), Block(
          If(Or(Less(Ref(item), Int(0)), Binary(IrBinaryOp.Greater, Ref(item), Int(255))), Raise(IrExceptionClass.ArgumentException, "bytes must be in range(0, 256)")),
          Add(Ref(list), Ref(item)))),
        new IrDeclare(result, new IrNewArray(_bytes, Count(Ref(list)))),
        new IrDeclare(i, Int32(0)),
        Counted(i, Count(Ref(list)), new IrStore(Ref(result), Ref(i), Convert(IrType.U8, Get(Ref(list), Ref(i), IrType.I64)))),
        Return(Ref(result)));
    });

  /// <summary>
  /// <c>s.encode()</c>: the UTF-8 bytes of the code points (RFC 3629); a lone surrogate raises
  /// <c>UnicodeEncodeError</c>, which UTF-8 has no bytes for.
  /// </summary>
  public IrCall Encode(IrExpr text, DeclaredException error)
    => this.Call("PyEncodeUtf8", _bytes, [text], name => {
      var (s, at, point, size, result, k) = (Local("text", IrType.String), Local("at", IrType.I32), Local("point", IrType.I32), Local("size", IrType.I32), Local("result", _bytes), Local("k", IrType.I32));
      IrExpr Length() => Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, Ref(s));
      IrStmt Next() => Assign(at, Plus(Ref(at), Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(s), Ref(at))));
      IrStmt Put(IrExpr value) => Block(new IrStore(Ref(result), Ref(k), Convert(IrType.U8, value)), Assign(k, Plus(Ref(k), Int32(1))));
      IrExpr Bits(int shift, int mask, int marker) => Binary(IrBinaryOp.Or, Binary(IrBinaryOp.And, Binary(IrBinaryOp.ShiftRight, Ref(point), Int32(shift)), Int32(mask)), Int32(marker));
      return Function(name, _bytes, [s], [at, point, size, result, k],
        new IrDeclare(at, Int32(0)),
        new IrDeclare(point, Int32(0)),
        new IrDeclare(size, Int32(0)),
        new IrWhile(Less(Ref(at), Length()), Block(
          Assign(point, Intrinsic(IrIntrinsicOp.StrCodePointAt, IrType.I32, Ref(s), Ref(at))),
          If(And(Binary(IrBinaryOp.GreaterOrEqual, Ref(point), Int32(0xD800)), Binary(IrBinaryOp.LessOrEqual, Ref(point), Int32(0xDFFF))),
            error.Raise(new IrStrConst("'utf-8' codec can't encode character: surrogates not allowed"))),
          Assign(size, Plus(Ref(size), Choose(Less(Ref(point), Int32(0x80)), Int32(1), Choose(Less(Ref(point), Int32(0x800)), Int32(2), Choose(Less(Ref(point), Int32(0x10000)), Int32(3), Int32(4)))))),
          Next())),
        new IrDeclare(result, new IrNewArray(_bytes, Ref(size))),
        new IrDeclare(k, Int32(0)),
        Assign(at, Int32(0)),
        new IrWhile(Less(Ref(at), Length()), Block(
          Assign(point, Intrinsic(IrIntrinsicOp.StrCodePointAt, IrType.I32, Ref(s), Ref(at))),
          new IrIf(Less(Ref(point), Int32(0x80)), Block(Put(Ref(point))),
            Block(new IrIf(Less(Ref(point), Int32(0x800)), Block(Put(Bits(6, 0x1F, 0xC0)), Put(Bits(0, 0x3F, 0x80))),
              Block(new IrIf(Less(Ref(point), Int32(0x10000)), Block(Put(Bits(12, 0x0F, 0xE0)), Put(Bits(6, 0x3F, 0x80)), Put(Bits(0, 0x3F, 0x80))),
                Block(Put(Bits(18, 0x07, 0xF0)), Put(Bits(12, 0x3F, 0x80)), Put(Bits(6, 0x3F, 0x80)), Put(Bits(0, 0x3F, 0x80)))))))),
          Next())),
        Return(Ref(result)));
    });

  /// <summary><c>ord(c)</c>: the code point of a str of one; <c>TypeError</c> for any other length.</summary>
  public IrCall Ord(IrExpr text)
    => this.Call("PyOrd", IrType.I64, [text], name => {
      var s = Local("text", IrType.String);
      return Function(name, IrType.I64, [s], [],
        If(Binary(IrBinaryOp.NotEqual, this.CodePointCount(Ref(s)), Int(1)), Raise(IrExceptionClass.InvalidCastException, "ord() expected a character")),
        Return(Convert(IrType.I64, Intrinsic(IrIntrinsicOp.StrCodePointAt, IrType.I32, Ref(s), Int32(0)))));
    });

  /// <summary><c>chr(n)</c>: the str of a code point, a pair of surrogates beyond U+FFFF; <c>ValueError</c> outside 0 to 0x10FFFF.</summary>
  public IrCall Chr(IrExpr point)
    => this.Call("PyChr", IrType.String, [point], name => {
      var (n, offset) = (Local("point", IrType.I64), Local("offset", IrType.I64));
      IrExpr Unit(IrExpr value) => Intrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, Convert(IrType.U16, value));
      return Function(name, IrType.String, [n], [offset],
        If(Or(Less(Ref(n), Int(0)), Binary(IrBinaryOp.Greater, Ref(n), Int(0x10FFFF))), Raise(IrExceptionClass.ArgumentException, "chr() arg not in range(0x110000)")),
        If(Less(Ref(n), Int(0x10000)), Return(Unit(Ref(n)))),
        new IrDeclare(offset, Binary(IrBinaryOp.Subtract, Ref(n), Int(0x10000))),
        Return(Intrinsic(IrIntrinsicOp.StrConcat, IrType.String,
          Unit(Plus(Int(0xD800), Binary(IrBinaryOp.ShiftRight, Ref(offset), Int32(10)))),
          Unit(Plus(Int(0xDC00), Binary(IrBinaryOp.And, Ref(offset), Int(0x3FF)))))));
    });

  /// <summary>
  /// <c>int.from_bytes(b, order, signed=…)</c>: the integer the bytes make; bytes that repeat its sign
  /// beyond 64 bits are dropped, and a value that needs more than 64 bits raises <c>OverflowError</c>.
  /// </summary>
  public IrCall FromBytes(IrExpr bytes, bool little, bool signed)
    => this.Call($"PyFromBytes{(little ? "Little" : "Big")}{(signed ? "Signed" : "")}", IrType.I64, [bytes], name => {
      var (b, n, k, j, value, negative, extension, remaining) = (Local("bytes", _bytes), Local("n", IrType.I32), Local("k", IrType.I32), Local("j", IrType.I32),
        Local("value", IrType.U64), Local("negative", IrType.Bool), Local("extension", IrType.I32), Local("remaining", IrType.I32));
      // The k-th byte counted from the most significant one.
      IrExpr Load(IrExpr index) => new IrArrayLoad(Ref(b), little ? Binary(IrBinaryOp.Subtract, Binary(IrBinaryOp.Subtract, Ref(n), Int32(1)), index) : index);
      IrExpr Byte(IrExpr index) => Convert(IrType.I32, Load(index));
      var overflow = Raise(IrExceptionClass.OverflowException, "int too large to convert");
      return Function(name, IrType.I64, [b], [n, k, j, value, negative, extension, remaining],
        new IrDeclare(n, new IrArrayLength(Ref(b))),
        new IrDeclare(negative, signed ? And(Binary(IrBinaryOp.Greater, Ref(n), Int32(0)), Binary(IrBinaryOp.GreaterOrEqual, Byte(Int32(0)), Int32(0x80))) : Bool(false)),
        new IrDeclare(extension, Choose(Ref(negative), Int32(0xFF), Int32(0))),
        new IrDeclare(k, Int32(0)),
        new IrWhile(And(Binary(IrBinaryOp.Greater, Binary(IrBinaryOp.Subtract, Ref(n), Ref(k)), Int32(8)), Equal(Byte(Ref(k)), Ref(extension))), Block(Assign(k, Plus(Ref(k), Int32(1))))),
        If(Binary(IrBinaryOp.Greater, Binary(IrBinaryOp.Subtract, Ref(n), Ref(k)), Int32(8)), overflow),
        new IrDeclare(value, UInt(0)),
        new IrDeclare(j, Ref(k)),
        new IrFor(Less(Ref(j), Ref(n)), Block(Assign(value, Binary(IrBinaryOp.Or, Binary(IrBinaryOp.ShiftLeft, Ref(value), Int32(8)), Convert(IrType.U64, Load(Ref(j)))))),
          Block(Assign(j, Plus(Ref(j), Int32(1))))),
        new IrDeclare(remaining, Binary(IrBinaryOp.Subtract, Ref(n), Ref(k))),
        If(And(Ref(negative), Less(Ref(remaining), Int32(8))),
          Assign(value, Binary(IrBinaryOp.Or, Ref(value), Binary(IrBinaryOp.ShiftLeft, UInt(ulong.MaxValue), Binary(IrBinaryOp.Multiply, Ref(remaining), Int32(8)))))),
        If(And(Equal(Ref(remaining), Int32(8)), Binary(IrBinaryOp.NotEqual, Equal(Binary(IrBinaryOp.ShiftRightLogical, Ref(value), Int32(63)), UInt(1)), Ref(negative))), overflow),
        Return(Convert(IrType.I64, Ref(value))));
    });

  /// <summary>
  /// <c>x.to_bytes(length, order, signed=…)</c>: the bytes of an integer; <c>OverflowError</c> where it
  /// does not fit in the length, or is negative and not signed; <c>ValueError</c> for a negative length.
  /// </summary>
  public IrCall ToBytes(IrExpr value, IrExpr length, bool little, bool signed)
    => this.Call($"PyToBytes{(little ? "Little" : "Big")}{(signed ? "Signed" : "")}", _bytes, [value, length], name => {
      var (x, n, result, k, octet) = (Local("value", IrType.I64), Local("length", IrType.I64), Local("result", _bytes), Local("k", IrType.I64), Local("octet", IrType.I64));
      var tooBig = Raise(IrExceptionClass.OverflowException, "int too big to convert");
      // The bounds of a length below 8 bytes: 2^(8n-1) for a signed value, 2^(8n) for an unsigned one.
      var bits = Binary(IrBinaryOp.Multiply, Ref(n), Int(8));
      IrExpr Power(IrExpr exponent) => Binary(IrBinaryOp.ShiftLeft, Int(1), Convert(IrType.I32, exponent));
      IrStmt fits = signed
        ? If(Or(Less(Ref(x), Binary(IrBinaryOp.Subtract, Int(0), Power(Binary(IrBinaryOp.Subtract, bits, Int(1))))),
            Binary(IrBinaryOp.GreaterOrEqual, Ref(x), Power(Binary(IrBinaryOp.Subtract, bits, Int(1))))), tooBig)
        : If(Binary(IrBinaryOp.GreaterOrEqual, Ref(x), Power(bits)), tooBig);
      IrExpr position = little ? Ref(k) : Binary(IrBinaryOp.Subtract, Binary(IrBinaryOp.Subtract, Ref(n), Int(1)), Ref(k));
      return Function(name, _bytes, [x, n], [result, k, octet],
        If(Less(Ref(n), Int(0)), Raise(IrExceptionClass.ArgumentException, "length argument must be non-negative")),
        If(Binary(IrBinaryOp.Greater, Ref(n), Int(int.MaxValue)), Raise(IrExceptionClass.OverflowException, "cannot fit 'int' into an index-sized integer")),
        signed ? Block() : If(Less(Ref(x), Int(0)), Raise(IrExceptionClass.OverflowException, "can't convert negative int to unsigned")),
        If(Equal(Ref(n), Int(0)), Block(If(Binary(IrBinaryOp.NotEqual, Ref(x), Int(0)), tooBig))),
        If(And(Binary(IrBinaryOp.Greater, Ref(n), Int(0)), Less(Ref(n), Int(8))), fits),
        new IrDeclare(result, new IrNewArray(_bytes, Convert(IrType.I32, Ref(n)))),
        new IrDeclare(k, Int(0)),
        new IrDeclare(octet, Int(0)),
        new IrFor(Less(Ref(k), Ref(n)), Block(
          Assign(octet, Choose(Less(Ref(k), Int(8)), Binary(IrBinaryOp.And, Binary(IrBinaryOp.ShiftRight, Ref(x), Convert(IrType.I32, Binary(IrBinaryOp.Multiply, Ref(k), Int(8)))), Int(0xFF)),
            Choose(Less(Ref(x), Int(0)), Int(0xFF), Int(0)))),
          new IrStore(Ref(result), Convert(IrType.I32, position), Convert(IrType.U8, Ref(octet)))), Block(Assign(k, Plus(Ref(k), Int(1))))),
        Return(Ref(result)));
    });

  /// <summary>The IR type a <c>struct</c> letter reads and writes, beside its single bytes.</summary>
  private static IrType Stored(char letter) => letter switch {
    'H' => IrType.U16,
    'h' => IrType.I16,
    'I' or 'L' => IrType.U32,
    'i' or 'l' => IrType.I32,
    'Q' => IrType.U64,
    'q' => IrType.I64,
    'f' => IrType.F32,
    _ => IrType.F64
  };

  /// <summary>A name for a helper of a format: the byte order and every letter, an upper-case one as itself, a lower-case one after an S.</summary>
  private static string FormatName(StructFormat format)
    => (format.Little ? "Little" : "Big") + string.Concat(format.Text.Skip(1).Where(character => !char.IsWhiteSpace(character)).Select(character => character switch {
      '?' => "T",
      _ when char.IsAsciiDigit(character) || char.IsUpper(character) => character.ToString(),
      _ => "S" + char.ToUpperInvariant(character)
    }));

  /// <summary>
  /// <c>struct.unpack(f, b)</c> and <c>struct.unpack_from(f, b, offset)</c>: the values of the fields in
  /// the byte order of the format; <c>struct.error</c> for a buffer of the wrong length or an offset
  /// outside it; a <c>Q</c> beyond 64 signed bits raises <c>OverflowError</c>.
  /// </summary>
  public IrCall Unpack(StructFormat format, IrExpr buffer, IrExpr? offset, IrType tupleType, DeclaredException error) {
    var key = $"PyUnpack{(offset is null ? "" : "From")}_{FormatName(format)}";
    return this.Call(key, tupleType, offset is null ? [buffer] : [buffer, offset], name => {
      var (b, at, length) = (Local("buffer", _bytes), Local("offset", IrType.I64), Local("length", IrType.I64));
      var body = new List<IrStmt> { new IrDeclare(length, Convert(IrType.I64, new IrArrayLength(Ref(b)))) };
      if (offset is null) {
        body.Add(new IrDeclare(at, Int(0)));
        body.Add(If(Binary(IrBinaryOp.NotEqual, Ref(length), Int(format.Size)), error.Raise(new IrStrConst($"unpack requires a buffer of {format.Size} bytes"))));
      } else {
        body.Add(If(Less(Ref(at), Int(0)), Assign(at, Plus(Ref(at), Ref(length)))));
        body.Add(If(Less(Ref(at), Int(0)), error.Raise(new IrStrConst("offset out of range for the buffer"))));
        body.Add(If(Less(Binary(IrBinaryOp.Subtract, Ref(length), Ref(at)), Int(format.Size)), error.Raise(new IrStrConst($"unpack_from requires a buffer of at least {format.Size} bytes"))));
      }

      var values = new List<IrExpr>();
      var locals = new List<IrLocal> { length };
      if (offset is null)
        locals.Add(at);

      foreach (var field in format.Fields) {
        var position = Convert(IrType.I32, Plus(Ref(at), Int(field.Offset)));
        IrExpr read = field.Letter switch {
          'B' => Convert(IrType.I64, new IrArrayLoad(Ref(b), position)),
          'b' => Convert(IrType.I64, Convert(IrType.I8, new IrArrayLoad(Ref(b), position))),
          '?' => Binary(IrBinaryOp.NotEqual, Convert(IrType.I32, new IrArrayLoad(Ref(b), position)), Int32(0)),
          'f' => Convert(IrType.F64, Intrinsic(format.Little ? IrIntrinsicOp.LoadLittle : IrIntrinsicOp.LoadBig, IrType.F32, Ref(b), position)),
          'd' => Intrinsic(format.Little ? IrIntrinsicOp.LoadLittle : IrIntrinsicOp.LoadBig, IrType.F64, Ref(b), position),
          _ => Intrinsic(format.Little ? IrIntrinsicOp.LoadLittle : IrIntrinsicOp.LoadBig, Stored(field.Letter), Ref(b), position)
        };
        if (field.Letter == 'Q') {
          var held = Local("q" + values.Count, IrType.U64);
          locals.Add(held);
          body.Add(new IrDeclare(held, read));
          body.Add(If(Binary(IrBinaryOp.Greater, Ref(held), UInt(long.MaxValue)), Raise(IrExceptionClass.OverflowException, "int too large")));
          read = Ref(held);
        }

        values.Add(field.Letter is 'f' or 'd' or '?' ? read : Convert(IrType.I64, read));
      }

      body.Add(Return(new IrStructNew(tupleType, values)));
      return Function(name, tupleType, offset is null ? [b] : [b, at], locals, body);
    });
  }

  /// <summary>
  /// <c>struct.pack(f, …)</c>: the bytes of the values in the byte order of the format, padding zero;
  /// <c>struct.error</c> for an integer outside its field, <c>OverflowError</c> for a finite float that
  /// a binary32 field turns into an infinity.
  /// </summary>
  public IrCall Pack(StructFormat format, IReadOnlyList<IrExpr> values, DeclaredException error) {
    var key = $"PyPack_{FormatName(format)}";
    return this.Call(key, _bytes, values, name => {
      var parameters = format.Fields.Select((field, index) => Local("v" + index, field.Type.Kind switch {
        Model.PyKind.Float => IrType.F64,
        Model.PyKind.Bool => IrType.Bool,
        _ => IrType.I64
      })).ToList();
      var result = Local("result", _bytes);
      var body = new List<IrStmt>();
      foreach (var (field, parameter) in format.Fields.Zip(parameters)) {
        (long Low, long High)? range = field.Letter switch {
          'B' => (0, 255),
          'b' => (-128, 127),
          'H' => (0, 65535),
          'h' => (-32768, 32767),
          'I' or 'L' => (0, 4294967295),
          'i' or 'l' => (-2147483648, 2147483647),
          'Q' => (0, long.MaxValue),
          _ => null
        };
        if (range is var (low, high))
          body.Add(If(Or(Less(Ref(parameter), Int(low)), Binary(IrBinaryOp.Greater, Ref(parameter), Int(high))),
            error.Raise(new IrStrConst(field.Letter == 'Q' ? "'Q' format requires 0 <= number <= 18446744073709551615" : $"'{field.Letter}' format requires {low} <= number <= {high}"))));

        if (field.Letter == 'f')
          body.Add(If(And(Intrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, Ref(parameter)), Not(Intrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, Convert(IrType.F32, Ref(parameter))))),
            Raise(IrExceptionClass.OverflowException, "float too large to pack with f format")));
      }

      body.Add(new IrDeclare(result, new IrNewArray(_bytes, Int32(format.Size))));
      foreach (var (field, parameter) in format.Fields.Zip(parameters)) {
        var position = Int32(field.Offset);
        body.Add(field.Letter switch {
          'B' or 'b' => new IrStore(Ref(result), position, Convert(IrType.U8, Ref(parameter))),
          '?' => new IrStore(Ref(result), position, Choose(Ref(parameter), new IrConst(IrType.U8, 1), new IrConst(IrType.U8, 0))),
          _ => new IrExprStmt(Intrinsic(format.Little ? IrIntrinsicOp.StoreLittle : IrIntrinsicOp.StoreBig, IrType.Void, Ref(result), position,
            field.Letter == 'd' ? Ref(parameter) : Convert(Stored(field.Letter), Ref(parameter))))
        });
      }

      body.Add(Return(Ref(result)));
      return Function(name, _bytes, parameters, [result], body);
    });
  }

  /// <summary><c>math.sqrt(x)</c>: the correctly rounded square root; <c>ValueError</c> for a negative number.</summary>
  public IrCall Sqrt(IrExpr value)
    => this.Call("PySqrt", IrType.F64, [value], name => {
      var x = Local("x", IrType.F64);
      return Function(name, IrType.F64, [x], [],
        If(Less(Ref(x), Float(0)), Raise(IrExceptionClass.ArgumentException, "math domain error")),
        Return(Intrinsic(IrIntrinsicOp.Sqrt, IrType.F64, Ref(x))));
    });

  /// <summary><c>math.isqrt(n)</c>: the largest integer whose square is at most <c>n</c>, exactly; <c>ValueError</c> for a negative number.</summary>
  public IrCall ISqrt(IrExpr value)
    => this.Call("PyISqrt", IrType.I64, [value], name => {
      var (n, root) = (Local("n", IrType.I64), Local("root", IrType.I64));
      return Function(name, IrType.I64, [n], [root],
        If(Less(Ref(n), Int(0)), Raise(IrExceptionClass.ArgumentException, "isqrt() argument must be nonnegative")),
        If(Less(Ref(n), Int(2)), Return(Ref(n))),
        new IrDeclare(root, new IrConvert(IrType.I64, Intrinsic(IrIntrinsicOp.Sqrt, IrType.F64, Convert(IrType.F64, Ref(n))), IrConversion.Saturate)),
        // The square root of the nearest double is within one of the exact one; compared by division, so that no square overflows.
        new IrWhile(Binary(IrBinaryOp.Greater, Ref(root), Binary(IrBinaryOp.Divide, Ref(n), Ref(root))), Block(Assign(root, Binary(IrBinaryOp.Subtract, Ref(root), Int(1))))),
        new IrWhile(Binary(IrBinaryOp.LessOrEqual, Plus(Ref(root), Int(1)), Binary(IrBinaryOp.Divide, Ref(n), Plus(Ref(root), Int(1)))), Block(Assign(root, Plus(Ref(root), Int(1))))),
        Return(Ref(root)));
    });

  /// <summary><c>math.gcd(a, b)</c>: the greatest common divisor of the magnitudes, never negative; <c>OverflowError</c> where it is 2^63.</summary>
  public IrCall Gcd(IrExpr left, IrExpr right)
    => this.Call("PyGcd", IrType.I64, [left, right], name => {
      var (a, b, x, y, t) = (Local("a", IrType.I64), Local("b", IrType.I64), Local("x", IrType.U64), Local("y", IrType.U64), Local("t", IrType.U64));
      IrExpr Magnitude(IrLocal value) => Choose(Less(Ref(value), Int(0)), Binary(IrBinaryOp.Subtract, UInt(0), Convert(IrType.U64, Ref(value))), Convert(IrType.U64, Ref(value)));
      return Function(name, IrType.I64, [a, b], [x, y, t],
        new IrDeclare(x, Magnitude(a)),
        new IrDeclare(y, Magnitude(b)),
        new IrDeclare(t, UInt(0)),
        new IrWhile(Binary(IrBinaryOp.NotEqual, Ref(y), UInt(0)), Block(
          Assign(t, Binary(IrBinaryOp.Remainder, Ref(x), Ref(y))),
          Assign(x, Ref(y)),
          Assign(y, Ref(t)))),
        If(Binary(IrBinaryOp.Greater, Ref(x), UInt(long.MaxValue)), Raise(IrExceptionClass.OverflowException, "int too large")),
        Return(Convert(IrType.I64, Ref(x))));
    });

}
