namespace Polypiler.Core.Backends.Python;

internal static partial class PythonHelpers {

  /// <summary>The class of a pointer: a region - a list of numbers - and a byte offset in it.</summary>
  public const string Pointer = "_PlyPointer";

  /// <summary>The pointer to the first element of a list or a view, or <c>None</c> for none or no elements.</summary>
  public const string Pin = "_ply_pin";

  /// <summary>The pointer to an element of a list or a view, which has to exist.</summary>
  public const string PinAt = "_ply_pin_at";

  /// <summary>A pointer some elements on.</summary>
  public const string PointerAdd = "_ply_pointer_add";

  /// <summary>The bytes between two pointers of one region, divided by the size of their elements.</summary>
  public const string PointerDifference = "_ply_pointer_difference";

  /// <summary>Whether two pointers are the same place.</summary>
  public const string PointerEqual = "_ply_pointer_equal";

  /// <summary>The bytes from one pointer to another of the same region; the invalid-operation trap for two.</summary>
  public const string PointerDistance = "_ply_pointer_distance";

  /// <summary>The value at a pointer, read as a <c>struct</c> format.</summary>
  public const string PointerLoad = "_ply_pointer_load";

  /// <summary>Writes a value at a pointer as a <c>struct</c> format.</summary>
  public const string PointerStore = "_ply_pointer_store";

  /// <summary>
  /// A pointer of the IR, which Python has no form of: a region - a list, the index of its first
  /// element, how many it has and the <c>struct</c> format of one - and a byte offset in it. A load or a
  /// store of the region's own type at a whole element is the element itself; any other one goes
  /// through the bytes of the elements it covers.
  /// </summary>
  private const string PointersSource = """
    class _PlyPointer:
        def __init__(self, array: list, base: int, count: int, code: str, offset: int) -> None:
            self.array = array
            self.base = base
            self.count = count
            self.code = code
            self.offset = offset


    def _ply_pin(source: object, code: str) -> object:
        if source is None or len(source) == 0:
            return None
        if isinstance(source, _PlyView):
            return _PlyPointer(source.array, source.start, source.length, code, 0)
        return _PlyPointer(source, 0, len(source), code, 0)


    def _ply_pin_at(source: object, index: int, code: str) -> object:
        if source is None:
            raise AttributeError("null reference")
        if index < 0 or index >= len(source):
            raise IndexError("index out of range")
        pointer = _ply_pin(source, code)
        pointer.offset = index * struct.calcsize("<" + code)
        return pointer


    def _ply_pointer_of(pointer: object) -> _PlyPointer:
        return _PlyPointer(None, 0, 0, "B", 0) if pointer is None else pointer


    def _ply_pointer_add(pointer: object, count: int, size: int) -> object:
        start = _ply_pointer_of(pointer)
        return _PlyPointer(start.array, start.base, start.count, start.code, start.offset + count * size)


    def _ply_pointer_distance(left: object, right: object) -> int:
        first = _ply_pointer_of(left)
        second = _ply_pointer_of(right)
        if first.array is not second.array:
            raise ArithmeticError("invalid operation")
        width = struct.calcsize("<" + first.code)
        return first.base * width + first.offset - second.base * width - second.offset


    def _ply_pointer_difference(left: object, right: object, size: int) -> int:
        distance = _ply_pointer_distance(left, right)
        return -(-distance // size) if distance < 0 else distance // size


    def _ply_pointer_equal(left: object, right: object) -> bool:
        if _ply_pointer_of(left).array is not _ply_pointer_of(right).array:
            return False
        return _ply_pointer_distance(left, right) == 0


    def _ply_pointer_load(pointer: object, code: str) -> object:
        if pointer is None or pointer.array is None:
            raise AttributeError("null reference")
        size = struct.calcsize("<" + code)
        width = struct.calcsize("<" + pointer.code)
        if pointer.offset < 0 or pointer.offset + size > pointer.count * width:
            raise IndexError("index out of range")
        if code == pointer.code and pointer.offset % width == 0:
            return pointer.array[pointer.base + pointer.offset // width]
        data = _ply_read_bytes(pointer.array, pointer.code, pointer.base * width + pointer.offset, size)
        return struct.unpack("<" + code, data)[0]


    def _ply_pointer_store(pointer: object, code: str, value: object) -> None:
        if pointer is None or pointer.array is None:
            raise AttributeError("null reference")
        size = struct.calcsize("<" + code)
        width = struct.calcsize("<" + pointer.code)
        if pointer.offset < 0 or pointer.offset + size > pointer.count * width:
            raise IndexError("index out of range")
        if code == pointer.code and pointer.offset % width == 0:
            pointer.array[pointer.base + pointer.offset // width] = value
        else:
            _ply_write_bytes(pointer.array, pointer.code, pointer.base * width + pointer.offset, struct.pack("<" + code, value))
    """;

}
