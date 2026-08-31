using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// Where a row sits in the tree the plugin serves: a library, then one <see cref="ScopeSegment"/>
/// per level below it.
/// </summary>
/// <remarks>
/// A path, rather than an item id, is what a chosen row can be stored as. An id names one of the
/// nine entities a show was split into; a path names the show, and keeps naming it when a tenth
/// release folder lands beside them. It also makes the marks the tree paints a string
/// comparison — a row is *selected* when a stored path equals it, *included* when a stored path is a
/// prefix of it, and *partially selected* when it is a prefix of a stored path — instead of a walk
/// up two different parent trees.
///
/// The wire form is the segments joined by <c>/</c>, with <c>%</c> and <c>/</c> escaped inside a
/// segment so a film called <c>Face/Off</c> cannot invent a level.
/// </remarks>
public sealed class ScopePath : IEquatable<ScopePath>
{
    private const char Separator = '/';

    private readonly string _text;

    private ScopePath(IReadOnlyList<string> segments, string text)
    {
        Segments = segments;
        _text = text;
    }

    /// <summary>
    /// Gets the path of no segments: the tree's root, whose children are the libraries.
    /// </summary>
    public static ScopePath Root { get; } = new(Array.Empty<string>(), string.Empty);

    /// <summary>
    /// Gets the segments, outermost first.
    /// </summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>
    /// Gets a value indicating whether this is the root.
    /// </summary>
    public bool IsRoot => Segments.Count == 0;

    /// <summary>
    /// Reads a path back from its wire form. Empty and null both mean the root; empty segments are
    /// dropped, so a stray or doubled separator cannot produce a level that matches nothing.
    /// </summary>
    public static ScopePath Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Root;
        }

        var segments = text
            .Split(Separator)
            .Where(part => part.Length > 0)
            .Select(Unescape)
            .ToList();

        return segments.Count == 0 ? Root : new ScopePath(segments, Format(segments));
    }

    /// <summary>
    /// The path of a row one level under this one.
    /// </summary>
    public ScopePath Append(string segment)
    {
        var segments = new List<string>(Segments.Count + 1);
        segments.AddRange(Segments);
        segments.Add(segment);
        return new ScopePath(segments, Format(segments));
    }

    /// <summary>
    /// Whether this path names the same row as <paramref name="other"/>, or one that contains it.
    /// A path contains itself, which is what makes an exactly chosen row also a covered row.
    /// </summary>
    public bool Contains(ScopePath other)
    {
        if (other is null || other.Segments.Count < Segments.Count)
        {
            return false;
        }

        for (int i = 0; i < Segments.Count; i++)
        {
            if (!string.Equals(Segments[i], other.Segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(ScopePath? other) =>
        other is not null && string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ScopePath);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_text);

    /// <inheritdoc />
    public override string ToString() => _text;

    private static string Format(IReadOnlyList<string> segments) =>
        string.Join(Separator, segments.Select(Escape));

    private static string Escape(string segment)
    {
        if (!segment.Contains('%') && !segment.Contains(Separator))
        {
            return segment;
        }

        var builder = new StringBuilder(segment.Length + 8);
        foreach (char c in segment)
        {
            builder.Append(c switch
            {
                '%' => "%25",
                Separator => "%2F",
                _ => c.ToString()
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// One left-to-right pass, never two replaces: unescaping <c>%2F</c> and then <c>%25</c> would
    /// re-read its own output, and a name containing the literal text <c>%2F</c> would come back
    /// different from the one that went in.
    /// </summary>
    private static string Unescape(string segment)
    {
        if (!segment.Contains('%'))
        {
            return segment;
        }

        var builder = new StringBuilder(segment.Length);
        for (int i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '%' && i + 2 < segment.Length)
            {
                var escape = segment.AsSpan(i, 3);
                if (escape.Equals("%2F", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append(Separator);
                    i += 2;
                    continue;
                }

                if (escape.Equals("%25", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append('%');
                    i += 2;
                    continue;
                }
            }

            builder.Append(segment[i]);
        }

        return builder.ToString();
    }
}
