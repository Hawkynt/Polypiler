namespace Polypiler.Core.Backends.Python;

internal static partial class PythonHelpers {

  /// <summary>The class of a view: a list, the index of its first element and a length, or the bytes of a list read with a <c>struct</c> format.</summary>
  public const string View = "_PlyView";

  /// <summary>A view of a list or of another view, its whole or a part of it; the argument trap outside it.</summary>
  public const string ViewOf = "_ply_view_of";

  /// <summary>Copies a range between two lists through a copy of the source; the bounds trap before anything is copied.</summary>
  public const string ArrayCopy = "_ply_array_copy";

  /// <summary>Stores a value in every element of a list or a view.</summary>
  public const string ArrayFill = "_ply_fill";

  /// <summary>Copies a view to the start of another through a copy; the argument trap, or false, when the target is shorter.</summary>
  public const string ViewCopy = "_ply_view_copy";

  /// <summary>Whether two views have the same elements.</summary>
  public const string ViewEqual = "_ply_view_equal";

  /// <summary>Whether two views of floating-point numbers have equal elements by <c>Equals</c>.</summary>
  public const string ViewEqualFloat = "_ply_view_equal_float";

  /// <summary>The bytes of a view read as elements of another number type.</summary>
  public const string Reinterpret = "_ply_reinterpret";

  /// <summary>Bytes of a list or a view from an offset on; the bounds trap for a range outside it.</summary>
  public const string Bytes = "_ply_bytes";

  /// <summary>Writes bytes into a list or a view from an offset on; the bounds trap before anything is written.</summary>
  public const string PutBytes = "_ply_put_bytes";

  /// <summary>
  /// A view of the IR, which Python has no type for: a <c>memoryview</c> needs a buffer, and the
  /// arrays are lists. The class checks every index against its own length, as a span does.
  /// </summary>
  private const string ViewsSource = """
    def _ply_read_bytes(array: list, source: str, start: int, count: int) -> bytes:
        width = struct.calcsize("<" + source)
        first = start // width
        data = bytes()
        for index in range(first, (start + count - 1) // width + 1):
            data = data + struct.pack("<" + source, array[index])
        return data[start - first * width:start - first * width + count]


    def _ply_write_bytes(array: list, source: str, start: int, data: bytes) -> None:
        width = struct.calcsize("<" + source)
        first = start // width
        last = (start + len(data) - 1) // width
        buffer = bytearray(_ply_read_bytes(array, source, first * width, (last - first + 1) * width))
        buffer[start - first * width:start - first * width + len(data)] = data
        for index in range(first, last + 1):
            at = (index - first) * width
            array[index] = struct.unpack("<" + source, bytes(buffer[at:at + width]))[0]


    class _PlyView:
        def __init__(self, array: list, start: int, length: int) -> None:
            self.array = array
            self.start = start
            self.length = length
            self.code = ""
            self.source = ""
            self.size = 1

        def __len__(self) -> int:
            return self.length

        def _at(self, index: int) -> int:
            if index < 0 or index >= self.length:
                raise IndexError("index out of range")
            return self.start + index * self.size

        def __getitem__(self, index: int) -> object:
            at = self._at(index)
            if self.code == "":
                return self.array[at]
            return struct.unpack("<" + self.code, _ply_read_bytes(self.array, self.source, at, self.size))[0]

        def __setitem__(self, index: int, value: object) -> None:
            at = self._at(index)
            if self.code == "":
                self.array[at] = value
            else:
                _ply_write_bytes(self.array, self.source, at, struct.pack("<" + self.code, value))

        def __iter__(self) -> object:
            for index in range(self.length):
                yield self[index]


    def _ply_view_of(source: object, start: int = 0, length: object = None) -> _PlyView:
        count = len(source)
        if length is None:
            length = count - start
        if start < 0 or length < 0 or start + length > count:
            raise ArgumentOutOfRangeError("argument out of range")
        if not isinstance(source, _PlyView):
            return _PlyView(source, start, length)
        view = _PlyView(source.array, source.start + start * source.size, length)
        view.code = source.code
        view.source = source.source
        view.size = source.size
        return view


    def _ply_array_copy(source: list, source_index: int, target: list, target_index: int, length: int) -> None:
        if length < 0 or source_index < 0 or target_index < 0 or source_index + length > len(source) or target_index + length > len(target):
            raise IndexError("index out of range")
        target[target_index:target_index + length] = source[source_index:source_index + length]


    def _ply_fill(items: object, value: object) -> None:
        for index in range(len(items)):
            items[index] = value


    def _ply_view_copy(source: object, target: object, attempt: bool) -> bool:
        if len(target) < len(source):
            if attempt:
                return False
            raise ArgumentOutOfRangeError("argument out of range")
        values = list(source)
        for index in range(len(values)):
            target[index] = values[index]
        return True


    def _ply_view_equal(left: object, right: object) -> bool:
        return list(left) == list(right)


    def _ply_view_equal_float(left: object, right: object) -> bool:
        if len(left) != len(right):
            return False
        for index in range(len(left)):
            if not _ply_float_equals(left[index], right[index]):
                return False
        return True


    def _ply_reinterpret(source: object, code: str, target: str) -> _PlyView:
        whole = _ply_view_of(source)
        size = struct.calcsize("<" + target)
        if whole.code == "":
            width = struct.calcsize("<" + code)
            view = _PlyView(whole.array, whole.start * width, whole.length * width // size)
            view.source = code
        else:
            view = _PlyView(whole.array, whole.start, whole.length * whole.size // size)
            view.source = whole.source
        view.code = target
        view.size = size
        return view


    def _ply_bytes(buffer: object, offset: int, size: int) -> bytes:
        if offset < 0 or offset + size > len(buffer):
            if isinstance(buffer, _PlyView):
                raise ArgumentOutOfRangeError("argument out of range")
            raise IndexError("index out of range")
        if not isinstance(buffer, _PlyView):
            return bytes(buffer[offset:offset + size])
        if buffer.code == "":
            return bytes(buffer.array[buffer.start + offset:buffer.start + offset + size])
        return _ply_read_bytes(buffer.array, buffer.source, buffer.start + offset, size)


    def _ply_put_bytes(buffer: object, offset: int, data: bytes) -> None:
        if offset < 0 or offset + len(data) > len(buffer):
            if isinstance(buffer, _PlyView):
                raise ArgumentOutOfRangeError("argument out of range")
            raise IndexError("index out of range")
        if not isinstance(buffer, _PlyView):
            buffer[offset:offset + len(data)] = data
        elif buffer.code == "":
            buffer.array[buffer.start + offset:buffer.start + offset + len(data)] = data
        else:
            _ply_write_bytes(buffer.array, buffer.source, buffer.start + offset, data)
    """;

}
