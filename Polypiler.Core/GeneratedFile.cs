namespace Polypiler.Core;

/// <summary>One file a backend produced.</summary>
/// <param name="Path">The path of the file, relative to the output directory.</param>
/// <param name="Content">The complete text of the file.</param>
public sealed record GeneratedFile(string Path, string Content);
