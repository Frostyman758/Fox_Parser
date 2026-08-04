# GZ → TPP animation port — handoff (03/08/2026)

Porting Ground Zeroes player animations into MGSV TPP. **Working in-game.** Uncommitted.

---

## State: what is live right now — updated 03/08/2026, NOT yet confirmed in-game

Three packs in `Z:\tpp\release\pack\player\motion\#windx11\`. All entry tables byte-identical
to stock (same hashes, same order, 0 index shifts); `.mog` untouched everywhere.

| pack | size | GZ bodies | foot-sync L/R (stock → new) |
|---|---|---|---|
| `player2_resident_motion.fpk` | 10,536,800 | 892 (+20 TPP clips kept, see below) | 350/350 → **647/645** |
| `player2_location_motion.fpk` | 9,479,200 | 505 across 12 mtars | unchanged or higher in every mtar |
| `player2_mtbs_motion.fpk` | 6,935,040 | 489 across 11 mtars | unchanged or higher in every mtar |

**1,886 clip slots now carry GZ bodies**, up from 912.

Backups of all three: `C:\rsearch\gzanim_backup\*.orig`. Copying them back restores stock.

The previous resident build (912 replaced, 627/625) is superseded — it had a real defect:

### `.enchnk` deletion bug — FIXED in TranscodeCmd

The transcoder deleted the template's `.enchnk` whenever the GZ clip carried no events:

```csharp
if (events[i].Length > 0) File.WriteAllBytes(en, events[i]);
else if (File.Exists(en)) File.Delete(en);      // <-- stripped TPP's foot-sync
```

That turns a clip that HAS foot-plant events into one that hasn't, which is the movement-lock
failure mode. It silently hit **20 clips in the deployed resident build**, 12 in `cqc` and 2 in
`ladder`. `ladder` went 2/2 → 0/0 — all foot-sync gone.

`TranscodeCmd` now refuses that swap and keeps TPP's whole clip instead (reported as
`kept TPP clip (GZ has no foot-sync)`). Regression suite after the change: **2227 passed /
28 failed = the exact pre-existing baseline.**

Lesson: the earlier session verified event counts *in aggregate* (350 → 627 looked like a
gain) and missed a per-mtar loss. Compare per file, not in total.

---

## Commands

```bash
modbldr-tools ganis <file.mtar> [-d dict] [--probe] [--probehash]
modbldr-tools transcode <gz.mtar> --template <tpp_v2.mtar> [-o out.mtar] [--merge] [--override] [--no-add] [--limit N]
```

The deployed build was made with:

```bash
modbldr-tools transcode player.mtar --template live_template.mtar --merge --override --no-add -o player2_resident.mtar
```

`--no-add` is **required**: see "Why additive builds break".

Rebuild loop: unpack the fpk (`modbldr-tools <file.fpk>`), swap the mtar in `<stem>_fpk\Assets\tpp\motion\mtar\player2\`, repack (`modbldr-tools <file>.fpk.json`), copy to `Z:\`.

---

## Uncommitted code (Fox_parser)

| File | What |
|---|---|
| `MgsvModBldr.Tools.Index/MtarGaniNames.cs` | GZ + TPP gani name hashing (`GzHash`, `IsGzLayout`, `GzTypeId`) |
| `MgsvModBldr.Tools.Mtar/Utility/NameResolver.cs` | **rewritten** — flavour-aware, fixes GZ repack corruption |
| `MgsvModBldr.Tools.Mtar/Mtar/MtarFile.cs`, `MtarFile2.cs` | `hashFlavor` + `hashFlavorSpecified` |
| `MgsvModBldr.Tools.Mtar/Mtar/MtarGaniFile.cs`, `MtarGaniFile2.cs` | `isGz` flag set on read |
| `MgsvModBldr.Tools.Mtar/Transcode/GaniV1.cs` | v1 FoxData reader (layout, blobs, **events**) |
| `MgsvModBldr.Tools.Mtar/Transcode/GaniV2.cs` | v2 gani body writer |
| `MgsvModBldr.Tools.Mtar/Transcode/TrackLayout.cs` | `.trk` layout reader |
| `MgsvModBldr.Tools.Cli/TranscodeCmd.cs` | `transcode` verb |
| `MgsvModBldr.Tools.Cli/GaniCmd.cs` | `ganis` verb (+ stride fix) |
| `MgsvModBldr.Tools.Cli/Cli.cs` | verb registration |

**Test suite: 2227 passed / 28 failed = exact pre-existing baseline, zero regression.**
The 28 are long-standing `xml differs from MtarTool reference` cases, unrelated.

---

## Verified results

- **GZ gani naming: 100%** — player 2406/2406, SoldierGz 1160/1160, chico/paz/facial all 100%. TPP unregressed (player2_resident 1253/1253).
- **Transcode is lossless** — 2406/2406 ganis; frameCount, unitFlags and **all 134,736 keyframe blobs** preserved byte-for-byte (modulo 16-byte alignment padding).
- **GZ repack no longer corrupts** — 112/112 and 68/68 hashes *and* payloads survive a round trip.

---

## Formats decoded

### GZ gani name hash (differs from TPP)

From `stringid_raw_hash` in `C:\rsearch\MgsGroundZeroes.exe.c` (~line 3005795):

```
h    = CityHash64(str, len+1)              // buffer INCLUDES the NUL
seed = (sbyte)str[0] * 0x10000 + len       // FIRST char + length
out  = HashLen16(h - K2, seed) & 0xFFFFFFFFFFFF   // 48-bit
```

`-0x622015f714c7d297` is CityHash `kMul`; `+0x651e95c4d06fbfb1` is `-K2`. So it equals
`CityHash64WithSeeds(str+"\0", K2, seed) & 48bit` — **identical to the existing `G0sHash.HashFileName`**.

Three deltas vs TPP: seed from the FIRST char + length (not last-8-reversed), the NUL is hashed, 48-bit not 50-bit.
**String form: the FULL `/Assets/…` path WITH leading slash, extension dropped.** GZ does NOT strip `/Assets/`; TPP does.
That one difference was the entire blocker.

Entry encoding is the same as `.g0s`: `hash48 + (typeId << 52)`, **typeId 11 = .gani**.
(The old "ext code 22" was TPP's `>>51` misapplied: `0xB0 >> 3`.)

### mtar containers

- **type 1** iff first entry's payload magic == `0x0BFCA2D2`, else **type 2** (`MtarConverter.GetMtarType`)
- **Entry stride: type 1 = 16 bytes, type 2 = 32 bytes.** Reading a type-2 table at 16 yields double the entries, every second one a record half.
- **Container type ≠ game.** TPP ships type-1 mtars (`TppRaven_layers`, `Ocelot2_facial`) using TPP hashing. Never infer flavour from `MtarFile` vs `MtarFile2` — detect from the hash.
- v1 = FoxData `ROOT→MOTION→UNIT` with inline blobs. v2 = shared `.trk` + flat bodies.

v2 gani body:
```
u32 frameCount | u8 pad | u8 paramCount | u16 pad
paramCount x { u32 name, f32 value }
unitCount x u8 unitFlags        (align 4)
segCount  x { u8 componentBitSize, u24 self-relative offset }   0 = no data
16 bytes pad
...blobs (16-byte aligned)...
```
`.trk` = 16-byte wrapper (magic `0x4FBDAAEF`) then `TrackHeader` at `0x10`.

**Blobs copy verbatim** — v1 and v2 use the same keyframe encoding.

### Why the player port worked

GZ and TPP player rigs are **identical**: 18 units, 56 segments, frameScale 5, all 18 unit name hashes + segment counts + types matching in order. So TPP's own `.trk` is reused verbatim — no bone remap.

CGWorld 2015_10 p.055 states it outright: *"人型のリグは基本的に「MGS4」から変わっていない"* — the humanoid rig is unchanged since MGS4. Non-humanoid rigs were generated per-case with the Procedural Rig Generator (wheels, dog legs), **so vehicles/animals will NOT match — check before attempting.**

### `.enchnk` = the event list (this was the movement-lock fix)

Not an "end chunk". Event data. `.exchnk` contains **zero** sync events; `.enchnk` had 349 L / 349 R.

```
MtarFile::SetupAnimFileAndEventCache
  → ImplAnimGraphFootFitEventCacheData::BuildNewTable(fox::anim::EventInfo*, ...)
      reads EventInfo->IsLoop, FrameCount
      filters event names MTEV_AG_SYNC_L (0x3450f814) / MTEV_AG_SYNC_R (0xd962d8ad)
      per section: GetStartFrame / GetEndFrame
      emits SectionInfo { i32 StartFrame, i32 EndFrame, FootSide Side }   (12 bytes)
      end < start on a looping clip → split into two sections
```
The motion graph is partitioned by which foot is forward (CGWorld p.054: 右足前 / 左足前). No sync events → no foot phase → locomotion states never transition out → **movement locks**.

**GZ carries these events** (370 L / 369 R) in FoxData node **`0x1622762d`**, payload byte-compatible with `.enchnk` (same magic `0x0BFE2CF6`, count, offset table). It runs from its node's data offset to the end of the gani entry — the node's own size field reads 0. The transcoder now extracts and writes it.

Observed layout inside:
```
14 f8 50 34   MTEV_AG_SYNC_L
01 00 00 00   1 section
0e 00 00 00   startFrame 14
35 00 00 00   endFrame   53
ad d8 62 d9   MTEV_AG_SYNC_R ... (wraps: start 53 → end 14)
```

### `.mog` — `FOXMOTIONGRAPH` (decoded and verified)

```
0x00 char[16] Signature "FOXMOTIONGRAPH\0"   (0xA7 padding fill throughout)
0x18 u8   AnimLayerCount        = 9
0x19 u8   UnknownD              = 5   (GetGraphName requires > 4)
0x1C u32  GraphCount            = 4
0x20 u32  GraphHeadersOffset    = 0x20   → headers at hdr + 0x20 + 0x20 = 0x40
0x24 u32  DefaultAnimParamsCount= 0
0x28 i32  DefaultAnimParamsOffset = 0xA66D8
0x2C u32  ParamsRelated         = 5
0x30 u32  ParamsOffset          = 0xA6920   → param chain at +0x30 = 0xA6950

graph headers  0x40, GraphCount x 0x38:
   +0x00 u32 dataOffset   +0x04 u8 AnimLayerCount   ... MaskName
   observed: 2, 1, 5, 1  → sums to 9 == header AnimLayerCount  ✔ cross-check

param chain    { u32 NextParamOffset (self-rel), u32 Name, u32 Count, i32 DataOffset }
   0xA6950  name=0x859bd53e count=4  → graph names   (Count == GraphCount ✔)
   0xA6970  name=0x185ebb9f count=63

anim hash table  0xA6828 .. 0xB2EC0, packed 8-byte stride, 1213 unique entry hashes
                 (interleaved with the param block — hashes resume at 0xAE660)

MotionGraphFormatNode
   0x00 u32 UnkAdjCount            adjacency / transition count
   0x04 u32 UnkAdjOffset           adjacency list
   0x08 u32 Maybe_ConnectNodeCount
   0x0C i32 BlendNodesOffset
   0x14 u8  Type
   0x18     AnimParamBinaryString Tag
   0x20     AnimParamBinarySet<ushort> CompTagThing
   0x28 i32 SelfOffset
```

**The mog references animations BY HASH** (1213 of 1285 entry hashes present), not by index —
`MtarFile::GetAnimFileByIndex` resolves internally.

---

## Why additive builds break (do not repeat)

Adding the GZ-only clips produced: flying around the map, inputs ignored, wrong animations.

1. Those clips have **no mog node referencing them**, so nothing can ever play them — zero benefit.
2. ~~Growing the mtar desynchronises the mog, which carries counts and sized tables against it~~
   — **WRONG, disproved 03/08/2026.** `fox::anim::MtarFile::GetAnimFile(PathId)` calls
   `SearchGani`, a **binary search over the entry table keyed on the 64-bit `Path.id`**
   (dev decompile ~line 5713921). Animation indices are derived at runtime and never stored,
   so entry count cannot desync anything. The 1285/1213 count-matching was coincidence — those
   are common u16/u32 values in a 700 KB file.

Ruled out as causes: duplicate hashes (0 in every build), wrong sort order (all correctly hash-sorted).

**The real cause is still unidentified.** Leading suspect, untested: the added entries were
written with GZ 48-bit ids (`hash48 | 11<<52`, top 16 bits `0x00b0`) instead of TPP PathCode64
(top 16 bits `0xfc5x`). Mixing the two id spaces in one table breaks the sort ordering the
binary search depends on, which returns the *wrong animation* for lookups that used to work —
matching the observed symptoms far better than a count desync. To test: rebuild additively and
check the entry table is monotonically ascending as unsigned u64 and that every id has a TPP
ext code.

**Use `--no-add`. Replace only.**

---

## Open problems + exact next steps

1. **Adding the 975 GZ-only animations.** (Not 1,494 — that figure compared GZ against
   `player2_resident` alone. TPP spreads player animation over 30 mtars in three packs. Against
   the real union: GZ 2,406, TPP 2,263, **shared 1,431, GZ-only 975, TPP-only 832**.)
   Nodes carry **adjacency lists**, so a new animation needs a node *and* every state that should
   reach it needs its adjacency list extended. This is motion-graph **authoring**, not a file patch.
   See `MOG_FORMAT.md` for the format work done so far — the PathId pool, the param chain and the
   tag map are decoded; the state-node body is not.
2. **Heli port (GZ Krokodil/`TppHeliEast_layers` → TPP West/UTH).** GZ has `TppHeliEast_layers.mtar` (12,800 B, `gntn_heli.fpk`) and `TppHeliWest_layers.mtar` (17,392 B, `e20040_area.fpk`) in `data_02.g0s`. **First check whether East and West share a track layout** — non-humanoid rigs were per-case, so assume not until proven. Also needs a `--map` name-mapping option (transcoder currently matches identical names only). Retargeting was offline Softimage work; there is no runtime retarget.
3. **Commit.** Nothing is committed in Fox_parser or FoxBrowser.

---

## Gotchas that cost real time

- **Entry names must keep the leading slash** (`/Assets/…`). `NameResolver` matches on `"/Assets/"`; trim it and every hash packs as **0**. Build stays clean — only output verification catches it.
- **`Path.Combine` discards the base** when the second part starts with `/`. Concatenate like `Import` does.
- **`Set-Location` does not move .NET's CWD** — use absolute paths with `modbldr-tools`.
- **`Compare-Object` on multi-MB byte arrays hangs for minutes.** Use `Get-FileHash`. (A "10-minute stall" was this, not the tool — unpack+repack is ~0.2 s.)
- **The live mtar is not the one in `chunk0.dat`.** `Z:\`'s fpk carries the `0\00.dat` build: **1,285** entries (30 unnamed), vs chunk0's 1,253. Templating from the wrong one silently deletes 32 animations. **Always template from the live fpk.**
- Same-named GZ/TPP animations have identical segment types, bit sizes and blob lengths but **different bytes** — they were re-authored between games. That is the point of the port, not a bug.

## Method notes

- 165k candidate string-variant probes returned 0 on the GZ hash. The exe answered it in minutes. **Go to the decompile early.**
- Two wrong theories shipped as broken builds (additive; `.exchnk`-is-optional). **Verify the artifact, not the build log.**
- `C:\rsearch\FoxClaude\wiki\` named `Fox.Anim.MtarFile` / `Fox.MotionGraph.MogFile` as separate engine classes — worth checking before theorising.

## Key paths

```
GZ install        C:\Program Files (x86)\Steam\steamapps\common\Metal Gear Solid Ground Zeroes
TPP packed data   C:\Program Files (x86)\Steam\steamapps\common\MGS_TPP - Copy\master
TPP live (Z:)     Z:\tpp\release\pack\player\motion\#windx11\
GZ decompile      C:\rsearch\MgsGroundZeroes.exe.c
TPP dev decompile C:\rsearch\Tpp_main_win64.exe.c
backup            C:\rsearch\gzanim_backup\player2_resident_motion.fpk.orig
```
