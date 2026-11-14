using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Tooling.Commands;

namespace Polypiler.Tooling.Permutations;

/// <summary>
/// <c>polypiler-tooling permutations import-bitperm [--fixtures &lt;dir&gt;] [--out &lt;dir&gt;] [--check]</c>
/// turns the BitPerm fixtures of <c>tooling/bitperm/export.mjs</c> (<c>tests/fixtures/bitperm</c> by
/// default) into documents of the <c>permutations</c> spec family (<c>spec/permutations</c> by default):
/// one mapping per fixture mapping, one cost table per CPU preset and the four grammars of BitPerm's
/// solutions. Every generated file is named <c>bitperm-*.yaml</c>; other documents of the output folders
/// are left alone. Without <c>--check</c> it writes the files that changed and deletes generated files no
/// longer produced; with <c>--check</c> it writes nothing and reports every file that differs, is missing
/// or is no longer produced. Output is deterministic: ordinal file order, LF line endings.
/// </summary>
public static class ImportBitPermCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "permutations";

  /// <summary>The command of <see cref="Verb"/> this class implements.</summary>
  public const string Import = "import-bitperm";

  /// <summary>The default fixture directory, relative to the current directory.</summary>
  public const string DefaultFixtures = "tests/fixtures/bitperm";

  /// <summary>The default output directory, relative to the current directory.</summary>
  public const string DefaultOut = "spec/permutations";

  /// <summary>BitPerm's cost of an operation a CPU does not have.</summary>
  public const int Unavailable = 999;

  /// <summary>The prefix of every generated file name and of the name part of every generated ID.</summary>
  public const string Prefix = "bitperm-";

  /// <summary>The prefix of every message the importer reports.</summary>
  private const string Reporter = "permutations import";

  /// <summary>The primitives of the permutations schema each BitPerm cost key stands for, in BitPerm's key order.</summary>
  public static IReadOnlyList<KeyValuePair<string, string[]>> CostKeys { get; } = [
    new("shift", ["shift"]),
    new("rotate", ["rotate"]),
    new("mul", ["multiply"]),
    new("bmi", ["pext", "pdep"]),
    new("pshufb", ["byte-shuffle"]),
    new("lut", ["lookup-table"]),
    new("basic", ["and", "or", "xor", "not"]),
    new("rlwinm", ["rotate-mask"]),
    new("bfx", ["bit-field-insert", "bit-field-extract"]),
    new("rev", ["byte-swap"]),
    new("rbit", ["bit-reverse"]),
  ];

  private static readonly JsonSerializerOptions _strings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

  /// <summary>Runs the arguments that follow the verb in the process's current directory.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the problems and the refusal of a command line.</param>
  /// <returns>0 on success, 1 when the fixtures cannot be imported or the output is out of date, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    => Run(args, output, error, Environment.CurrentDirectory);

  /// <summary>Runs the arguments that follow the verb, with relative paths taken from a directory of the caller's.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the problems and the refusal of a command line.</param>
  /// <param name="currentDirectory">The directory relative paths and the defaults are taken from.</param>
  /// <returns>0 on success, 1 when the fixtures cannot be imported or the output is out of date, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, string currentDirectory) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    if (args.Count == 0 || args[0] != Import)
      return Usage(error, args.Count == 0 ? $"{Verb} needs '{Import}'" : $"unknown {Verb} command '{args[0]}'");

    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var check = false;
    for (var index = 1; index < args.Count; ++index) {
      var option = args[index];
      if (option == "--check") {
        if (check)
          return Usage(error, "option '--check' is given more than once");
        check = true;
        continue;
      }

      if (option is not ("--fixtures" or "--out"))
        return Usage(error, $"unknown option '{option}'");
      if (index + 1 == args.Count)
        return Usage(error, $"option '{option}' requires a value");
      if (!values.TryAdd(option, args[++index]))
        return Usage(error, $"option '{option}' is given more than once");
    }

    var shown = values.GetValueOrDefault("--out") ?? DefaultOut;
    var target = Path.Combine(currentDirectory, shown);
    IReadOnlyList<GeneratedFile> files;
    try {
      files = Generate(Path.Combine(currentDirectory, values.GetValueOrDefault("--fixtures") ?? DefaultFixtures));
    } catch (Exception exception) when (exception is BitPermImportException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or PermutationModelException) {
      error.WriteLine($"{Reporter}: {exception.Message}");
      return Program.Failure;
    }

    return check ? Check(files, shown, target, output, error) : Write(files, shown, target, output, error);
  }

  /// <summary>Every document the fixtures yield, by its path relative to the output directory, in ordinal order.</summary>
  /// <param name="fixtures">The directory of the BitPerm fixtures: <c>pin.json</c>, <c>presets.json</c>, <c>limits.json</c>, <c>mappings/</c> and <c>vectors/</c>.</param>
  /// <returns>The generated files, with LF line endings.</returns>
  /// <exception cref="BitPermImportException">A preset has a cost key outside <see cref="CostKeys"/>, or a fixture lacks what a document needs.</exception>
  public static IReadOnlyList<GeneratedFile> Generate(string fixtures) {
    ArgumentNullException.ThrowIfNull(fixtures);
    using var pinDocument = Load(Path.Combine(fixtures, "pin.json"));
    var pin = Pin.From(pinDocument.RootElement);
    using var limits = Load(Path.Combine(fixtures, "limits.json"));
    using var presets = Load(Path.Combine(fixtures, "presets.json"));

    var files = new List<GeneratedFile>();
    var mappings = Path.Combine(fixtures, "mappings");
    foreach (var file in Directory.GetFiles(mappings, "*.json").Order(StringComparer.Ordinal))
      files.Add(MappingDocument(file, Path.Combine(fixtures, "vectors", Path.GetFileName(file)), pin));
    foreach (var preset in presets.RootElement.EnumerateArray())
      files.Add(CostDocument(preset, pin));
    files.AddRange(GrammarDocuments(Property(limits.RootElement, "MAX_SOLVER_ITERATIONS", "limits.json").GetInt32()));
    return [.. files.OrderBy(file => file.Path, StringComparer.Ordinal)];
  }

  /// <summary>The primitives and cycles a preset's costs contribute: every key by <see cref="CostKeys"/>, without the unavailable ones.</summary>
  /// <param name="preset">A preset of <c>presets.json</c>: <c>id</c>, <c>costs</c> and <c>enables</c>.</param>
  /// <returns>The pairs of primitive and cycles, in BitPerm's key order.</returns>
  /// <exception cref="BitPermImportException">A cost key is not in <see cref="CostKeys"/>.</exception>
  public static IReadOnlyList<KeyValuePair<string, int>> Costs(JsonElement preset) {
    var id = Property(preset, "id", "presets.json").GetString()!;
    var costs = Property(preset, "costs", $"preset '{id}'");
    var enables = Property(preset, "enables", $"preset '{id}'");
    var known = CostKeys.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    foreach (var cost in costs.EnumerateObject())
      if (!known.Contains(cost.Name))
        throw new BitPermImportException($"preset '{id}' has unknown cost key '{cost.Name}'");

    var result = new List<KeyValuePair<string, int>>();
    foreach (var (key, primitives) in CostKeys) {
      if (!costs.TryGetProperty(key, out var cost))
        continue;

      var cycles = cost.GetInt32();
      var enabled = !enables.TryGetProperty(key, out var flag) || flag.GetBoolean();
      if (cycles == Unavailable || !enabled)
        continue;

      result.AddRange(primitives.Select(primitive => new KeyValuePair<string, int>(primitive, cycles)));
    }

    return result;
  }

  private static GeneratedFile MappingDocument(string mappingFile, string vectorFile, Pin pin) {
    var fixtureName = Path.GetFileNameWithoutExtension(mappingFile);
    using var stream = File.OpenRead(mappingFile);
    var mapping = BitPermState.Read(stream, fixtureName);
    using var fixture = Load(mappingFile);
    var line = Property(Property(fixture.RootElement, "source", fixtureName), "line", fixtureName).GetInt32();

    using var buffer = new MemoryStream();
    BitMappingDocument.Write(mapping, buffer);
    var outputs = JsonNode.Parse(buffer.ToArray())!["outputs"]!.AsArray();

    var name = Prefix + fixtureName;
    var text = Header("mapping", name, $"BitPerm mapping {fixtureName}",
      $"The mapping of BitPerm fixture mappings/{fixtureName}.json, exported by tooling/bitperm/export.mjs from {pin.Page} line {line.ToString(CultureInfo.InvariantCulture)}; the vectors are BitPerm's raw evaluation of it, from vectors/{fixtureName}.json.");
    text.Append("  kind: mapping\n");
    text.Append("  inputWidth: ").Append(mapping.InputWidth.ToString(CultureInfo.InvariantCulture)).Append('\n');
    text.Append("  outputs: ").Append(FlowList(outputs.Select(Entry).ToArray())).Append('\n');
    text.Append("  bijective: ").Append(mapping.IsBijective ? "true" : "false").Append('\n');
    text.Append("  vectors:\n");
    foreach (var vector in Vectors(vectorFile, fixtureName, mapping.InputWidth)) {
      text.Append("    - {input: \"").Append(vector.Input).Append("\", output: \"").Append(vector.Output).Append('"');
      if (mapping.HasDontCares)
        text.Append(", mask: \"").Append(vector.Mask).Append('"');
      text.Append("}\n");
    }

    return new($"mappings/{name}.yaml", text.ToString());
  }

  /// <summary>The vectors of a mapping document, taken from BitPerm's: input 0, all ones, then the first two single-bit inputs.</summary>
  private static IEnumerable<(string Input, string Output, string Mask)> Vectors(string vectorFile, string fixtureName, int width) {
    using var document = Load(vectorFile);
    var where = $"vectors/{fixtureName}.json";
    var vectors = Property(document.RootElement, "vectors", where).EnumerateArray()
      .Select(vector => (Input: Property(vector, "input", where).GetString()!, Output: Property(vector, "output", where).GetString()!, Mask: Property(vector, "mask", where).GetString()!))
      .ToArray();
    var values = vectors.Select(vector => BigInteger.Parse("0" + vector.Input[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)).ToArray();
    var allOnes = (BigInteger.One << width) - 1;
    var singles = Enumerable.Range(0, vectors.Length).Where(index => values[index] != 0 && (values[index] & (values[index] - 1)) == 0).Take(2).ToArray();
    int Find(BigInteger value, string what) {
      var index = Array.IndexOf(values, value);
      return index >= 0 ? index : throw new BitPermImportException($"{where} has no vector for {what}");
    }

    if (singles.Length < 2)
      throw new BitPermImportException($"{where} has fewer than two single-bit inputs");
    return new[] { Find(BigInteger.Zero, "input 0"), Find(allOnes, "the all-ones input"), singles[0], singles[1] }.Select(index => vectors[index]).ToArray();
  }

  private static GeneratedFile CostDocument(JsonElement preset, Pin pin) {
    var id = Property(preset, "id", "presets.json").GetString()!;
    var cpu = Property(preset, "name", $"preset '{id}'").GetString()!;
    var comment = preset.TryGetProperty("comment", out var given) ? given.GetString()! : "";
    var costs = Costs(preset);
    var name = Prefix + id;
    var reference = $"BitPerm PRESETS at {pin.Short}" + (comment.Length == 0 ? "" : $", citing {comment}");
    var text = Header("cost", name, $"BitPerm cost preset {cpu}",
      $"The latencies BitPerm's preset '{id}' assigns, from presets.json exported by tooling/bitperm/export.mjs from {pin.Page}. BitPerm's cost keys map to primitives by the importer's key table (shift, rotate, mul: multiply, bmi: pext and pdep, pshufb: byte-shuffle, lut: lookup-table, basic: and, or, xor and not, rlwinm: rotate-mask, bfx: bit-field-insert and bit-field-extract, rev: byte-swap, rbit: bit-reverse); an operation BitPerm marks unavailable (cost {Unavailable.ToString(CultureInfo.InvariantCulture)} or disabled) has no entry. Values are copied, not re-derived; byte sizes are not known.");
    text.Append("  kind: cost\n");
    text.Append("  cpu: ").Append(Quoted(cpu)).Append('\n');
    text.Append("  costs:\n");
    foreach (var (primitive, cycles) in costs)
      text.Append("    - {primitive: ").Append(primitive).Append(", cycles: ").Append(cycles.ToString(CultureInfo.InvariantCulture)).Append("}\n");
    text.Append("  evidence:\n");
    text.Append("    kind: documented\n");
    text.Append("    reference: ").Append(Quoted(reference)).Append('\n');
    return new($"costs/{name}.yaml", text.ToString());
  }

  private static IEnumerable<GeneratedFile> GrammarDocuments(int maxSolverIterations) {
    (string Name, string Title, string[] Primitives, int Depth, string Mirrors)[] grammars = [
      ("composite", "BitPerm composite solver", ["shift", "rotate", "and", "or", "xor", "multiply"], maxSolverIterations,
        $"BitPerm's greedy and exhaustive composite solver: networks of shifts, rotations, masks and multiplications, at most MAX_SOLVER_ITERATIONS ({maxSolverIterations.ToString(CultureInfo.InvariantCulture)}) steps long"),
      ("bmi2", "BitPerm BMI2 solution", ["pext", "pdep", "or", "xor"], 4,
        "BitPerm's \"BMI2 (PEXT/PDEP)\" solution: a parallel extract and deposit, followed by the constant fix-up"),
      ("byte-shuffle", "BitPerm byte-shuffle solution", ["byte-shuffle"], 1,
        "BitPerm's \"PSHUFB\" solution: one byte shuffle"),
      ("lut", "BitPerm lookup-table solution", ["lookup-table"], 1,
        "BitPerm's \"LUT\" solution: one table lookup"),
    ];
    foreach (var (grammar, title, primitives, depth, mirrors) in grammars) {
      var name = Prefix + grammar;
      var text = Header("grammar", name, title,
        $"Mirrors {mirrors}. The grammar is target-neutral. A network built from it MUST be shown equivalent to its mapping as spec/permutations/proof-requirements.md requires before it is recorded.");
      text.Append("  kind: grammar\n");
      text.Append("  primitives: ").Append(FlowList(primitives)).Append('\n');
      text.Append("  maxDepth: ").Append(depth.ToString(CultureInfo.InvariantCulture)).Append('\n');
      yield return new($"grammars/{name}.yaml", text.ToString());
    }
  }

  /// <summary>The metadata block of a generated document, up to and including the line <c>spec:</c>.</summary>
  private static StringBuilder Header(string kind, string name, string title, string normative) => new StringBuilder()
    .Append("# Generated by polypiler-tooling permutations import-bitperm; edit the fixtures or the importer, not this file.\n")
    .Append("id: PERMUTATIONS.").Append(kind).Append('.').Append(name).Append('\n')
    .Append("title: ").Append(Quoted(title)).Append('\n')
    .Append("version: 1\n")
    .Append("status: draft\n")
    .Append("owner: Hawkynt\n")
    .Append("issue: PERM-SPEC\n")
    .Append("tests: []\n")
    .Append("introducedIn: P2\n")
    .Append("compatibility: none\n")
    .Append("reviewedRevision: 0\n")
    .Append("normative: ").Append(Quoted(normative)).Append('\n')
    .Append("spec:\n");

  /// <summary>An output entry as YAML: the input bit, or a one-key flow mapping.</summary>
  private static string Entry(JsonNode? entry) => entry is JsonObject entryObject
    ? string.Concat("{", entryObject.Single().Key, ": ", entryObject.Single().Value!.ToJsonString(), "}")
    : entry!.ToJsonString();

  /// <summary>A YAML flow sequence, sixteen items to a line.</summary>
  private static string FlowList(IReadOnlyList<string> items) {
    var text = new StringBuilder("[");
    for (var index = 0; index < items.Count; ++index) {
      if (index > 0)
        text.Append(index % 16 == 0 ? ",\n    " : ", ");
      text.Append(items[index]);
    }

    return text.Append(']').ToString();
  }

  /// <summary>A YAML double-quoted scalar; JSON's string syntax is one.</summary>
  private static string Quoted(string text) => JsonSerializer.Serialize(text, _strings);

  private static JsonDocument Load(string path) {
    if (!File.Exists(path))
      throw new BitPermImportException($"fixture '{path}' does not exist");
    return JsonDocument.Parse(File.ReadAllText(path));
  }

  private static JsonElement Property(JsonElement element, string name, string where)
    => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : throw new BitPermImportException($"{where} has no '{name}'");

  /// <summary>Compares the generated files with the output directory and reports every difference; writes nothing.</summary>
  private static int Check(IReadOnlyList<GeneratedFile> files, string shown, string directory, TextWriter output, TextWriter error) {
    var expected = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
    var problems = 0;
    foreach (var path in expected.Keys.Union(Generated(directory), StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
      var file = Path.Combine(directory, path);
      string? state;
      if (!expected.TryGetValue(path, out var generated))
        state = "is no longer generated";
      else if (!File.Exists(file))
        state = "is missing";
      else
        state = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal) == generated.Content ? null : "differs from the fixtures";

      if (state is null)
        continue;

      error.WriteLine($"{Reporter}: {Shown(shown, path)} {state}");
      ++problems;
    }

    if (problems > 0)
      return Program.Failure;

    output.WriteLine($"{Reporter}: {files.Count} documents up to date");
    return Program.Success;
  }

  /// <summary>Writes the files that changed and deletes the generated files no longer produced.</summary>
  private static int Write(IReadOnlyList<GeneratedFile> files, string shown, string directory, TextWriter output, TextWriter error) {
    var (written, deleted) = (0, 0);
    try {
      foreach (var file in files) {
        var path = Path.Combine(directory, file.Path);
        if (File.Exists(path) && File.ReadAllText(path) == file.Content)
          continue;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.Content, new UTF8Encoding(false));
        ++written;
      }

      var current = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
      foreach (var stale in Generated(directory).Where(path => !current.Contains(path))) {
        File.Delete(Path.Combine(directory, stale));
        ++deleted;
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      error.WriteLine($"{Reporter}: the output directory '{shown}' cannot be written: {exception.Message}");
      return Program.Failure;
    }

    output.WriteLine($"{Reporter}: {files.Count} documents, {written} written, {deleted} deleted");
    return Program.Success;
  }

  /// <summary>The generated files present in the output directory, relative to it and <c>/</c>-separated.</summary>
  private static IEnumerable<string> Generated(string directory)
    => new[] { "mappings", "grammars", "costs" }
      .Select(folder => (Folder: folder, Full: Path.Combine(directory, folder)))
      .Where(entry => Directory.Exists(entry.Full))
      .SelectMany(entry => Directory.GetFiles(entry.Full, Prefix + "*.yaml").Select(file => $"{entry.Folder}/{Path.GetFileName(file)}"))
      .ToArray();

  private static string Shown(string directory, string path) => directory.TrimEnd('/', '\\') + "/" + path;

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

  /// <summary>The pin of the fixtures: the page they were exported from.</summary>
  private sealed record Pin(string Repository, string Commit, string PagePath) {

    public string Short => this.Commit[..8];

    public string Page => $"{this.Repository}@{this.Commit}:{this.PagePath}";

    public static Pin From(JsonElement root) => new(
      Property(root, "repository", "pin.json").GetString()!,
      Property(root, "commit", "pin.json").GetString()!,
      Property(Property(root, "files", "pin.json").EnumerateArray().First(), "path", "pin.json").GetString()!);

  }

}

/// <summary>The BitPerm fixtures cannot be turned into spec documents.</summary>
/// <param name="message">What is wrong.</param>
public sealed class BitPermImportException(string message) : Exception(message);
