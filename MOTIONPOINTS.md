# Motion points (root trajectory) — GZ vs TPP
03/08/2026

Motion points are the trajectory tracks that move a character's root and its attach locators.
Strip them and anything glued to a surface (ladder, wall climb, vehicle) plays in place while
the game keeps moving you — the "slides across the map" symptom.

## Why the port lost them

`GaniV1`/`GaniV2` in `Tools.Mtar/Transcode` parse **units, segments, params and events only**.
Nothing reads or writes motion points, so every transcoded clip shipped with
`MotionPointTracksDataSize = 0`. Measured against stock: 715 of `player2_resident`'s replaced
clips and 4 of 5 in `player2_ladder` lost them.

Guarded for now in `TranscodeCmd` (skip replacement when the TPP clip has motion points, keyed
on `MtarGaniNames.NameHash` — **not** by table index, `outFile.files` order does not match the
raw entry table). Cost: the port drops from 1,886 clip slots to 353.

## Where the data lives

**Entry table** (`MtarTableList2`, stride 32): `MotionPointTracksOffset` at `+0x0E` (u16),
`MotionPointTracksDataSize` at `+0x10` (u16). **Header**: `MotionPointUnitCount` at `+0x10`.

**GZ (v1 FoxData)** — three children of the `MOTION` node, alongside `UNIT` (`0xc6e937b9`) and
the event list (`0x1622762d`):

| node | payload | meaning |
|---|---|---|
| `0xeab12d21` | 32..576 B | **name table** — `{u32 count}` then `{u32 StrCode32, u32 strOffset}` pairs, then the strings |
| `0xf0f377d9` | 32..240 B | `{u32 count}` then 8-byte records — parent/attach info |
| `0x1d75f6f3` | 96..23168 B | the **track data** |

Present in 335 of the first 400 ganis. The names are literal `MTP_*` strings, e.g.

```
02 00 00 00 | 5b8c79b2 1c000000 | 2dfc9f5d 24000000 | ... | "MTP_ADJUST_HEAD\0MTP_ADJUST_CHEST"
```

`StrCode32("MTP_ADJUST_HEAD") = b2798c5b` — verified against `modbldr-tools hash`. TPP v2 mtars
keep only the hash, so this table is the bridge between the two games.

## TPP's vocabulary is a SUBSET of GZ's

Searching both mtars for the 28 `MTP_*` hashes:

```
GZ player      28 of 28
TPP resident   21 of 28   — every one of the 21 also exists in GZ
TPP ladder      2 of 28   — MTP_LHAND_A, MTP_RHAND_A (the hands gripping the rungs)
```

**Every motion point TPP uses, GZ has.** So the mapping is a by-name selection, not a remap.

This corrects an earlier reading of "GZ 27 units vs TPP 8": those are per-mtar
`MotionPointUnitCount` values (how many units that archive uses), not the vocabulary. The
vocabularies are compatible, so motion-point transcoding is feasible.

Ladder using only `MTP_LHAND_A`/`MTP_RHAND_A` is exactly why dropping motion points made the
player slide off ladders.

## SOLVED — the v1 node payload IS the v2 file, byte for byte

No track decoding was needed. GZ's `0x1d75f6f3` payload and TPP's per-clip motion-point file
are the same format:

```
TPP  02 00 00 00 | 04 00 00 00 | 00 00 00 01 | 87 00 00 00 | 05 00 00 00 | 28 00 00 00 | 40 ...
GZ   02 00 00 00 | 04 00 00 00 | 00 00 00 01 | 14 00 00 00 | 05 00 00 00 | 28 00 00 00 | 40 ...
```

`TrackHeader` (UnitCount, SegmentCount, TrackId/flags, FrameCount, FrameRate — 0x14 bytes),
then one u32 offset per unit, then units in the same layout as bone units: `{u32 StrCode32
name, u8 segCount, u8 flags}` followed by 8-byte segment records. Exactly the same
relationship `.enchnk` has to the event node — copy the payload across, no re-encoding.

The real cause was one line in `TranscodeCmd`: it **deleted** the file on every replacement
("the template's exchnk described its body, not ours"). It now ships GZ's own tracks.

Verified: 944 of `player2_resident`'s entries carry this data, and unpacking produces exactly
944 files whose size distribution matches `field+0x10 * 0x10` exactly (min 80, max 18,672).

## The file is called `.mtp` now

The original MtarTool author named it `.exchnk`, which describes nothing. It holds MTP_* track
data, so it unpacks as `.mtp`. `.exchnk` is still accepted on read for existing unpacked
folders. Field renamed `exChunkSize` -> `motionPointsSize`, `ReadExChunkData` ->
`ReadMotionPointData`, `HasExChunk` -> `HasMotionPoints`.

## The archive's motion-point budget (mtar header +0x10)

`MtarHeader` +0x10 (`unknown4` in the tool) is the **largest motion-point unit count of any
clip in that archive**. Confirmed exactly on stock data:

```
player2_ladder   hdr=2  maxUnitsPerClip=2      player2_resident hdr=8  maxUnitsPerClip=8
player2_cqc      hdr=5  maxUnitsPerClip=5      player2_jump     hdr=2  maxUnitsPerClip=2
player2_vehicle  hdr=5  maxUnitsPerClip=5      player2_behind   hdr=4  maxUnitsPerClip=4
```

The engine sizes per-archive motion-point storage from it, so importing GZ clips — which reach
**27 units** where TPP tops out at 8 — while leaving the old value lets a clip overrun that
storage. **This is the best explanation yet for the original additive-build symptoms** (flying
around the map, wrong animations, inputs ignored): memory corruption, not a lookup failure.
The entry table itself was ruled out — an additive build is sorted ascending, has zero
duplicates, and every id carries TPP's ext code 8074.

`0` is a sentinel: 5 of 7 v2 fixtures store 0 while carrying up to 10 units, and **every**
archive with a non-zero value already covers its own worst case. So `MtarFile2.Import` now
leaves 0 alone, never lowers a real value, and raises one only when an imported clip needs
more. This lives in the packer, so any unpack -> repack is correct, not just transcode.

Budget raised on the current build: resident 8 -> 27, vram_resident 6 -> 24, cqc 5 -> 23,
vehicle 5 -> 23, ladder 2 -> 9, jump 2 -> 6, behind 4 -> 6, carry 4 -> 6.

## Result

Port restored to **1,879 clip slots** (from 353). Only 4 clips are still held back, where GZ
genuinely has no motion points for a clip TPP gives them to. Verified across all 24 changed
mtars: **0 entry tables altered, 0 clips losing motion points, 0 mtars losing foot-sync, 31
clips GAINING motion points** GZ has that TPP lacked. Ladder went 5 -> 6 clips with trajectory
data, 1,968 -> 2,912 bytes.
