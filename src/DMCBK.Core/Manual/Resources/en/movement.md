# Movement

MCC runs a local physics simulation, so the bot collides, falls and swims the way a player does
rather than teleporting. The server sees ordinary position updates.

## Commands

    /move 150 80 380     plan a route and walk it
    /move north          one step in a direction
    /move on             walk forward until told to stop
    /move center         centre on the current block
    /move get            print your position
    /move gravity off    hover, for testing

Add `-f` to allow moves MCC would otherwise refuse: falling, fire, lava.

`/look` aims your view without moving. Some servers check where you are looking before they accept
an interaction, so `/look` then `/useblock` is sometimes the difference between working and not.

## When the destination is not a place you can stand

A block holds a body only if there is footing under it and two blocks of clear space in it. A solid
block, a ladder rung, and the floor under a hanging stalactite all fail that test, and in a cave a
lot of ground that looks open fails it too.

Asked to walk into such a block, `/move` walks to the nearest block that *can* hold a body, which is
one of the six sharing a face with the one you named, and then says so:

    Nothing can stand in the block at X:-295.00 Y:-14.00 Z:393.00, so the walk stopped in the
    nearest block that can hold a body: X:-295.50 Y:-14.00 Z:393.50. That is not the destination
    you asked for.

**This is reported as a failure, not as an arrival**, because you asked to be somewhere the bot is
not. It did move, and it got as close as any body can get. No flag changes this: `-f` relaxes the
fall, fire and lava gates and cannot make a block occupiable.

Two failures that read alike and are not:

| Line | What it means |
| --- | --- |
| `Nothing can stand in the block at ...` | The block cannot hold a body. The bot is beside it. |
| `Failed to compute a safe path to ...` | The block is fine; the bot cannot get there. It did not move. |

If every block touching the destination is unstandable too, or none of them can be reached, you get
the second line and nothing moves.

`/blockinfo <x> <y> <z>` shows what the client believes is in a block. Check the block above it as
well: a spike hanging from a ceiling takes the head space, not the floor.

## Configuration

`client.toml` `[Gameplay]`:

| Key | Default | Meaning |
| --- | --- | --- |
| `Terrain` | true | Track the world. Nothing here works without it. |
| `Physics` | true | Local collision and gravity. |
| `Pathfinding` | true | Required for `/move <x> <y> <z>`. |
| `MovementSpeed` | 2 | Above 2 is usually read as cheating. |
| `MoveHeadWhileWalking` | true | Small head movements, to look less like a bot. |

`/help move` shows which of these are on right now.

## Known limits

These are reproduced and understood, not guesses.

> [!WARNING]
> `OnGround` can latch false when the player rests flush against the block it is trying to climb,
> which permanently closes the jump gate. Every observed case sits at an `x.7` or `z.7` coordinate,
> where the player half-width lands exactly on a block boundary. From a centred start the same climb
> takes 10 ticks.

> [!WARNING]
> Water currents are simulated correctly but not planned for. The planner costs a move through a
> current as ordinary walking, so it can hand the executor a route the executor cannot walk.

> [!WARNING]
> A long submerged route with corners in it can still run the bot out of air. A diagonal step under
> water costs about five times a straight one, and the breath budget prices it as a straight one, so a
> route through a winding flooded passage can be approved and then take half again as long as the
> budget allowed for. A route down a straight flooded corridor is not affected.

Bubble columns count as water for buoyancy and provide no lift, so elevators built from them do not
work. The bot's air supply is handled correctly in one: vanilla refills the lung inside a bubble
column, and so does the bot's own prediction of it.

## See also

`/man pathfinding` for how routes are chosen.
