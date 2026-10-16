# PMD:EoS Uplink Reader (C#)

This is an application to read USB data getting sent from Pokémon Mystery Dungeon: Explorers of Sky's Speedrun Mod, running on a DSPico. It stores its data into memory-mapped files, which can then be read by a Livesplit Autosplitter or other application.

## How it works (protocol v2)

The mod no longer has a built-in address table. On connect the reader
uploads the address table from `addresses.json` to the mod (CONFIG_BEGIN →
entries → COMMIT, ACK-paced), and the mod then streams one
variable-length frame per game frame containing the current value of every
configured address. The wire protocol is documented in
[`../docs/uplink-protocol.md`](../docs/uplink-protocol.md).

Uploads are **patient**: the game can be busy (saves, loading screens) and
unable to service USB for a while, so every upload step is retried
indefinitely (with a gentle backoff) until the mod confirms it — nothing
is aborted after a fixed number of attempts. On a **reconnect** (same
reader session) the reader first asks the mod for its table state (PING);
if the table is already applied (`cfg=A/N` matches), the upload is skipped
entirely and streaming resumes immediately.

**Soft resets are fast:** the mod sends one unsolicited status line (the
boot greeting) as soon as its uplink comes back up after a reset. A reader
that stayed connected receives it, sees the table was wiped, and
re-uploads immediately — streaming resumes in ~3-4 s instead of waiting
out the silence watchdog. A soft reset clears the mod's table, so the
next connect uploads again.

## The address file

The table lives **next to the executable** (`addresses.json` in the same
folder as `PMD_EoS_Uplink_Reader.exe`) — the location is hardcoded in the
app; there is no file picker and no hot reload. The build copies the
checked-in repo file there (`PreserveNewest`: an exe-adjacent file edited
newer than the repo file is not clobbered), so the repo file is the
editable source of truth — edit it and rebuild, or edit the exe-adjacent
copy directly. The reader creates it with defaults if missing.

The table is **loaded once when the reader starts**: to apply edits,
restart the reader (disconnect + connect is not enough).

Format:

```json
{
  "version": 1,
  "comment": "free-text note",
  "addresses": [
    { "name": "play_time_seconds", "address": "0x022ABFD4", "width": 4, "comment": "PLAY_TIME seconds" },
    { "name": "dungeon_current_floor", "path": ["0x02354138", "*+0x0", "+0x749"], "width": 1 }
  ]
}
```

- `name` (required, unique): label for the sample; index in the frame = position in the list.
- `address` **or** `path` (exactly one): a direct hex address, or a pointer path.
- `path`: first element is the base address; every following element is a step:
  - `"*+0xN"` — add N, then dereference the pointer at `cur + N`
  - `"+0xN"` — add N only
  - `"*"` — dereference at `cur + 0`
- `width`: 1, 2 or 4 bytes (default 4); narrower reads are zero-extended.
- `comment`: free-text annotation (ignored by the reader).

Limits: up to 4 steps per path, step offsets ≤ 0x7FFF, and the entry count
must fit what the mod reports in its `OK CONFIG ... max=` reply (120 in
the current build). Direct `address` entries must be inside ARM9 main RAM
(0x02000000-0x023FFFFF) — that check is typo feedback. **Pointer-path
bases may be any address**: a base is a pointer *location*, not a read,
and the mod only ever reads at dereference targets and the final address,
each of which is RAM-checked per frame.

That per-frame check is what makes part-time pointers work: a dungeon
variable's path samples `0` while the dungeon pointer is null (outside a
dungeon) and the real value inside — so you can connect anywhere and the
table is simply filled in once the pointers become valid. Nothing is ever
dereferenced outside RAM, so a bad pointer can never crash the game.

## Memory-mapped file (for autosplitters)

Name: `PMUplinkFrame`, size 6168 bytes. Format v3 is a self-describing
**name → value table**: one entry per configured address, resolved by the
`name` from `addresses.json` instead of by offset:

- `[0 .. 15]` header: magic `'P' 'M'`, format version `0x03` at `[2]`,
  flags `0` at `[3]`, entry_count u16 LE at `[4..5]`, epoch u16 LE at
  `[6..7]` (the mod's CONFIG_COMMIT counter), seq u32 LE at `[8..11]`,
  game_frame u32 LE at `[12..15]`
- `[16 + 48*i]` entry i (48 bytes): name — 32 bytes ASCII, NUL-padded,
  max 31 characters — at `[0..31]`, width u8 at `[32]` (1, 2 or 4), value
  u64 LE at `[36..43]` (the sample, zero-extended)
- `[6160..6161]` u16 checksum: byte sum of bytes `[0 .. 6160)`
- `[6164..6167]` u32 seqlock — odd = update in progress; read the
  seqlock, if odd retry, read the block, re-read the seqlock, retry if it
  changed

The table structure is (re)published whenever the mod's epoch changes
(e.g. after the re-upload that follows a soft reset); values update on
every frame. On disconnect the block keeps the last values. Names must be
plain ASCII, at most 31 characters, and unique — enforced when
`addresses.json` is loaded.

**Note for existing autosplitters:** the raw-frame layouts (the fixed
70-byte v1 frame and the variable-length v2 frame) are gone; resolve
entries by name.

## Offline self-test

`dotnet run -- --selftest` runs the protocol tests (frame codec, stream
resync, config parsing/validation, command encoding) without a device and
writes `selftest-results.txt`.

## Known quirks

Initiating the connection during the intro before the Top Level Menu
seems to often lead to in-game crashes. It seems to be more stable to
wait until you're at the Top Level Menu to connect.
