// Uilb layout container, GZ and TPP
// 09/07/2026
namespace MgsvModBldr.Tools.Ui.Uilb;

public sealed class UilbFile
{
    public const int ModelStride = 0x64, AnimStride = 0x14, CameraStride = 0x34, GraphStride = 0x50;

    public byte Version;                       // 0 GZ, 1 TPP
    public bool BlobAbsent;                    // empty file: @0x24/@0x28 = -1
    public byte[] ModelTable = [];
    public byte[] AnimTable = [];
    public byte[] CameraTable = [];
    public byte[] GraphTable = [];
    public byte[] PreLists = [];               // blob head: child/connection u16 index lists

    public List<ulong> GzIds = new();          // StrCode64 names
    public List<uint> TppIds = new();          // StrCode32 names
    public List<string> GzPaths = new();       // plaintext asset paths
    public List<ulong> TppPathIds = new();     // PathCode64

    public bool IsTpp => Version == 1;
    public int ModelCount => ModelTable.Length / ModelStride;
    public int AnimCount => AnimTable.Length / AnimStride;
    public int CameraCount => CameraTable.Length / CameraStride;
    public int GraphCount => GraphTable.Length / GraphStride;
    public int IdCount => IsTpp ? TppIds.Count : GzIds.Count;
    public int PathCount => IsTpp ? TppPathIds.Count : GzPaths.Count;
}
