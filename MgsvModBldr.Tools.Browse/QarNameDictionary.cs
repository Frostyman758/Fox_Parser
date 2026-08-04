// Process-wide loader for the sidecar name dictionaries
using MgsvModBldr.Tools.Qar;
using MgsvModBldr.Tools.G0s;
using MgsvModBldr.Tools.Fpk.Gz;

namespace MgsvModBldr.Tools.Browse;

// Lazy loader for qar_dictionary.txt — the table that maps QAR path hashes
// back to readable filenames. QAR and PFTXS trees use it to label entries;
// without it they fall back to hash-named paths (still browsable). The host
// must point us at the directory holding the dictionaries (for a NativeAOT
// DLL, AppContext.BaseDirectory is the HOST process, not the dll's folder).
public static class QarNameDictionary
{
    private static readonly object _lock = new();
    private static string? _dir;
    private static QarDictionary? _dict;
    private static bool _tried;

    // Set (or clear, with null) where the sidecar dictionaries live. Resets the
    // cache so the next Get() re-loads from the new location. The same directory
    // is handed to G0sHash (gzs_dictionary.txt) and FpkDictionary
    // (fpk_dictionary.txt) so every resolver looks in one place.
    public static void SetDir(string? dir)
    {
        lock (_lock) { _dir = dir; _tried = false; _dict = null; }
        G0sHash.DictionaryDirectory = dir;       // gzs_dictionary.txt (GZ .g0s names)
        FpkDictionary.DictionaryDirectory = dir; // fpk_dictionary.txt (GZ fpk names)
    }

    // Locked across the load so a background prewarm and a first browse can't
    // race — the second caller blocks until the table is ready, never sees null.
    public static QarDictionary? Get()
    {
        lock (_lock)
        {
            if (_tried) return _dict;
            _tried = true;
            try
            {
                var path = _dir is not null
                    ? Path.Combine(_dir, QarDictionary.DictionaryFileName)
                    : null;                   // null => QarDictionary's default lookup
                _dict = QarDictionary.Load(path);
            }
            catch { _dict = null; }
            return _dict;
        }
    }

    // Drop every cached name dictionary (qar/gzs/fpk) so its memory is freed
    // when no archive is open. They lazily reload on the next browse; the dir
    // is kept. (Re-assigning DictionaryDirectory to its current value is how
    // G0sHash/FpkDictionary expose a cache reset.)
    public static void ClearAll()
    {
        lock (_lock) { _tried = false; _dict = null; }       // qar (ours)
        QarDictionary.DropCache();                            // its own static cache too
        GzHashNames.Clear();                                  // GZ index over qar's blob
        G0sHash.DictionaryDirectory = G0sHash.DictionaryDirectory;          // gzs
        FpkDictionary.DictionaryDirectory = FpkDictionary.DictionaryDirectory; // fpk
    }
}
