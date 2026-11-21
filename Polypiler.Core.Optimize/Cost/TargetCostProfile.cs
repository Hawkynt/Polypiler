using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>The forms a cost profile lists for one operation; a form it does not list is unavailable.</summary>
/// <param name="Native">One instruction or library call of the target, or <see langword="null"/>.</param>
/// <param name="Sequence">The operation written out in place, or <see langword="null"/>.</param>
/// <param name="Helper">A shared helper function, or <see langword="null"/>.</param>
public sealed record OperationForms(Cost? Native, Cost? Sequence, HelperForm? Helper);

/// <summary>A shared helper: what each call costs, the body's execution included, and what the body costs once per module.</summary>
/// <param name="Call">The cost of one call.</param>
/// <param name="Body">The cost of the body, counted once.</param>
public sealed record HelperForm(Cost Call, Cost Body);

/// <summary>SIMD within a register: integer registers of <paramref name="RegisterBits"/> bits used as lanes.</summary>
/// <param name="RegisterBits">The width of the register.</param>
/// <param name="Setup">The cost of entering the path.</param>
/// <param name="PerBlock">The cost of one register full of elements.</param>
public sealed record SwarForm(long RegisterBits, long Setup, long PerBlock);

/// <summary>A vector unit of one width.</summary>
/// <param name="Width">The width in bits.</param>
/// <param name="Setup">The cost of entering the path.</param>
/// <param name="PerBlock">The cost of one vector full of elements.</param>
public sealed record SimdForm(long Width, long Setup, long PerBlock);

/// <summary>Worker threads.</summary>
/// <param name="Count">The number of threads.</param>
/// <param name="Spawn">The cost of starting and joining them.</param>
public sealed record ThreadsForm(long Count, long Spawn);

/// <summary>An accelerator the work can move to: a GPU or an NPU.</summary>
/// <param name="Transfer">The cost of moving the data there and back.</param>
/// <param name="PerElement">The cost of one unit of work there.</param>
public sealed record AcceleratorForm(long Transfer, long PerElement);

/// <summary>
/// The costs of one target profile, read from <c>spec/targets/&lt;language&gt;/&lt;profile&gt;/cost.yaml</c>
/// (format: <c>spec/targets/README.md</c>, schema <c>spec/schemas/cost-profile.schema.json</c>). A form the
/// file does not list is unavailable; a missing <c>simd</c>, <c>threads</c>, <c>gpu</c> or <c>npu</c> key
/// means the target has none.
/// </summary>
public sealed class TargetCostProfile {

  /// <summary>The name of the file in a target profile's folder.</summary>
  public const string FileName = "cost.yaml";

  private TargetCostProfile(string path, string name, long unknownTripCount, Cost @default, IReadOnlyDictionary<OperationKey, OperationForms> operations,
    long loopOverhead, SwarForm? swar, IReadOnlyList<SimdForm> simd, ThreadsForm? threads, AcceleratorForm? gpu, AcceleratorForm? npu) {
    this.Path = path;
    this.Name = name;
    this.UnknownTripCount = unknownTripCount;
    this.Default = @default;
    this.Operations = operations;
    this.LoopOverhead = loopOverhead;
    this.Swar = swar;
    this.Simd = simd;
    this.Threads = threads;
    this.Gpu = gpu;
    this.Npu = npu;
  }

  /// <summary>The file the profile was read from, as the caller named it.</summary>
  public string Path { get; }

  /// <summary>The key <c>profile</c>: the profile's name.</summary>
  public string Name { get; }

  /// <summary>The key <c>unknownTripCount</c>: the count used where neither a constant nor a range is known.</summary>
  public long UnknownTripCount { get; }

  /// <summary>The key <c>default</c>: the cost of an operation the profile does not list.</summary>
  public Cost Default { get; }

  /// <summary>The key <c>operations</c>: the forms of each operation the profile lists.</summary>
  public IReadOnlyDictionary<OperationKey, OperationForms> Operations { get; }

  /// <summary>The key <c>loop.overhead</c>: the test and step of one loop iteration.</summary>
  public long LoopOverhead { get; }

  /// <summary>The key <c>swar</c>, or <see langword="null"/> when the target has none.</summary>
  public SwarForm? Swar { get; }

  /// <summary>The key <c>simd</c>: the vector widths in the order listed, empty when the target has none.</summary>
  public IReadOnlyList<SimdForm> Simd { get; }

  /// <summary>The key <c>threads</c>, or <see langword="null"/> when the target has none.</summary>
  public ThreadsForm? Threads { get; }

  /// <summary>The key <c>gpu</c>, or <see langword="null"/> when the target has none.</summary>
  public AcceleratorForm? Gpu { get; }

  /// <summary>The key <c>npu</c>, or <see langword="null"/> when the target has none.</summary>
  public AcceleratorForm? Npu { get; }

  /// <summary>Where the cost profile of a target profile lives: <c>&lt;specRoot&gt;/targets/&lt;language&gt;/&lt;profile&gt;/cost.yaml</c>.</summary>
  /// <param name="specRoot">The spec root, <c>spec</c> in a checkout.</param>
  /// <param name="target">The target profile, <c>&lt;language&gt;/&lt;profile&gt;</c> such as <c>csharp/net10.0</c>.</param>
  /// <returns>The path.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException"><paramref name="target"/> is not two non-empty names joined by <c>/</c>.</exception>
  public static string PathOf(string specRoot, string target) {
    ArgumentNullException.ThrowIfNull(specRoot);
    ArgumentNullException.ThrowIfNull(target);
    var parts = target.Split('/');
    if (parts.Length != 2 || parts.Any(part => part.Length == 0))
      throw new ArgumentException($"'{target}' is not of the form <language>/<profile>", nameof(target));
    return System.IO.Path.Combine(specRoot, "targets", parts[0], parts[1], FileName);
  }

  /// <summary>Reads the cost profile of a target profile.</summary>
  /// <param name="specRoot">The spec root, <c>spec</c> in a checkout.</param>
  /// <param name="target">The target profile, <c>&lt;language&gt;/&lt;profile&gt;</c>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="CostProfileException"><c>PLY0355</c>: the target profile has no <c>cost.yaml</c>; <c>PLY0356</c>: it is malformed.</exception>
  public static TargetCostProfile ForTarget(string specRoot, string target) {
    var path = PathOf(specRoot, target);
    if (!File.Exists(path))
      throw new CostProfileException(Diagnostic.Create(CostDiagnostics.ProfileMissing, null, target));
    return Load(path);
  }

  /// <summary>Reads a cost profile file.</summary>
  /// <param name="path">The file.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
  /// <exception cref="IOException">The file cannot be read.</exception>
  /// <exception cref="CostProfileException"><c>PLY0356</c>: the file is malformed.</exception>
  public static TargetCostProfile Load(string path) {
    ArgumentNullException.ThrowIfNull(path);
    return Parse(File.ReadAllText(path), path);
  }

  /// <summary>Reads a cost profile from its text.</summary>
  /// <param name="yaml">The text.</param>
  /// <param name="path">The file the text is from, for the diagnostics.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  /// <exception cref="CostProfileException"><c>PLY0356</c>: the text is malformed.</exception>
  public static TargetCostProfile Parse(string yaml, string path) {
    ArgumentNullException.ThrowIfNull(yaml);
    ArgumentNullException.ThrowIfNull(path);
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(yaml));
    } catch (YamlException exception) {
      throw new Reader(path).Invalid(exception.Start, "(document)", exception.Message);
    }

    var reader = new Reader(path);
    if (stream.Documents.Count != 1)
      throw reader.Invalid(Mark.Empty, "(document)", "exactly one YAML document is required");
    return reader.Profile(stream.Documents[0].RootNode);
  }

  /// <summary>Reads the nodes of one file, naming the key path of every problem.</summary>
  private sealed class Reader(string path) {

    public CostProfileException Invalid(Mark at, string key, string problem)
      => new(Diagnostic.Create(CostDiagnostics.ProfileInvalid, null, path, key, problem) with { File = path, Line = (int)at.Line, Column = (int)at.Column });

    private static string Join(string prefix, string key) => prefix.Length == 0 ? key : prefix + "." + key;

    public TargetCostProfile Profile(YamlNode root) {
      var keys = this.Mapping(root, "(document)", "profile", "unknownTripCount", "default", "operations", "loop", "swar", "simd", "threads", "gpu", "npu");
      var name = this.Name(this.Required(keys, root, "", "profile"), "profile");
      var unknown = this.Number(this.Required(keys, root, "", "unknownTripCount"), "unknownTripCount", 0);
      var @default = this.Cost(this.Required(keys, root, "", "default"), "default");
      var operations = keys.TryGetValue("operations", out var node) ? this.Operations(node) : new Dictionary<OperationKey, OperationForms>();
      var loop = this.Required(keys, root, "", "loop");
      var overhead = this.Number(this.Required(this.Mapping(loop, "loop", "overhead"), loop, "loop", "overhead"), "loop.overhead", 0);
      var swar = keys.TryGetValue("swar", out node) ? this.Swar(node) : null;
      var simd = keys.TryGetValue("simd", out node) ? this.Simd(node) : [];
      var threads = keys.TryGetValue("threads", out node) ? this.Threads(node) : null;
      var gpu = keys.TryGetValue("gpu", out node) ? this.Accelerator(node, "gpu") : null;
      var npu = keys.TryGetValue("npu", out node) ? this.Accelerator(node, "npu") : null;
      return new(path, name, unknown, @default, operations, overhead, swar, simd, threads, gpu, npu);
    }

    private Dictionary<OperationKey, OperationForms> Operations(YamlNode node) {
      if (node is not YamlMappingNode mapping)
        throw this.Invalid(node.Start, "operations", "a mapping is required");

      var result = new Dictionary<OperationKey, OperationForms>();
      foreach (var (keyNode, value) in mapping.Children) {
        var text = this.KeyText(keyNode, "operations");
        var key = Join("operations", text);
        if (!OperationKey.TryParse(text, out var op) || !op.IsKnown)
          throw this.Invalid(keyNode.Start, key, "unknown operation");

        var forms = this.Mapping(value, key, "native", "sequence", "helper");
        if (forms.Count == 0)
          throw this.Invalid(value.Start, key, "at least one of native, sequence, helper is required");
        result.Add(op, new(
          forms.TryGetValue("native", out var native) ? this.Cost(native, Join(key, "native")) : null,
          forms.TryGetValue("sequence", out var sequence) ? this.Cost(sequence, Join(key, "sequence")) : null,
          forms.TryGetValue("helper", out var helper) ? this.Helper(helper, Join(key, "helper")) : null));
      }
      return result;
    }

    private HelperForm Helper(YamlNode node, string key) {
      var keys = this.Mapping(node, key, "call", "body");
      return new(this.Cost(this.Required(keys, node, key, "call"), Join(key, "call")), this.Cost(this.Required(keys, node, key, "body"), Join(key, "body")));
    }

    private SwarForm Swar(YamlNode node) {
      var keys = this.Mapping(node, "swar", "registerBits", "setup", "perBlock");
      return new(
        this.Number(this.Required(keys, node, "swar", "registerBits"), "swar.registerBits", 1),
        this.Number(this.Required(keys, node, "swar", "setup"), "swar.setup", 0),
        this.Number(this.Required(keys, node, "swar", "perBlock"), "swar.perBlock", 0));
    }

    private List<SimdForm> Simd(YamlNode node) {
      if (node is not YamlSequenceNode sequence)
        throw this.Invalid(node.Start, "simd", "a list is required");

      var result = new List<SimdForm>();
      foreach (var item in sequence.Children) {
        var key = $"simd[{result.Count}]";
        var keys = this.Mapping(item, key, "width", "setup", "perBlock");
        result.Add(new(
          this.Number(this.Required(keys, item, key, "width"), Join(key, "width"), 1),
          this.Number(this.Required(keys, item, key, "setup"), Join(key, "setup"), 0),
          this.Number(this.Required(keys, item, key, "perBlock"), Join(key, "perBlock"), 0)));
      }
      return result;
    }

    private ThreadsForm Threads(YamlNode node) {
      var keys = this.Mapping(node, "threads", "count", "spawn");
      return new(this.Number(this.Required(keys, node, "threads", "count"), "threads.count", 1), this.Number(this.Required(keys, node, "threads", "spawn"), "threads.spawn", 0));
    }

    private AcceleratorForm Accelerator(YamlNode node, string key) {
      var keys = this.Mapping(node, key, "transfer", "perElement");
      return new(this.Number(this.Required(keys, node, key, "transfer"), Join(key, "transfer"), 0), this.Number(this.Required(keys, node, key, "perElement"), Join(key, "perElement"), 0));
    }

    private Cost Cost(YamlNode node, string key) {
      var keys = this.Mapping(node, key, "latency", "size", "memory", "allocations");
      return new(
        this.Number(this.Required(keys, node, key, "latency"), Join(key, "latency"), 0),
        this.Number(this.Required(keys, node, key, "size"), Join(key, "size"), 0),
        keys.TryGetValue("memory", out var memory) ? this.Number(memory, Join(key, "memory"), 0) : 0,
        keys.TryGetValue("allocations", out var allocations) ? this.Number(allocations, Join(key, "allocations"), 0) : 0);
    }

    private Dictionary<string, YamlNode> Mapping(YamlNode node, string key, params string[] allowed) {
      if (node is not YamlMappingNode mapping)
        throw this.Invalid(node.Start, key, "a mapping is required");

      var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
      foreach (var (keyNode, value) in mapping.Children) {
        var name = this.KeyText(keyNode, key);
        if (!allowed.Contains(name, StringComparer.Ordinal))
          throw this.Invalid(keyNode.Start, key == "(document)" ? name : Join(key, name), "unknown key");
        result.Add(name, value);
      }
      return result;
    }

    private string KeyText(YamlNode keyNode, string key)
      => keyNode is YamlScalarNode { Value: { Length: > 0 } text } ? text : throw this.Invalid(keyNode.Start, key, "every key must be a name");

    private YamlNode Required(Dictionary<string, YamlNode> keys, YamlNode owner, string prefix, string name)
      => keys.TryGetValue(name, out var node) ? node : throw this.Invalid(owner.Start, Join(prefix, name), "required");

    private string Name(YamlNode node, string key)
      => node is YamlScalarNode { Value: { Length: > 0 } text } ? text : throw this.Invalid(node.Start, key, "a name is required");

    private long Number(YamlNode node, string key, long minimum) {
      if (node is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } text } && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= minimum)
        return value;
      throw this.Invalid(node.Start, key, minimum == 0 ? "a non-negative integer is required" : "a positive integer is required");
    }

  }

}
