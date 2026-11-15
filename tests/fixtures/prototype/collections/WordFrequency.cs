// A histogram of 1000 pseudo-random words drawn by xorshift32 from the seed 2026, with a word removed
// every 97 draws so that slots are freed and taken again, printed in the order .NET enumerates the
// dictionary. Written for this repository.
#nullable disable
using System.Collections.Generic;

namespace Polypiler.Fixtures.Prototype.Collections;

public static class WordFrequencyDemo {

  private static readonly string[] Words = [
    "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliett", "kilo", "lima", "mike",
    "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu"
  ];

  private static string Number(int value) {
    if (value == 0)
      return "0";

    var text = "";
    while (value > 0) {
      text = ((char)('0' + value % 10)).ToString() + text;
      value /= 10;
    }

    return text;
  }

  public static string Histogram(int draws) {
    var counts = new Dictionary<string, int>();
    var state = 2026u;
    for (var draw = 0; draw < draws; ++draw) {
      state ^= state << 13;
      state ^= state >> 17;
      state ^= state << 5;
      var word = Words[(int)(state % (uint)Words.Length)];
      if (counts.ContainsKey(word))
        counts[word] += 1;
      else
        counts[word] = 1;

      if (draw % 97 == 96)
        counts.Remove(Words[(int)(state % 7u)]);
    }

    var histogram = "";
    foreach (var entry in counts)
      histogram += entry.Key + "=" + Number(entry.Value) + ";";

    return histogram;
  }

}
