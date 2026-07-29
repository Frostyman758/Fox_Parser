// StaticArray property container
using MgsvModBldr.Tools.Fox.Types;

namespace MgsvModBldr.Tools.Fox.Containers
{
    public class FoxStaticArray<T> : FoxListBase<T> where T : IFoxValue, new()
    {
    }
}
