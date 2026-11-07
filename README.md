# SSMP.EnemyHealthSync

An addon for SSMP (Silksong Multiplayer) that shares enemy damage and deaths between players in the same room.

## Why this is needed

SSMP 0.3.1 does not sync enemies: its entity system is disabled, so every player runs their own copy of each enemy. Without this addon, an enemy you hit is unharmed on your friend's screen.

This addon syncs **damage and deaths only**. Enemy positions and AI are still separate for each player.

## Features

- **Shared damage**: damage you deal to an enemy is subtracted from everyone else's copy of it. Damage is sent as an amount, not as a health value, so hits from several players at once add up.
- **Shared deaths**: when an enemy dies for one player, it dies for everyone in the room (with its normal death and drops).
- **No double hits**: SSMP plays other players' attacks in your game, and those can hit your copy of an enemy. The addon ignores those hits, because the real damage already arrives from that player.
- **Late joiners**: if you enter a room where other players already killed enemies, those enemies die for you too.
- **Normal respawns**: once everyone has left a room, its enemies respawn as usual.
- **Works with any enemy** that has a `HealthManager`, including modded ones. No enemy list needed.

## Requirements

- Every player needs this addon installed, including the host.
- SSMP 0.3.1.

## Installation

1. Build the project (see below), or take `SSMPEnemyHealthSync.dll` from a release.
2. Copy it to `BepInEx/plugins/SSMP.EnemyHealthSync/`.

## Configuration

`BepInEx/config/com.ssmp.enemyhealthsync.cfg`:

```ini
[General]
Enabled = true      # Master toggle
SyncHealth = true   # Share damage dealt to enemies
SyncDeath = true    # Share enemy deaths

[Debug]
DebugLogging = false  # Log every damage/death packet
```

## How it works

1. When you enter a room (or connect), every `HealthManager` in it gets an ID. The ID is a hash of the scene name and the object's hierarchy path, so it is the same on every client no matter where the enemy currently is.
2. Harmony patches on `HealthManager.TakeDamage` / `ApplyExtraDamage` measure how much health each local hit removed and send that amount. A patch on `Die` (plus a per-frame check) sends deaths.
3. The server knows which room each player is in and relays damage/deaths to the other players in that room. It remembers deaths per room while someone is still there.
4. Receiving clients subtract the damage (never below 1 HP) and call `Die()` when a death arrives.

### Limitations

- Enemies spawned during a fight (summons, arena waves created at runtime) are synced on a best-effort basis: if they spawn in a different order on different clients, their IDs may not match.
- Enemies that start out dead from a player's own save data never send a death to others.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Game and SSMP references come from NuGet, so no local game install is needed to compile:

```powershell
dotnet build SSMP.EnemyHealthSync.sln
# or
.\build.ps1
```

To have the DLL copied into your game automatically, copy `SilksongPath.props.example` to `SilksongPath.props` and set `SilksongPluginsFolder`.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
