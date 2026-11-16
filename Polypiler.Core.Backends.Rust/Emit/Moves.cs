using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// Where a read of a variable may move its value (#1393): no path from the read reads the variable
/// again before it is given a new value. Rust moves a value that is not <c>Copy</c> where it is read,
/// so a read the variable outlives with its value still to be read would not compile - and where the
/// IR shares a part of the value between the two, a copy would not compute the same either. What the
/// analysis does not follow - a loop around the read that the variable outlives, a <c>try</c>, a
/// lambda - it counts as a later read, so it may refuse a move but never allows a wrong one.
/// </summary>
internal static class Moves {

  /// <summary>Whether a statement reads a variable for the last time before the variable ends or is given a new value, on every path.</summary>
  /// <param name="body">The body of the function the statement is in.</param>
  /// <param name="statement">The statement, one of the body; its expressions hold the read.</param>
  /// <param name="local">The variable.</param>
  public static bool IsLast(IrBlock body, IrStmt statement, IrLocal local) {
    var path = Path(body, statement);
    if (path is null)
      return false;

    bool Refers(IrStmt statement) => IrTree.Nodes(statement).Any(node => node is IrLocalRef other && other.Local == local);
    bool Declares(IrStmt statement) => ArrayOwnership.Statements(new IrBlock([statement])).Any(inner => inner is IrDeclare declare && declare.Local == local);
    static bool IsLoop(IrStmt statement) => statement is IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence;

    var (innermost, position) = path[^1];
    var own = innermost.Statements[position];
    if (IrTree.Nodes(own).Count(node => node is IrLocalRef other && other.Local == local) > 1
      || IrTree.Nodes(own).OfType<IrLambda>().Any(lambda => IrTree.Nodes(lambda.Body).Any(node => node is IrLocalRef other && other.Local == local)))
      return false;
    if (own is IrReturn || own is IrAssign assigned && assigned.Local == local)
      return true;
    if (IsLoop(own) && !Declares(own))
      return false;

    for (var level = path.Count - 1; level >= 0; --level) {
      var (block, index) = path[level];
      foreach (var next in block.Statements.Skip(index + 1)) {
        if (next is IrAssign assign && assign.Local == local)
          return !IrTree.Nodes(assign.Value).Any(node => node is IrLocalRef other && other.Local == local);
        if (Refers(next))
          return false;
        if (next is IrReturn or IrThrow)
          return true;
      }

      if (level == 0)
        break;

      // Leaving a block: a loop the variable outlives runs the read again; a try may read it in a handler.
      var (outer, at) = path[level - 1];
      var enclosing = outer.Statements[at];
      if (IsLoop(enclosing) && !Declares(enclosing) || enclosing is IrTry && Refers(enclosing))
        return false;
    }

    return true;
  }

  /// <summary>The blocks and statement positions from the body down to a statement, or <see langword="null"/>.</summary>
  private static List<(IrBlock Block, int Index)>? Path(IrBlock block, IrStmt target) {
    for (var index = 0; index < block.Statements.Count; ++index) {
      var statement = block.Statements[index];
      if (ReferenceEquals(statement, target))
        return [(block, index)];

      foreach (var inner in statement is IrBlock nested ? [nested] : IrTree.Blocks(statement)) {
        if (Path(inner, target) is { } found) {
          found.Insert(0, (block, index));
          return found;
        }
      }
    }

    return null;
  }

}
