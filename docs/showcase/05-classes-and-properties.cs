// Showcase: Classes, Properties & Reference Records
// Category: Object Model
// Summary: Reference classes with auto-properties, computed properties, init setters, and record classes.
public sealed class Meter {
  public int Count { get; private set; }
  public int Scale { get; init; }
  public int Total => this.Count * this.Scale;

  public Meter(int scale) => this.Scale = scale;

  public void Increment(int by) => this.Count += by;
}

public sealed record PointRecord(int X, int Y);

public static class ClassesShowcase {
  public static int Measure(int scale, int steps) {
    var meter = new Meter(scale);
    meter.Increment(steps);
    var p1 = new PointRecord(meter.Count, meter.Total);
    var p2 = new PointRecord(meter.Count, meter.Total);
    return p1 == p2 ? meter.Total : -1;
  }
}
