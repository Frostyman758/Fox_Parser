using System;

namespace MgsvModBldr.Tools.Ftex.Exceptions
{
    [Serializable]
    public abstract class FtexToolException : ApplicationException
    {
        protected FtexToolException(string message) : base(message)
        {
        }
    }
}
