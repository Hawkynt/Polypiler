using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Target;

/// <summary>
/// A pass over the syntax tree of a target language, run after the backend built the tree and before
/// the printer writes it (#1336, PRD §4.9, docs/architecture/PIPELINE.md stage 8). It rewrites the
/// tree into one that computes the same, and it carries an optimization number of the catalog: the
/// passes of the backends and the rules of the IR optimizer share one namespace of numbers, so that
/// <c>--disable-opt</c> and <c>--explain-opt</c> speak of both alike.
/// </summary>
/// <typeparam name="TTree">The tree of the target language.</typeparam>
/// <typeparam name="TProfile">The profile the tree is written for.</typeparam>
public interface ITargetPass<TTree, in TProfile> {

  /// <summary>The optimization number, <c>O####</c>.</summary>
  string Id { get; }

  /// <summary>What the pass does, as <c>--explain-opt</c> names it.</summary>
  string Name { get; }

  /// <summary>Runs the pass.</summary>
  /// <param name="tree">The tree.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="applied">Told what the pass changed, once per change, in words of the tree: <c>'$rotl' into 'F'</c>.</param>
  /// <returns>The rewritten tree; the tree itself when the pass changed nothing.</returns>
  TTree Run(TTree tree, TProfile profile, Action<string> applied);

}

/// <summary>One change a target pass made, as <c>--explain-opt</c> writes it.</summary>
/// <param name="Id">The optimization number of the pass.</param>
/// <param name="Name">The name of the pass.</param>
/// <param name="Detail">What it changed.</param>
public sealed record TargetPassApplication(string Id, string Name, string Detail) {

  /// <summary>The application as a note writes it: <c>O0500 helper inlining: '$rotl' into 'F'</c>.</summary>
  public override string ToString() => $"{this.Id} {this.Name}: {this.Detail}";

}

/// <summary>Which target passes run: those of the level that <c>--disable-opt</c> does not switch off.</summary>
public sealed record TargetPassOptions {

  /// <summary>Every pass.</summary>
  public static TargetPassOptions Default { get; } = new();

  /// <summary>No pass: the tree is printed as the backend built it.</summary>
  public static TargetPassOptions None { get; } = new() { Level = OptimizationLevel.None };

  /// <summary>How much the passes do: nothing, or every pass that is not switched off.</summary>
  public OptimizationLevel Level { get; init; } = OptimizationLevel.Default;

  /// <summary>The numbers of the passes that do not run.</summary>
  public IReadOnlySet<string> Disabled { get; init; } = new HashSet<string>();

}

/// <summary>The files a backend wrote, with what its target passes changed before it printed them.</summary>
/// <param name="Files">The files.</param>
/// <param name="Applications">The changes of the target passes, in the order they made them.</param>
public sealed record TargetEmission(IReadOnlyList<GeneratedFile> Files, IReadOnlyList<TargetPassApplication> Applications);

/// <summary>A tree after the passes, with what they changed in the order they changed it.</summary>
/// <typeparam name="TTree">The tree of the target language.</typeparam>
/// <param name="Tree">The tree.</param>
/// <param name="Applications">The changes.</param>
public sealed record TargetPipelineResult<TTree>(TTree Tree, IReadOnlyList<TargetPassApplication> Applications);

/// <summary>
/// The ordered target passes of a backend. They run in the order of the table, each once, on the tree
/// the one before it left; a pass the options switch off is skipped, and at <see cref="OptimizationLevel.None"/>
/// none runs. The same tree and options always give the same result.
/// </summary>
/// <typeparam name="TTree">The tree of the target language.</typeparam>
/// <typeparam name="TProfile">The profile the tree is written for.</typeparam>
public sealed class TargetPipeline<TTree, TProfile> {

  /// <summary>Makes a pipeline of passes.</summary>
  /// <param name="passes">The passes, in the order they run.</param>
  /// <exception cref="ArgumentException">A pass has no number of the form <c>O####</c>, or two have the same.</exception>
  public TargetPipeline(IEnumerable<ITargetPass<TTree, TProfile>> passes) {
    this.Passes = [.. passes];
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var pass in this.Passes) {
      if (!IrRule.IsId(pass.Id))
        throw new ArgumentException($"'{pass.Id}' is not an optimization number of the form O####.", nameof(passes));

      if (!seen.Add(pass.Id))
        throw new ArgumentException($"The optimization number {pass.Id} is registered twice.", nameof(passes));
    }
  }

  /// <summary>The passes, in the order they run.</summary>
  public IReadOnlyList<ITargetPass<TTree, TProfile>> Passes { get; }

  /// <summary>The numbers of the passes, in order.</summary>
  public IEnumerable<string> Ids => this.Passes.Select(pass => pass.Id);

  /// <summary>Runs the passes the options let run.</summary>
  /// <param name="tree">The tree as the backend built it.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="options">Which passes run.</param>
  /// <returns>The tree after the passes, and what they changed.</returns>
  public TargetPipelineResult<TTree> Run(TTree tree, TProfile profile, TargetPassOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var applications = new List<TargetPassApplication>();
    if (options.Level == OptimizationLevel.None)
      return new(tree, applications);

    foreach (var pass in this.Passes.Where(pass => !options.Disabled.Contains(pass.Id)))
      tree = pass.Run(tree, profile, detail => applications.Add(new TargetPassApplication(pass.Id, pass.Name, detail)));

    return new(tree, applications);
  }

}
