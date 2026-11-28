using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Ports;

namespace Polypiler.Core.BinaryFormats.Discovery;

/// <summary>The diagnostics of discovery, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class DiscoveryDiagnostics {

  /// <summary><c>PLY4070</c>: an indirect jump no resolver proved targets for. Arguments: <c>jump</c>, its address.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unresolved = new("PLY4070", DiagnosticSeverity.Warning, null,
    "indirect {0} at {1} could not be resolved; code reachable only through it stays undecoded", "machine.discovery.unresolved");

  /// <summary><c>PLY4071</c>: bytes control flow reaches that do not decode. Arguments: the address, where from, the ISA, the mode, the function.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Undecodable = new("PLY4071", DiagnosticSeverity.Warning, null,
    "bytes at {0} reached from {1} do not decode as {2} {3}; '{4}' is incomplete", "machine.discovery.undecodable");

  /// <summary><c>PLY4072</c>: a byte decoded as part of two instructions. Arguments: the byte, the two start addresses in ascending order.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Overlap = new("PLY4072", DiagnosticSeverity.Warning, null,
    "byte {0} belongs to instructions starting at {1} and at {2}", "machine.discovery.overlap");

  /// <summary><c>PLY4073</c>: control flow to an address outside the code that is no import. Arguments: the instruction, the target.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor LeavesCode = new("PLY4073", DiagnosticSeverity.Warning, null,
    "control flow from {0} leaves the code at {1}", "machine.discovery.external");

  /// <summary><c>PLY4074</c>: the instruction budget is exhausted. Argument: the budget.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Budget = new("PLY4074", DiagnosticSeverity.Error, ExitCode.BudgetExceeded,
    "discovery stopped after {0} instructions", "machine.discovery.budget");

}

/// <summary>
/// Recursive-traversal discovery of functions, blocks and code bytes (Schwarz, Debray and Andrews,
/// "Disassembly of Executable Code Revisited", WCRE 2002). Only bytes proven reachable from an entry
/// are decoded:
/// <list type="bullet">
/// <item>the seeds are the manifest's entries, the container's entries and its function symbols
/// (<see cref="Confidence.Proven"/>), and with <see cref="DiscoveryOptions.AcceptHeuristic"/> the
/// binary's hints (<see cref="Confidence.Heuristic"/>);</item>
/// <item>a jump or conditional jump adds its target to the function, or is a tail call when the target
/// is the entry of another known function;</item>
/// <item>a call makes its target a function (<see cref="Confidence.Derived"/>) and continues after
/// the call only once the callee is known to return on some path, to a fixed point;</item>
/// <item>a return or a trap ends a path; an indirect jump asks the resolver and ends the block as
/// unresolved without one (<c>PLY4070</c>); an indirect call continues;</item>
/// <item>bytes that do not decode end the path and make the function incomplete (<c>PLY4071</c>); a byte
/// decoded as part of two instructions is a conflict (<c>PLY4072</c>) and makes both incomplete; a target
/// outside the code is external (<c>PLY4073</c> unless it is an import slot).</item>
/// </list>
/// The result is deterministic: seeds are traversed by address, functions and blocks are listed by
/// address, and nothing depends on hash order.
/// </summary>
public sealed class Discovery : IProgramBoundaryResolver {

  /// <summary>Discovers what control flow reaches from the binary's entries.</summary>
  /// <param name="binary">The binary.</param>
  /// <param name="disassembler">Decodes its instructions.</param>
  /// <param name="resolver">Resolves indirect jumps through tables, or <see langword="null"/>.</param>
  /// <param name="options">The budget and whether heuristic candidates are traversed; the defaults when <see langword="null"/>.</param>
  /// <returns>The functions, the byte map and the diagnostics.</returns>
  public static DiscoveryResult Run(LoadedBinary binary, IDisassembler disassembler, IJumpTableResolver? resolver, DiscoveryOptions? options = null)
    => new Discovery().Resolve(binary, disassembler, resolver, options);

  /// <inheritdoc/>
  public DiscoveryResult Resolve(LoadedBinary binary, IDisassembler disassembler, IJumpTableResolver? resolver, DiscoveryOptions? options = null) {
    ArgumentNullException.ThrowIfNull(binary);
    ArgumentNullException.ThrowIfNull(disassembler);
    return new Walk(binary, disassembler, resolver, options ?? new DiscoveryOptions()).Run();
  }

  /// <summary>What discovery knows of one function while it traverses it.</summary>
  private sealed class Function(ulong entry, string? name, Confidence confidence, string evidence) {

    public ulong Entry { get; } = entry;
    public string? Name { get; } = name;
    public Confidence Confidence { get; } = confidence;
    public string Evidence { get; } = evidence;
    public bool Complete { get; set; } = true;
    public bool Returns { get; set; }
    public bool Queued { get; set; }
    public Queue<(ulong Address, ulong From)> Work { get; } = new();
    public HashSet<ulong> Visited { get; } = [];
    public SortedSet<ulong> Instructions { get; } = [];
    public HashSet<ulong> Leaders { get; } = [];
    public Dictionary<ulong, ulong> TailCalls { get; } = [];
    public HashSet<ulong> ReturningTailCalls { get; } = [];
    public Dictionary<ulong, ImmutableArray<ulong>> Tables { get; } = [];
    public HashSet<ulong> Unresolved { get; } = [];
    public HashSet<ulong> IndirectCalls { get; } = [];
    public Dictionary<ulong, List<ulong>> External { get; } = [];
    public HashSet<ulong> Invalid { get; } = [];

    public string Display => this.Name ?? MachineSyntax.Number(this.Entry);

  }

  private sealed class Walk(LoadedBinary binary, IDisassembler disassembler, IJumpTableResolver? resolver, DiscoveryOptions options) {

    private readonly BinaryImage _image = binary.Image;
    private readonly Dictionary<ulong, DisassembledInstruction> _instructions = [];
    private readonly Dictionary<ulong, ulong> _covering = [];
    private readonly Dictionary<ulong, List<Function>> _owners = [];
    private readonly SortedDictionary<ulong, Function> _functions = [];
    private readonly Queue<Function> _queue = new();
    private readonly List<(Function Caller, ulong Next, ulong From, Function Callee)> _pending = [];
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly ByteMap _bytes = new();
    private long _decoded;
    private bool _exhausted;

    public DiscoveryResult Run() {
      var seeds = new List<Function>();
      void Seed(ulong address, string? name, Confidence confidence, string evidence) {
        if (this._functions.ContainsKey(address))
          return;

        var function = new Function(address, name, confidence, evidence);
        this._functions[address] = function;
        seeds.Add(function);
      }

      foreach (var entry in binary.Entries)
        Seed(entry.Address, entry.Name, Confidence.Proven, "manifest");
      foreach (var entry in this._image.Entries)
        Seed(entry, null, Confidence.Proven, "entry");
      foreach (var symbol in this._image.Symbols.Where(symbol => symbol.Kind == SymbolKind.Function))
        Seed(symbol.Address, symbol.Name, Confidence.Proven, "symbol");
      if (options.AcceptHeuristic)
        foreach (var hint in binary.Hints)
          Seed(hint, null, Confidence.Heuristic, "hint");

      foreach (var function in seeds.OrderBy(function => function.Entry))
        this.Enqueue(function, function.Entry, function.Entry);

      while (true) {
        while (this._queue.TryDequeue(out var function)) {
          function.Queued = false;
          this.Traverse(function);
        }

        if (!this.ResumeReturningCalls())
          break;
      }

      if (this._exhausted)
        foreach (var function in this._functions.Values)
          function.Complete = false;

      return new([.. this._functions.Values.Select(this.Finished)], this._bytes, [.. this._diagnostics]);
    }

    private void Enqueue(Function function, ulong address, ulong from) {
      function.Work.Enqueue((address, from));
      if (function.Queued)
        return;

      function.Queued = true;
      this._queue.Enqueue(function);
    }

    /// <summary>Continues after every call whose callee now returns; whether any did.</summary>
    private bool ResumeReturningCalls() {
      var resumed = false;
      foreach (var call in this._pending.ToList()) {
        if (!this.Returns(call.Callee, []))
          continue;

        this._pending.Remove(call);
        this.Enqueue(call.Caller, call.Next, call.From);
        resumed = true;
      }

      return resumed;
    }

    /// <summary>Whether a function returns on some path: through a return, or a tail call to a function or an import that does.</summary>
    private bool Returns(Function function, HashSet<Function> seen) {
      if (function.Returns || function.ReturningTailCalls.Count > 0)
        return true;
      if (!seen.Add(function))
        return false;

      return function.TailCalls.Values.Any(target => this.Returns(this._functions[target], seen));
    }

    private void Traverse(Function function) {
      while (!this._exhausted && function.Work.TryDequeue(out var start))
        this.Path(function, start.Address, start.From);
    }

    /// <summary>Follows straight-line flow from an address until a path ends.</summary>
    private void Path(Function function, ulong address, ulong from) {
      var path = new List<DisassembledInstruction>();
      var at = address;
      while (function.Visited.Add(at)) {
        if (!binary.IsDecodable(at)) {
          this.External(function, from, at);
          return;
        }

        if (this.Decode(function, at, from) is not { } instruction)
          return;

        function.Instructions.Add(at);
        this.Own(function, instruction);
        path.Add(instruction);
        var next = at + (ulong)instruction.Length;
        switch (instruction.Flow) {
          case FlowKind.Call when instruction.Targets.Length > 0:
            if (!this.Call(function, instruction, next))
              return;
            break;
          case FlowKind.Call or FlowKind.IndirectCall:
            function.IndirectCalls.Add(at);
            break;
          case FlowKind.Jump:
            this.Jump(function, instruction);
            return;
          case FlowKind.ConditionalJump:
            this.Jump(function, instruction);
            function.Leaders.Add(next);
            break;
          case FlowKind.Return:
            function.Returns = true;
            return;
          case FlowKind.IndirectJump:
            this.Indirect(function, instruction, path);
            return;
          case FlowKind.Trap:
            return;
        }

        from = at;
        at = next;
      }
    }

    /// <summary>A call: its target becomes a function; whether the path goes on after it now.</summary>
    private bool Call(Function function, DisassembledInstruction call, ulong next) {
      var target = call.Targets[0];
      if (!binary.IsDecodable(target)) {
        var import = this.External(function, call.Address, target);
        return import;
      }

      if (!this._functions.TryGetValue(target, out var callee)) {
        callee = new Function(target, null, Confidence.Derived, $"call from {MachineSyntax.Number(call.Address)}");
        this._functions[target] = callee;
        this.Enqueue(callee, target, call.Address);
      }

      if (this.Returns(callee, []))
        return true;

      this._pending.Add((function, next, call.Address, callee));
      return false;
    }

    /// <summary>The target of a jump: a tail call to another function, an external target, or a block of this function.</summary>
    private void Jump(Function function, DisassembledInstruction jump) {
      var target = jump.Targets[0];
      if (target != function.Entry && this._functions.ContainsKey(target)) {
        function.TailCalls[jump.Address] = target;
        return;
      }

      if (!binary.IsDecodable(target)) {
        if (this.External(function, jump.Address, target))
          function.ReturningTailCalls.Add(jump.Address);
        return;
      }

      function.Leaders.Add(target);
      this.Enqueue(function, target, jump.Address);
    }

    /// <summary>An indirect jump: the validated targets of its table join the function, else the block ends unresolved.</summary>
    private void Indirect(Function function, DisassembledInstruction jump, List<DisassembledInstruction> path) {
      var table = resolver?.Resolve(this._image, path, jump);
      if (table is null || table.Targets.IsEmpty || !table.Targets.All(binary.IsDecodable)) {
        function.Unresolved.Add(jump.Address);
        this.Report(DiscoveryDiagnostics.Unresolved, "jump", MachineSyntax.Number(jump.Address));
        return;
      }

      function.Tables[jump.Address] = table.Targets;
      this._bytes.Mark(table.TableAddress, table.TableBytes, ByteClass.Data);
      foreach (var target in table.Targets) {
        function.Leaders.Add(target);
        this.Enqueue(function, target, jump.Address);
      }
    }

    /// <summary>A target outside the code, recorded on the instruction; whether it is an import slot, which is no finding.</summary>
    private bool External(Function function, ulong from, ulong target) {
      if (!function.External.TryGetValue(from, out var targets))
        function.External[from] = targets = [];
      targets.Add(target);
      if (this._image.Imports.Any(import => import.SlotAddress == target))
        return true;

      this.Report(DiscoveryDiagnostics.LeavesCode, MachineSyntax.Number(from), MachineSyntax.Number(target));
      return false;
    }

    /// <summary>The instruction at an address, decoded once; <see langword="null"/> when it does not decode or the budget is spent.</summary>
    private DisassembledInstruction? Decode(Function function, ulong at, ulong from) {
      if (this._instructions.TryGetValue(at, out var known))
        return known;

      if (this._decoded >= options.MaxInstructions) {
        this._exhausted = true;
        this.Report(DiscoveryDiagnostics.Budget, options.MaxInstructions);
        return null;
      }

      ++this._decoded;
      var mode = this._image.ModeAt(at);
      var instruction = disassembler.Disassemble(this._image, at, mode, new DiagnosticBag());
      if (instruction is null || instruction.Flow == FlowKind.Invalid || instruction.Length <= 0
          || Enumerable.Range(0, instruction.Length).Any(offset => this._image.ByteAt(at + (ulong)offset) is null)) {
        function.Complete = false;
        function.Invalid.Add(at);
        this.Report(DiscoveryDiagnostics.Undecodable, MachineSyntax.Number(at), MachineSyntax.Number(from), disassembler.Isa, mode, function.Display);
        return null;
      }

      this._instructions[at] = instruction;
      this._bytes.Mark(at, instruction.Length, ByteClass.Code);
      for (var offset = 0UL; offset < (ulong)instruction.Length; ++offset) {
        var address = at + offset;
        if (!this._covering.TryGetValue(address, out var other)) {
          this._covering[address] = at;
          continue;
        }

        this._bytes.Mark(address, 1, ByteClass.Conflict);
        this.Report(DiscoveryDiagnostics.Overlap, MachineSyntax.Number(address), MachineSyntax.Number(Math.Min(other, at)), MachineSyntax.Number(Math.Max(other, at)));
        foreach (var owner in this._owners[other])
          owner.Complete = false;
        function.Complete = false;
      }

      return instruction;
    }

    /// <summary>Records that a function holds an instruction; one with a conflicting byte makes it incomplete.</summary>
    private void Own(Function function, DisassembledInstruction instruction) {
      if (!this._owners.TryGetValue(instruction.Address, out var owners))
        this._owners[instruction.Address] = owners = [];
      if (!owners.Contains(function))
        owners.Add(function);
      if (Enumerable.Range(0, instruction.Length).Any(offset => this._bytes[instruction.Address + (ulong)offset] == ByteClass.Conflict))
        function.Complete = false;
    }

    private void Report(DiagnosticDescriptor descriptor, params object[] arguments) => this._diagnostics.Add(Diagnostic.Create(descriptor, null, arguments));

    /// <summary>The blocks of a traversed function: a block starts at the entry, at a target, after a block-ending instruction and after a gap.</summary>
    private DiscoveredFunction Finished(Function function) {
      var blocks = new List<DiscoveredBlock>();
      var current = new List<DisassembledInstruction>();
      void Close() {
        if (current.Count > 0)
          blocks.Add(this.Block(function, current));
        current = [];
      }

      foreach (var start in function.Instructions) {
        var instruction = this._instructions[start];
        if (current.Count > 0 && (start != current[^1].Address + (ulong)current[^1].Length || function.Leaders.Contains(start) || start == function.Entry))
          Close();

        current.Add(instruction);
        if (instruction.Flow is FlowKind.Jump or FlowKind.ConditionalJump or FlowKind.Return or FlowKind.Trap or FlowKind.IndirectJump)
          Close();
      }

      Close();
      return new(function.Entry, function.Name, function.Confidence, function.Evidence, [.. blocks], function.Complete);
    }

    private DiscoveredBlock Block(Function function, List<DisassembledInstruction> instructions) {
      var last = instructions[^1];
      var next = last.Address + (ulong)last.Length;
      var successors = new SortedSet<ulong>();
      void Successor(ulong address) {
        if (function.Instructions.Contains(address))
          successors.Add(address);
      }

      switch (last.Flow) {
        case FlowKind.Jump or FlowKind.ConditionalJump:
          if (!function.TailCalls.ContainsKey(last.Address))
            Successor(last.Targets[0]);
          if (last.Flow == FlowKind.ConditionalJump)
            Successor(next);
          break;
        case FlowKind.IndirectJump:
          foreach (var target in function.Tables.GetValueOrDefault(last.Address, []))
            Successor(target);
          break;
        case FlowKind.Return or FlowKind.Trap:
          break;
        default:
          Successor(next);
          break;
      }

      return new(instructions[0].Address, next - 1, [.. instructions], [.. successors]) {
        Unresolved = function.Unresolved.Contains(last.Address),
        TailCall = function.TailCalls.TryGetValue(last.Address, out var tail) ? tail : null,
        ExternalTargets = [.. instructions.SelectMany(instruction => function.External.GetValueOrDefault(instruction.Address) ?? []).Distinct().Order()],
        UnresolvedCall = instructions.Any(instruction => function.IndirectCalls.Contains(instruction.Address)),
        Invalid = function.Invalid.Contains(next) ? next : null
      };
    }

  }

}
