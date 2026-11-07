# SSMP.EnemyHealthSync

An addon for SSMP (Silksong Multiplayer) that keeps enemies in sync between players in the same room: their movement, attacks, flinches, health and deaths.

## Why this is needed

SSMP 0.3.1 does not sync enemies: its entity system is disabled, so every player runs their own copy of each enemy. Without this addon, enemies move and attack differently on every screen, and an enemy you hit is unharmed on your friend's screen.

## Enemy owners and puppets

The SSMP server does not run the game, so it can't run enemies itself. Instead, **each enemy's AI runs on one player's game: its owner**.

- The owner's copy is the real one. Everyone else in the room sees a **puppet**: the same enemy with its AI switched off, copying the owner's position, facing, animations (attack wind-ups, flinches, staggers), health and attack hitboxes.
- Enemy AI only notices the player whose game it runs on, so **ownership follows the players**: when another player is clearly closer to an enemy, its owner hands it to them, and the enemy starts chasing and attacking that player.
- When you enter a room, its enemies belong to the room host (the first player in the room). When a player leaves, their enemies go back to the room host; if the room host leaves, the next player takes over.

## Features

- **Synced movement and attacks**: enemies are in the same place, doing the same thing, on every screen. Attack hitboxes switch on and off with the owner's, so an attack that hits you on your screen really hit you.
- **Enemies fight everyone**: each enemy targets the player it's nearest to, not just one player.
- **Shared damage**: anyone's hits count. On the owner's game, another player's hit plays as a real hit, so the enemy flinches and staggers for everyone. Damage is sent as an amount, so hits from several players at once add up.
- **Shared deaths**: an enemy dies when its owner's copy dies, for everyone in the room (with its normal death and drops). Bosses whose own logic plays the death are detected too.
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
SyncMovement = true # Enemy owners run enemies, others see puppets (false = damage/deaths only)

[Network]
StateSendRate = 20  # Enemy updates per second sent by enemy owners (5-60)

[Ownership]
Handover = true       # Hand enemies to the nearest player (false = room host runs every enemy)
HandoverRange = 15    # Only hand an enemy to a player within this distance of it
BossHpThreshold = 300 # Enemies with this much HP (or a boss-style death) hand over more cautiously

[Debug]
DebugLogging = false  # Log every damage/death packet and ownership change
```

## How it works

1. When you enter a room (or connect), every `HealthManager` in it gets an ID. The ID is a hash of the scene name and the object's hierarchy path, so it is the same on every client no matter where the enemy currently is.
2. The server keeps the players of each room in the order they entered (the first is the room host) and who owns each enemy. It sends each player the list of enemies they own, and only accepts enemy snapshots from an enemy's owner.
3. Each owner sends a snapshot of its enemies up to `StateSendRate` times per second: position, facing, current animation clip, HP and which attack hitboxes are on. Unchanged enemies are only re-sent twice per second. These packets are unreliable, since a newer one always follows.
4. Another player's enemy becomes a puppet when its first snapshot arrives. Its PlayMaker FSMs and AI scripts are disabled, its rigidbody becomes kinematic, and it glides toward the owner's position and plays the owner's animation. If snapshots stop for 2 seconds, it gets its own AI back.
5. Four times per second, each owner checks its enemies: if another player in the room is within `HandoverRange` and at least 4 units closer than the owner (8 for bosses), and the owner has had the enemy for at least 2 seconds (8 for bosses), it hands the enemy over. Only the current owner can hand an enemy over, so two players never claim the same one.
6. Harmony patches on `HealthManager.TakeDamage` / `ApplyExtraDamage` measure how much health each local hit removed and send that amount with the hit direction. The owner plays it as a real `Hit` and then corrects HP to exactly that amount. Puppets show the hit flash but never lose their last HP or die on their own; deaths come from the owner.

### Limitations

- Projectiles and other objects enemies spawn during an attack don't appear on puppets yet.
- Room logic outside the enemies (arena gates, battle waves) still runs separately on each player's game.
- When an enemy changes owner, the new owner's copy resumes from where its AI was paused, so it may briefly hitch or restart its current move. Bosses hand over less often to keep fights smooth, but each switch can still show a hitch.
- Enemies spawned during a fight (summons, arena waves created at runtime) are synced on a best-effort basis: if they spawn in a different order on different clients, their IDs may not match.
- Enemies that start out dead from a player's own save data never send a death to others.
- Which components count as "AI" on puppets is decided by a name-based allowlist in `EnemyMirror.KeepOnPuppet`; some enemies may need it tuned.

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
