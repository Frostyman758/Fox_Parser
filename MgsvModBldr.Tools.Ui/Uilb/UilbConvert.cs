// Uilb GZ to TPP transform
// 09/07/2026
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Ui.Uilb;

public static class UilbConvert
{
    /// <summary>StrCode64→low32 truncation (== StrCode32); paths→PathCode64; rest byte-copied.</summary>
    public static UilbFile GzToTpp(UilbFile gz)
    {
        if (gz.IsTpp) throw new ArgumentException("already TPP");
        var t = new UilbFile
        {
            Version = 1, BlobAbsent = gz.BlobAbsent,
            ModelTable = gz.ModelTable, AnimTable = gz.AnimTable,
            CameraTable = gz.CameraTable, GraphTable = gz.GraphTable,
            PreLists = gz.PreLists,
        };
        foreach (var id in gz.GzIds) t.TppIds.Add((uint)id);
        foreach (var p in gz.GzPaths) t.TppPathIds.Add(GameHash.PathCode(p));
        return t;
    }
}
