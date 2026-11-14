// Record structs and their field-wise equality: an amount in cents with a currency code, and a
// pair of doubles, whose equality is double.Equals - a NaN equals a NaN, +0.0 equals -0.0.
// Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public record struct Money(long Cents, int Currency);

public static class Ledger {

  public readonly record struct P2(double X, double Y);

  public const int Euro = 978;

  public static bool Same(long a, long b) => new Money(a, Euro) == new Money(b, Euro);

  public static bool Differs(long a, int currency) => new Money(a, Euro) != new Money(a, currency);

  public static bool EqualsCall(long a, long b) => new Money(a, Euro).Equals(new Money(b, Euro));

  // True: P2(NaN, 0.0) equals P2(NaN, -0.0).
  public static bool NaNEqual() => new P2(double.NaN, 0.0) == new P2(double.NaN, -0.0);

  public static bool PairEqual(double x1, double y1, double x2, double y2) => new P2(x1, y1) == new P2(x2, y2);

  // Adds an amount in place and reads it back: a record struct that is not read-only can be written.
  public static long Deposit(long cents, long more) {
    var account = new Money(cents, Euro);
    account.Cents += more;
    var copy = account;
    copy.Cents = 0;
    return account.Cents + copy.Cents;
  }

}
