using System.Linq;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Checking;

/// <summary>The text of an expression for a message, as <c>types.ExprString</c> writes it, shortened where it is long.</summary>
internal static class GoNodeText {

  /// <summary>The text of a node.</summary>
  public static string Of(GoNode? node) => node is null ? "" : node.Kind switch {
    "Ident" => node.Text("Name")!,
    "BasicLit" => node.Text("Value")!,
    "ParenExpr" => $"({Of(node.Node("X"))})",
    "SelectorExpr" => $"{Of(node.Node("X"))}.{Of(node.Node("Sel"))}",
    "IndexExpr" => $"{Of(node.Node("X"))}[{Of(node.Node("Index"))}]",
    "IndexListExpr" => $"{Of(node.Node("X"))}[{string.Join(", ", node.Nodes("Indices").Select(Of))}]",
    "SliceExpr" => $"{Of(node.Node("X"))}[{Of(node.Node("Low"))}:{Of(node.Node("High"))}{(node.Flag("Slice3") ? ":" + Of(node.Node("Max")) : "")}]",
    "TypeAssertExpr" => $"{Of(node.Node("X"))}.({(node.Node("Type") is { } type ? Of(type) : "type")})",
    "CallExpr" => $"{Of(node.Node("Fun"))}({string.Join(", ", node.Nodes("Args").Select(Of))}{(node.Flag("spread") ? "..." : "")})",
    "StarExpr" => "*" + Of(node.Node("X")),
    "UnaryExpr" => node.Text("Op") + Of(node.Node("X")),
    "BinaryExpr" => $"{Of(node.Node("X"))} {node.Text("Op")} {Of(node.Node("Y"))}",
    "KeyValueExpr" => $"{Of(node.Node("Key"))}: {Of(node.Node("Value"))}",
    "CompositeLit" => (node.Node("Type") is { } literal ? Of(literal) : "") + "{…}",
    "FuncLit" => "func literal",
    "ArrayType" => $"[{Of(node.Node("Len"))}]{Of(node.Node("Elt"))}",
    "Ellipsis" => "..." + Of(node.Node("Elt")),
    "StructType" => "struct{…}",
    "FuncType" => "func(…)",
    "InterfaceType" => node.Node("Methods")?.Nodes("List").Count > 0 ? "interface{…}" : "interface{}",
    "MapType" => $"map[{Of(node.Node("Key"))}]{Of(node.Node("Value"))}",
    "ChanType" => node.Text("Dir") switch {
      "send" => "chan<- " + Of(node.Node("Value")),
      "recv" => "<-chan " + Of(node.Node("Value")),
      _ => "chan " + Of(node.Node("Value"))
    },
    _ => node.Kind
  };

}
