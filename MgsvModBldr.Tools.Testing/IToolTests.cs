namespace MgsvModBldr.Tools.Testing;

/// <summary>
/// One per ported tool. The Cli test wrapper collects every
/// implementation, filters by <see cref="Name"/>, optionally calls
/// <see cref="Harvest"/> to refresh fixtures from Z:\, then <see cref="Run"/>
/// to evaluate the byte-exact gate. Keeping each tool's verification next
/// to its tool means a tool can be lifted out (lib + .Tests) on its own.
/// </summary>
public interface IToolTests
{
    /// <summary>Short filter token, e.g. "fsop", "fox", "subp".</summary>
    string Name { get; }

    /// <summary>Refresh this tool's fixtures from Z:\ / reference oracle.</summary>
    void Harvest();

    /// <summary>Run the gate; print per-file results; return (pass, fail).</summary>
    (int pass, int fail) Run();
}
