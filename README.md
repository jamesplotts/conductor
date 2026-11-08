# Conductor

A Railroader mod for the 0.6 built-in mod loader.

Conductor runs a passenger train between the stations you pick. It drives the train with Auto Engineer waypoints, not Road mode, so it can take the train anywhere: station tracks, branches, and fuel stops.

## Using it

Couple your coaches to an engine, open the engine (click it), and on the **Car** tab press **Conductor**.

- **Stations:** every station on the map, in order along the line, with branches under their junction. Tick the ones to call at. The train runs them top to bottom and back; the first and last ticked are the ends of the line. Stations you can't call at yet are greyed out with the reason ("not purchased", "track not built"). The list is worked out fresh each time, so stations from an expansion show up as soon as their track is open.
- **Fuel Stops:** press **Add Fuel Stop** and click the track beside a water spout, coal chute or diesel stand. Set **Refuel Below**. After a station, if the engine's water, coal or diesel is below that level, the train pulls up at the nearest fuel stop with the tender's filler right under the spout, fills up, tops up anything else at a stop close by, and carries on.
- **Speed:** how fast the Auto Engineer runs between stops.
- **Start** begins at the nearest ticked station. **Turn Back** sends the train to the station behind it. **Stop** hands the train back to you.

At each station the train waits until everyone for that station is off, and until it's full or nobody is left waiting for the stations ahead. The coaches only take on passengers for the stations still ahead on that leg.

Taking the Auto Engineer off waypoints stops Conductor for that train.

## Good to know

- Don't run another mod that drives the same engine (Passenger Helper, Waypoint Queue) at the same time.
- Line the switches for station tracks off the main line, as the Auto Engineer won't run through a switch set against it.
- Settings and runs are saved with the game. In multiplayer, the host runs the trains; everyone sees the window.

## Install

1. In Railroader, turn on **Preferences > Features > Experimental > Allow Unsafe DLL Mods**, then restart the game.
2. Unzip `Conductor_<version>.zip` into `Documents\Railroader\Mods\`. You should end up with `Mods\Conductor\` holding `Info.json`, `Conductor.dll` and `0Harmony.dll`.
3. On the main menu, open **Mods** and enable Conductor.

## Building

Copy `Paths.user.example` to `Paths.user` and set `GameDir` (and `ModDevDir` for test builds). Then:

- `dotnet build Conductor/Conductor.csproj` builds and copies the mod into `Documents/Railroader/ModDev/Conductor`.
- `dotnet build Conductor/Conductor.csproj -c Release` writes `bin/Conductor_<version>.zip`.

MIT licensed. Includes Harmony by Andreas Pardeike (MIT). The fuel-stop lining up follows the approach of Waypoint Queue by PrinceOfPluto (MIT).
