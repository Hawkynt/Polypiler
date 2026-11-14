// Words of a command line: split at blanks and commas, trimmed, joined again and scored by a switch
// on each word. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Tokens {

  private static readonly string[] Names = { "add", "sub", "mul" };

  public static int Score(string line) {
    string[] words = line.Trim().Split(' ');
    int score = 0;
    foreach (string word in words) {
      switch (word.Trim()) {
        case "add":
        case "plus":
          score += 1;
          break;
        case "sub":
          score -= 1;
          break;
        case "":
          break;
        default:
          score += 100;
          break;
      }
    }

    return score;
  }

  public static string Normalize(string line) {
    string[] parts = line.Split(',');
    for (int index = 0; index < parts.Length; index++)
      parts[index] = parts[index].Trim();

    return string.Join(";", parts);
  }

  public static string Keyword(int index) => Names[index];

  public static bool IsKeyword(string word) {
    foreach (string name in Names)
      if (name == word)
        return true;

    return false;
  }

}
