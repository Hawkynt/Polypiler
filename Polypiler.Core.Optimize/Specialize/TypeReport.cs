using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Optimize.Specialize;

/// <summary>The lattice level a place of a translated module has, by the type of the IR it was given (ADR 0300 decision 2).</summary>
public enum TypeLevel {

  /// <summary>One type of the IR: a number, a truth value, a string, an array, a collection, a function.</summary>
  Exact,

  /// <summary>A declared struct, class, interface or enum.</summary>
  Nominal,

  /// <summary>A generated record type of a shape (#1588).</summary>
  Shape,

  /// <summary>An optional value.</summary>
  Optional,

  /// <summary>A closed union.</summary>
  Union,

  /// <summary>The dynamic type.</summary>
  Variant

}

/// <summary>A place of a translated module as the report counts it.</summary>
/// <param name="Function">The function.</param>
/// <param name="Name">The local or parameter, or the function's name for its result.</param>
/// <param name="Kind">Whether it is a local, a parameter or a result.</param>
/// <param name="Level">The level of its type.</param>
/// <param name="Reason">For a variant, the first join that made it one, or why it stays one.</param>
public sealed record TypedPlace(string Function, string Name, DynamicPlaceKind Kind, TypeLevel Level, string? Reason) {

  /// <summary>The certainty of a place of the output: proven where it has a type, dynamic where it is a variant.</summary>
  public Certainty Certainty => this.Level == TypeLevel.Variant ? Certainty.Dynamic : Certainty.Proven;

}

/// <summary>
/// How statically typed a translation is (ADR 0300 decision 7, #1590): every local, parameter and
/// result of the module's own functions - a language's helpers aside - counted by the level of the
/// type the output gives it and by its certainty, per function and for the module, with the static
/// ratio of the places below a variant, and for every place that stayed a variant the reason the
/// analysis found. A module without places has the ratio 1.
/// </summary>
public static class TypeReport {

  /// <summary>The version of the layout of the report, which <c>spec/schemas/type-report.schema.json</c> describes.</summary>
  public const int SchemaVersion = 1;

  /// <summary>The places of a translated module.</summary>
  /// <param name="module">The module as it is emitted.</param>
  /// <param name="semantics">The semantics table of its source language, which names the helpers and finds the reasons; <see langword="null"/> for a language without one.</param>
  /// <returns>The places, function by function in module order.</returns>
  public static IReadOnlyList<TypedPlace> Places(IrModule module, IDynamicSemantics? semantics) {
    ArgumentNullException.ThrowIfNull(module);
    var reasons = semantics is null ? [] : DeDynamicize.Analyse(module, semantics, DeDynamicizeOptions.Default).Places
      .GroupBy(place => (place.Function, place.Name, place.Kind)).ToDictionary(group => group.Key, group => group.First().Reason);
    var places = new List<TypedPlace>();
    foreach (var function in module.Functions.Where(function => semantics?.IsHelper(function.Name) != true)) {
      TypedPlace Place(string name, DynamicPlaceKind kind, IrType type) {
        var level = LevelOf(type);
        return new(function.Name, name, kind, level, level != TypeLevel.Variant ? null
          : reasons.GetValueOrDefault((function.Name, name, kind)) ?? (semantics is null ? "the source language has no semantics table to type it" : "dynamic in the source"));
      }

      places.AddRange(function.Parameters.Select(parameter => Place(parameter.Name, DynamicPlaceKind.Parameter, parameter.Type)));
      places.AddRange(function.Locals.Where(local => !function.Parameters.Contains(local)).Select(local => Place(local.Name, DynamicPlaceKind.Local, local.Type)));
      if (function.ReturnType != IrType.Void)
        places.Add(Place(function.Name, DynamicPlaceKind.Result, function.ReturnType));
    }

    return places;
  }

  /// <summary>The level of a type of the IR.</summary>
  /// <param name="type">The type.</param>
  /// <returns>The level.</returns>
  public static TypeLevel LevelOf(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type.IsVariant ? TypeLevel.Variant
      : type.IsUnion ? TypeLevel.Union
      : type.IsOptional ? TypeLevel.Optional
      : type.Declaration is not null ? TypeLevel.Nominal
      : TypeLevel.Exact;
  }

  /// <summary>The static ratio of places: those below a variant over all, rounded to two decimals; 1 for no place.</summary>
  /// <param name="places">The places.</param>
  /// <returns>The ratio.</returns>
  public static decimal Ratio(IReadOnlyCollection<TypedPlace> places) {
    ArgumentNullException.ThrowIfNull(places);
    return places.Count == 0 ? 1m : Math.Round((decimal)places.Count(place => place.Level != TypeLevel.Variant) / places.Count, 2, MidpointRounding.ToEven);
  }

  /// <summary>The report as JSON.</summary>
  /// <param name="module">The module as it is emitted.</param>
  /// <param name="semantics">The semantics table of its source language, or <see langword="null"/>.</param>
  /// <returns>The JSON text, indented, with LF line ends and a final line end.</returns>
  public static string Json(IrModule module, IDynamicSemantics? semantics) {
    var places = Places(module, semantics);
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" })) {
      writer.WriteStartObject();
      writer.WriteNumber("schemaVersion", SchemaVersion);
      writer.WriteString("module", module.Name);
      writer.WritePropertyName("summary");
      Counts(writer, places);
      writer.WriteStartArray("functions");
      foreach (var group in places.GroupBy(place => place.Function)) {
        var own = group.ToList();
        writer.WriteStartObject();
        writer.WriteString("name", group.Key);
        writer.WritePropertyName("counts");
        Counts(writer, own);
        writer.WriteStartArray("variants");
        foreach (var place in own.Where(place => place.Level == TypeLevel.Variant)) {
          writer.WriteStartObject();
          writer.WriteString("name", place.Name);
          writer.WriteString("kind", place.Kind.ToString().ToLowerInvariant());
          writer.WriteString("reason", place.Reason);
          writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
      }

      writer.WriteEndArray();
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
  }

  private static void Counts(Utf8JsonWriter writer, IReadOnlyList<TypedPlace> places) {
    writer.WriteStartObject();
    writer.WriteNumber("places", places.Count);
    writer.WriteStartObject("levels");
    foreach (var level in Enum.GetValues<TypeLevel>())
      writer.WriteNumber(level.ToString().ToLowerInvariant(), places.Count(place => place.Level == level));
    writer.WriteEndObject();
    writer.WriteStartObject("certainty");
    foreach (var certainty in Enum.GetValues<Certainty>().Reverse())
      writer.WriteNumber(certainty.ToString().ToLowerInvariant(), places.Count(place => place.Certainty == certainty));
    writer.WriteEndObject();
    writer.WriteNumber("staticRatio", decimal.Parse(Ratio(places).ToString("0.00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    writer.WriteEndObject();
  }

}
