// Showcase: Enums & Nullable Optionals
// Category: Types & Data
// Summary: Flags enum bit operations and nullable scalar types with null-coalescing and lifted arithmetic.
using System;

[Flags]
public enum Permissions {
  None = 0,
  Read = 1,
  Write = 2,
  Execute = 4
}

public static class EnumsAndOptionalsShowcase {
  public static int Resolve(Permissions flags, int? overrideValue) {
    int baseScore = flags.HasFlag(Permissions.Write) ? 10 : 1;
    int? adjusted = overrideValue + baseScore;
    return adjusted ?? baseScore;
  }
}
