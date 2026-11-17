using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>
/// Turns a machine vector into the input and expected bytes of a general test vector, so machine
/// fixtures flow through the byte-comparing verification oracles: canonical JSON, UTF-8, no whitespace,
/// object keys in ordinal order. The expected bytes hold only what a projection observes, so two traces
/// that agree on it encode to the same bytes; the wildcard of an expected undefined flag is the one
/// thing the bytes cannot express, which leaves <see cref="TraceComparer"/> the authority on a verdict.
/// </summary>
public static class MachineVectorCodec {

  private static readonly JsonWriterOptions _options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

  /// <summary>The input bytes of a vector: its call, if it has one, and its initial state.</summary>
  /// <param name="vector">The vector.</param>
  /// <returns>The canonical JSON.</returns>
  public static byte[] EncodeInput(MachineVector vector) {
    ArgumentNullException.ThrowIfNull(vector);
    return Canonical(writer => {
      writer.WriteStartObject();
      if (vector.Call is { } call) {
        writer.WriteStartObject("call");
        writer.WriteString("contract", call.Contract);
        writer.WriteStartArray("arguments");
        foreach (var argument in call.Arguments)
          writer.WriteStringValue(TraceEvent.Hex(argument));
        writer.WriteEndArray();
        writer.WriteEndObject();
      }

      MachineVectorFile.WriteState(writer, vector.Initial);
      writer.WriteEndObject();
    });
  }

  /// <summary>
  /// The bytes of what a projection observes of a trace: the outcome, each observed register and flag
  /// (<see langword="null"/> where the trace has none), each observed byte, the compared events and, when
  /// observed, the cycle count.
  /// </summary>
  /// <param name="trace">The trace.</param>
  /// <param name="projection">What is observed.</param>
  /// <returns>The canonical JSON.</returns>
  public static byte[] EncodeTrace(ObservableTrace trace, TraceProjection projection) {
    ArgumentNullException.ThrowIfNull(trace);
    ArgumentNullException.ThrowIfNull(projection);
    return Canonical(writer => {
      writer.WriteStartObject();
      writer.WriteString("outcome", TraceComparer.Name(trace.Outcome));
      writer.WriteStartObject("registers");
      foreach (var name in projection.Registers)
        if (trace.Final.Registers.TryGetValue(name, out var value))
          writer.WriteString(name, TraceEvent.Hex(value));
        else
          writer.WriteNull(name);
      writer.WriteEndObject();
      writer.WriteStartObject("flags");
      foreach (var name in projection.Flags)
        if (trace.Final.Flags.GetValueOrDefault(name) is { } flag)
          writer.WriteBoolean(name, flag);
        else
          writer.WriteNull(name);
      writer.WriteEndObject();
      writer.WriteStartArray("memory");
      foreach (var span in projection.Memory)
        for (var offset = 0; offset < span.Length; ++offset)
          if (trace.Final.ByteAt(span.Address + (ulong)offset) is { } item)
            writer.WriteNumberValue(item);
          else
            writer.WriteNullValue();
      writer.WriteEndArray();
      writer.WriteStartArray("events");
      foreach (var item in TraceComparer.Observed(trace, projection))
        MachineVectorFile.WriteEvent(writer, item);
      writer.WriteEndArray();
      if (projection.Cycles)
        if (trace.Cycles is { } cycles)
          writer.WriteNumber("cycles", cycles);
        else
          writer.WriteNull("cycles");
      writer.WriteEndObject();
    });
  }

  /// <summary>The expected bytes of a vector under a projection: <see cref="EncodeTrace"/> of its expected trace.</summary>
  /// <param name="vector">The vector.</param>
  /// <param name="projection">What is observed.</param>
  /// <returns>The canonical JSON.</returns>
  public static byte[] EncodeExpected(MachineVector vector, TraceProjection projection) {
    ArgumentNullException.ThrowIfNull(vector);
    return EncodeTrace(vector.Expected, projection);
  }

  private static byte[] Canonical(Action<Utf8JsonWriter> write) {
    using var draft = new MemoryStream();
    using (var writer = new Utf8JsonWriter(draft, _options))
      write(writer);

    var node = JsonNode.Parse(draft.ToArray());
    using var result = new MemoryStream();
    using (var writer = new Utf8JsonWriter(result, _options))
      Sorted(writer, node);
    return result.ToArray();
  }

  private static void Sorted(Utf8JsonWriter writer, JsonNode? node) {
    switch (node) {
      case JsonObject item:
        writer.WriteStartObject();
        foreach (var (name, value) in item.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
          writer.WritePropertyName(name);
          Sorted(writer, value);
        }

        writer.WriteEndObject();
        break;
      case JsonArray list:
        writer.WriteStartArray();
        foreach (var value in list)
          Sorted(writer, value);
        writer.WriteEndArray();
        break;
      case null:
        writer.WriteNullValue();
        break;
      default:
        node.WriteTo(writer);
        break;
    }
  }

}
