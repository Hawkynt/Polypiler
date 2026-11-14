// A journal of letters kept in a string between calls, with a ring of the last four texts it held.
// Module state of the type string and of an array of strings. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Journal {

  private static string _text = "";
  private static readonly string[] _ring = { "", "", "", "" };
  private static int _count;

  public static int Add(int code) {
    _text = _text + (char)code;
    _ring[_count & 3] = _text;
    ++_count;
    return _text.Length;
  }

  public static string Back(int steps) => _ring[(_count - steps) & 3];

}
