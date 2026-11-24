using System;

public static class OutputEffect {
  public static int Run(bool choose) {
    Console.WriteLine("first");
    if (choose)
      Emit("");
    Emit("second");
    return 42;
  }

  public static int Trap(int divisor) {
    Console.WriteLine("first");
    Emit(Text(divisor));
    return 42;
  }

  private static void Emit(string text) => Console.WriteLine(text);
  private static string Text(int divisor) => 42 / divisor != 0 ? "done" : "zero";
}
