using System;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tests.Optimize.Cost;

/// <summary>A cost profile's text with the keys of every mapping in a random order; lists keep theirs.</summary>
internal static class ProfileShuffle {

  public static string Shuffle(string yaml, Random random) {
    var stream = new YamlStream();
    stream.Load(new StringReader(yaml));
    var shuffled = new YamlStream(new YamlDocument(Shuffle(stream.Documents[0].RootNode, random)));
    using var writer = new StringWriter();
    shuffled.Save(writer, false);
    return writer.ToString();
  }

  private static YamlNode Shuffle(YamlNode node, Random random) => node switch {
    YamlMappingNode mapping => new YamlMappingNode(mapping.Children.OrderBy(_ => random.Next()).Select(pair => new System.Collections.Generic.KeyValuePair<YamlNode, YamlNode>(pair.Key, Shuffle(pair.Value, random)))),
    YamlSequenceNode sequence => new YamlSequenceNode(sequence.Children.Select(child => Shuffle(child, random))),
    _ => node
  };

}
