namespace Polypiler.Core.Backends.Python;

internal static partial class PythonHelpers {

  /// <summary>A reference converted to a class: itself, or the cast trap.</summary>
  public const string Cast = "_ply_cast";

  /// <summary>An exception with its inner exception as its cause.</summary>
  public const string Caused = "_ply_caused";

  /// <summary>The message of an exception: its first argument as text.</summary>
  public const string Message = "_ply_message";

  /// <summary>Writes the value of a cell and is the value: an assignment to a cell inside an expression.</summary>
  public const string Put = "_ply_put";

  /// <summary>Raises an exception where an expression stands.</summary>
  public const string Throw = "_ply_throw";

  /// <summary>The value an optional value holds; the missing-value trap for none.</summary>
  public const string Unwrap = "_ply_unwrap";

  /// <summary>The order of two values of a generic definition as .NET's comparer of a narrow integer gives it: their difference.</summary>
  public const string Difference = "_ply_difference";

  /// <summary>The order of two floating-point numbers as .NET's comparer gives it: a NaN below every number, -0 equal to 0.</summary>
  public const string CompareFloat = "_ply_compare_float";

  /// <summary>The equality of two floating-point numbers as <c>double.Equals</c>: a NaN equals a NaN.</summary>
  public const string FloatEquals = "_ply_float_equals";

  /// <summary>A floating-point key with every NaN made the one NaN, which a <c>dict</c> then finds by identity.</summary>
  public const string FloatKey = "_ply_float_key";

  /// <summary>A new array of new structs of a class, one per element.</summary>
  public const string NewStructs = "_ply_new_structs";

  /// <summary>A restartable sequence: an object whose every enumeration calls a generator function anew.</summary>
  public const string Sequence = "_PlySequence";

  /// <summary>The name of an enum's value as .NET's <c>Enum.ToString</c> gives it.</summary>
  public const string EnumFormat = "_ply_enum_format";

  /// <summary>Whether a member of an enum has a value.</summary>
  public const string EnumDefined = "_ply_enum_defined";

  /// <summary>.NET's <c>Enum.Parse</c>.</summary>
  public const string EnumParse = "_ply_enum_parse";

  /// <summary>Whether a value has every bit of a flag.</summary>
  public const string HasFlag = "_ply_has_flag";

  /// <summary>ECMA-262's <c>ToInt32</c> of a number.</summary>
  public const string ToInt32 = "_ply_to_int32";

  /// <summary>ECMA-262's <c>ToUint32</c> of a number.</summary>
  public const string ToUint32 = "_ply_to_uint32";

  /// <summary>
  /// The helpers of the object model, the optional values, the enums and the generic definitions: what
  /// Python has no expression of its own for.
  /// </summary>
  private const string ObjectsSource = """
    def _ply_cast(value: object, kind: object) -> object:
        if value is not None and not isinstance(value, kind):
            raise InvalidCastError("invalid cast")
        return value


    def _ply_caused(error: BaseException, cause: object) -> BaseException:
        error.__cause__ = cause
        return error


    def _ply_message(error: BaseException) -> str:
        if len(error.args) == 0:
            return ""
        return str(error.args[0])


    def _ply_put(cell: list, value: object) -> object:
        cell[0] = value
        return value


    def _ply_throw(error: BaseException) -> object:
        raise error


    def _ply_unwrap(value: object) -> object:
        if value is None:
            raise RuntimeError("missing value")
        return value


    def _ply_difference(left: int, right: int) -> int:
        return left - right


    def _ply_compare_float(left: float, right: float) -> int:
        if left < right:
            return -1
        if left > right:
            return 1
        if left == right:
            return 0
        if left != left:
            return 0 if right != right else -1
        return 1


    def _ply_float_equals(left: float, right: float) -> bool:
        return left == right or left != left and right != right


    def _ply_float_key(value: float) -> float:
        return math.nan if value != value else value


    def _ply_new_structs(kind: object, length: int) -> list:
        items = []
        for _ in range(length):
            items.append(kind())
        return items


    class _PlySequence:
        def __init__(self, make: object, *args: object) -> None:
            self._make = make
            self._args = args

        def __iter__(self) -> object:
            return self._make(*self._args)


    def _ply_enum_format(value: int, members: tuple, flags: bool, bits: int) -> str:
        mask = (1 << bits) - 1
        unsigned = int(value) & mask
        for member in members:
            if member[0] & mask == unsigned:
                return member[1]
        if not flags or unsigned == 0:
            return str(int(value))
        rest = unsigned
        found = []
        for member in reversed(members):
            flag = member[0] & mask
            if rest != 0 and flag != 0 and rest & flag == flag:
                rest = rest & ~flag
                found.append(member[1])
        if rest != 0:
            return str(int(value))
        found.reverse()
        return ", ".join(found)


    def _ply_enum_defined(value: int, members: tuple) -> bool:
        for member in members:
            if member[0] == value:
                return True
        return False


    def _ply_enum_parse(text: str, ignore_case: bool, members: tuple, low: int, high: int) -> int:
        trimmed = _ply_trim_net(text, True, False)
        if trimmed == "":
            raise ValueError("unknown name")
        if trimmed[0] in "0123456789+-":
            try:
                return _ply_parse_int_net(trimmed, low, high)
            except FormatError:
                pass
        mask = high if low == 0 else 2 * high + 1
        bits = 0
        for part in trimmed.split(","):
            name = _ply_trim_net(part, True, True)
            value = None
            for member in members:
                if member[1] == name or ignore_case and member[1].lower() == name.lower():
                    value = member[0]
                    break
            if value is None:
                raise ValueError("unknown name")
            bits = bits | value & mask
        if low < 0 and bits > high:
            bits = bits - 2 * (high + 1)
        return bits


    def _ply_has_flag(value: int, flag: int) -> bool:
        return value & flag == flag


    def _ply_to_int32(value: float) -> int:
        if value != value or math.isinf(value):
            return 0
        bits = int(value) & 0xFFFFFFFF
        return bits - 0x100000000 if bits >= 0x80000000 else bits


    def _ply_to_uint32(value: float) -> int:
        if value != value or math.isinf(value):
            return 0
        return int(value) & 0xFFFFFFFF
    """;

}
