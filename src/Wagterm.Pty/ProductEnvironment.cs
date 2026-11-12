namespace Wagterm.Pty;

public static class ProductEnvironment
{
    public static bool IsUpstreamVariable(string name)
        => name.Equals("AGWINTERM", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("AGWINTERM_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("AGW_", StringComparison.OrdinalIgnoreCase);

    public static void RemoveUpstreamVariables<T>(IDictionary<string, T> environment)
    {
        foreach (string name in environment.Keys.Where(IsUpstreamVariable).ToArray())
            environment.Remove(name);
    }
}
