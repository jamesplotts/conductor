# Conductor

A Railroader mod for the 0.6 built-in mod loader. Download it from [Nexus Mods](https://www.nexusmods.com/railroader/mods/2301) or the [Releases](https://github.com/jamesplotts/conductor/releases) page.

Conductor runs a passenger train between the stations you pick. It drives the train with Auto Engineer waypoints, not Road mode, so it can take the train anywhere: station tracks, branches, and fuel stops.

## Using it

Couple your coaches to an engine, open the engine (click it), and on the **Car** tab press **Conductor**.

- **Timetable Mode:** instead of running back and forth, the train runs the game's own timetable (turn the timetable on in Preferences and write it in the Timetable window). Tick the passenger trains the engine runs. It calls at each train's stations in order and doesn't leave a station before that train's time there (late, it leaves as soon as passengers are done). At the end of one train it goes out as the next ticked train that leaves from that station, turning on the wye first if the engine would be at the back. **Start** picks the ticked train due away soonest from the station nearest the engine, or else the one due away soonest anywhere, and runs to its first station. With **Lay Up Overnight** on, a wait of more than 90 minutes for the next train is spent parked (as below), back at the platform 30 minutes before it's due; the Last Run and Resume times aren't used. The game gathers passengers at each station for the timetabled trains, so they turn up when the trains are due. The **Running As** field shows which train the engine is now.
- **Stations:** every station on the map, in order along the line, with branches under their junction. Tick the ones to call at. The train runs them top to bottom and back; the first and last ticked are the ends of the line. Stations you haven't bought or built track to yet aren't listed. The list is worked out fresh each time, so stations from an expansion show up as soon as their track is open.
- **Shortest Stop:** the least time the train stands at each station, even when nobody is getting on or off (30 seconds unless you change it). Give it longer if another mod, such as Railside Dispatch, loads baggage or mail at stations: Conductor doesn't know when that's done.
- **Wait to Fill Up:** for each station on the line, how full the coaches should be before the train leaves (Don't wait, 25% up to 100%). The train holds there, boarding passengers as they turn up, until it's that full or it has waited as long as **Wait No Longer Than** (1 hour unless you change it).
- **Use Station Sidings:** where a station has a passing siding beside the main line, the train stops on the siding, clear of both switches, and leaves the main open (when it fits; otherwise it uses the main). On by default.
- **Avoid Oncoming Trains:** on the way to a station, the train looks about three miles down its route every few seconds for a train coming the other way. It reads the other train's Auto Engineer route when it has one (Waypoint mode), or follows the switches as they lie in the direction it's moving (Road mode, or driven by hand). If their routes cross head-on, it picks a passing siding on its route that the other train's route goes through, and waits on whichever track the other train isn't taking, stopped 100 feet clear of the switches so it doesn't foul them: the siding if the other train is on the main, the main if it's routed through the siding. It takes the farthest one it can be into in time, else the nearest. Standing in a siding already (at a station, say) with the other train routed around it, it just waits there. Once the other train is off the siding and its route no longer crosses it, the train carries on. It doesn't wait for a train still more than about two miles away along its route, and a switching move only counts up to where it reverses. It gives up if the other train hasn't come any closer in five minutes, or after half an hour of game time, and doesn't wait for that train again for fifteen minutes. Two Conductor trains don't both take the siding: one holds the main. On by default.
- **Fuel Stops:** set **Refuel Below**. With **Use Any Open Stop** on (the default), the train uses any water spout, coal chute or diesel stand in an opened area, such as the Whittier engine shop, and later Bryson's engine house once it opens. Turn it off to pick your own with **Add Fuel Stop** (click the track beside the spout). After a station, if the engine's water, coal or diesel is below that level, the train pulls up at the nearest fuel stop with the tender's filler right under the spout, fills up, tops up anything else at a stop close by, and carries on.
- **Turn Around:** with **Turn on Wyes** on, at each end of the line the train turns on the nearest wye if the engine would otherwise be at the back for the trip home: in by one leg, all the way into the tail, and out by the other leg, at the **Turn Speed** (15 mph unless you change it; coupling up is always at 10 mph). It doesn't stop halfway along a wye leg or the far track of a run-around: those waypoints only steer it through the right switch, and it runs straight on past them. Before each turning move (and every few seconds during it) Conductor checks the route the Auto Engineer would take: if the direct way is blocked, say by cars or another train on the main, the AE would happily go round by reversing through the tail and turn the train too early, so instead the train waits, with the reason in its status, and carries on once the way is clear. It only uses a station's own wye (the station nearest it). When the end of the line has no wye, the train leaves with the engine at the back, and turns at the next station along the way that has one. A train too long for the tail stands in a passing siding while the engine cuts off, turns on the wye by itself, runs around the train and couples on at the front. Where the train is standing counts if there's a way around it (spurs and other switches along the tracks are fine); otherwise it pulls into the nearest siding that fits, within about a mile and a half. The tail is measured along the track through any switches, so a wye whose tail runs on up a branch, like Whittier's up the Connelly Creek branch, turns the whole train. An observation car (car type PBO) on the rear, platform outward, can't stay there, since the game pays its 20% fare bonus only when it's the last car with its platform end free: the engine fetches it, takes it through the wye too, sets it on the other end of the train platform outward, then runs around once more to couple on. The standing coaches get their handbrakes on while the engine is away, and the air is connected and the brakes released before the train leaves. Without a passing siding, or with one too short for the engine, the train runs back with the engine at the rear. An engine that's already at the front but running backward is turned the same way, and one at the rear but facing the right way just runs around.
- **Overnight:** with **Lay Up Overnight** on, a train that reaches an end of the line at or after **Last Run Starts** (8:30 pm unless you change it) lets everyone off, takes nobody on, and parks for the night, so no passengers are left aboard. It parks on a repair track that holds the whole train, so the shop works on it overnight: of those nearby, the one that works on the most of the train's cars (engine, tender and coaches, since worn coaches cost the railroad passenger reputation). It looks about five miles out when any car is below 90% or needs oiling, a couple of miles otherwise. Failing that, in a passing siding nearby (the siding rather than the main); failing that, at the station. At **Resume At** (6:00 am unless you change it) it comes back to the station, boards passengers and starts the day's service. Off by default.
- **Speed:** how fast the Auto Engineer runs between stops.
- **Enabled:** whether Conductor runs this engine at all. It's off for every engine until you turn it on, so freight and work-train engines are left alone (the Car tab shows "off"); turn it on for your passenger engines. Engines already set up for a run count as on. Untick it to take a train over by hand without losing its place, for example to untangle it from another train. The train stops where it is, lets go of any spout, and Conductor leaves it alone; you can drive it or set your own waypoints. Tick it again and the train carries on with what it was doing, from wherever it is now. The Car tab shows "Conductor disabled" meanwhile.
- **Start** puts the Auto Engineer on Waypoint and begins at the nearest ticked station, or the next one ahead when the train is between stations. From partway along the line it goes first the way **First Toward** says, or by default the way the train is moving (the way the engine faces, when it's stopped). **Turn Back** sends the train back to the station it last left and runs the line the other way from there, whether it was heading for a station, pulled into a siding for a meet or going to a fuel stop. At a platform it leaves the other way; during a layover it sets the way it goes in the morning; it does nothing partway through a wye turn (press it again once the turn is done). **Stop** hands the train back to you.

At each station the train waits until everyone for that station is off, and until it's full or nobody is left waiting for the stations ahead (and as long as Shortest Stop and Wait to Fill Up say). The coaches only take on passengers for the stations still ahead on that leg. If the train leaves full with people still waiting, Conductor says so in the message log; another coach would carry them. The game log (Player.log) notes every departure with how many seats were taken.

Where the station's building is loaded, the train stops with the middle of its coaches beside it, the engine running on past so it doesn't take up the platform; the whole train stays on the platform track where it fits.

The **Log** at the bottom of the window lists what Conductor has done with that engine, newest first: arrivals and departures, meets, wye turns, fuel stops and anything it's waiting for. Scroll back to see why a train did what it did.

Taking the Auto Engineer off waypoints stops Conductor for that train.

## Good to know

- Don't run another mod that drives the same engine (Passenger Helper, say) at the same time. Waypoint Queue can stay installed. While Conductor is running an engine, Waypoint Queue can't add waypoints or apply a route to it (you get a message saying so), and any queue or looping route it already had for that engine is cleared, so it can't take the train over. Other engines are left to Waypoint Queue. While Conductor is running an engine, the Choose Waypoint button in its engine controls is greyed out and the waypoint key does nothing; untick Enabled or press Stop to pick a waypoint yourself.
- The game's own Auto Engineer station stops (the AI passenger stop setting) are switched off for engines Conductor is running, since Conductor makes the stops itself; the game's version stops short of the platform when the train is backing up. Other engines are unaffected.
- The Auto Engineer throws switches itself on the way, unless a car is standing on one or it is under dispatcher (CTC) control.
- Settings and runs are saved with the game. In multiplayer, the host runs the trains; everyone sees the window.

## Changelog

### 0.2.2 (2026-11-24)
- The Conductor status on an engine's Car tab is on its own line under the Conductor button, so a long status no longer runs over the button.

### 0.2.1 (2026-11-24)
- Waypoint Queue can no longer take over a Conductor train: while Conductor is running an engine, Waypoint Queue can't add waypoints or apply a route to it, and any queue or looping route it already had for the engine is cleared.

### 0.2.0 (2026-11-23)
- **Timetable Mode:** runs the game's own timetable, train by train, instead of shuttling between the ends of the line.
- **Shortest Stop** and **Wait to Fill Up** (with **Wait No Longer Than**) settings for how long the train stands at stations.
- Coaches stop centered on the station building instead of at the far end of the platform track.
- A **Log** at the bottom of the Conductor window shows that engine's Conductor messages.
- **Turn Speed** setting for wye turns and run-arounds (15 mph by default). The train no longer stops halfway along a wye leg.
- Before a turning move, the train checks the Auto Engineer's route and waits if the direct way is blocked, rather than letting the AE reverse through the tail and turn the train too early. It no longer holds a move whose reversal is part of the turn.
- Meets: trains more than about two miles away along their route are ignored, a switching train only counts up to where it reverses, and the train stops waiting for a train that hasn't come closer in five minutes (and doesn't wait for it again for fifteen).
- **Turn Back** now works whenever the train is running: on the way to a station, in a meet siding, heading for a fuel stop, at a platform or during a layover.
- The Choose Waypoint button and key are disabled for engines Conductor is running. Waypoint Queue can stay installed.
- Conductor says in the message log when a train leaves full with passengers still waiting.

### 0.1.1 (2026-11-15)
- Fuel stops learn how far the engine overshoots the spout and correct for it.

### 0.1.0 (2026-11-15)
- First release.

## Install

1. In Railroader, turn on **Preferences > Features > Experimental > Allow Unsafe DLL Mods**, then restart the game.
2. Unzip `Conductor_<version>.zip` into `Documents\Railroader\Mods\`. You should end up with `Mods\Conductor\` holding `Info.json`, `Conductor.dll` and `0Harmony.dll`.
3. On the main menu, open **Mods** and enable Conductor.

## Building

Copy `Paths.user.example` to `Paths.user` and set `GameDir` (and `ModDevDir` for test builds). Then:

- `dotnet build Conductor/Conductor.csproj` builds and copies the mod into `Documents/Railroader/ModDev/Conductor`.
- `dotnet build Conductor/Conductor.csproj -c Release` writes `bin/Conductor_<version>.zip`.

MIT licensed. Includes Harmony by Andreas Pardeike (MIT). The fuel-stop lining up, and the uncoupling, air and coupling steps of a run-around, follow the approach of Waypoint Queue by PrinceOfPluto (MIT).
