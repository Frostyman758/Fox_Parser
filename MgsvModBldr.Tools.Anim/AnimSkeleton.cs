// Bind skeleton the pose evaluator needs
// 04/08/2026
using System.Numerics;

namespace MgsvModBldr.Tools.Anim;

// AnimSkinner and FrdvFile only ever read four things off a model: each bone's name index,
// its parent, and its LOCAL and WORLD bind positions — plus the name table. This is that
// surface and nothing else, so the pose math can run in the parser without dragging the whole
// FMDL reader (meshes, materials, textures) along with it.
//
// Field names and shapes match FoxBrowser's FmdlBone/FmdlModel EXACTLY, so the ported files
// differ from the originals only by type name. Populate it from whatever has a bind pose.
public readonly record struct AnimBone(int NameIndex, int ParentIndex,
    Vector4 LocalPosition, Vector4 WorldPosition);

public sealed class AnimSkeleton
{
    /// <summary>Bind bones, in the model's own order — .frdv indexes this directly.</summary>
    public List<AnimBone> Bones { get; } = new();

    /// <summary>Name table; a bone's name is Names[bone.NameIndex] (StrCode32 in the low 32).</summary>
    public List<ulong> Names { get; } = new();
}
