using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// Every setting the configuration page can write comes back as it was written.
/// </summary>
/// <remarks>
/// Written after the language list was found growing by its own default on every load. That was
/// one property, but the failure was a property of the <i>serializer</i>, so naming the settings
/// this checks would only ever catch the ones somebody thought of. It walks the configuration
/// instead: every property the page's JSON can reach, on the document and on each of its nested
/// objects, set to something that is not the default, put through
/// <see cref="XmlSerializer"/> exactly as Jellyfin persists it, and read back.
///
/// <para>Clamping setters are not false positives here: each property is read back after being
/// assigned, so what the round trip is compared against is the value the setter accepted, not the
/// value offered to it.</para>
/// </remarks>
public class ConfigSurvivesASaveTests
{
    private static PluginConfiguration RoundTrip(PluginConfiguration config)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var buffer = new StringWriter();
        serializer.Serialize(buffer, config);
        return (PluginConfiguration)serializer.Deserialize(new StringReader(buffer.ToString()))!;
    }

    /// <summary>The settings the page reads and writes; the XML-only storage members are not among them.</summary>
    private static IEnumerable<PropertyInfo> PageProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    private static bool IsNested(Type type) =>
        type.Namespace?.StartsWith("Jellyfin.Plugin.DialogueBoost.Configuration", StringComparison.Ordinal) == true
        && !type.IsEnum;

    /// <summary>A value this property does not already hold, so a reverted save is visible.</summary>
    private static object? Distinctive(PropertyInfo property, object current, int seed)
    {
        var type = property.PropertyType;

        if (type == typeof(bool))
        {
            return !(bool)property.GetValue(current)!;
        }

        if (type == typeof(int))
        {
            // Inside every bound the configuration declares, so a clamp cannot be mistaken for a revert.
            return 3 + (seed % 5);
        }

        if (type == typeof(double))
        {
            return 2.5 + (seed % 4);
        }

        if (type == typeof(string))
        {
            return "value-" + seed.ToString(CultureInfo.InvariantCulture);
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().ToList();
            var held = property.GetValue(current);
            return values.FirstOrDefault(v => !Equals(v, held)) ?? held;
        }

        if (type == typeof(List<string>))
        {
            return new List<string> { "aa" + seed, "bb" + seed };
        }

        if (type == typeof(List<Guid>))
        {
            return new List<Guid> { new Guid(seed, 0, 0, new byte[8]) };
        }

        return null;
    }

    /// <summary>Assigns every reachable setting, and returns what each one was actually set to.</summary>
    private static Dictionary<string, object?> Fill(object node, string prefix, ref int seed)
    {
        var expected = new Dictionary<string, object?>();

        foreach (var property in PageProperties(node.GetType()))
        {
            var path = prefix + property.Name;

            if (IsNested(property.PropertyType))
            {
                foreach (var pair in Fill(property.GetValue(node)!, path + ".", ref seed))
                {
                    expected[pair.Key] = pair.Value;
                }

                continue;
            }

            var value = Distinctive(property, node, ++seed);
            Assert.True(value is not null, $"{path} is a {property.PropertyType.Name}, which this test does not know how to vary — teach it, or the setting goes unchecked.");

            property.SetValue(node, value);

            // What the setter accepted, which is not always what it was offered.
            expected[path] = Describe(property.GetValue(node));
        }

        return expected;
    }

    private static Dictionary<string, object?> Read(object node, string prefix)
    {
        var actual = new Dictionary<string, object?>();

        foreach (var property in PageProperties(node.GetType()))
        {
            var path = prefix + property.Name;

            if (IsNested(property.PropertyType))
            {
                foreach (var pair in Read(property.GetValue(node)!, path + "."))
                {
                    actual[pair.Key] = pair.Value;
                }

                continue;
            }

            actual[path] = Describe(property.GetValue(node));
        }

        return actual;
    }

    private static object? Describe(object? value) => value switch
    {
        null => null,
        string s => s,
        IEnumerable list => string.Join("|", list.Cast<object>().Select(x => x?.ToString())),
        _ => value.ToString()
    };

    /// <summary>Nothing the page can set is quietly changed by being saved.</summary>
    [Fact]
    public void EverySettingThePageCanWriteSurvivesASave()
    {
        var config = new PluginConfiguration();
        var seed = 0;
        var expected = Fill(config, string.Empty, ref seed);

        var actual = Read(RoundTrip(config), string.Empty);

        // A walk that reached nothing would pass in silence. These are the shapes that can break:
        // the setting that did break, a clamped int, an enum, a list of ids, and a nested object.
        Assert.Contains("DialogueBoostProfile.ProcessLanguages", expected.Keys);
        Assert.Contains("MaxConcurrentJobs", expected.Keys);
        Assert.Contains("WatchedBy", expected.Keys);
        Assert.Contains("WatchedByUserIds", expected.Keys);
        Assert.Contains("TrackSelectionRules.AllowedCodecs", expected.Keys);
        Assert.Contains("CustomProfile.ProcessLanguages", expected.Keys);
        Assert.True(expected.Count >= 40, $"only {expected.Count} settings were reached.");

        foreach (var pair in expected)
        {
            Assert.True(
                Equals(pair.Value, actual[pair.Key]),
                $"{pair.Key} was saved as '{pair.Value}' and read back as '{actual[pair.Key]}'.");
        }
    }

    /// <summary>
    /// And saving a fresh install changes nothing either — the shape the language list failed in.
    /// A setting that drifts on an unedited save moves <c>ParamsHash</c> and re-encodes the library.
    /// </summary>
    [Fact]
    public void ShippedDefaultsAreUnchangedByRepeatedSaves()
    {
        var expected = Read(new PluginConfiguration(), string.Empty);

        var config = new PluginConfiguration();
        for (var i = 0; i < 5; i++)
        {
            config = RoundTrip(config);
        }

        foreach (var pair in expected)
        {
            var actual = Read(config, string.Empty);
            Assert.True(
                Equals(pair.Value, actual[pair.Key]),
                $"{pair.Key} ships as '{pair.Value}' but reads as '{actual[pair.Key]}' after five saves.");
        }
    }
}
