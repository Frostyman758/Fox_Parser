# FOXMOTIONGRAPH (.mog) — format notes
03/08/2026

## Tooling

```bash
modbldr-tools <file.mog>              # -> file.mog.xml
modbldr-tools <file.mog.xml>          # -> file.mog   (refuses to write a graph that fails validation)
modbldr-tools mog <f.mog>             # structure report
modbldr-tools mog <f.mog> --validate  # every structural invariant
modbldr-tools mog <f.mog> --pool      # gani PathIds resolved to names
modbldr-tools mog <a> <b> --diff      # compare two graphs
modbldr-tools mog <gz.mog> --repath-gz2tpp -o out.mog
modbldr-tools mog <host> <donor> --graft --regate -o out.mog
```

`--graft` replicates the donor's states for clips the host lacks, anchored on animations both
graphs play, and re-gates the edges onto the host's own tag vocabulary. It is the whole
workflow in one command — no scripts.

**`--validate` runs automatically on every pack.** The engine checks almost nothing, so a
broken graph loads and silently produces no pose; the tool refuses to emit one instead. It
catches all of: unsorted tag map, node id != index+1, dangling self-relative offsets,
out-of-range or unsorted tag sets, adjacency outside 1..n, non-boundary edge endpoints, missing
`0xA7` filler.

The XML carries the decoded structure **plus the original file as a base64 `<image>`**. Parts
of a mog are still undecoded, and writing from structure alone would drop them; building
starts from the image and overwrites only what the XML describes.

Round trip is **byte-exact** on all 37 corpus files, and `test mog` gates it.

Arrays that keep their length are written back in place. An array that grows is appended at
the end of the file and its owning offset repointed — safe because every offset is
self-relative, so moving an array invalidates only the one field addressing it. Verified:
adding 2 comp tags to a node and 3 request tags to an edge grew the file by exactly 10 bytes,
relocated both arrays, and left all 3,087 edges resolving.

Decoded from the TPP dev decompile (`C:\rsearch\Tpp_main_win64.exe.c`) and checked against
two real files:

| file | size | source |
|---|---|---|
| `TppPlayer2_layers.mog` | 732,872 | TPP `player2_resident_motion.fpk` (live `Z:\` build) |
| `TppGzPlayer_layers.mog` | 1,343,232 | GZ `data_02.g0s` → `plmot_base_gz.fpk` |

Engine classes: `fox::motiongraph::MogFile` / `MogFileImpl`, structs `MotionGraphFormat*`.

---

## Offset convention

Every offset in the file is **self-relative**: the target is the address of the field that
holds it, plus the value. Confirmed in `MogFileImpl::BindTagMap`:

```c
pcVar2 = header->Signature + header->ParamsOffset + 0x30;              // params
pcVar2 = &pcVar2->NextParamOffset + pcVar2->NextParamOffset;           // next param
TagsMap.Tag = &pcVar2->DataOffset + pcVar2->DataOffset;                // param payload
```

Padding filler byte is `0xA7` throughout.

## Header — `MotionGraphFormatHeader`

```
0x00 char[16] Signature      "FOXMOTIONGRAPH\0" + 0xA7
0x10 u32      Unknown        TPP 6, GZ 5
0x14 i32      UnknownOffset  -> same target as DefaultAnimParamsOffset in both files
0x18 u8       AnimLayerCount 9 in both
0x19 u8       UnknownD       5 in both; must be > 4 or the tag map is skipped
0x1C u32      GraphCount     4 in both
0x20 i32      GraphHeadersOffset
0x24 u32      DefaultAnimParamsCount   0 in both
0x28 i32      DefaultAnimParamsOffset
0x2C u32      ParamsRelated  TPP 5, GZ 4
0x30 i32      ParamsOffset
```

## Graph headers — stride 0x38

`MogFileImpl::ConvertToGraphLayerIndex` walks these at `GraphHeadersOffset + 0x24 + n*0x38`
reading one byte, summing it until it exceeds a global layer index — so **+0x04 is
`AnimLayerCount`** and the per-graph counts sum to the header's `AnimLayerCount`.

```
0x00 i32 MaskArrayOffset   -> array of 8-byte StringIds (NOT a string)
0x04 u8  AnimLayerCount
0x08 u32 StateNodeCount
0x0C i32 StateNodesOffset
0x34 i32 AnimLayerInfosOffset
```

Observed:

| graph | TPP layers / stateNodes | GZ layers / stateNodes |
|---|---|---|
| 0 | 2 / 1871 | 2 / 1688 |
| 1 | 1 / 4 | 5 / 450 |
| 2 | 5 / 122 | 1 / 114 |
| 3 | 1 / 3 | 1 / 3 |

Layer sums are 9 in both. Graphs 1 and 2 are swapped in order between the games; the mask
StringIds are **byte-identical across games** (`0000e6fbf8fad071`, `0000f4cd88fb8ae1`), so
both graphs use the same mask vocabulary.

## Param chain

```
0x00 i32 NextParamOffset   0 = end
0x04 u32 Name              StrCode32
0x08 u32 Count
0x0C i32 DataOffset
```

Two params in both files:

- `0x859bd53e` count 4 (== GraphCount) — four u32s, purpose not yet confirmed.
- `0x185ebb9f` — **the tag map**, consumed by `MogFileImpl::BindTagMap` as an array of
  8-byte `StringId`, sorted ascending. **TPP 63 tags, GZ 329 tags.**

Neither name reverses against the dictionaries (motion-graph vocabulary is not dictionary
content, same as Lua command verbs).

## Animation binding

`MotionGraphFormatNode` (0x48 bytes) holds **no animation hash**. Animations are reached
through `AnimParamBinaryArray<AnimParamBinaryPath>`:

```
AnimParamBinaryArray<T>            AnimParamBinaryPath
  0x00 i32 Count                     0x00 u32 IdOffset   -> self-relative to an 8-byte PathId
  0x04 i32 DataOffset                (element size 4)
```

Elements live at `&DataOffset + DataOffset + i*4` (`fox::gk::SetTargetPose`).

```c
// fox::anim::AnimParamBinaryPath::GetPathId
uVar1 = *(PathId**)(&this->IdOffset + (int)this->IdOffset);
if ((uVar1 & 0xffff000000000000) != 0) return uVar1;   // else null
```

**A PathId whose top 16 bits are zero is treated as null.** Both games satisfy this: TPP
`.gani` ids carry ext code `0xfc5x` in the top 16 bits, GZ ids are `hash48 | (11 << 52)` =
`0x00b0`. So the ids differ in *both* the base hash and the ext code, but neither is null.

Path pointers do not appear in packed arrays inside the graph body. They sit at a **16-byte
stride**, one 4-byte pointer per slot, with every other byte in the slot left as `0xA7`
filler. TPP has 3,211 such pointers into 2,188 distinct pool slots; GZ has 5,243 into 2,406.
Animations are referenced by more than one node.

### The PathId pool

Both files end with a large, essentially contiguous array of 8-byte PathIds:

| | pool range | entries matched |
|---|---|---|
| TPP | `0xa6828` .. `0xb2ec0` | 2188 hits / 2186 distinct, 2170 of 2187 gaps are exactly 8 |
| GZ  | `0x13dea8` .. `0x142a00` | 2406 hits / 2406 distinct, one 32-byte gap |

The TPP pool covers **all 30 TPP player mtars**, not just `player2_resident` — matching
against `resident` alone makes it look like 121 scattered runs. GZ's pool references every
one of its 2406 animations exactly once.

## Graph header — the rest of it

The header is a run of (count, self-relative offset) pairs. Confirmed against both files:

```
0x00 i32 MaskArrayOffset    -> array of 8-byte StringIds
0x04 u8  AnimLayerCount
0x08 u32 StateNodeCount     0x0C i32 StateNodesOffset    -> nodes, stride 0x48
0x10 u32 EdgeCount          0x14 i32 EdgesOffset         -> edges, stride 0x28
0x18 u32 EntryNodeCount     0x1C i32 EntryNodesOffset    -> sorted u16 node indices
0x20 u32 SpecialNodeCount   0x24 i32 SpecialNodesOffset  -> sorted u16 node indices
0x34 i32 AnimLayerInfosOffset -> AnimLayerCount x {u8 maxDatas, u8 maxNodes}
```

`AnimLayerInfos` matches the decompile's 2-byte `MotionGraphFormatAnimLayerInfo`: TPP graph 0
has 2 layers and reads `(12,20)`, `(8,32)`.

**Entry node list (+0x18/+0x1C).** Sorted `u16` node indices, all below `StateNodeCount`. Its
members are overwhelmingly the tag-gated types — TPP 21 of 33 are Type 2, GZ 145 of 250 are
Type 2 plus 42 Type 7 — exactly the types `CheckPathTransition` tests with `CompTag`. They
average ~2.4x the outgoing edges and about half the blend nodes of unlisted nodes. Reading it
as *the index of states reachable by tag query* fits every observation: it saves the engine
scanning all 1871 nodes to answer "enter the state matching these tags".

This matters for authoring: making a new state reachable by TPP's code plausibly requires it
to be Type 2 or 7, carry a `CompTag` set, **and** be listed here.

**Special node list (+0x20/+0x24).** Same encoding, tiny (3, 3, 3, 1 entries observed), always
near the end of the node array. Purpose unconfirmed — likely default/fallback states.

## State nodes — 0x48 stride

`MogFileImpl::CreateTagMap` walks them with `jOff += 0x48`. The array base is
`&graphHeader.StateNodesOffset + StateNodesOffset`, i.e. base `graphHeader + 0x0C`.

Settled empirically, since the decompile's `&graphHeader->field_0x28` reads ambiguously: for
every node in every graph of both files, `SelfOffset` resolves to a node boundary under base
`+0x0C`, and never under base `+0x28`.

```
0x00 u32 CountA             0x04 i32 OffsetA     (see caveat below)
0x08 u32 BlendNodeCount     0x0C i32 BlendNodesOffset
0x14 u8  Type
0x18 i32 NameTagOffset      -> one 8-byte StringId (TPP 670 nodes have one, GZ 1664)
0x1C u32 CompTagCount       0x20 i32 CompTagOffset
         -> sorted u16 INDICES into the tag map — the node's transition gate
0x12 u16 NodeId            **index + 1** — exact on every node of every graph in both games
0x28 i32 SelfOffset        always exactly -0x28, i.e. a pointer to the node itself
0x2C u32 ?                 equals the out-edge count on most, but not all, stock nodes
```

**`+0x12` is load-bearing when authoring.** It is a 1-based node id, verified on 1871/1871,
4/4, 122/122 and 3/3 TPP nodes and 1688/1688, 450/450, 114/114, 3/3 GZ nodes. A newly authored
node left at 0 produces a graph that loads but yields **no pose at all — the player is
invisible and cannot move** (observed in-game 03/08/2026, twice, before this was found).
`MogBuilder` now writes it for every node, which is a no-op on existing ones.

New nodes also need a real StringId at `+0x18`; zero makes the field point at the node itself
and the engine reads garbage. The XML carries it as `name`.

The `CompTag` set is what `CheckPathTransition` tests via `CompTag(..., &node->CompTagThing)`
for nodes of Type 2 and 7. Validated as sorted `u16` below the tag count on **1871/1871** TPP
nodes and **1688/1688** GZ nodes.

**TPP: 1,201 of 1,871 nodes carry no comp tags at all. GZ: every node carries 3–7.** GZ's
graph is tag-driven; TPP's is mostly reached structurally through edges.

Two corrections to earlier drafts of this file:

- `SelfOffset` is **-0x28 on all 1871 TPP nodes** — a self-pointer, so
  `if (SelfOffset != 0) node = &node->SelfOffset + SelfOffset` is a no-op. GZ stores 0 and
  behaves identically. There is no "redirect mechanism" and no TPP/GZ difference here.
- `+0x00`/`+0x04` is **not** the tag list. It is the node's **outgoing edge list** — see below.
  (The pairs of negative i32s it lands on, e.g. `0xfff82c4cfff71700`, are simply an edge's two
  node pointers.)

### Node -> edges, and edge orientation

`node+0x00` is a count and `node+0x04` a self-relative offset to that many `i32`s, each
self-relative to an edge. Verified exhaustively: across every graph in both files, **every
listed pointer lands on an edge boundary, every edge is referenced exactly once, and the
referencing node is always the node at `edge+0x00`** — 3087/3087 and 5610/5610, zero
exceptions, no edge referenced twice.

```
edge + 0x00 -> SOURCE node (the node whose list holds this edge)
edge + 0x04 -> DESTINATION node
```

That settles the orientation and gives the complete transition graph.

## Animation link

`blendNode + 0x04` is self-relative to an `AnimParamBinaryPath` (a 4-byte self-relative
pointer), which points to the 8-byte PathId. Confirmed on 3,487 of 5,130 TPP blend nodes and
5,235 of 8,809 GZ ones; the remainder are non-leaf blend operators with no animation of their
own. So the chain is **state node -> blend nodes -> PathId**.

## Blend nodes — 0x2C stride

From `MotionGraphBlendValueBinderImpl::SetUseValuesNew`: the array runs from
`&node.BlendNodesOffset + BlendNodesOffset` for `BlendNodeCount` records of 0x2C.

```
0x00 u8  Type      0x01 u8 FloatIndex     0x02 u8 Flags
0x10 u32 ValueCount
0x14 i32 ValuesOffset -> ValueCount records of 8 bytes; byte 0 is a blend-value index,
                         0xFF = none (used as bit (v&0x1f) of word (v>>5))
```

Counts from the real files: TPP 2000 state nodes / 5130 blend nodes; GZ 2255 / 8809.

## Edges — 0x28 stride, the graph header's second block

The transition list. `graphHeader + 0x10` is the count, `+0x14` the offset.

Stride settled empirically: at 0x28 the `+0x20/+0x24` field validates as a sorted `u16` set
with every value below the file's tag count, on **every** record — TPP 3087/3087, 120/120,
2/2; GZ 5611/5611. No other stride comes close.

```
0x00 i32 -> state node (self-relative)
0x04 i32 -> state node (self-relative)
0x20 u32 RequestTagCount
0x24 i32 -> RequestTagCount sorted u16 INDICES into the tag map (self-relative)
```

Both node pointers land exactly on a state-node boundary for every edge in every graph of
both files, and never on the same node. Which end is source and which is destination is
**not** established — neither side is sorted; fan-out maxima are 28 (side A) vs 136 (side B)
in TPP graph 0.

The `u16` set is `AnimParamBinarySet<ushort>`, read by `impl::CheckRequestTagsEdge`, which
merge-intersects it against the controller's dynamic tags and bails the moment the sorted
sets diverge — so the edge is taken only when every request tag is currently set.

Totals: TPP 3,210 edges (0 unresolved), GZ 7,119 (1 unresolved, a null pointer).

**TPP has zero tag-gated edges; GZ has 1,595.** TPP gates transitions on the node's own
`AnimParamBinarySet<ushort>` at node +0x1C instead — `CheckPathTransition` calls
`CompTag(..., &node->CompTagThing)` for nodes of Type 2 and 7. This is a real architectural
divergence, not a data difference, and it is why the two graphs cannot be spliced naively.

## Enum meanings

### Blend node `Flags` — bit 0 is MIRROR

`(Flags & 1)` makes the engine play that leaf's animation mirrored. It is not just a pose
reflection: `MotionUtility::NormarizeEventName(name, isMirror)` swaps the event through
`AnimEventMirrorMap::GetPairName(0x7bf9301d, name)`, so **`MTEV_AG_SYNC_L` becomes
`MTEV_AG_SYNC_R`** and the mirrored clip lands on the opposite foot. One clip therefore covers both
foot phases. `LayerControl::IsMirror` reads the resulting runtime flag as bit 9 of the data
control's word at +0x30.

TPP's player graph: **3,396 leaves at 0x80, 91 at 0x81** — 74 distinct clips played mirrored, and
**20 of them have no foot-phase sibling in the archive at all**, because the mirror IS the sibling.

This matters when porting animations in: replacing a mirror-played clip changes BOTH directions,
and a donor clip authored without mirroring in mind plays correctly one way and wrong the other.
`transcode --mirror-safe <mog>` skips them.

TPP has the mirroring machinery GZ lacks — `AnimControlImpl::CalcMirrorOffset`,
`RigPose::MirrorPose`, `LayerControl::IsMirror`, `animx::MirrorOperator` — which is why GZ ships
separate clips where TPP mirrors one.

### Blend node `Type`

From the `switch (param_1->Type)` in `MotionGraphBlendNodeTraverser::BuildTree`:

```
0  leaf — plays the animation, no sub-tree built
1  Two            2  Layers        3  Custom
4  Select         5  StringSelect  6  Add
7  Subtract       9  Single
```

### Node `Type`

From `CheckNodePair` / `CheckPathTransition`:

- `Type == 1` — acts as a wildcard source: `if (node1->Type != 1 && node1 != node2) reject`.
- `Type == 2` or `7` — directly enterable; both skip the
  `MotionGraphPathFinder::IsLogicalAdjacentNode` check, and both are what `CompTag` gates.
  These dominate the entry-node list.
- `Type == 4` — `CheckPathTransition` returns success immediately (already there / terminal).

### Edge `+0x18/+0x1C` — a second tag set

`CheckNodePair` reads `(AnimParamBinarySet<ushort>*)(edge + 0x18)` and `CompTag`s it against a
node's own `CompTagThing`. So an edge carries **two** conditions: this destination comp-tag
set, and the request-tag set at `+0x20/+0x24` matched against the controller's dynamic tags.

Validated as sorted `u16` below the tag count on **3210/3210** TPP edges and **7119/7119** GZ
edges. TPP uses it on 189 edges, GZ on 4,829.

The pair also forms a match score in `CheckNodePair`: 1 base, 2 if the comp-tag set is
non-empty, 3 or 4 if request tags are present too — more specific edges win.

```
edge 0x00 i32 source node      0x04 i32 destination node
     0x18 u32 CompTagCount     0x1C i32 -> sorted u16 tag indices
     0x20 u32 RequestTagCount  0x24 i32 -> sorted u16 tag indices
```

## Complete state node layout — 0x48

```
0x00 u32 OutEdgeCount       0x04 i32 OutEdgesOffset   -> OutEdgeCount i32 self-rel ptrs to edges
0x08 u32 BlendNodeCount     0x0C i32 BlendNodesOffset -> blend nodes, stride 0x2C
0x10 u16 ?                  0x12 u16 NodeId           = index + 1, exact on every node
0x14 u8  Type               0x15..0x17 0xA7 filler
0x18 i32 NameTagOffset      -> one 8-byte StringId
0x1C u32 CompTagCount       0x20 i32 CompTagsOffset   -> sorted u16 tag-map indices
0x24 u32 ?                  0 on 1798 of 1871
0x28 i32 SelfOffset         always -0x28 (points at the node itself)
0x2C u32 AdjacentCount      0x30 i32 AdjacentOffset   -> sorted u16 NodeIds (1-based)
0x34 u32 ? (always 0)       0x38 i32 ?Offset
0x3C u32 ? (0/1/2)          0x40 i32 ?Offset          -> u16 array
0x44 i32 GroupTagOffset     -> one 8-byte StringId, distinct from NameTag
```

**Adjacency (`+0x2C/+0x30`)** is a precomputed logical-adjacency list — sorted `u16` node ids,
**1123/1123 sorted and all within 1..n in TPP, 1470/1470 in GZ**. It is a superset of the
node's own out-edge destinations for 924 of 1123, so it is reachability, not just direct edges.
`MotionGraphPathFinder::IsLogicalAdjacentNode` is what consumes it.

**GroupTag (`+0x44`)** resolves to a valid 48-bit StringId on **1871/1871** nodes and shares its
target with `NameTag` on exactly 1 — a genuinely separate per-node id, matching the engine's
`CompareCurrentGroup` / `ComparePreviousGroup` pair.

## Complete edge layout — 0x28

```
0x00 i32 -> SOURCE node        0x04 i32 -> DESTINATION node
0x08 .. 0x17  TRIGGER DATA (see below) — NOT independent enum bytes
0x18 u32 CompTagCount     0x1C i32 -> sorted u16 tag indices
0x20 u32 RequestTagCount  0x24 i32 -> sorted u16 tag indices
```

### `0x08..0x17` is trigger data, and it is a pointer chain

Reading those bytes as small enums cost a crash. `MotionGraphControlBuiltin::TriggerCheck`:

```asm
movsxd rax, dword ptr [rbx+10h]   ; i32
add    rbx, 10h
test   eax, eax
je     skip                        ; ZERO = this edge has no trigger
mov    rdx, rax
add    rdx, rbx                    ; self-relative -> trigger record
call   IsOnTriggerEventName
```

and the callee dereferences again — `movsxd rcx,[rdx]` then `mov r8d,[rcx+rdx]`. So the region
holds **self-relative offsets**, and a value copied from another file points nowhere:

```
rcx = ffffffffa7000002      <- 0xA7000002, i.e. bytes "02 00 00 A7" synthesised into an edge
mov r8d, dword ptr [rcx+rdx]   ACCESS VIOLATION
CheckPath -> CheckPathTransition -> CheckNodePair -> CheckEdge -> TriggerCheck
  from BasicActionImpl::StateStandMoveTurn   (crash on the player's first move)
```

**An authored edge must leave `0x08..0x17` entirely zero** — that is the engine's own encoding
for "no trigger", short-circuited by the `test/je`. Stock edges always carry trigger data (with
`0xA7` filler at `+0x0B`), so the validator only demands the filler when the region is non-zero.

`+0x08`..`+0x0C` are single bytes, not u32s — the u32 reads `0xA700A701` / `0xA7A7A700` because
`0xA7` filler sits between them. `+0x0B` is `0xA7` on all 3,087 edges.

## Relocating a struct must repoint EVERY self-relative field

This was the real cause of three "invisible, immobile player" builds, and it affected the
**existing** nodes, not the authored ones. Relaying the node array copies each node's raw bytes
verbatim — including offsets measured from its **old** address. Repointing only the obvious
three (`+0x04` out-edges, `+0x0C` blend nodes, `+0x20` comp tags) left
**`+0x18`, `+0x30`, `+0x38`, `+0x40`, `+0x44` dangling on all 1,871 nodes**; many landed past
end-of-file. Edges have the same trap at `+0x14`, `+0x1C`, `+0x24`.

A node has eight self-relative fields and an edge five. Move the struct, recompute all of them:

```
NODE  0x04  0x0C  0x18  0x20  0x30  0x38  0x40  0x44
EDGE  0x00  0x04  0x14  0x1C  0x24
```

Detection: walk every offset field after building and check the target is in-file; the
out-of-bounds adjacency reads are what exposed it.

## Authoring a node or edge — what a new struct needs

A field-by-field diff of authored structs against the stock distribution
(`mog_fielddiff.py`) found these mandatory fields, all now written by `MogBuilder`:

```
NODE  +0x30  +0x38  +0x40  +0x44   zero in all 433 authored, NEVER zero in 1871 stock nodes
                                    1642 / 1607 / 1607 / 1765 distinct values = four offsets
EDGE  +0x08  +0x0c                 zero in all 651 authored, NEVER zero in 3087 stock edges
                                    20 / 15 distinct = counts or enums
EDGE  +0x14                        zero in all 651, NEVER zero in stock; 3087 distinct
                                    = one per edge, an offset to per-edge data
```

Also cosmetic but worth matching: the `u32` at node `+0x14` reads `0xa7a7a700` in stock (Type
byte plus `0xA7` filler), not `0`.

Two real bugs were found and fixed along the way — the unsorted tag map and the missing node id
at `+0x12` — but neither was the blocker. **Editing existing structures is proven and safe
(byte-exact round trip, verified array growth); creating new ones needs these seven fields
decoded first.**

## Authoring new states

`modbldr-tools <file.mog.xml>` creates any node, edge or blend node whose element has **no
`at` attribute**. The builder allocates it, relocates the owning array, and repoints
everything into it.

Worked example — grafting a GZ-only animation as a new TPP state:

```xml
<node type="2" outEdges="" compTags="5">
  <blend type="0" floatIndex="0" flags="0" anim="fc5000927f29a3d0" />
</node>
<edge from="0" to="1871" compTags="" requestTags="5" />
```

plus adding `3087` to node 0's `outEdges` and `1871` to the graph's `entryNodes`. Result:
1871 -> 1872 nodes, 3087 -> 3088 edges, 33 -> 34 entry nodes, node type 2, `SelfOffset` -0x28,
a leaf blend node resolving to the grafted PathId, and **all 1,872 nodes' out-edge pointers
still landing on edge boundaries**.

Relocating an array leaves the old copy as dead space — the authored file above is 991,326
bytes against 732,872, which is exactly the original plus the relocated node (134 KB) and edge
(123 KB) arrays. Harmless, and it keeps every untouched structure byte-stable.

## Tag names — the vocabulary is plain CamelCase English

Neither mog stores a single string, and the internal writers (`AddCurrentNodeTag`,
`SetCurrentNodeTags`, `QueryNode`) have no game-code callers carrying constants. The hashes
crack directly instead: `modbldr-tools unhash <ids> -d candidates.txt` against a generated
wordlist. Tags are **StrCode64 of plain CamelCase English, no prefix**.

TPP's 63, of which 18 are named so far (`mog_tagnames.tsv`, 85 named across both games):

```
Back  Crawl  Damage  Dead  Front  Horse  Idle  Left  MoveStart
MoveStop  Normal  RideOn  Right  Squat  Stand  Throw  Wall  init
```

Those are exactly the tags that dominate the Rosetta co-occurrence — Squat 397, Stand 361,
Crawl 84. Identical names hash identically, so the 24 "shared" tags are literally the same
words; the vocabularies need no translation, GZ simply uses ~300 concepts TPP never raises.
Node names (+0x18) follow the same convention — 1,603 distinct in TPP's graph.

## The tag map MUST stay sorted

Both stock mogs store the tag map ascending, and it is load-bearing: `FindTagIndex` resolves a
StringId against it, and `CheckRequestTagsEdge` merge-intersects sorted `u16` sets, bailing the
moment the sets diverge. Appending new tags unsorted breaks every lookup — no tag resolves, so
no state matches, so the player gets **no pose at all: invisible and unable to move** (observed
in-game 03/08/2026).

Merging new tags into sorted order shifts the original indices, so every existing node
`CompTag` set and every edge comp/request set has to be rewritten through the old->new map, and
each set re-sorted. Verified lossless: across all four graphs, **0 of 2,000 original nodes and
0 of 3,210 original edges changed which tag StringIds they resolve to**.

## Which out-edge wins — the selection scoring

`MotionGraphControlBuiltin::CanMoveState` scores each candidate out-edge, and `SelectMoveEdge`
keeps the best. This is absolute priority, not a hint.

```c
bVar2 = 1;
if (edge->CompTagCount != 0) bVar2 = 2;                  // edge +0x18
if (CheckRequestTagsEdge(edge, this->field_0x108)) {
    if (edge->RequestTagCount != 0)                      // edge +0x20
        bVar2 = (bVar2 == 2) + 3;                        // 4 when comp too, else 3
    if (bVar2 < param_4) return bVar2;                   // can't beat the best so far — early out
    if (node1==node2 || node2->Type==2 || node2->Type==7 || IsLogicalAdjacentNode(node1,node2))
        return PathCheck(...) ? bVar2 : 0;
}
return 0;
```

| condition carried by the edge | score |
|---|---|
| none | 1 |
| CompTags only | 2 |
| RequestTags only | 3 |
| both | 4 |

`SelectMoveEdge` then:

- keeps a candidate only when `bestSoFar <= score`;
- on a strictly higher score (`bestSoFar < score`) **wipes the winner and the whole candidate
  list** — a higher-scoring edge does not merely outrank a lower one, it erases it;
- on an equal score tiebreaks on tag *count* — `CompTagCount` at score 2, `RequestTagCount` at
  score 3-4 — and needs `iVar9 < iVar12`, strictly greater, so an **exact tie goes to whichever
  edge comes first in the node's out-edge list**;
- passes `bestSoFar` as `CanMoveState`'s `param_4`, which is why a hopeless candidate returns its
  score without running the adjacency and `PathCheck` work.

`CanMoveStateByTagsNode` / `SelectMoveEdgeByTagsNode` are the same scoring with the threshold
written as `param_3 <= bVar4`.

**The two tag sets are different checks and cannot be folded together.** `CompTag` tests the
edge's set against the *path node's* own CompTag set — graph-internal data, so a donor-only tag
here is still satisfiable by a donor node carrying it. `CheckRequestTagsEdge` tests against
`this->field_0x108`, the control's **live** tag set written by game code (`TppPlayer2`), so a tag
the host game never sets leaves that edge permanently dormant.

**Only the first `field_0x17c` out-edges of a node are ever evaluated** — `SelectMoveEdge`
returns out of its loop at that index. It comes from the control's createContext byte 2 and is
**100** when absent. Edges past it are silently dead; `--validate` checks this.

Measured on stock `TppPlayer2_layers.mog` (graph0, 3,087 edges): **2,898 score 1, 189 score 2,
0 score 3, 0 score 4.** Stock never exceeds 2, so scores 3 and 4 are free for authored edges that
must take precedence.

### The consequence for grafting

Filtering a donor condition down to the host's shared vocabulary **broadens** it — 329 GZ tags
against 63 TPP tags, 24 shared, so a five-tag GZ condition can survive as one. The edge then
fires in contexts the donor never meant, and because a conditional edge outscores a stock
unconditional one it wins those contexts *outright*. That is the movement race observed in-game
(v15): the GZ clip starts, then flips back to TPP's the moment the diluted tag stops matching.

The fix is not to lose the race but to stop entering it:

- keep donor conditions **faithful** — an unsatisfiable tag leaves the edge dormant and stock
  behaviour simply carries on, which is strictly safer than a broadened one that wins;
- lift genuine entries to **score 4** by *also* placing the shared-vocabulary part of the
  condition in RequestTags. That outranks anything stock can field, and it **narrows** the edge —
  both checks must now pass — instead of widening it;
- leave exits out of grafted states **unconditional** (score 1) so escape is always available;
  internal donor-to-donor edges keep their real conditions.

Winning the score is only half of it. A dominant edge with a diluted condition is worse than no
edge at all, because it wins everywhere it should have stayed quiet.

## Blend nodes — the tree, from the engine

`fox::motiongraph::MotionGraphFormatBlendNode` is named in the decompile:

```
0x00 u8  Type            0 leaf, 1 Two, 2 Layers, 3 Custom, 4 Select,
                         5 StringSelect, 6 Add, 7 Subtract, 9 Single
0x01 u8  FloatIndex      0x02 u8 Flags   (bit 0 = MIRROR)
0x04 i32 DataOffset      self-relative, TYPE-SPECIFIC (see below)
0x08 u32 LinkDescCount   0x0C i32 LinkDescsOffset
0x14 i32                 self-rel to 8-byte records, indexed by FloatIndex
0x20 f32 UnkFloat        0x24 i32 self-rel pointer
0x28 u8  FunctionOutParam
```

**Children are indices, not pointers.** `MotionGraphFormatUtility::GetConnectBlendNode`:

```c
childIndex = *(byte*)(&blend->LinkDescCount + idx*8 + blend->LinkDescsOffset + 4);
if (childIndex != 0xff && childIndex < stateNode->ConnectNodeCount)
    return (BlendNode*)(&stateNode->BlendNodesOffset + childIndex*0x2c + stateNode->BlendNodesOffset);
```

So LinkDescs are **8-byte records** self-relative from `+0x0C`, byte 0 is the child's index into
the OWNING STATE NODE's blend array (`0xff` = unconnected). The whole tree therefore lives inside
that one array — nothing outside it, and no pointer chasing. `MotionGraphBlendNodeTraverser::
BuildTree` switches on `Type` and each type's `BuildTree` calls `GetConnectBlendNode` per port.

`+0x04` is type-specific: a leaf points at an `AnimParamBinaryPath`; `MotionGraphLayersBlendNodeData
::BuildTree` reads `{ u32 Count; i32 RecordsOffset; }` then `Count` 8-byte records, each
self-relatively addressing an 8-byte StringId.

## How much of the format is actually modelled

The XML carries the original file as a base64 `<image>`, so a byte-exact round trip proves nothing
about how much is understood. `modbldr-tools test mog` measures the real thing over the 37-file
corpus (28 TPP + 9 GZ, `gzanim_tmp\corpus`):

```
model accounts for 88.1% of 3,577 KB
unmodelled: 262,645 B  0xA7 filler (alignment slack)
             38,955 B  zero bytes
            134,270 B  REAL DATA still undecoded
```

Padding is two thirds of the gap, so **96.3% of the real bytes are accounted for**; 3.7% is genuinely
undecoded. What is left, all named rather than mysterious: blend `+0x14` and `+0x24`, the
type-specific `+0x04` blocks for types 1/3/4/5/6/7 (only leaf and Layers are decoded), node `+0x24`
and the two u16 arrays at `+0x34/+0x38` and `+0x3C/+0x40`, the edge trigger chain at `0x08..0x17`,
and the four u32s of param `0x859bd53e`.

## Grafting rules learned the hard way

**Close the donor subgraph.** Cloning only the nodes that play a wanted clip is not enough: a
state's real exit often leads to a donor node that plays nothing new — a pure transition state.
Skip it and that exit is dropped, stranding the player. Pull in every unanchored node the seeds
can reach until every edge lands on a node that exists: another new one, or an anchored host
state.

**Never drop an exit when re-gating.** Dropping a conditional edge stops the player *entering*
a donor state unprompted, which is the point. Applying the same rule to edges *out of* a donor
state strands them — that is the input lock, in-game. Leaving a state is always safe, so exits
keep their filtered tags and are never dropped. This alone took dropped edges 232 -> 67 and
playable clips 249 -> 295.

**Never create an entry edge into a state the player cannot leave.** This is the in-game input
lock, and it is the one rule worth checking mechanically. A grafted state is a *trap* if it
cannot reach the host graph again — and escape is transitive, so a state whose exits all lead to
other trapped states is itself a trap. Measured on the v12 build: **24 of 122 enterable grafted
states were traps.** Dropping those entry edges (47 of them) leaves 98 enterable states, 0 traps.

Bisecting proved where the fault lived, after several wrong guesses: a build with all 464
grafted **nodes and zero new edges** played normally. So node presence is harmless and the lock
is entirely a property of the edges. `--no-edges` and `--max-states N` exist for exactly this.

**Terminal states are normal — do not "fix" them.** A node with no outgoing edge is ordinary:
**TPP graph 0 has 744 of 1,871 (39.8%)**, GZ has 220 of 1,688 (13%). The engine leaves them by
some route other than an edge. Synthesising fallback edges back to a predecessor is solving a
problem that does not exist.

## Learning a GZ -> TPP tag mapping (the Rosetta run)

1,373 animations are played by nodes in **both** player graphs, so their nodes' tag sets can
be paired to learn a correspondence. Method: repath GZ's pool into TPP id space, index each
graph as animation -> nodes -> tags, then score every (GZ tag, TPP tag) pair by Jaccard over
the shared animations. Result in `gz2tpp_tags.tsv`.

```
GZ tags with a confident mapping (co>=3, J>=0.5) : 47 of 329
   identity (same StringId)                      :  6
   learned cross-mappings                        : 41
TPP tags never reached by any mapping            : 54 of 63
```

Several are unambiguous (J = 1.000 at co = 30 and 22), and the identical-id tags carry the
bulk of the weight (`49a67b579157` co=397, `7a4db14d320a` co=361, `98736ff23fa2` co=84).

**But 282 of GZ's 329 tags have no confident TPP equivalent, and 54 of TPP's 63 tags are
never reached.** The vocabularies genuinely diverge outside the shared core. Re-tagging GZ-only
nodes into TPP's space is therefore *partial*: it can carry the clips whose gating happens to
sit in the mapped 47, and cannot carry the rest. That is consistent with the GZ-only clips
being moves TPP's player controller has no state for.

## Can GZ's mog be dropped into TPP? No.

The tag maps were compared directly (`0x185ebb9f` payload, both files):

```
TPP tags = 63     GZ tags = 329
shared   = 24     TPP-only = 39     GZ-only = 305
```

**39 of TPP's 63 tags do not exist in GZ's graph.** TPP player code that queries one of them
would find nothing, so replacing `TppPlayer2_layers.mog` with a converted GZ graph silently
removes those behaviours. GZ's graph is not an older TPP graph — the two diverged.

What *would* be needed to run GZ's graph on TPP data is only a pool rewrite (each 8-byte
PathId GZ id -> name -> TPP PathCode64, in place, same size, no offsets move). That part is
cheap and safe. The tag gap is the blocker, not the hashes.

## Verification ledger

VERIFIED — offset convention; header fields; graph-header stride, `AnimLayerCount` slot and
count/offset pairs; state node array base and 0x48 stride; node tag list; blend node 0x2C
stride and blend-value sets; **edge 0x28 stride, both node pointers and the request-tag set**;
param chain walk; tag map identity and counts; `AnimParamBinaryArray`/`AnimParamBinaryPath`
layout; `GetPathId` top-16-bits rule; pool extents and coverage; GZ→TPP pool repath
(cross-checked to the name-derived 1,431 / 975 split).

SUSPECTED — param `0x859bd53e` is per-graph (count matches GraphCount, values are plausible
self-relative offsets but only two of four land on a graph's mask array).

Also VERIFIED — node out-edge list at +0x00/+0x04 and **edge orientation** (source = +0x00,
destination = +0x04); node `CompTag` gate at +0x1C/+0x20; node name tag at +0x18; the
animation link `blend node +0x04 -> AnimParamBinaryPath -> PathId`; entry-node and
special-node index lists; `AnimLayerInfos` as `{u8 maxDatas, u8 maxNodes}` pairs;
`SelfOffset` is a constant -0x28 self-pointer.

SUSPECTED — the entry-node list (+0x18/+0x1C) is the index of states reachable by tag query
(its members are overwhelmingly the Type 2/7 nodes `CompTag` gates, with far more edges and
fewer blend nodes than average); param `0x859bd53e` is one StringId per graph (count matches
GraphCount, all four targets are 8-byte StringIds, two coincide with a graph's mask array).

Also VERIFIED — blend node `Type` dispatch table; node `Type` roles for 1, 2, 4 and 7; edge
`+0x18/+0x1C` as a second `AnimParamBinarySet<ushort>` (3210/3210 and 7119/7119); node and
edge creation with array relocation and repointing.

NOT DECODED (semantics only; every byte is now located and sized) — node `+0x10`, `+0x24`,
`+0x34/+0x38`, `+0x3C/+0x40`; edge `+0x08`, `+0x09`, `+0x0A`, `+0x0C` and the per-layer u16
array at `+0x10/+0x14`; node `Type` 0 and 6 (the common cases; 1,567 of TPP's nodes are Type 0, so it
is presumably the plain state with no special entry behaviour); blend-node `Flags` bits;
blend node fields at 0x08/0x0C, the float at 0x20 and the int at 0x24; the special-node
list's purpose.

**Every array in the file can now be located, sized and walked**, and the full transition
graph (nodes, out-edges, destinations, tag gates, animations) can be reconstructed. What is
left is the meaning of individual enum values, not structure.
