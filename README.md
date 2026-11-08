# Conductor

A Railroader mod for the 0.6 built-in mod loader.

Conductor runs a passenger train between the stations you pick. It drives the train with Auto Engineer waypoints, not Road mode, so it can take the train anywhere: station tracks, branches, and fuel stops.

## Using it

Couple your coaches to an engine, open the engine (click it), and on the **Car** tab press **Conductor**.

- **Stations:** every station on the map, in order along the line, with branches under their junction. Tick the ones to call at. The train runs them top to bottom and back; the first and last ticked are the ends of the line. Stations you can't call at yet are greyed out with the reason ("not purchased", "track not built"). The list is worked out fresh each time, so stations from an expansion show up as soon as their track is open.
- **Use Station Sidings:** where a station has a passing siding beside the main line, the train stops on the siding, clear of both switches, and leaves the main open (when it fits; otherwise it uses the main). On by default.
- **Meet Oncoming Trains:** on the way to a station, the train looks about three miles down its route every few seconds for a train coming the other way. It reads the other train's Auto Engineer route when it has one (Waypoint mode), or follows the switches as they lie in the direction it's moving (Road mode, or driven by hand). If their routes cross head-on, it picks a passing siding on its route that the other train's route goes through, and waits on whichever track the other train isn't taking: the siding if the other train is on the main, the main if it's routed through the siding. It takes the farthest one it can be into in time, else the nearest. Standing in a siding already (at a station, say) with the other train routed around it, it just waits there. Once the other train is off the siding and its route no longer crosses it, the train carries on. It gives up after half an hour of game time if the other train never comes. Two Conductor trains don't both take the siding: one holds the main. On by default.
- **Fuel Stops:** set **Refuel Below**. With **Use Any Open Stop** on (the default), the train uses any water spout, coal chute or diesel stand in an opened area, such as the Whittier engine shop, and later Bryson's engine house once it opens. Turn it off to pick your own with **Add Fuel Stop** (click the track beside the spout). After a station, if the engine's water, coal or diesel is below that level, the train pulls up at the nearest fuel stop with the tender's filler right under the spout, fills up, tops up anything else at a stop close by, and carries on.
- **Turning:** with **Turn on Wyes** on, at each end of the line the train turns on the nearest wye if the engine would otherwise be at the back for the trip home: in by one leg, all the way into the tail, and out by the other leg, at 10 mph. The section lists every wye on open track, how long its tail is, and whether this train fits. It only uses a station's own wye (the station nearest it). When the end of the line has no wye, the train leaves with the engine at the back, and turns at the next station along the way that has one. A train too long for the tail stands in a passing siding while the engine cuts off, turns on the wye by itself, runs around the train and couples on at the front. Where the train is standing counts if there's a way around it (spurs and other switches along the tracks are fine); otherwise it pulls into the nearest siding that fits, within about a mile and a half. The tail is measured along the track through any switches, so a wye whose tail runs on up a branch, like Whittier's up the Connelly Creek branch, turns the whole train. An observation car (car type PBO) on the rear, platform outward, can't stay there, since the game pays its 20% fare bonus only when it's the last car with its platform end free: the engine fetches it, takes it through the wye too, sets it on the other end of the train platform outward, then runs around once more to couple on. The standing coaches get their handbrakes on while the engine is away, and the air is connected and the brakes released before the train leaves. Without a passing siding, or with one too short for the engine, the train runs back with the engine at the rear. An engine that's already at the front but running backward is turned the same way, and one at the rear but facing the right way just runs around.
- **Speed:** how fast the Auto Engineer runs between stops.
- **Enabled:** whether Conductor runs this engine at all. It's off for every engine until you turn it on, so freight and work-train engines are left alone (the Car tab shows "off"); turn it on for your passenger engines. Engines already set up for a run count as on. Untick it to take a train over by hand without losing its place, for example to untangle it from another train. The train stops where it is, lets go of any spout, and Conductor leaves it alone; you can drive it or set your own waypoints. Tick it again and the train carries on with what it was doing, from wherever it is now. The Car tab shows "Conductor disabled" meanwhile.
- **Start** begins at the nearest ticked station. **Turn Back** sends the train to the station behind it. **Stop** hands the train back to you.

At each station the train waits until everyone for that station is off, and until it's full or nobody is left waiting for the stations ahead. The coaches only take on passengers for the stations still ahead on that leg.

Taking the Auto Engineer off waypoints stops Conductor for that train.

## Good to know

- Don't run another mod that drives the same engine (Passenger Helper, Waypoint Queue) at the same time.
- The game's own Auto Engineer station stops (the AI passenger stop setting) are switched off for engines Conductor is running, since Conductor makes the stops itself; the game's version stops short of the platform when the train is backing up. Other engines are unaffected.
- The Auto Engineer throws switches itself on the way, unless a car is standing on one or it is under dispatcher (CTC) control.
- Settings and runs are saved with the game. In multiplayer, the host runs the trains; everyone sees the window.

## Install

1. In Railroader, turn on **Preferences > Features > Experimental > Allow Unsafe DLL Mods**, then restart the game.
2. Unzip `Conductor_<version>.zip` into `Documents\Railroader\Mods\`. You should end up with `Mods\Conductor\` holding `Info.json`, `Conductor.dll` and `0Harmony.dll`.
3. On the main menu, open **Mods** and enable Conductor.

## Building

Copy `Paths.user.example` to `Paths.user` and set `GameDir` (and `ModDevDir` for test builds). Then:

- `dotnet build Conductor/Conductor.csproj` builds and copies the mod into `Documents/Railroader/ModDev/Conductor`.
- `dotnet build Conductor/Conductor.csproj -c Release` writes `bin/Conductor_<version>.zip`.

MIT licensed. Includes Harmony by Andreas Pardeike (MIT). The fuel-stop lining up, and the uncoupling, air and coupling steps of a run-around, follow the approach of Waypoint Queue by PrinceOfPluto (MIT).
