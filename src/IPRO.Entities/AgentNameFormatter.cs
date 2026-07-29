namespace IPRO.Entities;

// One place that decides how an agent's name and designation combine, because the Designation
// field holds two kinds of value with opposite placement rules:
//
//   an honorific  -- "Ms."  -> goes BEFORE the name, no punctuation:  "Ms. Raniah Motamed"
//   a credential  -- "CFP"  -> goes AFTER the name, comma-separated:  "Raniah Motamed, CFP"
//
// Every template used to make this call on its own, which is how "Raniah Motamed  Ms." and a
// designation stranded on the line below both ended up shipping. Templates now ask this helper.
public static class AgentNameFormatter
{
    // Matched after stripping a trailing period, so "Dr" and "Dr." both count.
    private static readonly HashSet<string> Honorifics = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "miss", "mx", "dr", "prof", "professor",
        "rev", "reverend", "fr", "sr", "hon", "sir", "dame", "madam", "madame",
        "m", "mme", "mlle", // French forms, for Quebec agents
    };

    public static bool IsHonorific(string? designation)
    {
        var value = designation?.Trim().TrimEnd('.').Trim();
        return !string.IsNullOrEmpty(value) && Honorifics.Contains(value);
    }

    /// <summary>Agent's name with the designation placed correctly for what it is.</summary>
    public static string FullName(AgentUser agent) =>
        FullName(agent.FirstName, agent.LastName, agent.Designation);

    public static string FullName(string? firstName, string? lastName, string? designation)
    {
        var name = $"{firstName} {lastName}".Trim();
        var value = designation?.Trim();
        if (string.IsNullOrEmpty(value)) return name;
        if (string.IsNullOrEmpty(name)) return value;

        // Punctuation is left exactly as the agent typed it. Adding a period would produce
        // "Miss." and "Sir.", which are wrong, and "Ms" without one is correct British style --
        // so this decides placement only, never spelling.
        return IsHonorific(value) ? $"{value} {name}" : $"{name}, {value}";
    }

    /// <summary>
    /// The designation only when it belongs on a separate line -- i.e. a credential the caller
    /// chose not to append inline. Honorifics return empty, because they're already in the name.
    /// </summary>
    public static string TrailingDetail(string? designation) =>
        IsHonorific(designation) ? string.Empty : designation?.Trim() ?? string.Empty;
}
