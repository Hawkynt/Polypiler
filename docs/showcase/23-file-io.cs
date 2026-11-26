// Showcase: File I/O (Boundary)
// Category: Unsupported Boundaries
// Summary: Host filesystem reads via System.IO.File; explicitly refused until effectful I/O adapters land.
using System.IO;

public static class FileIoBoundary {
  public static int LineCount(string path)
    => File.ReadAllLines(path).Length;
}
