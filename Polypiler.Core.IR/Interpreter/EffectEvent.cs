using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// One observable effect of an interpreted run (#119, PRD §17.1): what an external model or an evaluator did to the world
/// outside the program, appended to <see cref="ExecutionResult.Trace"/> in execution order.
/// </summary>
/// <param name="Channel">What the effect reaches: a device, a stream, a port (<c>vic2</c>, <c>stdout</c>).</param>
/// <param name="Kind">What it does there (<c>Device.Write</c>, <c>Stream.Write</c>).</param>
/// <param name="Payload">The values it carries, in order.</param>
public sealed record EffectEvent(string Channel, string Kind, ImmutableArray<IrValue> Payload) {

  /// <summary>Two events are equal when channel, kind and every value of the payload are.</summary>
  /// <param name="other">The other event.</param>
  /// <returns>Whether they are equal.</returns>
  public bool Equals(EffectEvent? other)
    => other is not null && this.Channel == other.Channel && this.Kind == other.Kind && this.Payload.SequenceEqual(other.Payload);

  /// <summary>A hash over channel, kind and payload.</summary>
  /// <returns>The hash.</returns>
  public override int GetHashCode() {
    var hash = new HashCode();
    hash.Add(this.Channel);
    hash.Add(this.Kind);
    foreach (var value in this.Payload)
      hash.Add(value);

    return hash.ToHashCode();
  }

  /// <summary>The event as a trace prints it: <c>Kind:Channel</c> and the values of the payload, separated by spaces.</summary>
  /// <returns>The text, for example <c>Device.Write:vic2 53280 0</c>.</returns>
  public override string ToString() => string.Join(' ', ((IEnumerable<string>)[$"{this.Kind}:{this.Channel}"]).Concat(this.Payload.Select(value => value.ToString())));

}

/// <summary>Where the evaluators and the external models of a run append their <see cref="EffectEvent"/>s.</summary>
public interface IEffectSink {

  /// <summary>Appends an event to the trace of the run.</summary>
  /// <param name="effect">The event.</param>
  void Emit(EffectEvent effect);

}

/// <summary>
/// What the functions outside the module do (#119): the interpreter runs no native code, so every call of a foreign
/// function (<see cref="IrForeignCall"/>) in <see cref="IrInterpreter.Run"/> is resolved through the model of the run.
/// A call the model does not model, or a run without a model, ends <see cref="ExecutionOutcome.Aborted"/> with
/// <c>PLY0123</c>.
/// </summary>
public interface IExternalModel {

  /// <summary>Performs a call of a foreign function, when the model models it.</summary>
  /// <param name="function">The declaration of the function.</param>
  /// <param name="arguments">The arguments, evaluated left to right, one per parameter.</param>
  /// <param name="effects">Where the call appends what it does.</param>
  /// <param name="result">The value returned, or <see langword="null"/> for a function returning <see cref="IrType.Void"/>.</param>
  /// <returns>Whether the model models the function; on <see langword="false"/> the call has done nothing.</returns>
  bool TryCall(IrForeignFunction function, ImmutableArray<IrValue> arguments, IEffectSink effects, out IrValue? result);

}

/// <summary>An <see cref="IExternalModel"/> built from one handler per foreign function, by the function's name in the module.</summary>
public sealed class ExternalModel : IExternalModel {

  private readonly Dictionary<string, Func<ImmutableArray<IrValue>, IEffectSink, IrValue?>> _handlers = new(StringComparer.Ordinal);

  /// <summary>Models a function by a handler.</summary>
  /// <param name="function">The name of the foreign function in the module.</param>
  /// <param name="handler">What a call does: it takes the arguments and the sink, and returns the value, <see langword="null"/> for none.</param>
  /// <returns>This model.</returns>
  public ExternalModel Add(string function, Func<ImmutableArray<IrValue>, IEffectSink, IrValue?> handler) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(handler);
    this._handlers[function] = handler;
    return this;
  }

  /// <summary>Models a function returning nothing as one event per call, whose payload is the arguments.</summary>
  /// <param name="function">The name of the foreign function in the module.</param>
  /// <param name="channel">The channel of the event.</param>
  /// <param name="kind">The kind of the event.</param>
  /// <returns>This model.</returns>
  public ExternalModel AddEvent(string function, string channel, string kind)
    => this.Add(function, (arguments, effects) => {
      effects.Emit(new(channel, kind, arguments));
      return null;
    });

  /// <inheritdoc/>
  public bool TryCall(IrForeignFunction function, ImmutableArray<IrValue> arguments, IEffectSink effects, out IrValue? result) {
    ArgumentNullException.ThrowIfNull(function);
    result = null;
    if (!this._handlers.TryGetValue(function.Name, out var handler))
      return false;

    result = handler(arguments, effects);
    return true;
  }

}
