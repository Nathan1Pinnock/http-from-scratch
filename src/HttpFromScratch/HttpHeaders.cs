using System.Collections;

namespace HttpFromScratch;

/// <summary>
/// An ordered, case-insensitive collection of header fields. A name may appear
/// more than once, which is how HTTP carries list-valued headers on the wire.
/// </summary>
public sealed class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _fields = [];

    public int Count => _fields.Count;

    public void Add(string name, string value) => _fields.Add(new(name, value));

    /// <summary>Replaces every existing value of <paramref name="name"/> with a single one.</summary>
    public void Set(string name, string value)
    {
        Remove(name);
        Add(name, value);
    }

    public bool Remove(string name) => _fields.RemoveAll(f => Is(f.Key, name)) > 0;

    public bool Contains(string name) => _fields.Exists(f => Is(f.Key, name));

    /// <summary>The first value sent for <paramref name="name"/>, or null if there was none.</summary>
    public string? this[string name]
    {
        get
        {
            foreach (var field in _fields)
            {
                if (Is(field.Key, name))
                {
                    return field.Value;
                }
            }

            return null;
        }
    }

    /// <summary>Every value sent for <paramref name="name"/>, in wire order.</summary>
    public IEnumerable<string> GetValues(string name)
    {
        foreach (var field in _fields)
        {
            if (Is(field.Key, name))
            {
                yield return field.Value;
            }
        }
    }

    /// <summary>
    /// True if any value of <paramref name="name"/>, treated as a comma-separated
    /// list, contains <paramref name="token"/>. This is how "Connection: close"
    /// and friends are meant to be read.
    /// </summary>
    public bool HasToken(string name, string token)
    {
        foreach (var value in GetValues(name))
        {
            foreach (var part in value.Split(','))
            {
                if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
