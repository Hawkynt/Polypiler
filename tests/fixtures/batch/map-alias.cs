using System.Collections.Generic;

public static class Kernel {
  public static int Run() {
    Dictionary<string, int> ages = new() {
      ["Alice"] = 25,
      ["alice"] = 5,
      [""] = 7,
      ["a% !&|<>^[]"] = 4,
      ["zero"] = 0
    };
    var alias = ages;
    Dictionary<string, int> other = new() { ["Alice"] = 1 };
    alias["Alice"] += ages["alice"] + ages[""];
    if (!ages.ContainsKey("zero") || ages.ContainsKey("ALICE"))
      return 99;
    return ages["Alice"] + ages["a% !&|<>^[]"] + other["Alice"];
  }
}
