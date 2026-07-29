// Switch enum property value
using System.Collections.Generic;

namespace MgsvModBldr.Tools.Fox.Enums
{
    public class FoxSwitch : IFoxEnum
    {
        protected readonly List<FoxEnumValue> _values;

        protected FoxSwitch()
        {
            _values = new List<FoxEnumValue>();
        }

        public string Name { get; set; }

        public IEnumerable<FoxEnumValue> Values
        {
            get { return _values; }
        }
    }
}
