using System;

namespace MgsvModBldr.Tools.Ftex.Exceptions
{
    [Serializable]
    public class AssertionFailedException : FtexToolException
    {
        public AssertionFailedException(string message) : base(message)
        {
        }
    }
}
