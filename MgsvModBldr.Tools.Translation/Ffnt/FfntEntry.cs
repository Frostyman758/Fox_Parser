// Based on FfntTool/Ffnt/FfntEntry.cs
using System.IO;

namespace MgsvModBldr.Tools.Translation.Ffnt
{
    public abstract class FfntEntry
    {
        public abstract FfntEntryHeader GetHeader(Stream outputStream);
        public abstract void Write(Stream outputStream);
    }
}
