# Fox UI formats: GZ vs TPP delta specs (RE notes)
09/07/2026

## STATUS: all four converters BUILT and gated (test ui = 2189/0 suite-wide)

- uilb/uigb: byte-identical round-trips (320+762 / 77+64) + convert gates.
- uif: geometry/ref equivalence gate 331/331 (independent TPP-layout parse).
- uia: copy (identity proven).
- CLI: `modbldr-tools gzui <file|folder> [-o out]`.

## Late corrections (trust these over older sections)

- uif GZ string table = u64 StrCode64 × header@0x0C (count = ENTRY count,
  no /2 halving — FoxUiEditor's reader was wrong).
- uigb GZ header: @0x24 = links+params POOL (== TPP buffers), @0x1C = pool
  SIZE; StrCode64 table at pool+size; path strings after it. (The reader's
  "@0x24 = strT" was wrong.)
- uigb layout entry GZ 8 B == first 8 B of TPP's 12 B; append {ffff,0}.
- uigb SetText params: GZ 72 B → TPP 64 B; tail 4×u32 → 4×u8 + pad; done
  IN PLACE (parB=64, pool keeps 8 slack bytes → no offset cascades).
- uif technique table @hdr 0x2C (rel buffers): {str32 technique, u32 const}
  before pathIds; const stable per technique corpus-wide (LyBL=359,
  LyADD=1651, LyMUL=17803...). Emitted from the file's str-table technique.
- uif vertex controls: GZ 16 B {desc8, f32 a, f32 b} → TPP 24 B
  {desc8, vec4}; desc u32@+4 = runtime destination (kept verbatim,
  fail-soft — verify in-game).
- uif common prio: GZ f32@0x38 × 4095 → TPP i16@+2 (corpus: 0.5→2047,
  0.1→409, 1.0→4095), clamped to i16.
- uif stencil (typ 4): geometry like mesh; tail +0x24..0x38 verbatim EXCEPT
  +0x28 u16 = total geometry bytes before strT (== strRel; patched after
  layout).
- GZ "/as/<ciphered>" texture refs: cipher unrecoverable, but PS3 GZ uifs
  predate it and carry REAL paths → per-(node,slot) join mined
  `Uif/gz_as_texmap.tsv` (140 refs; embedded resource; conflicts exist
  from PS3/PC authoring drift — first-wins). TPP ships the entire GZ
  texture set (texture6_gzs0.dat), so mapped refs all resolve in TPP.

## In-game debugging findings (09/07/2026 night — all fixed, gates green)

- uif buffers: POOLS FIRST. vertsRel=0 <= uvsRel <= colorsRel, all three
  always set (796/796) — the engine sizes vertex/uv/color streams from the
  offset deltas. Remap/idx/tri tables AFTER pools. Tables-first + colorsRel
  = -1 renders alpha textures as opaque white boxes.
- uif header flags: bit1 set on every TPP file — OR it in on convert.
- uif TEXT nodes: POSITIONAL tail right after the 0x8C(GZ)/0x88(TPP) base —
  {u16,u16} pair list + 8 B shader-param entries, all str-table INDEX based
  → copy verbatim, no rebase. 97/486 GZ text nodes have one (up to ~3 KB).
  Truncating it = engine walks garbage when building text units (crash).
- GZ vertex control record = 16 B {desc 8, f32 a, f32 b} (not 8) → TPP
  24 B {desc 8, vec4 (a,b,0,0)}; desc u32@+4 = runtime dest, keep verbatim.
- Packaging: TPP's fpkd PRELOADS layout paths at boot (e.g.
  hud_lmenu/equip_cross.uilb) — those paths must keep TPP-parseable
  content; ship GZ top layouts at their own gz/ paths and let the
  (converted) graph reference them.

## Equip cross staging (09/07/2026)

`C:\rsearch\gz_equip_cross_mod\` = 79 files at game paths: converted
gz_equip_cross.uigb (+ copy at TPP's GraphAsset .../ld_layer/equip_cross.uigb),
5 uilb, 7 uif (texmapped, prio-scaled), ~60 uia. TPP's own cross lives in
chunk0 `.../pack/ui/ui_default_data2.fpk`. 6 meshes carry fail-soft controls.

Goal: convert GZ-era uilb/uif/uigb/uia to TPP so GZ UI assets load in TPP.
Evidence: GZ loader FUN_14128e4b0 (MgsGroundZeroes.exe Ghidra export),
TPP loader fox::ui::GraphResourceLayout::SetupResourceFromRawFile
(Tpp_main_win64 dev exe, VA 0x141c5f9b0), fixture pair
`test_fixtures/ui/ui format examples/uilb/PC {GZ,TPP}/UI_bino.uilb`.

Status ledger: VERIFIED = byte-checked vs real files + both loaders.

## Cross-format facts (VERIFIED)

- GZ id tables hold u64 **StrCode64(name)**; TPP holds u32 **StrCode32(name)**;
  StrCode32 = low 32 bits of StrCode64 → **conversion = truncation**, works for
  every id without knowing the plaintext. (bino ids match "UI_bino",
  "UI_bino_setin", "UI_bino_setout" stems.)
- GZ path references are plaintext strings (`/Assets/tpp/...` — GZ already
  uses the tpp namespace); TPP replaces them with u64 **PathCode64(path)**
  (QAR/PathServer hash: 51-bit base + 13-bit ext code). Verified: 4/4 bino
  PathIds = modbldr PathCode64 of the GZ strings, same order.
- TPP loaders hard-reject GZ versions (uif: `!= 0x202` @ 0x141c295f7;
  uilb: u32@4 `!= 0x101`) and contain no string-path fallback → data-side
  conversion is the only route.
- .uia magic 0x0bfca2d2 = GANI family (TPP checks it in fox::anim::GaniBaseFile)
  and has no GZ/TPP version split in the header — see uia section.

## UILB (VERIFIED)

Common skeleton, little-endian:

| off  | type | meaning |
|------|------|---------|
| 0x00 | u32  | 'UILB' |
| 0x04 | u32  | GZ: 0x001, TPP: 0x101 (byte4 magic=1, byte5 version 0/1) |
| 0x08 | u16  | model count (entries 0x64 B) |
| 0x0A | u16  | anim count (entries 0x14 B) |
| 0x0C | u16  | camera count (entries 0x34 B) |
| 0x0E | u16  | graph count (entries 0x50 B) |
| 0x10 | u16  | name-id count |
| 0x12 | u16  | path count |
| 0x14 | u32  | model table off (abs; -1 = none) |
| 0x18 | u32  | anim table off (abs) |
| 0x1C | u32  | camera table off (abs; -1 = none) |
| 0x20 | u32  | graph table off (abs; -1 = none) |
| 0x24 | u32  | id table off RELATIVE to blob (= size of pre-list region) |
| 0x28 | u32  | GZ: ABSOLUTE off of string-entry table; TPP: PathId table off RELATIVE to blob |
| 0x2C | u32  | blob base (abs). GZ blob: [pre-lists][u64 ids][strings]; TPP blob: [pre-lists][u32 ids][u64 PathIds] |

- GZ string entry (at abs @0x28, 8 B each): {u32 len, u32 relOff}; string at
  blob+relOff, NUL-terminated.
- Pre-list region (blob+0 .. blob+@0x24): u16 id-table indexes for model
  child lists (entry+0x5C relOff into blob, entry+0x60 count) and
  connection lists. Byte-copy on convert.
- Model entry 0x64 B, layouts identical GZ/TPP: +0 u16 nameIdx, +2 u16
  pathIdx (uif), +4 u32 flags (0x80→colorFlag4, 2→0x1000, 4→0x4000,
  8→billboard, 0x10→stencilModel, 0x20→stencilOut), +8 u32 priority,
  +0x0C..0x4B transform floats (trans/rot-quat/scale/color; the wiki's
  mystery 0x803F = 1.0f), +0x4C f32 billboardMin, +0x50 f32 billboardMax
  (only read by TPP; GZ files carry 0 — byte-copy), +0x54/+0x56 u16
  connection id idxs (0xffff none), +0x58 u32 inherit flags (0x1F default),
  +0x5C u32 child list relOff, +0x60 u16 child count, +0x62 u8 stencilTest.
- Anim entry 0x14 B identical: +0 u16 nameIdx, +2 u16 fileIdxA (uia),
  +4 u16 fileIdxB (paired _s uia, 0xffff none), +0x10 f32 speed.
- Camera entry 0x34 B identical: +0 u16 nameIdx, then projection params.
- Graph entry 0x50 B identical: +0 u16 nameIdx, +2 u16 pathIdx (uigb),
  +0x48/+0x4A u16 connection idxs, +0x4E u16 mode.

### Conversion GZ→TPP

1. u32@4 = 0x101.
2. Byte-copy model/anim/camera/graph tables + pre-list region.
3. id table: each u64 → u32 (truncate). Self-consistency (children,
   connections, anim keys) is preserved because every reference goes
   through the same table.
4. Drop string-entry table + strings; emit u64 PathId table in the SAME
   order = PathCode64(string). Entry indexes in tables stay valid.
5. Recompute @0x28 (= @0x24 + 4*idCount, blob-relative) and @0x2C
   (shrinks by 8*pathCount: string-entry table removal).
6. Note: GZ/TPP same-name retail files can differ in authoring (TPP bino
   pre-lists {1,2} vs GZ {0,0}+{1,2}); conversion preserves GZ content,
   NOT the TPP retail file.

## UIF (VERIFIED structure; empirics flagged)

Evidence: GZ chain FUN_14126d6f0 (SetUp) → FUN_14126bb80 (parse) →
FUN_14126c040 (node factory) → FUN_141261f80 (common) / FUN_141268c00+d80+830
(mesh) / FUN_141269020 (vertex controls) / FUN_141258c90 (text).
TPP: typed structs in dev Ghidra project (ModelNodeCommonInfo/MeshInfo/
LineInfo/TextInfo/ModelNodeHeader) + FoxBrowser UifReader (796-fixture
verified) + fox::ui::Model::CreateAllModelNodes @6423774.

### Header

| off  | GZ (0x20 hdr) | TPP (0x30 hdr) |
|------|----------------|----------------|
| 0x00 | 'UIF ' | same |
| 0x04 | u32 0x102 | u32 0x202 |
| 0x08 | u16 flags (bit0 → model flag 0x100000) | same |
| 0x0A | u16 node count | same |
| 0x0C | u16 str count — counts u32 SLOTS (GZ hashes = count/2 u64s!) | u16 count of u32 StrCode32s |
| 0x0E | u16 path count | same |
| 0x10 | u32 node table off (abs) | same |
| 0x14 | u32 str table off rel to buffers | same (u32 entries) |
| 0x18 | u32 path ENTRY table off (ABS): {u32 len, u32 relOff}×n, relOff → buffers+relOff cstring; entry.len==-1 → none | u32 PathCode64 table off rel to buffers |
| 0x1C | u32 buffers off (abs) | same |
| 0x20 | — (header ends) | u32 vertsRel (shared vertex pool, rel buffers) |
| 0x24 | — | u32 uvsRel |
| 0x28 | — | u32 colorsRel |
| 0x2C | — | u32 (== @0x18 dup? assert across corpus) |

- Node table both: {i16 nameIdx, u16 type, u32 dataOff(abs)} ×n + 0xFFFF
  sentinel entry. Types: 0 Null, 1 Common, 2 Mesh, 3 Text, 4 Stencil,
  5 Line, 6 Invalid(skip).
- GZ nodes link parents by comparing 48-bit StrCode64 (mask 0xffffffffffff;
  root sentinel = 0xb8a0bf169f98).

### ModelNodeCommonInfo: GZ 0x50 vs TPP 0x4C

| field | GZ | TPP |
|---|---|---|
| parent name idx | i16 @0x00 | i16 @0x00 |
| priority | ??? @0x02 unread; float @0x38 → node+0x98 | i16 @0x02 |
| flags | u32 @0x04 (same ModelNodeInfoFlags enum: 1 USE_PALETTE, 0x100 ROT_QUAT, 0x10000 HAS_VERTICES, text align bits 0x100000.., same in both) | u32 @0x04 |
| scale vec3 | @0x08 | @0x08 |
| rot vec3/quat4 | @0x18 (+W @0x24 if 0x100) | same |
| translate vec3 | @0x28 | same |
| color rgba | @0x3C | @0x38 |
| secondary name idx | u16 @0x4C (unconfirmed) | u16 @0x48 (UnknownSecondaryNameStrCode32Index) |
| palette color idx | u16 @0x4E | u16 @0x4A |

Conversion: color moves -4; palette/secondary move -4; TPP priority i16 =
(short)(GZ float @0x38) — CHECK CORPUS (GZ @0x02 values, float ranges,
cross-check same-name pairs UI_bino.uif etc).

### ModelNodeMeshInfo (at common end: GZ +0x50, TPP +0x4C), field offs rel to mf

Identical positions: +0x00 u16 vCnt, +0x02 u16 triCnt, +0x14 u32 triOff,
+0x18 u16 vColorCtrlCount, +0x1A u16 vCtrlCount, +0x1C u32 vColorCtrlsOff,
+0x20 u32 vCtrlsOff, +0x24 u16 materialInstanceNameIdx, +0x26 u16
shaderTechniqueNameIdx, +0x28 u16 texParamCount, +0x2A u16 shaderParamCount,
+0x2C u32 texParamsOff (ABS), +0x30 u32 shaderParamsOff (ABS), +0x34 u32
billboardLimitsOff (ABS → 2 floats min/max).

Geometry fields differ:
- GZ: +0x04 vertsOff, +0x08 uvsOff, +0x0C colorsOff (flag 0x40000), +0x10
  extra buf (node+0x138), all rel-to-buffers DIRECT arrays of 16B Vector4
  records, indexed by vertex i. Tris: u16×3 per tri at +0x14 (rel buffers).
- TPP: +0x04 vertexRemapOff, +0x08 uvRemapOff, +0x0C colorIndicesOff,
  +0x10 uvIndicesOff, +0x14 triIndicesOff — u16 remap tables (rel buffers)
  into SHARED pools at header vertsRel/uvsRel/colorsRel.
  pos[i]=Verts[remap[i]], uv[i]=UVs[uvRemap[i]], color[i]=Colors[colorIdx[i]].

Conversion: concat GZ per-mesh arrays into shared pools, emit identity
remap tables (remap[i]=poolBase+i). Tri indices are logical (0..vCnt) in
both — byte-copy.

- Tex params both: {u16 slotNameIdx, u16 pathIdx}×count @texParamsOff.
- Shader params both: 8B {u16, u16 nameIdx, f32 value} (TPP typedef says
  {u16 Type, u16 NameIdx, Vector4} — reader uses 8B stride, verified).
- Vertex controls: GZ 8B descriptors {u16 nameIdx, u8, u8, u32 relOff →
  Vector4 at buffers+relOff}; TPP 24B inline {8B descriptor + Vector4}.
  Repack on convert (exact TPP descriptor semantics: pin via fixtures /
  BN fox/ui/ModelNodeMesh.cpp during implementation).

### Text node (GZ +4 shift throughout)

Same flag-bit alignment enum. GZ: color idx @0x80, font idx @0x82,
billboardOff @0x88 (TPP: 0x7C/0x7E/0x84); font W/H/textSpace/lineSpace
bytes follow. TPP TextInfo = common 0x4C + bbox 0x20 + scale vec @0x6C.

### Stencil/Line

Stencil ≈ mesh (own create path, same info layout, flag bit17 variants).
Line: TPP ModelNodeLineInfo typedef known ({u16 lineCount, u16 vCnt,
linesOff, vertsOff, colorsOff, controls, billboard}); GZ analog direct-
buffer. RARE — count corpus occurrences before investing (maybe none in
GZ ui set).

### uif conversion summary

1. Header 0x20→0x30 (add verts/uvs/colors rel + @0x2C), version 0x202.
2. Str table: u64×(count/2) → u32×count' (truncate; header count
   semantics change: GZ counts u32 slots = 2×hashes, TPP counts hashes —
   keep INDEXES identical, they already index hash-positions in both).
3. Path entries+strings → PathCode64 table (same order); refs unchanged.
4. Per node: common -4 repack, mesh geometry direct→shared+remap,
   controls repack, text -4 repack; recompute all dataOffs/table offs.
5. Texture paths (from tex params) are GZ ftex assets — packaging must
   ship converted GZ ftex (modbldr ftex GZ support) or retarget to TPP
   textures. Log referenced texture paths per file.

## UIGB (VERIFIED structure; empirics flagged)

Evidence: GZ FUN_141264f40 (ref collector) + FUN_14124c460 (validator);
UiCore UigbReader (TPP full parse incl. node graph, GZ branch parses
nodes with the SAME body parser); SubtitlesBasic.uigb exists in BOTH
fixture sets with identical node count (14) and string count (38) —
the Rosetta pair for byte-level diffs.

### Header

| field | GZ | TPP |
|---|---|---|
| magic | 'UIGB' @0 | same |
| u32@4 | 1 | 0x101 (byte5 version 0→1) |
| node count | u16 @0x08 | same |
| uilb count | u8 @0x0A | same |
| uigb count | u8 @0x0B | same |
| str count | u16 @0x0C | u32 @0x10 (0x0C u8 = StrCodeCount=0, 0x0D u8 = section6 count) |
| path count | u16 @0x0E | same |
| node table | u32 @0x10 | u32 @0x14 |
| layout table | u32 @0x14 | u32 @0x18 |
| section4 (child uigb) | u32 @0x18 | u32 @0x1C |
| ??? | u32 @0x1C (0xD8 in SubtitlesBasic — pin via differ) | u32 @0x20 = section5/o2 (-1 in shipped set) |
| path table | u32 @0x20 | rel off u32 @0x28 (rel to buffers) |
| str table / buffers | u32 @0x24 (StrCode64 table; also base for path strings + params) | strRel u32 @0x2C + strings(-1) @0x30 + buffers @0x34 |
| section6 | (none?) | u32 @0x24, count u8 @0x0D — purpose TBD; check if 0/-1 legal across corpus |

- Str table: GZ u64 StrCode64 → TPP u32 StrCode32 (truncate; identity
  verified again — reader keeps low-32 and node types resolve).
- Path table: GZ {u32 len, u32 relOff → buffers+relOff cstring} → TPP
  PathCode64 (hash the string; order/indexes preserved).
- Layout entries: GZ 8 B {u16 nameIdx, u16 pathIdx, u16 drawPriIdx,
  u16 connIdx} → TPP 12 B {u16 nameIdx, u16 pathIdx, u8 flag1, u8 n2,
  u16 drawPriIdx, u16 connIdx, u16 u1} — synthesize flag1/n2/u1
  (pin defaults via Rosetta diff).
- Node headers {u16 typeIdx, u16 nameIdx, u8 size, u8 type} + per-type
  inline bodies (Page/Phase 6, Event 30, Action 34, Operation 26 B):
  SAME in both (GZ parses fine with the TPP body parser). Bodies carry
  ABSOLUTE offsets (input edges 8 B {nodeIdx,prop1,prop2 u16}, out edges
  4 B) and buffers-RELATIVE param offsets + link pool cursor —
  all must be rebased on write.
- Param blobs: type-specific packed values; refs are table INDEXES
  (survive conversion if table order kept). Diff Rosetta params to
  confirm no encoding drift.
- Section4 (child graphs): GZ 0x54 B entries {u16 nameIdx, u16 pathIdx,
  ..., u8 loadFlag @+7?}; TPP stride ~12 (derived). Rare (uigbCount>0);
  pin via corpus scan.
- GZ sentinel StrCode64 0xb8a0bf169f98 (="empty/root") skipped in ref
  walks.

### Rosetta findings 09/07/2026 (SubtitlesBasic GZ 0x517 vs TPP 0x449)

- Node tables IDENTICAL node-for-node: same type/name INDEXES, sizes,
  kinds, edge counts. Only offsets shift (+0x10 = header growth 0x28→0x38)
  and param offsets/sizes differ per kind.
- File order GZ: hdr, nodes, edge pools (8 B in-edges, then frefs, then
  4 B out-edges — exactly filling to layoutT), layoutT, pathT, StrCode64
  table, PARAMS pool, links, path strings.
  File order TPP: hdr, nodes, edge pools, layoutT, section6, buffers
  (= [4 B?][links u16×n][params]), StrCode32 table, PathCode64 table.
- GZ @0x1C = params pool total SIZE (0xD8 = exact span of all parB).
  Params pool sits after the StrCode64 table (base needs 1 more probe —
  first par@16 suggests a 16 B pool head: 4 unknown + 3 links + pad).
- Param blobs embed RAW StrCode64 VALUES (48-bit) in BOTH games (TPP
  kind-13 params: 3×StrCode64 + 4 floats + box floats 32/48 + flags) —
  params are NOT index-based for hashes. Sizes differ per kind:
  Action kind=13: GZ 72 → TPP 64 (-8); kind=8: 2=2; kind=3: 40=40;
  Event: 8=8. Per-kind translation tables needed (diff more pairs:
  SubtitlesSideshow + kind census over 77 GZ files).
- GZ pathT can contain EMPTY entries {len=0} (TPP drops them — converter
  should too, remapping path indexes).
- TPP section6 (count@0x0D, off@0x24): 12 B/entry sample
  `00000000 60010000 00000000` — purpose TBD; GZ has no equivalent.
- TPP file ends with 1 stray byte after pathT (0x449) — align/terminator,
  check corpus.

### Semantic caveat (validation phase)

Node TypeHash = StrCode32 of node class name. GZ-era native callback
classes (EvCall/Act nodes) must exist in the TPP exe to instantiate.
Inventory all GZ TypeHashes across the 77 GZ uigb fixtures, resolve
against TPP's registered classes, report unmapped (those nodes will be
inert or need substitution).

## UIA (VERIFIED identical — no conversion)

FoxData container (magic 0x0bfca2d2, 32 B header, 48 B nodes, TrackHeader
payloads), GANI family. BOTH games key nodes by 32-bit hash (GZ uses
fox::SearchDataNodeHash32 too — FUN_140dc8040), so uia carries StrCode32-
compatible ids natively; uilb name ids truncate to exactly these.
Cross-parse proof 09/07/2026: 1053/1053 GZ + 1097/1097 TPP fixtures parse
with the same layout (uia_crossparse.py), zero failures.
Conversion = copy the file. (Segment codecs = shared anim codecs; in-game
test is the final gate.)
