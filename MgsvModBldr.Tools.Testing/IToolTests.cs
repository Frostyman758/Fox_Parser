// Per-tool test suite contract
namespace MgsvModBldr.Tools.Testing;

public interface IToolTests
{
    string Name { get; }

    void Harvest();

    (int pass, int fail) Run();
}
