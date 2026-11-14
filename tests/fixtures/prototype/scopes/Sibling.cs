// Two sibling blocks that each declare t, one int and one double: a name C# allows twice. Written
// for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Sibling {

  public static int F(int n) {
    int s = 0;
    if (n > 0) {
      int t = n * 2;
      s += t;
    } else {
      double t = n * 0.5;
      s += t < -1.0 ? 1 : 0;
    }

    return s;
  }

}
