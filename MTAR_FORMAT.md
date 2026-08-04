# MTAR — motion archive

Field names are the engine's own, taken from `fox::anim::MtarHeader`, `MtarTableList2`,
`TrackHeader` and `MtarFlags` in the dev-exe decompile (`C:\rsearch\Tpp_main_win64.exe.c`).
Verified against every stock player archive: **30/30 repack byte-identical**.

## File layout

```
0x00                      MtarHeader (0x20)
0x20                      MtarTableList2[FileCount]      (0x20 each, sorted by Path.id)
CommonInfoOffset          CommonInfo node chain (see below)
first UnitTracksOffset    gani bodies, each optionally followed by its motion-point tracks
last section              motion-event packages (EvpHeader), contiguous, run to EOF
```

The file is always a whole number of **16-byte lines** — pad the tail or the last event package
runs off a ragged edge.

## MtarHeader — 0x20

```
0x00 u32  Version              201403250 on TPP/GZ player archives
0x04 u32  FileCount
0x08 u16  UnitCount            tracks (bones); mirrors the common TrackHeader
0x0A u16  SegmentCount         mirrors the common TrackHeader
0x0C u16  ShaderNodeCount      0 in every archive measured
0x0E u16  ShaderUnitCount      0 in every archive measured
0x10 u16  MotionPointUnitCount largest motion-point unit count of any clip here
0x12 u16  Flags                MtarFlags
0x14 u32  CommonInfoOffset     SELF-RELATIVE FROM 0x10
0x18..0x1F unused
```

`MotionPointUnitCount` sizes the engine's per-archive motion-point storage. Importing clips with
more units than it declares overruns that storage — corrupt animation, dead inputs. **0 is a
sentinel**: leave it, never lower it, raise it only when a clip needs more.

`CommonInfoOffset` is relative to the address of the `MotionPointUnitCount` field, not to the file
start: `MtarFile::GetCommonTrackHeader` returns `&MotionPointUnitCount + CommonInfoOffset`.

```c
enum MtarFlags { NEW = 0x1000, HAS_SKEL_LIST = 0x4000 };
```

`NEW` selects the 32-byte `MtarTableList2` entry; without it the engine reads the 16-byte v1
table. Observed across 49 fixtures: `0x0` (26 files, v1), `0x1000` (7), `0x5000` (16).

## MtarTableList2 — 0x20, the v2 entry

```
0x00 u64  Path                     PathId; the table MUST be sorted ascending on it
0x08 u32  UnitTracksOffset         absolute file offset of the gani body
0x0C u16  UnitTracksDataSize       in 16-byte lines
0x0E u16  MotionPointTracksOffset
0x10 u16  MotionPointTracksDataSize in 16-byte lines
0x12 u16  ShaderTracksOffset       0 on every entry measured
0x14..0x17 unused
0x18 u32  MotionEventsOffset       absolute offset of this clip's EvpHeader
0x1C..0x1F unused
```

**The table is sorted by hash, the payload is not.** `MtarFile::GetAnimFile` runs `SearchGani`, a
binary search over `Path.id`, so the table order is mandatory — but Konami lays the gani bodies out
in an unrelated order (93 of 94 entries differ from table order in `player2_cqc`). A packer that
writes bodies in table order relocates every clip in the file. Motion-event packages follow the
same order as the bodies.

## What the payload files are

| unpacked as | engine type | what it is |
|---|---|---|
| `.gani` | `TrackMiniHeader` | the animation itself |
| `.mtp` | motion-point tracks | root trajectory; stripping it makes clips slide |
| `.enchnk` | `EvpHeader` | event package — carries `MTEV_AG_SYNC_L/R` foot plants |
| `.trk` | node `0x4fbdaaef` | the common track layout every clip shares |
| `.chnk` | the remaining nodes | motion-point table and/or skeleton list |

`TrackHeader` (0x14): `int UnitCount, uint SegmentCount, u16 TrackId, byte UnknownA, byte UnknownB,
int FrameCount, sbyte FrameRate`. Its `UnitCount`/`SegmentCount` mirror the file header's, which is
what makes them a usable compatibility check: a clip set can only be dropped into an archive whose
track layout matches.

All three payloads are decoded and modelled — the XML carries typed `<TrackInfo>`, `<Units>`,
`<Segments>`, `<MotionPointUnits>` and `<SkeletonList>` elements, not `.trk`/`.chnk` blobs.
`NextNodeOffset` is always `align16(16 + DataSize)`, and the gap from the chain to the first gani
body follows the same rule, so no padding is stored: it is derived.

## CommonInfo — a chain of named nodes

Everything between the entry table and the first gani body is a **singly linked list of
`MtarMiniDataNode`**, starting at `CommonInfoOffset` (from the FILE START — the +0x10 base in
`GetCommonTrackHeader` is just that function returning the first node's payload).
`AnimFile::GetSkeletonList2` walks it looking for a node by name.

```
MtarMiniDataNode (0x10 header, payload follows)
0x00 u32 Name            StrCode32
0x04 u32 DataSize        payload bytes after this 16-byte header
0x08 u32 NextNodeOffset  self-relative from &Name; 0 = last node
0x0C u32 (0)
```

There is no separate ".chnk" section — the old reader misread node 0's fields as
"signature / length / chunkOffset" and treated everything after it as an opaque chunk.

| Name | payload |
|---|---|
| `0x4fbdaaef` | common track info — the shared `TrackHeader` and its units |
| `0x3b9a7784` | motion-point unit table |
| `0x91e4534b` | skeleton list (`HAS_SKEL_LIST` only) |

### 0x4fbdaaef — common track info

```
TrackHeader (0x14)
u32 unitOffsets[UnitCount]     SELF-RELATIVE TO THE TRACKHEADER START, 0 = absent
per unit:
  TrackUnit  { u32 Name (StrCode32); u8 SegmentCount; u8 Flags; u16 pad }
  TrackData[SegmentCount] { i32 DataOffset; i16 SegmentId;
                            u8 Packed_Type4_NextEntryOffset4; u8 ComponentBitSize }
```

The walk is `TrackControl::GetTrackControlSize(TrackHeader*)`: unit at
`&common->UnitCount + unitOffsets[i]`, segments at `unit + 1`, segment type is
`*(u8*)(segment + 6) & 0xf`. `TrackUnitFlags { LOOP=1, HERMITE_VECTOR_INTERPOLATION=2,
IS_STATIC=4 }`.

Verified on player2_resident: 18 units whose `SegmentCount`s sum to **56**, exactly the
`TrackHeader.SegmentCount`.

### 0x3b9a7784 — motion-point unit table

```
u32 Count
[Count] x { u32 MotionPointName, u32 BoneName }    both StrCode32
```

`DataSize == 4 + 8*Count` exactly. player2_ladder declares two: `MTP_RHAND_A` (0x632c5c53) ->
`SKL_023_RHAND`, `MTP_LHAND_A` (0x47bbfc94) -> `SKL_013_LHAND` — the hand attachments, which is
why stripping motion points made the player slide off ladders.

**This count is NOT `MtarHeader.MotionPointUnitCount`.** In stock archives the header is always
less than or equal to the table count and is frequently the 0 sentinel (trashbox 0/4, cqc 0/6,
gimmick 5/11). The header bounds units per CLIP; the table is the archive-wide vocabulary.

### 0x91e4534b — skeleton list

```
u32 Count
u32 BoneName[Count]      StrCode32
```

TppHeliEast declares 21: `SKL_000_ROOT`, `SKL_002_GUN`, `SKL_004_ROTORCONT`, `SKL_006_SUBROTOUT`,
`SKL_008_LLWDOOR`, `SKL_010_RUPDOOR`... — the helicopter rig.

## Sizing rules — derive, never scan

Both original sizing routines guessed by scanning for magic values, and both could run off the end
of the file. Sizes come from layout boundaries instead:

- **event package**: packages are contiguous and are the last section, so each runs to the next
  one's offset and the final one to EOF.
- **chunk**: runs from its offset to the first gani body.

The scans survive only as bounded fallbacks.

## Authoring

A new archive needs a hand-written XML plus the payload files; the table, hashes, offsets, sizes
and alignment are all derived. Verified: a 3-entry archive built from scratch unpacks correctly
with a properly hash-sorted table.

The one hard requirement is a **`.trk`** — it pins the track layout every clip must match, so a new
archive for an existing rig reuses that rig's `.trk`. A novel skeleton needs the `.trk` payload
format decoded, which is not done.

## Moving ganis between the two games

```
raw Ground Zeroes   Version 201304220   Flags 0x0      -> 16-byte v1 table, NO common TrackHeader
MGSV TPP            Version 201403250   Flags 0x1000   -> 32-byte v2 table, ONE shared TrackHeader
```

`MtarFile::GetCommonTrackHeader` returns null unless `Flags & NEW`, so **a GZ archive has no shared
track layout — each gani carries its own. A TPP gani does not; it depends on the archive's common
TrackHeader.** That is the real portability boundary, and it is why clips must be transcoded rather
than copied.

What makes the port possible anyway: the human rig is unchanged between the games —
GZ `hostage_layers` and TPP `player2_*` both declare **UnitCount 18, SegmentCount 56**. Different
rigs are not interchangeable (GZ `TppHeliEast_layers` is UnitCount 22, SegmentCount 24).

So moving a clip needs three things to line up:
1. **container version** — rebuild the entry table, v1 (16 bytes) <-> v2 (32 bytes);
2. **track layout** — source and destination `UnitCount`/`SegmentCount` must match, which is the
   check the transcoder already enforces against the destination `.trk`;
3. **motion-point units** — every `MTP_*` the clip uses must be declared in the destination's
   MotionPointUnitTable, and `MotionPointUnitCount` must cover it.

## Importing clips — the table has to grow

`.mtp` tracks use the SAME layout as the shared track info: `TrackHeader`, unit offsets at +0x14
self-relative to the payload start, each unit's first u32 being an `MTP_*` name. That is
`AnimFile::GetMotionPointIndex` walking it.

A clip's units must all appear in the destination's MotionPointUnitTable, or
`GetMotionPointParent` returns 0 and the point has no bone to hang off. Konami's archives are
always self-consistent — player2_ladder declares 2 and uses 2. Importing GZ clips broke that: the
same archive then used 9. `transcode` now merges each imported clip's units into the table, taking
the bone from the source gani's own parent records (FoxData node `0xf0f377d9`, pairs indexed by
unit index, second word = bone — exactly what `GetMotionPointParent` reads). Verified: **0
undeclared units across all 17 converted archives**, 131 units added.

## Names, not hashes

Bone and motion-point ids are StrCode32 and both vocabularies ship as dictionaries, so the XML
carries `SKL_023_RHAND` and `MTP_LHAND_A`. Writing accepts either form — anything that is not
exactly 8 hex digits is hashed — so an edited file still round-trips byte for byte.

The 28 `MTP_*` names came out of GZ archives with `ganis <mtar> --mtp-names`, which harvests the
literal strings and forward-hashes each one. That needs no knowledge of GZ's name-table layout,
which is just as well: `0xeab12d21` is the `.sand` ROOT node in TPP, and no engine function reads
a motion-point name table, so a parse of it could not be checked against anything.

The 18 track-unit names in the player rig resolve in no dictionary and are left as hashes.

## Still open

- `TrackData.DataOffset` is stored but never dereferenced by the runtime — `ChangeTimeFast`,
  `GetTrackControlSize` and the TrackControl ctor all index keyframes by `SegmentId` instead and
  only use `&record->DataOffset` as the record's own address. Carried as authored.
- The skeleton list's fixed 12-byte trailer. `MakeMatchList2` reads exactly `Count` hashes and
  stops, so nothing in the engine consumes it; purpose unidentified.
