// DynamicArray property container
using MgsvModBldr.Tools.Fox.Types;

namespace MgsvModBldr.Tools.Fox.Containers
{
    public class FoxDynamicArray<T> : FoxListBase<T> where T : IFoxValue, new()
    {
    }
}
