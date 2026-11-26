// Showcase: User-Defined Operator Overloading (Boundary)
// Category: Unsupported Boundaries
// Summary: Custom operator overloads on structs; explicitly refused until frontend lowering lands.
public readonly struct Money {
  public readonly int Cents;
  public Money(int cents) => this.Cents = cents;
  public static Money operator +(Money a, Money b) => new(a.Cents + b.Cents);
}

public static class OperatorOverloadingBoundary {
  public static int Total(int a, int b) => (new Money(a) + new Money(b)).Cents;
}
