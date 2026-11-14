using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>A named, typed variable of a function: a parameter or a declared local.</summary>
/// <param name="Name">The identifier as the source wrote it.</param>
/// <param name="Type">The type of every value the variable holds.</param>
public sealed record IrLocal(string Name, IrType Type);

/// <summary>An expression; it evaluates its operands left to right and yields a value of <see cref="Type"/>.</summary>
/// <param name="Type">The type of the value, which fixes width, signedness and wrap-around.</param>
public abstract record IrExpr(IrType Type);

/// <summary>
/// A constant. Integers carry their numeric value, a <c>u64</c> its 64 bits (so a value above the
/// largest <c>i64</c> is negative here), truth values 0 or 1, floating-point numbers their IEEE 754
/// bits as <see cref="IrOperators.Bits"/> gives them.
/// </summary>
/// <param name="Type">The type of the constant.</param>
/// <param name="Value">The value, within the range of <paramref name="Type"/>.</param>
public sealed record IrConst(IrType Type, long Value) : IrExpr(Type);

/// <summary>
/// A constant of a module: one value, or an array whose elements never change. An array is a table:
/// it can be read, measured and copied from, and nothing else - it is never stored into and never
/// passed on as a value, so that no other name can reach it.
/// </summary>
/// <param name="Name">The name, unique among the constants and functions of the module.</param>
/// <param name="Type">A truth value, a number, or an array of numbers.</param>
/// <param name="Values">The value, or the elements in order, each as an <see cref="IrConst"/> carries it.</param>
public sealed record IrModuleConst(string Name, IrType Type, IReadOnlyList<long> Values);

/// <summary>The value of a constant of the module, or the table it is.</summary>
/// <param name="Constant">The constant read.</param>
public sealed record IrConstRef(IrModuleConst Constant) : IrExpr(Constant.Type);

/// <summary>The current value of a parameter or local.</summary>
/// <param name="Local">The variable read.</param>
public sealed record IrLocalRef(IrLocal Local) : IrExpr(Local.Type);

/// <summary>The operators with one operand.</summary>
public enum IrUnaryOp {

  /// <summary>
  /// Two's-complement negation of a signed integer, where the smallest value negates to itself, or
  /// the change of sign of a floating-point number.
  /// </summary>
  Negate,

  /// <summary>Negation of a signed integer that traps on the smallest value, which has no negative.</summary>
  NegateChecked,

  /// <summary>Bitwise complement of a 32- or 64-bit integer.</summary>
  BitNot,

  /// <summary>Negation of a truth value.</summary>
  LogicalNot

}

/// <summary>An operator applied to one operand; the result has the operand's type.</summary>
/// <param name="Op">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record IrUnary(IrUnaryOp Op, IrExpr Operand) : IrExpr(Operand.Type);

/// <summary>The operators with two operands.</summary>
public enum IrBinaryOp {

  /// <summary>Addition: modulo 2^width for integers, rounded to nearest even for floating point.</summary>
  Add,

  /// <summary>Subtraction: modulo 2^width for integers, rounded to nearest even for floating point.</summary>
  Subtract,

  /// <summary>
  /// Multiplication: modulo 2^width for integers, rounded to nearest even for floating point. It is
  /// never fused with an addition.
  /// </summary>
  Multiply,

  /// <summary>Addition of integers that traps when the sum is not a value of the type.</summary>
  AddChecked,

  /// <summary>Subtraction of integers that traps when the difference is not a value of the type.</summary>
  SubtractChecked,

  /// <summary>Multiplication of integers that traps when the product is not a value of the type.</summary>
  MultiplyChecked,

  /// <summary>
  /// Division. Of integers it truncates towards zero and traps on a zero divisor and on the one signed
  /// quotient that does not fit (smallest value divided by -1). Of floating-point numbers it follows
  /// IEEE 754 and never traps.
  /// </summary>
  Divide,

  /// <summary>
  /// Remainder with the sign of the dividend, the quotient truncated; of integers it traps exactly
  /// where <see cref="Divide"/> does, of floating-point numbers never.
  /// </summary>
  Remainder,

  /// <summary>Bitwise and.</summary>
  And,

  /// <summary>Bitwise or.</summary>
  Or,

  /// <summary>Bitwise exclusive or.</summary>
  Xor,

  /// <summary>Shift left by the low 5 bits of the <c>i32</c> count, the low 6 bits for a 64-bit value.</summary>
  ShiftLeft,

  /// <summary>Shift right by the masked count: arithmetic on a signed value, logical on an unsigned one.</summary>
  ShiftRight,

  /// <summary>Shift right by the masked count, filling with zeros whatever the signedness.</summary>
  ShiftRightLogical,

  /// <summary>Equality of two numbers of one type or two truth values; a NaN equals nothing.</summary>
  Equal,

  /// <summary>Inequality of two numbers of one type or two truth values.</summary>
  NotEqual,

  /// <summary>Less than, in the signedness of the operands; false when an operand is a NaN, like every ordering.</summary>
  Less,

  /// <summary>Less than or equal, in the signedness of the operands.</summary>
  LessOrEqual,

  /// <summary>Greater than, in the signedness of the operands.</summary>
  Greater,

  /// <summary>Greater than or equal, in the signedness of the operands.</summary>
  GreaterOrEqual,

  /// <summary>Conjunction that evaluates the right operand only when the left one is true.</summary>
  LogicalAnd,

  /// <summary>Disjunction that evaluates the right operand only when the left one is false.</summary>
  LogicalOr

}

/// <summary>
/// An operator applied to two operands. Arithmetic, bitwise and shift operators yield the type of the
/// left operand; comparisons and the logical operators yield a truth value.
/// </summary>
/// <param name="Op">The operator.</param>
/// <param name="Left">The operand evaluated first.</param>
/// <param name="Right">The operand evaluated second.</param>
public sealed record IrBinary(IrBinaryOp Op, IrExpr Left, IrExpr Right)
  : IrExpr(Op >= IrBinaryOp.Equal ? IrType.Bool : Left.Type);

/// <summary>What a conversion does with a value the target type cannot hold.</summary>
public enum IrConversion {

  /// <summary>The value modulo 2^width of the target type.</summary>
  Wrap,

  /// <summary>The conversion traps with an integer overflow; a NaN traps as well.</summary>
  Trap,

  /// <summary>
  /// A floating-point number is truncated towards zero and becomes the smallest or largest value of
  /// the integer type when it lies outside; a NaN becomes 0.
  /// </summary>
  Saturate

}

/// <summary>
/// A conversion between numeric types. Between integers it wraps or traps; from floating point to
/// an integer it saturates or traps; to floating point it rounds to nearest even, has no value it
/// cannot convert and carries <see cref="IrConversion.Wrap"/>.
/// </summary>
/// <param name="Type">The target type.</param>
/// <param name="Operand">The value converted.</param>
/// <param name="Contract">What happens to a value outside the target type.</param>
public sealed record IrConvert(IrType Type, IrExpr Operand, IrConversion Contract) : IrExpr(Type) {

  /// <summary>A conversion that wraps around.</summary>
  /// <param name="type">The target type.</param>
  /// <param name="operand">The value converted.</param>
  public IrConvert(IrType type, IrExpr operand) : this(type, operand, IrConversion.Wrap) { }

}

/// <summary>A choice that evaluates the condition and then exactly one of the two branches.</summary>
/// <param name="Condition">The truth value deciding the branch.</param>
/// <param name="WhenTrue">The value when the condition holds.</param>
/// <param name="WhenFalse">The value when it does not.</param>
public sealed record IrConditional(IrExpr Condition, IrExpr WhenTrue, IrExpr WhenFalse) : IrExpr(WhenTrue.Type);

/// <summary>An element of an array or of a view; traps when the index is outside it.</summary>
/// <param name="Array">The array or view read.</param>
/// <param name="Index">The zero-based index.</param>
public sealed record IrArrayLoad(IrExpr Array, IrExpr Index) : IrExpr(Array.Type.Element ?? IrType.Void);

/// <summary>The number of elements of an array, as an <c>i32</c>.</summary>
/// <param name="Array">The array measured.</param>
public sealed record IrArrayLength(IrExpr Array) : IrExpr(IrType.I32);

/// <summary>A new array whose elements are all zero; traps on a negative length.</summary>
/// <param name="Type">The array type.</param>
/// <param name="Length">The number of elements.</param>
public sealed record IrNewArray(IrType Type, IrExpr Length) : IrExpr(Type);

/// <summary>A call of another function of the same module; arguments are evaluated left to right.</summary>
/// <param name="Function">The name of the function called.</param>
/// <param name="Type">The return type of that function.</param>
/// <param name="Arguments">One argument per parameter.</param>
public sealed record IrCall(string Function, IrType Type, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>
/// A step by one as an expression. The target is a variable or an array element, whose array and
/// index are evaluated once; the target is read, one is added or subtracted in the type of the target
/// and the result is stored. A result outside an integer type wraps around or, when
/// <paramref name="Trap"/> is set, traps before anything is stored.
/// </summary>
/// <param name="Target">An <see cref="IrLocalRef"/> or an <see cref="IrArrayLoad"/> of a numeric type.</param>
/// <param name="Increment">Whether one is added; otherwise it is subtracted.</param>
/// <param name="Postfix">Whether the expression yields the value from before the step, otherwise the new one.</param>
/// <param name="Trap">Whether an integer result outside the type traps instead of wrapping around.</param>
public sealed record IrStep(IrExpr Target, bool Increment, bool Postfix, bool Trap) : IrExpr(Target.Type);

/// <summary>
/// A new value of a struct, built from one value per field in declaration order; the arguments are
/// evaluated in that order.
/// <para>
/// A struct is a value, never a reference. This is stated here once for every node of the IR and
/// every implementation of it: every read of a struct value - of a variable, a parameter, an array
/// element or a field - yields a copy, so an assignment, an argument, a returned value, a store into
/// an array and a load from one each copy the value, and two variables never share one. A write of a
/// field through a place (<see cref="IrFieldStore"/>) changes that place alone; a field of an array
/// element is a field of the element itself, not of a copy of it. A field of an array type holds the
/// array by reference, as every array is held. Two struct values are equal when
/// <see cref="IrStructEquals"/> says so; the default value has every field zero or false.
/// </para>
/// </summary>
/// <param name="Type">The struct type.</param>
/// <param name="Arguments">The value of every field, in declaration order.</param>
public sealed record IrStructNew(IrType Type, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>
/// The default value of a struct: every number zero, every truth value false, every struct field its
/// own default. A struct with a field of an array type has none.
/// </summary>
/// <param name="Type">The struct type.</param>
public sealed record IrStructDefault(IrType Type) : IrExpr(Type);

/// <summary>
/// The value of a field. The target is evaluated first; when it is a place - a variable, a
/// parameter, an array element or a field of a place - the field is read there, and only the field
/// is copied, as every read of a struct value is.
/// </summary>
/// <param name="Target">A value of a struct type that has the field.</param>
/// <param name="Field">The field, one of the target type's own.</param>
public sealed record IrFieldLoad(IrExpr Target, IrField Field) : IrExpr(Field.Type);

/// <summary>
/// Field-wise equality of two values of one struct type, the fields compared in declaration order:
/// numbers of an integer type and truth values by value, floating-point numbers as
/// <c>double.Equals</c> compares them - a NaN equals a NaN, and <c>+0.0</c> equals <c>-0.0</c> -
/// a struct field recursively, and an array field by identity. Both operands are evaluated, left first.
/// </summary>
/// <param name="Left">The value evaluated first.</param>
/// <param name="Right">The value evaluated second.</param>
public sealed record IrStructEquals(IrExpr Left, IrExpr Right) : IrExpr(IrType.Bool);

/// <summary>The operations an <see cref="IrIntrinsic"/> performs.</summary>
public enum IrIntrinsicOp {

  /// <summary>A new array holding the arguments in order; the type of the node is the array type.</summary>
  ArrayOf,

  /// <summary>
  /// <c>(source, sourceIndex, target, targetIndex, length)</c>: copies a range between two arrays of
  /// one type as if through a buffer, so that overlapping ranges of one array are safe. Traps with an
  /// index out of range, before anything is copied, when a range is not inside its array.
  /// </summary>
  ArrayCopy,

  /// <summary><c>(array, value)</c>: stores the value in every element.</summary>
  ArrayFill,

  /// <summary><c>(array)</c>: stores zero in every element.</summary>
  ArrayClear,

  /// <summary>
  /// The absolute value of an integer or a floating-point number; an unsigned integer is its own.
  /// The smallest value of a signed integer type has none and traps with an integer overflow.
  /// </summary>
  Abs,

  /// <summary>The smaller of two numbers of one type; of floating-point numbers a NaN if either is one, and -0 before +0.</summary>
  Min,

  /// <summary>The larger of two numbers of one type; of floating-point numbers a NaN if either is one, and +0 after -0.</summary>
  Max,

  /// <summary>
  /// -1, 0 or 1 as an <c>i32</c> for a negative, zero or positive integer or floating-point number.
  /// A NaN has no sign and traps with an invalid operation.
  /// </summary>
  Sign,

  /// <summary>The square root of a floating-point number, correctly rounded.</summary>
  Sqrt,

  /// <summary>The largest integral floating-point number that is not larger than the operand.</summary>
  Floor,

  /// <summary>The smallest integral floating-point number that is not smaller than the operand.</summary>
  Ceiling,

  /// <summary>The operand without its fraction.</summary>
  Truncate,

  /// <summary>The nearest integral floating-point number; a tie goes to the even one.</summary>
  RoundEven,

  /// <summary>The nearest integral floating-point number; a tie goes away from zero.</summary>
  RoundAway,

  /// <summary><c>(value, count)</c>: an integer rotated left by the <c>i32</c> count modulo its width, a negative count included.</summary>
  RotateLeft,

  /// <summary><c>(value, count)</c>: an integer rotated right by the <c>i32</c> count modulo its width.</summary>
  RotateRight,

  /// <summary>The number of set bits of an integer, as an <c>i32</c>; of a signed one those of its bit pattern.</summary>
  PopCount,

  /// <summary>The number of zero bits above the highest set bit of an integer, as an <c>i32</c>; the width for 0.</summary>
  LeadingZeroCount,

  /// <summary>The number of zero bits below the lowest set bit of an integer, as an <c>i32</c>; the width for 0.</summary>
  TrailingZeroCount,

  /// <summary>The position of the highest set bit of the bit pattern of an integer, as an <c>i32</c>; 0 for 0.</summary>
  Log2,

  /// <summary>The full product of two <c>i32</c> as an <c>i64</c>, or of two <c>u32</c> as a <c>u64</c>.</summary>
  BigMul,

  /// <summary>The IEEE 754 bits of an <c>f64</c> as an <c>i64</c>, or of an <c>f32</c> as an <c>i32</c>.</summary>
  FloatBits,

  /// <summary>The <c>f64</c> with the bits of an <c>i64</c>, or the <c>f32</c> with the bits of an <c>i32</c>.</summary>
  BitsFloat,

  /// <summary>
  /// The nearest integral floating-point number; a tie goes to the larger one, towards positive
  /// infinity. A number between -0.5 and zero, -0.5 included, becomes the negative zero. It is
  /// ECMA-262's <c>Math.round</c>.
  /// </summary>
  RoundHalfUp,

  /// <summary>
  /// ECMA-262's <c>ToInt32</c> of an <c>f64</c>, as an <c>i32</c>: the number without its fraction,
  /// modulo 2^32, read as a signed integer. A NaN and both infinities become 0. No value traps, and
  /// a number beyond 2^53 is reduced exactly, which no conversion with a contract does.
  /// </summary>
  ToInt32,

  /// <summary>ECMA-262's <c>ToUint32</c> of an <c>f64</c>, as a <c>u32</c>: like <see cref="ToInt32"/>, read as an unsigned integer.</summary>
  ToUint32,

  // The integer and bit operations that IntegerOperations defines, each on every integer type.

  /// <summary>Whether an integer has an odd number of set bits: 1 or 0 as an <c>i32</c>.</summary>
  Parity,

  /// <summary>An integer with its bytes in the opposite order; one of 8 bits is itself.</summary>
  ByteSwap,

  /// <summary>An integer with its bits in the opposite order.</summary>
  BitReverse,

  /// <summary>Whether an integer is a power of two, as a truth value; never 0 and never a negative value.</summary>
  IsPow2,

  /// <summary>The smallest power of two not below the bit pattern of an integer, modulo 2^width; 0 for 0.</summary>
  RoundUpPow2,

  /// <summary><c>(a, b)</c>: the upper half of the product of two integers in twice their width, signed or unsigned as their type.</summary>
  MulHigh,

  /// <summary><c>(a, b)</c>: the quotient rounded towards negative infinity; it traps where <see cref="IrBinaryOp.Divide"/> does.</summary>
  DivFloor,

  /// <summary><c>(a, b)</c>: the remainder of <see cref="DivFloor"/>, with the sign of the divisor; it traps where <see cref="IrBinaryOp.Divide"/> does.</summary>
  ModFloor,

  /// <summary><c>(a, b)</c>: the quotient of the Euclidean division; it traps where <see cref="IrBinaryOp.Divide"/> does.</summary>
  DivEuclid,

  /// <summary><c>(a, b)</c>: the remainder of the Euclidean division, never negative; it traps where <see cref="IrBinaryOp.Divide"/> does.</summary>
  ModEuclid,

  /// <summary>
  /// <c>(value, low, high)</c>: the value within two bounds; a low bound above the high one traps with an
  /// invalid operation. Of floating-point numbers as <c>Math.Clamp</c> computes it: a NaN value is
  /// returned, a NaN bound ignored.
  /// </summary>
  Clamp,

  /// <summary>The absolute value of an integer modulo 2^width: the smallest value of a signed type is its own.</summary>
  AbsWrap,

  /// <summary><c>(a, b)</c>: the sum of two integers, the nearest value of the type when it lies outside.</summary>
  AddSaturate,

  /// <summary><c>(a, b)</c>: the difference of two integers, the nearest value of the type when it lies outside.</summary>
  SubSaturate,

  /// <summary><c>(a, b)</c>: the product of two integers, the nearest value of the type when it lies outside.</summary>
  MulSaturate,

  /// <summary><c>(a, b, carry)</c>: the low word of <c>a + b + carry</c> of an unsigned type, the carry a truth value.</summary>
  AddCarry,

  /// <summary><c>(a, b, carry)</c>: whether <c>a + b + carry</c> of an unsigned type reaches 2^width, as a truth value.</summary>
  CarryOut,

  /// <summary><c>(a, b, borrow)</c>: the low word of <c>a - b - borrow</c> of an unsigned type, the borrow a truth value.</summary>
  SubBorrow,

  /// <summary><c>(a, b, borrow)</c>: whether <c>a - b - borrow</c> of an unsigned type is negative, as a truth value.</summary>
  BorrowOut,

  /// <summary><c>(a, b)</c>: whether the sum of two integers is not a value of their type, as a truth value; it never traps.</summary>
  AddOverflows,

  /// <summary><c>(a, b)</c>: whether the difference of two integers is not a value of their type; it never traps.</summary>
  SubOverflows,

  /// <summary><c>(a, b)</c>: whether the product of two integers is not a value of their type; it never traps.</summary>
  MulOverflows,

  /// <summary>
  /// <c>(value, start, length)</c>: the field of <c>length</c> bits from bit <c>start</c> on, at the
  /// bottom with zeros above; a field outside the width traps with an invalid operation.
  /// </summary>
  ExtractBits,

  /// <summary>
  /// <c>(value, field, start, length)</c>: the value with that field replaced by the lowest bits of
  /// <c>field</c>; a field outside the width traps with an invalid operation.
  /// </summary>
  InsertBits,

  /// <summary><c>(value, position)</c>: whether one bit is set, as a truth value; a position outside the width traps with an invalid operation.</summary>
  TestBit,

  /// <summary><c>(value, position)</c>: the value with one bit set; a position outside the width traps with an invalid operation.</summary>
  SetBit,

  /// <summary><c>(value, position)</c>: the value with one bit cleared; a position outside the width traps with an invalid operation.</summary>
  ClearBit,

  /// <summary><c>(value, position)</c>: the value with one bit inverted; a position outside the width traps with an invalid operation.</summary>
  ToggleBit,

  /// <summary>
  /// <c>(count)</c>: the integer of the type of the node whose lowest <c>count</c> bits are set, all
  /// ones for the width; a count outside 0 to the width traps with an invalid operation.
  /// </summary>
  LowMask,

  /// <summary>The lowest set bit of an integer alone, <c>x &amp; -x</c>.</summary>
  IsolateLowestSet,

  /// <summary>An integer without its lowest set bit, <c>x &amp; (x - 1)</c>.</summary>
  ClearLowestSet,

  /// <summary>The bits of an integer up to and including its lowest set bit, <c>x ^ (x - 1)</c>; all ones for 0.</summary>
  MaskToLowestSet,

  /// <summary><c>(value, bits)</c>: the lowest <c>bits</c> bits extended by their sign; <c>bits</c> outside 1 to the width traps with an invalid operation.</summary>
  SignExtend,

  /// <summary><c>(value, bits)</c>: the lowest <c>bits</c> bits with zeros above; <c>bits</c> outside 1 to the width traps with an invalid operation.</summary>
  ZeroExtend,

  /// <summary>
  /// <c>(array, index)</c>: the integer of 16, 32 or 64 bits, or the IEEE 754 binary32 or binary64
  /// number, of the type of the node whose bytes stand in a <c>u8[]</c> from the index on, the lowest
  /// first. A byte outside the array traps with an index out of range.
  /// </summary>
  LoadLittle,

  /// <summary><c>(array, index)</c>: like <see cref="LoadLittle"/>, the highest byte first.</summary>
  LoadBig,

  /// <summary>
  /// <c>(array, index, value)</c>: writes the bytes of an integer of 16, 32 or 64 bits, or the IEEE 754
  /// encoding of an <c>f32</c> or <c>f64</c>, into a <c>u8[]</c> from the index on, the lowest first.
  /// A byte outside the array traps with an index out of range before anything is written.
  /// </summary>
  StoreLittle,

  /// <summary><c>(array, index, value)</c>: like <see cref="StoreLittle"/>, the highest byte first.</summary>
  StoreBig,

  /// <summary><c>(x, y, z)</c>: <c>x * y + z</c> of floating-point numbers, rounded once.</summary>
  FusedMultiplyAdd,

  /// <summary><c>(x, y)</c>: the remainder of IEEE 754 §5.3.1, <c>x - n * y</c> with <c>n</c> the integer nearest to <c>x / y</c>; exact.</summary>
  IeeeRemainder,

  /// <summary>The reciprocal <c>1 / x</c> of a floating-point number, rounded once.</summary>
  Reciprocal,

  /// <summary>
  /// <c>(x, digits, mode)</c>: a floating-point number rounded to decimal digits as .NET's
  /// <c>Math.Round</c> does; the mode is a constant of <c>MidpointRounding</c>, and digits outside
  /// 0 to 15 (0 to 6 for <c>f32</c>) trap with an invalid operation.
  /// </summary>
  RoundDigits,

  /// <summary><c>(x, y)</c>: the magnitude of <c>x</c> with the sign bit of <c>y</c>.</summary>
  CopySign,

  /// <summary><c>(x, y)</c>: the operand of the smaller magnitude, a NaN if either is one, the negative one of equal magnitudes.</summary>
  MinMagnitude,

  /// <summary><c>(x, y)</c>: the operand of the larger magnitude, a NaN if either is one, the positive one of equal magnitudes.</summary>
  MaxMagnitude,

  /// <summary><c>(x, y)</c>: the smaller operand, a NaN operand ignored, -0 before +0.</summary>
  MinNumber,

  /// <summary><c>(x, y)</c>: the larger operand, a NaN operand ignored, +0 after -0.</summary>
  MaxNumber,

  /// <summary><c>(x, n)</c>: <c>x * 2^n</c> with an <c>i32</c> exponent, rounded once.</summary>
  ScaleB,

  /// <summary>The binary exponent of a floating-point number as an <c>i32</c>; the smallest <c>i32</c> for zero, the largest for an infinity or a NaN.</summary>
  ILogB,

  /// <summary>The next floating-point number towards positive infinity, as <c>Math.BitIncrement</c>.</summary>
  BitIncrement,

  /// <summary>The next floating-point number towards negative infinity, as <c>Math.BitDecrement</c>.</summary>
  BitDecrement,

  /// <summary>The fraction of C's <c>frexp</c>: the number scaled by a power of two into [0.5, 1) in magnitude.</summary>
  Fraction,

  /// <summary>The exponent of C's <c>frexp</c>, as an <c>i32</c>; 0 for a zero, an infinity and a NaN.</summary>
  Exponent,

  /// <summary>Whether a floating-point number is a NaN.</summary>
  IsNaN,

  /// <summary>Whether a floating-point number is an infinity.</summary>
  IsInfinity,

  /// <summary>Whether a floating-point number is finite.</summary>
  IsFinite,

  /// <summary>Whether the sign bit of a floating-point number is set, of -0 and of a NaN as well.</summary>
  IsNegative,

  /// <summary>Whether a floating-point number is normal.</summary>
  IsNormal,

  /// <summary>Whether a floating-point number is subnormal.</summary>
  IsSubnormal,

  /// <summary>Whether a floating-point number is finite and integral.</summary>
  IsInteger,

  /// <summary>An angle in degrees in radians: <c>(x * pi) / 180</c>, two operations as .NET computes it.</summary>
  DegreesToRadians,

  /// <summary>An angle in radians in degrees: <c>(x * 180) / pi</c>, two operations as .NET computes it.</summary>
  RadiansToDegrees,

  /// <summary><c>(a, b, t)</c>: <c>a + (b - a) * t</c>, the three operations in that order.</summary>
  Lerp,

  /// <summary>The cube root; contract <c>ulp(1)</c>.</summary>
  Cbrt,

  /// <summary><c>(x, y)</c>: <c>sqrt(x^2 + y^2)</c> without overflow; an infinity wins over a NaN; contract <c>ulp(1)</c>.</summary>
  Hypot,

  /// <summary>The reciprocal square root <c>1 / sqrt(x)</c>, rounded once; contract <c>ulp(1)</c>.</summary>
  ReciprocalSqrt,

  /// <summary>The sine of a floating-point number in radians; contract <c>ulp(1)</c>.</summary>
  Sin,

  /// <summary>The cosine of a floating-point number in radians; contract <c>ulp(1)</c>.</summary>
  Cos,

  /// <summary>The hyperbolic sine; contract <c>ulp(2)</c>.</summary>
  Sinh,

  /// <summary>The hyperbolic cosine; contract <c>ulp(2)</c>.</summary>
  Cosh,

  /// <summary>The hyperbolic tangent; contract <c>ulp(2)</c>.</summary>
  Tanh,

  /// <summary>e raised to a floating-point number; contract <c>ulp(1)</c>.</summary>
  Exp,

  /// <summary>2 raised to a floating-point number; contract <c>ulp(1)</c>.</summary>
  Exp2,

  /// <summary>10 raised to a floating-point number; contract <c>ulp(1)</c>.</summary>
  Exp10,

  /// <summary><c>e^x - 1</c> without the cancellation near zero; contract <c>ulp(1)</c>.</summary>
  ExpM1,

  /// <summary>The tangent of a floating-point number in radians; contract <c>ulp(1)</c>.</summary>
  Tan,

  /// <summary>The arcsine, in [-pi/2, pi/2]; a NaN outside [-1, 1]; contract <c>ulp(1)</c>.</summary>
  Asin,

  /// <summary>The arccosine, in [0, pi]; a NaN outside [-1, 1]; contract <c>ulp(1)</c>.</summary>
  Acos,

  /// <summary>The arctangent, in [-pi/2, pi/2]; contract <c>ulp(1)</c>.</summary>
  Atan,

  /// <summary><c>(y, x)</c>: the angle of the point (x, y), in [-pi, pi], with the signs of IEEE 754 §9.2.1; contract <c>ulp(1)</c>.</summary>
  Atan2,

  /// <summary>The inverse hyperbolic sine; contract <c>ulp(1)</c>.</summary>
  Asinh,

  /// <summary>The inverse hyperbolic cosine; a NaN below 1; contract <c>ulp(1)</c>.</summary>
  Acosh,

  /// <summary>The inverse hyperbolic tangent; infinite at -1 and 1, a NaN outside; contract <c>ulp(1)</c>.</summary>
  Atanh,

  /// <summary>The natural logarithm; -infinity at zero, a NaN below; contract <c>ulp(1)</c>.</summary>
  Log,

  /// <summary>The logarithm to base 10; contract <c>ulp(1)</c>.</summary>
  Log10,

  /// <summary><c>ln(1 + x)</c> without the cancellation near zero; contract <c>ulp(1)</c>.</summary>
  Log1P,

  /// <summary><c>(a, b)</c>: the logarithm of <c>a</c> to base <c>b</c> as .NET defines it, <c>Log(a) / Log(b)</c> after its special cases; contract <c>ulp(2)</c>.</summary>
  LogBase,

  /// <summary><c>(x, y)</c>: <c>x</c> raised to <c>y</c> with the special cases of IEEE 754 §9.2.1; contract <c>ulp(1)</c>.</summary>
  Pow,

  /// <summary><c>(x, y)</c>: ECMA-262's exponentiation: <see cref="Pow"/>, except that a NaN exponent and <c>(±1, ±infinity)</c> give a NaN; contract <c>ulp(1)</c>.</summary>
  PowEcma,

  /// <summary><c>(x, n)</c>: the <c>n</c>-th root of IEEE 754 §9.2 for an <c>i32</c> <c>n</c>; contract <c>ulp(1)</c>.</summary>
  RootN,

  // The views of #1325: docs/reference/views.md.

  /// <summary>
  /// <c>(source)</c>, <c>(source, start)</c> or <c>(source, start, length)</c>: a view of an array or of another
  /// view, the whole of it, or the elements from <c>start</c> on, to its end or <c>length</c> of them. A range outside the source traps with an argument out of
  /// range. A writable view is never made of a read-only one.
  /// </summary>
  ViewOf,

  /// <summary>
  /// <c>(view, start)</c> or <c>(view, start, length)</c>: the part of a view from <c>start</c> on, to its end or
  /// <c>length</c> elements of it, of the same type; a range outside it traps with an argument out of range.
  /// </summary>
  Slice,

  /// <summary><c>(view)</c>: the number of elements of a view, as an <c>i32</c>.</summary>
  ViewLength,

  /// <summary>
  /// <c>(length)</c>: a writable view of a new run of zeros of the element type of the node, which lives no
  /// longer than the function; a negative length traps with an integer overflow, as an array's does.
  /// </summary>
  StackAlloc,

  /// <summary>
  /// <c>(source, target)</c>: copies every element of a view to the start of a writable one of the same
  /// element type, as if through a buffer, so that overlapping views are safe; a target shorter than the
  /// source traps with an argument out of range before anything is copied.
  /// </summary>
  ViewCopy,

  /// <summary><c>(source, target)</c>: <see cref="ViewCopy"/> when the target is long enough, then true; false and nothing copied otherwise.</summary>
  ViewTryCopy,

  /// <summary><c>(view, value)</c>: stores the value in every element of a writable view.</summary>
  ViewFill,

  /// <summary>
  /// <c>(a, b)</c>: whether two views of one element type have the same length and equal elements, a
  /// floating-point element compared as <c>double.Equals</c> compares it: a NaN equals a NaN, and
  /// <c>+0.0</c> equals <c>-0.0</c>.
  /// </summary>
  ViewEqual,

  /// <summary><c>(view)</c>: a new array holding the elements of a view.</summary>
  ViewToArray,

  /// <summary>
  /// <c>(view)</c>: the bytes of a view read as elements of another number type, the lowest byte first,
  /// whatever the host's order: a view of the same memory, of the same writability, whose length is the
  /// number of whole elements the bytes hold.
  /// </summary>
  Reinterpret

}

/// <summary>
/// An operation of the base library that has an exact meaning. Its arguments are evaluated left to
/// right; <see cref="IrOperators.Accepts(IrIntrinsicOp, IrType, IReadOnlyList{IrType})"/> says which
/// types it takes.
/// </summary>
/// <param name="Op">The operation.</param>
/// <param name="Type">The type of the result, <see cref="IrType.Void"/> when there is none.</param>
/// <param name="Arguments">The operands.</param>
public sealed record IrIntrinsic(IrIntrinsicOp Op, IrType Type, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>A statement.</summary>
public abstract record IrStmt;

/// <summary>A sequence of statements with its own scope for declared locals.</summary>
/// <param name="Statements">The statements, in execution order.</param>
public sealed record IrBlock(IReadOnlyList<IrStmt> Statements) : IrStmt;

/// <summary>The declaration of a local, visible until the enclosing block ends.</summary>
/// <param name="Local">The local declared.</param>
/// <param name="Initializer">Its first value, or <see langword="null"/> when it is assigned later.</param>
public sealed record IrDeclare(IrLocal Local, IrExpr? Initializer) : IrStmt;

/// <summary>An assignment to a parameter or local.</summary>
/// <param name="Local">The variable written.</param>
/// <param name="Value">The value stored.</param>
public sealed record IrAssign(IrLocal Local, IrExpr Value) : IrStmt;

/// <summary>
/// A write to an element of an array or of a writable view. Array, index and value are evaluated in
/// that order, then the index is checked; the write traps when it is outside the array.
/// </summary>
/// <param name="Array">The array or view written.</param>
/// <param name="Index">The zero-based index.</param>
/// <param name="Value">The value stored, of the element type.</param>
public sealed record IrStore(IrExpr Array, IrExpr Index, IrExpr Value) : IrStmt;

/// <summary>
/// A write to a field of a place: of a variable or parameter, of an array element, or of a field of
/// such a place. The target place is evaluated first - for an array element its array and index,
/// which are checked - then the value; the write changes that place alone (see <see cref="IrStructNew"/>).
/// A read-only field is never written this way.
/// </summary>
/// <param name="Target">The place: an <see cref="IrLocalRef"/>, an <see cref="IrArrayLoad"/> or an <see cref="IrFieldLoad"/> of a place.</param>
/// <param name="Field">The field, one of the target type's own.</param>
/// <param name="Value">The value stored, of the field's type.</param>
public sealed record IrFieldStore(IrExpr Target, IrField Field, IrExpr Value) : IrStmt;

/// <summary>An expression evaluated for its effect; the value is dropped.</summary>
/// <param name="Expression">The expression.</param>
public sealed record IrExprStmt(IrExpr Expression) : IrStmt;

/// <summary>A conditional statement.</summary>
/// <param name="Condition">The truth value deciding the branch.</param>
/// <param name="Then">Executed when the condition holds.</param>
/// <param name="Else">Executed when it does not, or <see langword="null"/>.</param>
public sealed record IrIf(IrExpr Condition, IrBlock Then, IrBlock? Else) : IrStmt;

/// <summary>A loop that tests its condition before every pass.</summary>
/// <param name="Condition">The truth value that keeps the loop running.</param>
/// <param name="Body">The statements of one pass.</param>
public sealed record IrWhile(IrExpr Condition, IrBlock Body) : IrStmt;

/// <summary>A loop that runs its body once before it tests its condition for the first time.</summary>
/// <param name="Body">The statements of one pass; a <see cref="IrContinue"/> goes on to the condition.</param>
/// <param name="Condition">The truth value that keeps the loop running.</param>
public sealed record IrDoWhile(IrBlock Body, IrExpr Condition) : IrStmt;

/// <summary>
/// A loop that tests its condition before every pass and runs a step after every pass - also after a
/// pass that <see cref="IrContinue"/> ended, which is what tells it from an <see cref="IrWhile"/>
/// whose body ends with the step.
/// </summary>
/// <param name="Condition">The truth value that keeps the loop running.</param>
/// <param name="Body">The statements of one pass.</param>
/// <param name="Step">What follows every pass: declarations, assignments, stores into arrays and fields, and expressions only.</param>
public sealed record IrFor(IrExpr Condition, IrBlock Body, IrBlock Step) : IrStmt;

/// <summary>One section of a <see cref="IrSwitch"/>.</summary>
/// <param name="Labels">The constants that select the section, as an <see cref="IrConst"/> carries them.</param>
/// <param name="IsDefault">Whether the section is also selected when no label of the switch matches.</param>
/// <param name="Body">The statements of the section.</param>
public sealed record IrSwitchSection(IReadOnlyList<long> Labels, bool IsDefault, IrBlock Body);

/// <summary>
/// A choice by the value of an integer: the one section runs that carries the value as a label, or the
/// default section when none does, or nothing. There is no fall-through: when the body of a section
/// ends, the switch ends, exactly as when it executes a <see cref="IrBreak"/>.
/// </summary>
/// <param name="Value">The integer that selects the section.</param>
/// <param name="Sections">The sections; a label stands in at most one of them.</param>
public sealed record IrSwitch(IrExpr Value, IReadOnlyList<IrSwitchSection> Sections) : IrStmt;

/// <summary>Leaves the innermost enclosing loop or switch.</summary>
public sealed record IrBreak : IrStmt;

/// <summary>Ends the current pass of the innermost enclosing loop; the step of an <see cref="IrFor"/> still runs.</summary>
public sealed record IrContinue : IrStmt;

/// <summary>Leaves the function.</summary>
/// <param name="Value">The value returned, or <see langword="null"/> in a function returning nothing.</param>
public sealed record IrReturn(IrExpr? Value) : IrStmt;

/// <summary>A function.</summary>
/// <param name="Name">The name, unique in its module.</param>
/// <param name="IsPublic">Whether the function is visible outside the module.</param>
/// <param name="ReturnType">The type returned, or <see cref="IrType.Void"/>.</param>
/// <param name="Parameters">The parameters, in call order.</param>
/// <param name="Locals">Every local the body declares.</param>
/// <param name="Body">The statements of the function.</param>
public sealed record IrFunction(
  string Name,
  bool IsPublic,
  IrType ReturnType,
  IReadOnlyList<IrLocal> Parameters,
  IReadOnlyList<IrLocal> Locals,
  IrBlock Body);

/// <summary>A module: the functions, constants and globals of one source class, and the types they use.</summary>
/// <param name="Name">The name of the module.</param>
/// <param name="Functions">The functions, in source order.</param>
/// <param name="Constants">The constants, in source order.</param>
public sealed record IrModule(string Name, IReadOnlyList<IrFunction> Functions, IReadOnlyList<IrModuleConst> Constants) {

  /// <summary>A module without constants.</summary>
  /// <param name="name">The name of the module.</param>
  /// <param name="functions">The functions, in source order.</param>
  public IrModule(string name, IReadOnlyList<IrFunction> functions) : this(name, functions, []) { }

  /// <summary>
  /// The types the module declares, in source order. Every declared type a function, a local, a field
  /// or a node of the module names is one of them.
  /// </summary>
  public IReadOnlyList<IrTypeDeclaration> Types { get; init; } = [];

  /// <summary>
  /// The globals of the module, in source order, which is the order they are initialised in. Every
  /// global a function or an initializer names is one of them.
  /// </summary>
  public IReadOnlyList<IrGlobal> Globals { get; init; } = [];

}
