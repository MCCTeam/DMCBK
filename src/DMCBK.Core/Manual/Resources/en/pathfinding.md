# Pathfinding

`/move <x> <y> <z>` plans a route, then walks it. Planning and walking are separate, and they can
disagree.

## How a route is built

The planner searches over discrete moves: step, jump, fall, swim. Each has a cost. It finds the
cheapest sequence to the goal, and replans when the world turns out differently from the plan.

If the exact destination is not standable, the planner falls back to the nearest place that is.

## Breathing

A route that goes under water is checked against the bot's lung before it is walked, and refused if
one lung cannot pay for it. That check is not only a veto: where the route passes an air source, the
planner **schedules a pause there** and prices the wait as part of the route, so a dive that needs two
lungs is planned as two legs with a stop between them rather than refused outright.

An air source can be as little as a single air block in the ceiling of a flooded passage. The bot has
to stop at one of those to use it - a body swimming past picks up a few ticks of air and no more - and
stopping is exactly what the scheduled pause is. While it holds, the bot presses nothing but its
position and watches its own lung; it leaves when the lung is full, when the planned wait is spent, or
early if the cell turns out not to be giving air back.

Water breathing and conduit power both suspend the lung, and either one alone is enough. With one of
them held the route needs no pauses at all and none are planned. They are read live rather than
captured, so if the effect is revoked mid-dive the route is replanned against the lung the bot actually
has, and abandoned if it can no longer be survived.

With `--logging.debugmessages=true` each pause reports itself:

    Breath hold at (722.5, 101.0, 840.5) released (the lung is full): held 40 ticks, air 176 -> 300

## When it fails

`Could not reach the destination` carries the reason. The three you will actually meet:

- No path was found. The goal is walled off, or it is not standable and nothing near it is either.
- The plan ran out of ticks. Each segment has a tick budget, and the budget is a flat constant that
  does not know whether you are walking or swimming, so a legitimate swim can exhaust a budget
  calibrated for land.
- The executor could not follow the plan. Usually a water current, see below.

## Checking what the bot sees

The planner works from the client's own copy of the world, which is not always the server's.

    /blockinfo 150 79 380       what the client believes is there
    /blockinfo 150 79 380 -s    and its neighbours
    /chunk status               which chunks are loaded
    /chunk ui                   fullscreen responsive chunk map in TUI mode

If `/blockinfo` disagrees with the server, the planner is working from the wrong map and the route
will look inexplicable.

## Known limits

> [!WARNING]
> There is no flow field. Water push is simulated but not planned for, so routes through a current
> are costed as ordinary walking.

> [!WARNING]
> Segment tick budgets are flat constants regardless of medium or distance.

> [!NOTE]
> A jump is never aimed at a floor that is only under a *centred* body. A pointed dripstone tip is
> 6/16 of a block wide and inset 5/16, so a landing that arrives pressed against it comes down
> beside it instead of on it, a whole step lower, and the next move starts from a place the plan
> never described. The planner refuses those landings, so `/move` can answer "no path" where a
> player would happily hop across. Walking is unaffected: a deck of stalagmites is still a floor and
> is still crossed on foot.

> [!WARNING]
> The breath budget prices a diagonal step under water as if it were a straight one. Measured, a
> submerged diagonal costs about five times a submerged straight step, so a winding flooded route can
> be approved and then cost half again what the budget allowed. A straight flooded corridor is priced
> correctly.

Pathfinding lives in UMPK, not in MCC. Adding a move type or changing a cost is a change there.
