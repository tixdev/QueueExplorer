namespace DynaQueueExplorer.Core.Security;

/// <summary>Case-sensitive glob matching supporting <c>*</c> (any sequence) and <c>?</c> (any single character).</summary>
public static class Glob
{
    public static bool IsMatch(string pattern, string value)
    {
        int p = 0, v = 0, starP = -1, starV = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v]))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starV = v;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                v = ++starV;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
