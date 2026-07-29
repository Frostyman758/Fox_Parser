// Missing sidecar exception
using System;

namespace MgsvModBldr.Tools.Ftex.Exceptions
{
    [Serializable]
    public class MissingFtexsFileException : FtexToolException
    {
        public MissingFtexsFileException(string message) : base(message)
        {
        }
    }
}
