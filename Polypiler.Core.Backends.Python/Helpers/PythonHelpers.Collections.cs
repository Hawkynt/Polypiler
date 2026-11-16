namespace Polypiler.Core.Backends.Python;

internal static partial class PythonHelpers {

  /// <summary>The element of a list at an index; the argument trap outside it.</summary>
  public const string ListGet = "_ply_list_get";

  /// <summary>Writes the element of a list at an index; the argument trap outside it.</summary>
  public const string ListSet = "_ply_list_set";

  /// <summary>Inserts an element into a list at an index up to its length; the argument trap outside.</summary>
  public const string ListInsert = "_ply_list_insert";

  /// <summary>Removes the element of a list at an index; the argument trap outside it.</summary>
  public const string ListRemoveAt = "_ply_list_remove_at";

  /// <summary>Removes the last element of a list and is it, or <c>None</c> for an empty list.</summary>
  public const string ListPop = "_ply_list_pop";

  /// <summary>The first index of a floating-point number by <c>Equals</c>: a NaN is found, -0 is 0.</summary>
  public const string IndexOfFloat = "_ply_index_of_float";

  /// <summary>The first index of a floating-point number by <c>==</c>: a NaN is never found.</summary>
  public const string IndexOfStrict = "_ply_index_of_strict";

  /// <summary>The first index of a value by its equality, or -1.</summary>
  public const string IndexOfValue = "_ply_index_of_value";

  /// <summary>The first index of a value by identity for references and by value otherwise, or -1.</summary>
  public const string IndexOfIdentity = "_ply_index_of_identity";

  /// <summary>Whether a list holds a value by SameValueZero.</summary>
  public const string Includes = "_ply_includes";

  /// <summary>The value of a key; the key trap for an absent one.</summary>
  public const string MapGet = "_ply_map_get";

  /// <summary>Inserts a key or replaces its value; a <c>None</c> result, as a statement.</summary>
  public const string MapPut = "_ply_map_put";

  /// <summary>Inserts a key; the duplicate-key trap for a present one.</summary>
  public const string MapAdd = "_ply_map_add";

  /// <summary>Inserts a key where it is absent and says whether it did.</summary>
  public const string MapTryAdd = "_ply_map_try_add";

  /// <summary>Removes a key and says whether it was present.</summary>
  public const string MapRemove = "_ply_map_remove";

  /// <summary>A map or a set in the order of .NET's slots: a new key takes the slot a removal freed last.</summary>
  public const string Slots = "_PlySlots";

  /// <summary>A list that counts its changes, for the checked loops over it.</summary>
  public const string VersionedList = "_PlyList";

  /// <summary>The elements of a list, trapping at the step after a change of it, as .NET's enumerator does.</summary>
  public const string EachChecked = "_ply_each_checked";

  /// <summary>The keys of a map or a set, trapping at the step after a new key, skipping removed ones, as .NET's enumerator does.</summary>
  public const string EachKey = "_ply_each_key";

  /// <summary>The keys of a map or a set as ECMA-262's iterators visit them: added ones too, removed ones not.</summary>
  public const string EachLiveKey = "_ply_each_live_key";

  /// <summary>The operations on lists, maps and sets where Python counts from the end, raises otherwise or has no single call.</summary>
  private const string CollectionsSource = """
    def _ply_list_get(items: list, index: int) -> object:
        if index < 0 or index >= len(items):
            raise ArgumentOutOfRangeError("argument out of range")
        return items[index]


    def _ply_list_set(items: list, index: int, value: object) -> None:
        if index < 0 or index >= len(items):
            raise ArgumentOutOfRangeError("argument out of range")
        items[index] = value


    def _ply_list_insert(items: list, index: int, value: object) -> None:
        if index < 0 or index > len(items):
            raise ArgumentOutOfRangeError("argument out of range")
        items.insert(index, value)


    def _ply_list_remove_at(items: list, index: int) -> None:
        if index < 0 or index >= len(items):
            raise ArgumentOutOfRangeError("argument out of range")
        items.pop(index)


    def _ply_list_pop(items: list) -> object:
        if len(items) == 0:
            return None
        return items.pop()


    def _ply_index_of_float(items: list, value: float) -> int:
        for index in range(len(items)):
            if items[index] == value or items[index] != items[index] and value != value:
                return index
        return -1


    def _ply_index_of_strict(items: list, value: float) -> int:
        for index in range(len(items)):
            if items[index] == value:
                return index
        return -1


    def _ply_index_of_value(items: list, value: object) -> int:
        for index in range(len(items)):
            if items[index] == value:
                return index
        return -1


    def _ply_index_of_identity(items: list, value: object) -> int:
        for index in range(len(items)):
            if items[index] is value or items[index] == value and not hasattr(value, "__dict__"):
                return index
        return -1


    def _ply_includes(items: list, value: object) -> bool:
        return _ply_index_of_identity(items, value) >= 0


    def _ply_map_get(items: dict, key: object) -> object:
        if key not in items:
            raise KeyError("key not found")
        return items[key]


    def _ply_map_put(items: dict, key: object, value: object) -> None:
        items[key] = value


    def _ply_map_add(items: dict, key: object, value: object) -> None:
        if key in items:
            raise ValueError("duplicate key")
        items[key] = value


    def _ply_map_try_add(items: dict, key: object, value: object) -> bool:
        if key in items:
            return False
        items[key] = value
        return True


    def _ply_map_remove(items: dict, key: object) -> bool:
        if key not in items:
            return False
        items.pop(key)
        return True


    class _PlySlots(dict):
        def __init__(self) -> None:
            dict.__init__(self)
            self._slots = []
            self._free = []

        def __setitem__(self, key: object, value: object) -> None:
            if key not in self:
                if len(self._free) > 0:
                    self._slots[self._free.pop()] = key
                else:
                    self._slots.append(key)
            dict.__setitem__(self, key, value)

        def pop(self, key: object, *args: object) -> object:
            if key in self:
                index = self._slots.index(key)
                self._slots[index] = None
                self._free.append(index)
            return dict.pop(self, key, *args)

        def clear(self) -> None:
            dict.clear(self)
            self._slots = []
            self._free = []

        def __iter__(self) -> object:
            for key in list(self._slots):
                if key is not None:
                    yield key

        def keys(self) -> object:
            return list(self)

        def values(self) -> object:
            return list(map(self.__getitem__, self))

        def items(self) -> object:
            return list(zip(self, map(self.__getitem__, self)))


    class _PlyList(list):
        version = 0

        def _changed(self) -> None:
            self.version = self.version + 1

        def append(self, value: object) -> None:
            self._changed()
            list.append(self, value)

        def insert(self, index: int, value: object) -> None:
            self._changed()
            list.insert(self, index, value)

        def pop(self, *args: object) -> object:
            self._changed()
            return list.pop(self, *args)

        def clear(self) -> None:
            self._changed()
            list.clear(self)

        def __setitem__(self, index: object, value: object) -> None:
            self._changed()
            list.__setitem__(self, index, value)

        def __delitem__(self, index: object) -> None:
            self._changed()
            list.__delitem__(self, index)


    def _ply_each_checked(items: list) -> object:
        version = getattr(items, "version", 0)
        index = 0
        while True:
            if getattr(items, "version", 0) != version:
                raise RuntimeError("collection modified")
            if index >= len(items):
                return
            yield items[index]
            index = index + 1


    def _ply_each_key(items: dict) -> object:
        keys = list(items)
        known = set(keys)
        for key in keys:
            for present in items:
                if present not in known:
                    raise RuntimeError("collection modified")
            if key in items:
                yield key
        for present in items:
            if present not in known:
                raise RuntimeError("collection modified")


    def _ply_each_live_key(items: dict) -> object:
        seen = set()
        while True:
            following = None
            for key in items:
                if key not in seen:
                    following = key
                    break
            if following is None:
                return
            seen.add(following)
            yield following
    """;

}
