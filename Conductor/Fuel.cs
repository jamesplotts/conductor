using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Messages;
using Game.State;
using Helpers;
using Model;
using Model.AI;
using Model.Ops;
using Model.Definition.Data;
using Model.Ops.Definition;
using RollingStock;
using Track;
using UnityEngine;

namespace Conductor
{
    // Water spouts, coal chutes and diesel stands, and getting an engine filled at one. A loader
    // only fills a car whose filler is within a hand's width of it, and only while its sequencer
    // has been asked to load, as when a player pulls the spout down.
    internal static class Fuel
    {
        public const string Water = "rr/water";
        public const string Coal = "rr/coal";
        public const string Diesel = "rr/diesel-fuel";

        private static List<CarLoadTargetLoader> _loaders;
        private static List<CarLoaderSequencer> _sequencers;
        private static float _foundAt;
        private static readonly FieldInfo SourceIndustryId = typeof(CarLoadTargetLoader).GetField("sourceIndustryId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static void Forget()
        {
            _loaders = null;
            _sequencers = null;
        }

        // Found again every half minute: loaders in an area come to life when the area opens.
        private static List<CarLoadTargetLoader> Loaders()
        {
            if (_loaders == null || _loaders.Any(l => l == null) || Time.realtimeSinceStartup - _foundAt > 30f)
            {
                _foundAt = Time.realtimeSinceStartup;
                _loaders = Object.FindObjectsByType<CarLoadTargetLoader>(FindObjectsSortMode.None).ToList();
                _sequencers = Object.FindObjectsByType<CarLoaderSequencer>(FindObjectsSortMode.None).ToList();
            }
            return _loaders;
        }

        public static CarLoadTargetLoader Find(string loaderId) =>
            Loaders().FirstOrDefault(l => l.keyValueObject != null && l.keyValueObject.RegisteredId == loaderId);

        // Every fuel stop for these loads that a train can use now: in an opened area, on built track.
        public static List<CarLoadTargetLoader> Open(IEnumerable<string> loads)
        {
            var wanted = new HashSet<string>(loads);
            Loaders();
            return _loaders.Where(l => l != null && l.isActiveAndEnabled && l.load != null && wanted.Contains(l.load.id) && l.keyValueObject != null
                    && _sequencers.Any(s => s != null && s.keyValueObject != null && s.keyValueObject.RegisteredId == l.keyValueObject.RegisteredId)
                    && IsOpen(l))
                .ToList();
        }

        private static bool IsOpen(CarLoadTargetLoader loader)
        {
            string industryId = SourceIndustryId?.GetValue(loader) as string;
            Industry industry = string.IsNullOrEmpty(industryId) ? null : OpsController.Shared?.IndustryForId(industryId);
            if (industry != null)
                return industry.IsProgressionEnabled && industry.IsTrackReachable;
            return Graph.Shared.TryGetLocationFromWorldPoint(loader.transform.position, 10f, out _);
        }

        // What this engine burns and drinks.
        public static List<string> LoadsFor(BaseLocomotive loco)
        {
            if (loco.CarType.IsLocomotiveSteam)
                return new List<string> { Water, Coal };
            if (loco.CarType.IsLocomotiveDiesel)
                return new List<string> { Diesel };
            return new List<string>();
        }

        public static string Describe(string loadId)
        {
            Load load = CarLoadLibrary.GetLoad(loadId);
            return load != null ? load.description : loadId;
        }

        // The loader nearest a point on the track that serves one of these loads, within reach.
        public static CarLoadTargetLoader Nearest(Location location, IEnumerable<string> loads, float within = 25f)
        {
            var wanted = new HashSet<string>(loads);
            Vector3 at = Graph.Shared.GetPosition(location);
            return Loaders().Where(l => l != null && l.load != null && wanted.Contains(l.load.id) && l.keyValueObject != null)
                .Select(l => (loader: l, d: Flat(Point(l), at)))
                .Where(x => x.d <= within).OrderBy(x => x.d).Select(x => x.loader).FirstOrDefault();
        }

        // The loader's spout, in game coordinates (as the track graph uses).
        public static Vector3 Point(CarLoadTargetLoader loader) => WorldTransformer.WorldToGame(loader.transform.position);

        // The car carrying an engine's water, coal or diesel: a steam engine's tender, or the engine itself.
        public static Car FuelCar(BaseLocomotive loco, string loadId, out int slotIndex)
        {
            var candidates = new List<Car> { loco };
            if (loco.TryGetAdjacentCar(Car.LogicalEnd.A, out Car a))
                candidates.Add(a);
            if (loco.TryGetAdjacentCar(Car.LogicalEnd.B, out Car b))
                candidates.Add(b);
            foreach (Car car in candidates)
            {
                List<LoadSlot> slots = car.Definition.LoadSlots;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].RequiredLoadIdentifier == loadId)
                    {
                        slotIndex = i;
                        return car;
                    }
                }
            }
            slotIndex = -1;
            return null;
        }

        // How full the engine is of this load, 0 to 1, or -1 if it doesn't carry it.
        public static float Level(BaseLocomotive loco, string loadId)
        {
            Car car = FuelCar(loco, loadId, out int slot);
            if (car == null)
                return -1f;
            float capacity = car.Definition.LoadSlots[slot].MaximumCapacity;
            if (capacity <= 0f)
                return -1f;
            CarLoadInfo? info = car.GetLoadInfo(slot);
            return Mathf.Clamp01((info?.Quantity ?? 0f) / capacity);
        }

        // Where the car's filler for this load is, in game coordinates.
        public static bool TryFillerPoint(Car car, int slotIndex, out Vector3 point, out float radius)
        {
            CarLoadTarget target = car.GetComponentsInChildren<CarLoadTarget>().FirstOrDefault(t => t.slotIndex == slotIndex);
            if (target == null)
            {
                point = default;
                radius = 0f;
                return false;
            }
            Vector3 local = car.transform.InverseTransformPoint(target.transform.position);
            point = car.GetTransformMatrix(Graph.Shared).MultiplyPoint3x4(local);
            radius = target.radius;
            return true;
        }

        // How far the filler is from the spout, flat; float.MaxValue when it can't tell.
        public static float Miss(BaseLocomotive loco, CarLoadTargetLoader loader)
        {
            Car car = FuelCar(loco, loader.load.id, out int slot);
            if (car == null || !TryFillerPoint(car, slot, out Vector3 filler, out _))
                return float.MaxValue;
            return Flat(filler, Point(loader));
        }

        public static bool LinedUp(BaseLocomotive loco, CarLoadTargetLoader loader)
        {
            Car car = FuelCar(loco, loader.load.id, out int slot);
            if (car == null || !TryFillerPoint(car, slot, out Vector3 filler, out float radius))
                return false;
            return Flat(filler, Point(loader)) <= radius + loader.radius;
        }

        // Where to send the train so the filler stops under the spout: the end of the train nearest
        // the spout leads, so it goes past the spout by the distance from that end to the filler.
        // When the spout is under the train already, it first backs the train clear (clearing = true).
        public static bool TryApproach(BaseLocomotive loco, CarLoadTargetLoader loader, out Location target, out bool clearing, out string why)
        {
            target = default;
            clearing = false;
            why = null;
            Graph graph = Graph.Shared;
            Car car = FuelCar(loco, loader.load.id, out int slot);
            if (car == null || !TryFillerPoint(car, slot, out Vector3 filler, out _))
            {
                why = $"{loco.DisplayName} doesn't take {Describe(loader.load.id)}";
                return false;
            }
            if (!graph.TryGetLocationFromWorldPoint(loader.transform.position, 10f, out Location spout))
            {
                why = "the fuel stop isn't beside any track";
                return false;
            }
            List<Car> train = loco.EnumerateCoupled().ToList();
            Location endA = train[0].LocationFor(Car.LogicalEnd.A);
            Location endB = train[train.Count - 1].LocationFor(Car.LogicalEnd.B);
            Vector3 spoutAt = graph.GetPosition(spout);
            Vector3 aAt = graph.GetPosition(endA), bAt = graph.GetPosition(endB);
            float toA = Flat(aAt, spoutAt), toB = Flat(bAt, spoutAt), length = Flat(aAt, bAt);
            bool nearA = toA <= toB;
            Location near = nearA ? endA : endB, far = nearA ? endB : endA;
            if (toA < length && toB < length)
            {
                // Under the train: pull the far end away until the near end is a little past the spout.
                clearing = true;
                target = graph.LocationByMoving(graph.LocationOrientedToward(far, spout), -(Mathf.Min(toA, toB) + 15f), false, true);
                return true;
            }
            // Cars from the leading end up to and including the fuel car, then back off by the
            // part of the fuel car beyond its filler.
            List<Car> leading = nearA ? train : Enumerable.Reverse(train).ToList();
            int index = leading.IndexOf(car);
            float toFuelCarEnd = AutoEngineerPlanner.CalculateTotalLength(leading.Take(index + 1).ToList());
            Car.LogicalEnd fuelCarFarEnd = car.ClosestLogicalEndTo(far, graph) ;
            float beyondFiller = Flat(graph.GetPosition(car.LocationFor(fuelCarFarEnd)), filler);
            float past = toFuelCarEnd - beyondFiller;
            target = graph.LocationByMoving(graph.LocationOrientedToward(spout, near), -past, false, true);
            return true;
        }

        // Pulls the spout down (or lets it go) as a player would.
        public static bool SetLoading(CarLoadTargetLoader loader, bool on)
        {
            Loaders();
            string id = loader.keyValueObject.RegisteredId;
            CarLoaderSequencer sequencer = _sequencers?.FirstOrDefault(s => s != null && s.keyValueObject != null && s.keyValueObject.RegisteredId == id);
            if (sequencer == null)
                return false;
            StateManager.ApplyLocal(new PropertyChange(id, sequencer.readWantsLoadingKey, new BoolPropertyValue(on)));
            return true;
        }

        public static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
    }
}
