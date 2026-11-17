using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeMemory.Core;

/// <summary>
/// Light word-form normalization for search: Russian (Snowball algorithm), Ukrainian (suffix stripping)
/// and English (plural/-ed/-ing). Stems are stored in a separate FTS column and the query is matched both
/// as typed and by stem, so "клавиатурой" finds "клавиатуры" and "decisions" finds "decision".
/// It is a heuristic: it does not understand meaning and can occasionally join unrelated words.
/// </summary>
public static partial class Stemmer
{
    [GeneratedRegex(@"[\p{L}\p{N}_]+")] private static partial Regex WordRegex();

    /// <summary>Space-separated unique stems of all words in the texts (stored in the FTS "stems" column).</summary>
    public static string StemText(params string?[] texts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match m in WordRegex().Matches(text))
            {
                var s = Stem(m.Value);
                if (s.Length == 0 || !seen.Add(s)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(s);
            }
        }
        return sb.ToString();
    }

    /// <summary>Stem of one word (lower-case). Words that are not Cyrillic or Latin are returned lower-cased.</summary>
    public static string Stem(string word)
    {
        var w = word.ToLowerInvariant().Replace('ё', 'е');
        if (w.Length < 4 || w.Any(char.IsDigit)) return w;
        if (IsCyrillic(w))
            return w.IndexOfAny(['і', 'ї', 'є', 'ґ']) >= 0 ? Ukrainian(w) : Russian(w);
        if (IsLatin(w)) return English(w);
        return w;
    }

    private static bool IsCyrillic(string w) => w.All(c => c is >= 'а' and <= 'я' or 'і' or 'ї' or 'є' or 'ґ' or '\'' or '_');
    private static bool IsLatin(string w) => w.All(c => c is >= 'a' and <= 'z');

    // ------------------------------------------------------------------ Russian (Snowball)

    private const string RuVowels = "аеиоуыэюя";

    private static readonly string[] RuGerund1 = ["вшись", "вши", "в"];
    private static readonly string[] RuGerund2 = ["ившись", "ывшись", "ивши", "ывши", "ив", "ыв"];
    private static readonly string[] RuReflexive = ["ся", "сь"];
    private static readonly string[] RuAdjective =
        ["ими", "ыми", "его", "ого", "ему", "ому", "ее", "ие", "ые", "ое", "ей", "ий", "ый", "ой", "ем", "им", "ым", "ом", "их", "ых", "ую", "юю", "ая", "яя", "ою", "ею"];
    private static readonly string[] RuParticiple1 = ["ем", "нн", "вш", "ющ", "щ"];
    private static readonly string[] RuParticiple2 = ["ивш", "ывш", "ующ"];
    private static readonly string[] RuVerb1 = ["ете", "йте", "ешь", "нно", "ла", "на", "ли", "ем", "ло", "но", "ет", "ют", "ны", "ть", "й", "л", "н"];
    private static readonly string[] RuVerb2 =
        ["ейте", "уйте", "ила", "ыла", "ена", "ите", "или", "ыли", "ило", "ыло", "ено", "ует", "уют", "ены", "ить", "ыть", "ишь",
         "ей", "уй", "ил", "ыл", "им", "ым", "ен", "ят", "ит", "ыт", "ую", "ю"];
    private static readonly string[] RuNoun =
        ["иями", "ями", "ами", "иях", "ией", "иям", "ием", "ев", "ов", "ие", "ье", "еи", "ии", "ей", "ой", "ий", "ям", "ем", "ам", "ом",
         "ах", "ях", "ию", "ью", "ия", "ья", "а", "е", "и", "й", "о", "у", "ы", "ь", "ю", "я"];
    private static readonly string[] RuDerivational = ["ость", "ост"];
    private static readonly string[] RuSuperlative = ["ейше", "ейш"];

    private static string Russian(string w)
    {
        var rv = RegionAfterFirstVowel(w, RuVowels);
        if (rv >= w.Length) return w;
        var r1 = R(w, 0, RuVowels);
        var r2 = R(w, r1, RuVowels);
        var s = new StringBuilder(w);

        // Step 1
        if (!RemovePreceded(s, rv, RuGerund1, "ая") && !Remove(s, rv, RuGerund2))
        {
            Remove(s, rv, RuReflexive);
            if (Remove(s, rv, RuAdjective))
            {
                if (!RemovePreceded(s, rv, RuParticiple1, "ая")) Remove(s, rv, RuParticiple2);
            }
            else if (!RemovePreceded(s, rv, RuVerb1, "ая") && !Remove(s, rv, RuVerb2))
            {
                Remove(s, rv, RuNoun);
            }
        }
        // Step 2
        Remove(s, rv, ["и"]);
        // Step 3
        Remove(s, Math.Max(r2, rv), RuDerivational);
        // Step 4
        if (EndsWith(s, rv, "нн")) s.Length--;
        else if (Remove(s, rv, RuSuperlative)) { if (EndsWith(s, rv, "нн")) s.Length--; }
        else Remove(s, rv, ["ь"]);
        return s.ToString();
    }

    // ------------------------------------------------------------------ Ukrainian (light suffix stripping)

    private const string UkVowels = "аеєиіїоуюя";
    private static readonly string[] UkReflexive = ["ся", "сь", "ись"];
    private static readonly string[] UkEndings =
        ["ами", "ями", "ові", "еві", "ого", "ому", "ими", "іми", "ій", "ою", "ею", "єю",
         "ати", "ити", "іти", "ують", "юють", "ають", "яють", "ить", "ать", "ла", "ли", "ло", "ів", "їв", "ах", "ях", "ям", "ям", "ом", "ем", "єм", "им", "ім",
         "их", "іх", "ий", "ої", "ей", "у", "ю", "а", "я", "і", "ї", "е", "є", "о", "и", "ь"];

    private static string Ukrainian(string w)
    {
        var rv = RegionAfterFirstVowel(w, UkVowels);
        if (rv >= w.Length) return w;
        var s = new StringBuilder(w);
        Remove(s, rv, UkReflexive);
        Remove(s, rv, UkEndings);
        return s.ToString();
    }

    // ------------------------------------------------------------------ English (light)

    private static string English(string w)
    {
        if (w.EndsWith("ies") && w.Length > 4) return w[..^3] + "y";
        if (w.EndsWith("sses")) return w[..^2];
        if (w.EndsWith("ss") || w.EndsWith("us") || w.EndsWith("is")) return w;
        if (w.EndsWith("ing") && HasVowel(w[..^3]) && w.Length - 3 >= 3) return Undouble(w[..^3]);
        if (w.EndsWith("ed") && HasVowel(w[..^2]) && w.Length - 2 >= 3) return Undouble(w[..^2]);
        if (w.EndsWith("es") && (w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("xes") || w.EndsWith("zes"))) return w[..^2];
        if (w.EndsWith('s')) return w[..^1];
        return w;
    }

    private static bool HasVowel(string s) => s.IndexOfAny(['a', 'e', 'i', 'o', 'u', 'y']) >= 0;

    private static string Undouble(string s) =>
        s.Length >= 3 && s[^1] == s[^2] && s[^1] is not ('l' or 's' or 'z' or 'e') ? s[..^1] : s;

    // ------------------------------------------------------------------ helpers

    private static int RegionAfterFirstVowel(string w, string vowels)
    {
        for (var i = 0; i < w.Length; i++)
            if (vowels.Contains(w[i])) return i + 1;
        return w.Length;
    }

    /// <summary>Snowball R region: after the first non-vowel that follows a vowel, starting at <paramref name="from"/>.</summary>
    private static int R(string w, int from, string vowels)
    {
        for (var i = from + 1; i < w.Length; i++)
            if (!vowels.Contains(w[i]) && vowels.Contains(w[i - 1])) return i + 1;
        return w.Length;
    }

    private static bool EndsWith(StringBuilder s, int region, string suffix)
    {
        if (s.Length - suffix.Length < region) return false;
        for (var i = 0; i < suffix.Length; i++)
            if (s[s.Length - suffix.Length + i] != suffix[i]) return false;
        return true;
    }

    /// <summary>Removes the longest matching suffix that lies inside the region.</summary>
    private static bool Remove(StringBuilder s, int region, string[] suffixes)
    {
        string? best = null;
        foreach (var x in suffixes)
            if ((best is null || x.Length > best.Length) && EndsWith(s, region, x)) best = x;
        if (best is null) return false;
        s.Length -= best.Length;
        return true;
    }

    /// <summary>Like <see cref="Remove"/>, but the suffix must be preceded by one of <paramref name="preceding"/> (which stays).</summary>
    private static bool RemovePreceded(StringBuilder s, int region, string[] suffixes, string preceding)
    {
        string? best = null;
        foreach (var x in suffixes)
        {
            if (best is not null && x.Length <= best.Length) continue;
            if (!EndsWith(s, region, x)) continue;
            var before = s.Length - x.Length - 1;
            if (before >= region && preceding.Contains(s[before])) best = x;
        }
        if (best is null) return false;
        s.Length -= best.Length;
        return true;
    }
}
